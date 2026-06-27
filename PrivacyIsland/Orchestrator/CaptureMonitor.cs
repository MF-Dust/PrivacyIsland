using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrivacyIsland.Config;
using PrivacyIsland.Ipc;
using PrivacyIsland.Logging;
using PrivacyIsland.Native;
using PrivacyIsland.Statistics;

namespace PrivacyIsland.Orchestrator;

/// <summary>
/// 编排器（替代原生 main.c）：轮询 media_capture.exe，发现即用 nmm_injector.exe 注入 hook DLL，
/// 并把 DLL 经共享内存上报的状态分发给提醒/自动化/统计/日志。
/// 注入需要权限——通常要求以管理员身份运行 ClassIsland，否则 OpenProcess 失败。
/// </summary>
public sealed class CaptureMonitor : IHostedService, IDisposable
{
    const string TargetProcessName = "media_capture";   // 不含 .exe
    const string DllFileName = "PrivacyIslandHook.dll";
    const string InjectorFileName = "nmm_injector.exe";
    static readonly TimeSpan InjectionRetryInterval = TimeSpan.FromSeconds(15);

    readonly string _folder;
    readonly ILogger<CaptureMonitor> _logger;
    Timer? _timer;
    int _lastInjectedPid;     // 同一目标进程注入成功后不重复处理
    int _lastAttemptPid;      // 注入失败时保留 pid，并按冷却时间重试
    DateTime _lastAttemptUtc;
    int _lastInjectionCode;
    string _lastInjectionMessage = "尚未尝试注入";
    int _polling;             // 防止轮询重入
    bool _awaitingDelay;      // 收到 start 后，等首条 "Delay N s" 以统计本次延迟

    // 分层暂停：多个来源（manual/automation/lesson）可各自请求暂停，任一生效即暂停。
    readonly object _pauseGate = new();
    readonly HashSet<string> _pauseSources = new();
    (int min, int max)? _delayOverride;   // 临时延迟覆盖（如上课加强延迟），不写 config.json

    string _dllPath = "";
    string _injectorPath = "";

    public PluginConfig Config { get; private set; }
    public SharedMemoryBridge? Bridge { get; private set; }
    public CaptureStats Stats { get; private set; }

