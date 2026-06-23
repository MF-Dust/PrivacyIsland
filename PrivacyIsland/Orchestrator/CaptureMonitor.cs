using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrivacyIsland.Config;
using PrivacyIsland.Ipc;
using PrivacyIsland.Logging;
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
    const string DllFileName = "NoMoreMonitor_Dll.dll";
    const string InjectorFileName = "nmm_injector.exe";

    readonly string _folder;
    readonly ILogger<CaptureMonitor> _logger;
    Timer? _timer;
    int _handledPid;          // 已处理过的目标 pid（成功/失败都记，避免每秒重试同一实例）
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
        int pid = FindTargetPid();
        if (pid == 0) { _handledPid = 0; return; }
        if (pid == _handledPid) return;     // 该进程实例已处理（成功或失败都不再每秒重试）
        _handledPid = pid;
        Inject(pid);
    }

    static int FindTargetPid()
    {
        var procs = Process.GetProcessesByName(TargetProcessName);
        if (procs.Length == 0) return 0;
        int pid = procs[0].Id;
        foreach (var p in procs) p.Dispose();
        return pid;
    }

    PluginOperationResult Inject(int pid)
    {
        if (!File.Exists(_injectorPath))
        {
            string message = $"找不到注入器：{_injectorPath}";
            PluginLog.Error(message);
            return PluginOperationResult.Fail(message);
        }
        if (!File.Exists(_dllPath))
        {
            string message = $"找不到 hook DLL：{_dllPath}";
            PluginLog.Error(message);
            return PluginOperationResult.Fail(message);
        }

        int code = RunInjector($"--inject {pid} \"{_dllPath}\"");
        if (code == 0)
        {
            string message = $"已注入 media_capture.exe (pid={pid})";
            PluginLog.Info(message);
            return PluginOperationResult.Ok(message);
        }
        else
        {
            string message = $"注入失败 (pid={pid}, code={code})。请尝试以管理员身份运行 ClassIsland。";
            PluginLog.Warn(message);
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
        int pid = FindTargetPid();
        return
            $"注入器存在: {(File.Exists(_injectorPath) ? "是" : "否")}\n" +
            $"hook DLL 存在: {(File.Exists(_dllPath) ? "是" : "否")}\n" +
            $"IPC 就绪: {(Bridge != null ? "是" : "否")}\n" +
            $"以管理员运行: {(IsAdmin() ? "是" : "否（跨进程注入通常需要）")}\n" +
            $"检测到 media_capture.exe: {(pid == 0 ? "否" : $"是 (pid={pid})")}\n" +
            $"已注入的 pid: {(_handledPid == 0 ? "无" : _handledPid.ToString())}\n" +
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
        int pid = FindTargetPid();
        if (pid == 0)
        {
            const string message = "未找到 media_capture.exe，无法注入";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }

        _handledPid = pid;
        return Inject(pid);
    }

    public PluginOperationResult EjectNow()
    {
        int pid = FindTargetPid();
        if (pid == 0)
        {
            const string message = "未找到 media_capture.exe，无法弹射";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }

        int code = RunInjector($"--eject {pid} \"{DllFileName}\"");
        _handledPid = 0;
        if (code == 0)
        {
            const string message = "已弹射 hook DLL";
            PluginLog.Info(message);
            return PluginOperationResult.Ok(message);
        }
        else
        {
            string message = $"弹射失败 (code={code})";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
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