    public CaptureMonitor(string pluginConfigFolder, ILogger<CaptureMonitor> logger)
    {
        _folder = pluginConfigFolder;
        _logger = logger;
        PluginLog.Init(_logger);
        Config = new PluginConfig();
        Stats = new CaptureStats();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Config = PluginConfig.Load(_folder);
        Stats = CaptureStats.Load(_folder);

        string dir = Path.GetDirectoryName(typeof(CaptureMonitor).Assembly.Location) ?? AppContext.BaseDirectory;
        _dllPath = Path.Combine(dir, DllFileName);
        _injectorPath = Path.Combine(dir, InjectorFileName);
        if (!File.Exists(_dllPath)) PluginLog.Error($"找不到 hook DLL：{_dllPath}");
        if (!File.Exists(_injectorPath)) PluginLog.Error($"找不到注入器：{_injectorPath}");

        Bridge = new SharedMemoryBridge();
        Bridge.StateReceived += OnState;
        Bridge.Start(Config.MinDelaySeconds, Config.MaxDelaySeconds, Config.StealthMode);

        PrivacyIslandRuntime.Monitor = this;

        _timer = new Timer(_ => PollSafe(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    void PollSafe()
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try { PollOnce(); }
        catch (Exception ex) { PluginLog.Error("轮询异常：" + ex.Message); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    void PollOnce()
    {
        var target = FindTargetProcess();
        if (target is null)
        {
            _lastInjectedPid = 0;
            _lastAttemptPid = 0;
            return;
        }

        if (target.Pid == _lastInjectedPid) return;     // 该进程实例已成功处理
        if (target.Pid == _lastAttemptPid &&
            DateTime.UtcNow - _lastAttemptUtc < InjectionRetryInterval)
            return;                                     // 失败后冷却，避免每秒刷日志/拉起注入器

        _lastAttemptPid = target.Pid;
        _lastAttemptUtc = DateTime.UtcNow;
        var result = Inject(target);
        if (result.Success) _lastInjectedPid = target.Pid;
    }

    static TargetProcessInfo? FindTargetProcess()
    {
        var procs = Process.GetProcessesByName(TargetProcessName);
        if (procs.Length == 0) return null;

        try
        {
            var candidates = new List<TargetProcessInfo>(procs.Length);
            foreach (var p in procs)
            {
                try { candidates.Add(TargetProcessInfo.FromProcess(p)); }
                catch { candidates.Add(new TargetProcessInfo(p.Id, "", "", "", "", "", false, null)); }
            }

            // 希沃 2026 版本的媒体采集工具箱入口位于 toolbox\media_capture\media_capture.exe。
            // 多实例时优先选这个路径，再退回到有希沃版本信息的进程，最后才取 pid 最小的实例。
            return candidates
                .OrderByDescending(c => c.IsSeewoMediaCaptureToolbox)
                .ThenByDescending(c => c.IsLikelySeewo)
                .ThenBy(c => c.Pid)
                .FirstOrDefault();
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    PluginOperationResult Inject(TargetProcessInfo target)
    {
        if (!File.Exists(_injectorPath))
        {
            string message = $"找不到注入器：{_injectorPath}";
            PluginLog.Error(message);
            _lastInjectionCode = -10;
            _lastInjectionMessage = message;
            return PluginOperationResult.Fail(message);
        }
        if (!File.Exists(_dllPath))
        {
            string message = $"找不到 hook DLL：{_dllPath}";
            PluginLog.Error(message);
            _lastInjectionCode = -11;
            _lastInjectionMessage = message;
            return PluginOperationResult.Fail(message);
        }

        int code = RunInjector($"--inject {target.Pid} \"{_dllPath}\"");
        _lastInjectionCode = code;
        if (code == 0)
        {
            string message = $"已注入 media_capture.exe (pid={target.Pid}, {target.DisplayName})";
            PluginLog.Info(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Ok(message);
        }
        else
        {
            string message = $"注入失败 (pid={target.Pid}, code={code}, {target.DisplayName})。将在 {InjectionRetryInterval.TotalSeconds:0}s 后重试；请确认 ClassIsland 以管理员身份运行。";
            PluginLog.Warn(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Fail(message);
        }
    }

    int RunInjector(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(_injectorPath, args) { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            return p.WaitForExit(8000) ? p.ExitCode : -2;
        }
        catch (Exception ex) { PluginLog.Error("启动注入器失败：" + ex.Message); return -3; }
    }

    // ---- 控制面（供自动化行动 / 设置页 / 课程控制器调用）----

    /// <summary>当前是否处于暂停态（任一暂停源生效）。供规则读取。</summary>
    public bool EffectivePaused
    {
        get { lock (_pauseGate) return _pauseSources.Count > 0; }
    }

    /// <summary>
    /// 分层暂停：按来源 key 增删暂停请求，任一来源生效即暂停。
    /// 来源约定：manual=设置页/手动，automation=自动化行动，lesson=课程联动。
    /// </summary>
    public void SetPauseSource(string key, bool active)
    {
        bool effective;
        bool changed;
        lock (_pauseGate)
        {
            changed = active ? _pauseSources.Add(key) : _pauseSources.Remove(key);
            effective = _pauseSources.Count > 0;
        }
        if (!changed) return;     // 该来源状态未变，避免重复写/刷日志
        ApplyEffectiveToBridge();
        PluginLog.Info(effective
            ? $"防护已暂停（来源：{key}；摄像头将不被延迟）"
            : "防护已恢复（无暂停来源）");
    }

    /// <summary>兼容旧调用：等价于 manual 暂停源。</summary>
    public void SetPaused(bool paused) => SetPauseSource("manual", paused);

    /// <summary>
    /// 临时延迟覆盖：非空写入覆盖值（不落盘，用于上课加强延迟）；空则清除覆盖、恢复 Config 基准延迟。
    /// </summary>
    public void ApplyDelayOverride(int? min, int? max)
    {
        (int min, int max)? next;
        if (min.HasValue && max.HasValue)
        {
            int lo = Math.Clamp(min.Value, 1, 30);
            int hi = Math.Clamp(max.Value, 1, 30);
            if (hi < lo) hi = lo;
            next = (lo, hi);
        }
        else
        {
            next = null;
        }

        if (next.Equals(_delayOverride)) return;   // 无变化，避免重复写/刷日志（每次自动保存都会重评估）

        _delayOverride = next;
        PluginLog.Info(next.HasValue
            ? $"已应用临时延迟覆盖：{next.Value.min}-{next.Value.max}s（不写配置）"
            : $"已清除临时延迟覆盖，恢复基准 {Config.MinDelaySeconds}-{Config.MaxDelaySeconds}s");
        ApplyEffectiveToBridge();
    }

    public void SetDelay(int min, int max)
    {
        Config.MinDelaySeconds = min;
        Config.MaxDelaySeconds = max;
        SaveAndApply();
    }

    /// <summary>设置页保存：校验当前 Config、落盘、写共享内存（带当前暂停态与延迟覆盖）。</summary>
    public void SaveAndApply()
    {
        Config.Clamp();
        Config.Save(_folder);
        ApplyEffectiveToBridge();
        PluginLog.Info($"设置已保存：延迟 {Config.MinDelaySeconds}-{Config.MaxDelaySeconds}s, 隐身={Config.StealthMode}, 语音={Config.SpeechEnabled}（隐身需重注入生效）");
    }

    /// <summary>把当前生效的延迟（覆盖优先于基准）与暂停态一次性写入共享内存。</summary>
    void ApplyEffectiveToBridge()
    {
        var (min, max) = _delayOverride ?? (Config.MinDelaySeconds, Config.MaxDelaySeconds);
        Bridge?.WriteConfig(min, max, EffectivePaused, Config.StealthMode);
    }

    /// <summary>应用内功能测试：注入一帧合成状态，走真实分发路径（提醒/触发器/规则/统计全联动），无需真注入。</summary>
    public void Simulate(int state, string message) => Bridge?.Simulate(state, message);

    /// <summary>诊断信息：文件/IPC/目标进程/注入/权限状态，给设置页的功能测试区展示。</summary>
    public string Diagnostics()
    {
        var target = FindTargetProcess();
        string targetSummary = target is null ? "否" : $"是 (pid={target.Pid}, {target.DisplayName})";
        string bootSummary = target is null ? "未检测" : DescribeBootConfig(target.ExecutablePath);
        string portsSummary = target is null ? "未检测" : DescribeListeningPorts(target.Pid);
        return
            $"注入器存在: {(File.Exists(_injectorPath) ? "是" : "否")}\n" +
            $"hook DLL 存在: {(File.Exists(_dllPath) ? "是" : "否")}\n" +
            $"IPC 就绪: {(Bridge != null ? "是" : "否")}\n" +
            $"以管理员运行: {(IsAdmin() ? "是" : "否（跨进程注入通常需要）")}\n" +
            $"检测到 media_capture.exe: {targetSummary}\n" +
            $"目标路径: {DisplayPath(target?.ExecutablePath)}\n" +
            $"目标版本: {DisplayVersion(target)}\n" +
            $"目标 BootConfig: {bootSummary}\n" +
            $"目标监听端口: {portsSummary}\n" +
            $"反编译接口: {MediaCaptureProtocol.CapabilitySummary}\n" +
            $"已注入的 pid: {(_lastInjectedPid == 0 ? "无" : _lastInjectedPid.ToString())}\n" +
            $"最近注入结果: {_lastInjectionMessage} (code={_lastInjectionCode})\n" +
            $"摄像头当前活动: {(Bridge?.CameraActive == true ? "是" : "否")}";
    }

    static bool IsAdmin()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>PrivacyIsland 日志进入 ClassIsland 宿主日志，不再维护独立日志目录。</summary>
    public PluginOperationResult OpenLogsFolder()
    {
        const string message = "PrivacyIsland 日志已写入 ClassIsland 日志，不再生成独立日志文件。";
        PluginLog.Info(message);
        return PluginOperationResult.Ok(message);
    }

    public PluginOperationResult InjectNow()
    {
        var target = FindTargetProcess();
        if (target is null)
        {
            const string message = "未找到 media_capture.exe，无法注入";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }

        _lastAttemptPid = target.Pid;
        _lastAttemptUtc = DateTime.UtcNow;
        var result = Inject(target);
        if (result.Success) _lastInjectedPid = target.Pid;
        return result;
    }

    public PluginOperationResult EjectNow()
    {
        var target = FindTargetProcess();
        if (target is null)
        {
            const string message = "未找到 media_capture.exe，无法弹射";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }

        int code = RunInjector($"--eject {target.Pid} \"{_dllPath}\"");
        _lastInjectedPid = 0;
        _lastInjectionCode = code;
        if (code == 0)
        {
            const string message = "已弹射 hook DLL";
            PluginLog.Info(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Ok(message);
        }
        else
        {
            string message = $"弹射失败 (code={code})";
            PluginLog.Warn(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Fail(message);
        }
    }

    static string DisplayPath(string? path) => string.IsNullOrWhiteSpace(path) ? "未知（权限不足或进程已退出）" : path;

    static string DisplayVersion(TargetProcessInfo? target)
    {
        if (target is null) return "未检测";
        var parts = new[] { target.FileVersion, target.ProductVersion, target.Description, target.Product }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        string text = string.Join(" / ", parts);
        return string.IsNullOrWhiteSpace(text) ? "未知" : text;
    }

    static string DescribeListeningPorts(int pid)
    {
        try
        {
            var ports = TcpTable.GetListeningPorts(pid);
            return ports.Count == 0 ? "未发现（RPC/HTTP 可能尚未初始化）" : string.Join(", ", ports);
        }
        catch (Exception ex)
        {
            return "读取失败：" + ex.Message;
        }
    }

    static string DescribeBootConfig(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return "未知（无法读取目标路径）";
        try
        {
            string? dir = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(dir)) return "未知（目标路径无目录）";
            string path = Path.Combine(dir, "BootConfig.json");
            if (!File.Exists(path)) return "未找到";

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("default", out var root)) return "已找到（无 default 节）";
            string launcher = root.TryGetProperty("launcher", out var launcherElement) ? launcherElement.GetString() ?? "" : "";
            string needGuard = root.TryGetProperty("needGuard", out var needGuardElement) && needGuardElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (needGuardElement.GetBoolean() ? "true" : "false")
                : "未知";
            string order = root.TryGetProperty("order", out var orderElement) ? orderElement.ToString() : "未知";
            return $"launcher={launcher}, needGuard={needGuard}, order={order}";
        }
        catch (Exception ex)
        {
            return "读取失败：" + ex.Message;
        }
    }

    sealed record TargetProcessInfo(
        int Pid,
        string ExecutablePath,
        string FileVersion,
        string ProductVersion,
        string Description,
        string Product,
        bool Is32Bit,
        string? Machine)
    {
        public bool IsSeewoMediaCaptureToolbox =>
            ExecutablePath.Contains(@"\toolbox\media_capture\media_capture.exe", StringComparison.OrdinalIgnoreCase);

        public bool IsLikelySeewo =>
            IsSeewoMediaCaptureToolbox ||
            Product.Contains("希沃", StringComparison.OrdinalIgnoreCase) ||
            Description.Contains("媒体采集", StringComparison.OrdinalIgnoreCase);

        public string DisplayName
        {
            get
            {
                string arch = Machine ?? (Is32Bit ? "x86" : "x64/未知");
                if (!string.IsNullOrWhiteSpace(FileVersion)) arch += ", v" + FileVersion;
                if (IsSeewoMediaCaptureToolbox) arch += ", toolbox";
                return arch;
            }
        }

        public static TargetProcessInfo FromProcess(Process process)
        {
            string path = "";
            try { path = process.MainModule?.FileName ?? ""; }
            catch { }

            FileVersionInfo? version = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { version = FileVersionInfo.GetVersionInfo(path); }
                catch { }
            }

            string? machine = TryReadPeMachine(path);
            return new TargetProcessInfo(
                process.Id,
                path,
                version?.FileVersion ?? "",
                version?.ProductVersion ?? "",
                version?.FileDescription ?? "",
                version?.ProductName ?? "",
                string.Equals(machine, "x86", StringComparison.OrdinalIgnoreCase),
                machine);
        }

        static string? TryReadPeMachine(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt16() != 0x5A4D) return null; // MZ
                fs.Position = 0x3C;
                int peOffset = br.ReadInt32();
                if (peOffset <= 0 || peOffset > fs.Length - 6) return null;
                fs.Position = peOffset;
                if (br.ReadUInt32() != 0x00004550) return null; // PE\0\0
                ushort machine = br.ReadUInt16();
                return machine switch
                {
                    0x014C => "x86",
                    0x8664 => "x64",
                    0x01C4 => "ARM",
                    0xAA64 => "ARM64",
                    _ => "0x" + machine.ToString("X4"),
                };
            }
            catch { return null; }
        }
    }

    // ---- DLL 状态分发 ----

    void OnState(CaptureSnapshot s)
    {
        switch (s.State)
        {
            case IpcProtocol.StatusStart:
                Stats.RecordCapture(isDirectShow: s.Message.Contains("DS"));
                _awaitingDelay = true;
                PluginLog.CaptureStart(s.Message);
                break;

            case IpcProtocol.StatusLog:
                if (_awaitingDelay)
                {
                    int sec = ParseDelaySeconds(s.Message);
                    if (sec > 0) { Stats.AddDelay(sec); _awaitingDelay = false; }
                }
                break;

            case IpcProtocol.StatusStop:
                PluginLog.CaptureStop(s.Message);
                break;

            case IpcProtocol.StatusError:
                PluginLog.Error($"DLL 错误：{s.Message} (code={s.Error})");
                break;

            case IpcProtocol.StatusInfo:
            case IpcProtocol.StatusReady:
                PluginLog.Info(s.Message);
                break;
        }

        // 提醒/触发器会触达 Avalonia UI，必须在 UI 线程分发（读线程是后台线程）。
        var ui = Avalonia.Threading.Dispatcher.UIThread;
        if (ui.CheckAccess()) PrivacyIslandRuntime.RaiseState(s);
        else ui.Post(() => PrivacyIslandRuntime.RaiseState(s));
    }

    static int ParseDelaySeconds(string msg)
    {
        var m = Regex.Match(msg, @"Delay\s+(\d+)\s*s");
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (Bridge != null)
        {
            Bridge.StateReceived -= OnState;
            Bridge.Dispose();
            Bridge = null;
        }
    }
}
