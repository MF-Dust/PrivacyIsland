using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
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

    // 自愈阈值。反汇编确认 hook DLL 有一条专用心跳线程：每 ~5s（WaitForSingleObject 超时 5000ms）
    // 在互斥锁下把 GetTickCount() 写入 heartbeat 偏移；不随捕获活动变化，是纯粹的「DLL 存活」信号。
    static readonly TimeSpan HookConfirmWindow = TimeSpan.FromSeconds(10);   // 注入后多久没 Ready/心跳/帧算「未确认存活」（≈2 拍心跳宽限）
    static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(15); // 心跳多久没变算「冻结」（≈3 拍，5s 一拍）
    const int MaxSelfHealReinjects = 2;                                      // 每个 pid 自愈重注入次数上限

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

    // OS 独立探测 + 融合/自愈状态（除标注外仅 timer 线程访问）。
    readonly CapabilityUsageProbe _cameraProbe = new("webcam");
    readonly CapabilityUsageProbe _microphoneProbe = new("microphone");
    readonly CapabilityUsageProbe _screenProbe = new("graphicsCaptureWithoutBorder");
    volatile bool _osCameraInUse;    // media_capture 的 OS 探测结果（timer 写，诊断读）
    volatile bool _fusedActive;      // 融合后的有效活动态（timer 写，规则/诊断读）
    bool _syntheticActive;           // 是否已补发过合成 start（避免重复）
    DateTime _injectedUtc;           // 本 pid 最近一次成功注入时刻
    int _healthPid;                  // 当前健康跟踪的 pid
    int _reinjectBudget;             // 该 pid 剩余自愈重注入预算
    bool _loggedTargetPid;           // 发现/消失日志去抖

    // 希沃隐私风险：timer 线程维护状态，UI/规则线程读取；确认框按 PID 去重并串行显示。
    readonly object _privacyGate = new();
    readonly Dictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot> _privacyRisks = new();
    readonly HashSet<(int Pid, DateTime? StartTimeUtc)> _promptedPrivacyProcesses = new();
    readonly Queue<PrivacyRiskSnapshot> _privacyPromptQueue = new();
    bool _privacyPromptShowing;
    string _privacyScanNote = "无";
    string _lastPrivacyOperation = "尚无";

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

        // 每拍都跑 OS 探测 + 融合——即使已注入的 pid 也要跑，「已开却没测到」正是发生在这里。
        bool osInUse = target?.ExecutablePath is string p && p.Length > 0 && _cameraProbe.IsInUseBy(p);
        _osCameraInUse = osInUse;
        UpdateFusion(target, osInUse);
        PollPrivacyRisks(target, osInUse);

        if (target is null)
        {
            if (_loggedTargetPid) { PluginLog.Info("media_capture.exe 已退出，等待下次出现"); _loggedTargetPid = false; }
            if (Bridge?.CameraActive == true) Bridge.ForceInactive("media_capture 已退出");
            _fusedActive = false;   // 目标已消失，融合态立即置否，不等下一拍
            _lastInjectedPid = 0;
            _lastAttemptPid = 0;
            return;
        }

        if (!_loggedTargetPid) { PluginLog.Info($"发现 media_capture.exe (pid={target.Pid}, {target.DisplayName})"); _loggedTargetPid = true; }

        if (target.Pid == _lastInjectedPid) { MaybeSelfHeal(target, osInUse); return; }   // 已处理——但仍监控 hook 是否真活着
        if (target.Pid == _lastAttemptPid &&
            DateTime.UtcNow - _lastAttemptUtc < InjectionRetryInterval)
            return;                                     // 失败后冷却，避免每秒刷日志/拉起注入器

        _lastAttemptPid = target.Pid;
        _lastAttemptUtc = DateTime.UtcNow;
        if (_healthPid != target.Pid) { _healthPid = target.Pid; _reinjectBudget = MaxSelfHealReinjects; }
        var result = Inject(target);
        if (result.Success) { _lastInjectedPid = target.Pid; _injectedUtc = DateTime.UtcNow; }
    }

    void PollPrivacyRisks(TargetProcessInfo? cameraTarget, bool cameraOsInUse)
    {
        var current = new Dictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot>();
        var notes = new List<string>();

        bool cameraTargetValid = cameraTarget is not null && IsExpectedPrivacyTarget(
            PrivacyRiskKind.Camera,
            cameraTarget.ProcessName,
            cameraTarget.Product,
            cameraTarget.OriginalFilename,
            cameraTarget.IsSignedBySeewo);
        if (ShouldTrackCameraPrivacyRisk(_fusedActive, cameraTargetValid))
        {
            bool hookActive = Bridge?.CameraActive == true;
            string evidence = hookActive && cameraOsInUse
                ? "hook 与 Windows 均检测到摄像头正在使用"
                : hookActive
                    ? "hook 检测到摄像头正在使用"
                    : "Windows 检测到摄像头正在使用";
            var risk = ToPrivacyRisk(PrivacyRiskKind.Camera, cameraTarget!, evidence);
            current[(risk.Kind, risk.ProcessId, risk.ProcessStartTimeUtc)] = risk;
        }

        if (Config.EnableScreenCaptureMonitoring)
        {
            AddProcessRisks(current, notes, PrivacyRiskKind.ScreenCapture, "screenCapture",
                "希沃屏幕采集组件已启动（进程信号，不代表已确认每次截图）");
            AddCapabilityRisks(current, PrivacyRiskKind.ScreenCapture, _screenProbe,
                "Windows 检测到无边框屏幕捕获正在使用");
        }
        if (Config.EnableRemoteControlMonitoring)
        {
            AddProcessRisks(current, notes, PrivacyRiskKind.RemoteControl, "rtcRemoteDesktop",
                "希沃远程桌面组件已启动");
        }
        if (Config.EnableMicrophoneMonitoring)
        {
            AddCapabilityRisks(current, PrivacyRiskKind.Microphone, _microphoneProbe,
                "Windows 检测到麦克风正在使用");
        }

        List<PrivacyRiskSnapshot> changed = new();
        lock (_privacyGate)
        {
            foreach (var (key, risk) in current)
            {
                if (!_privacyRisks.ContainsKey(key)) changed.Add(risk);
                _privacyRisks[key] = risk;
            }

            foreach (var (key, old) in _privacyRisks.ToArray())
            {
                if (current.ContainsKey(key)) continue;
                _privacyRisks.Remove(key);
                changed.Add(old with { Active = false, Evidence = old.Evidence + "；状态已结束" });
            }

            var activeProcesses = current.Keys
                .Where(key => key.Pid > 0)
                .Select(key => (key.Pid, key.StartTimeUtc))
                .ToHashSet();
            _promptedPrivacyProcesses.RemoveWhere(identity => !activeProcesses.Contains(identity));
        }

        _privacyScanNote = notes.Count == 0 ? "无" : string.Join("; ", notes);
        foreach (var risk in changed) PublishPrivacyRisk(risk, prompt: true);
    }

    internal static bool ShouldTrackCameraPrivacyRisk(bool fusedActive, bool targetVerified)
        => fusedActive && targetVerified;

    void AddProcessRisks(
        IDictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot> current,
        ICollection<string> notes,
        PrivacyRiskKind kind,
        string processName,
        string evidence)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            foreach (var process in processes)
            {
                TargetProcessInfo info;
                try { info = TargetProcessInfo.FromProcess(process); }
                catch
                {
                    notes.Add($"{processName}.exe(pid={process.Id}) 无法读取元数据");
                    continue;
                }

                if (!IsExpectedPrivacyTarget(kind, info.ProcessName, info.Product, info.OriginalFilename, info.IsSignedBySeewo))
                {
                    notes.Add($"{processName}.exe(pid={process.Id}) 未通过希沃数字签名/产品校验");
                    continue;
                }

                var risk = ToPrivacyRisk(kind, info, evidence);
                current[(kind, info.Pid, info.StartTimeUtc)] = risk;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    void AddCapabilityRisks(
        IDictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot> current,
        PrivacyRiskKind kind,
        CapabilityUsageProbe probe,
        string evidence)
    {
        foreach (string path in probe.InUseApps())
        {
            foreach (var info in FindProcessesByPath(path))
            {
                if (!IsSeewoCapabilityProcess(info)) continue;
                current[(kind, info.Pid, info.StartTimeUtc)] = ToPrivacyRisk(kind, info, evidence);
            }
        }
    }

    static IEnumerable<TargetProcessInfo> FindProcessesByPath(string executablePath)
    {
        string name;
        try { name = Path.GetFileNameWithoutExtension(executablePath); }
        catch { yield break; }
        if (string.IsNullOrWhiteSpace(name)) yield break;

        var processes = Process.GetProcessesByName(name);
        try
        {
            foreach (var process in processes)
            {
                TargetProcessInfo info;
                try { info = TargetProcessInfo.FromProcess(process); }
                catch { continue; }
                if (string.Equals(info.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
                    yield return info;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    static PrivacyRiskSnapshot ToPrivacyRisk(PrivacyRiskKind kind, TargetProcessInfo info, string evidence)
        => new(kind, true, info.Pid, info.StartTimeUtc, info.ProcessName, info.ExecutablePath, evidence);

    internal static bool IsExpectedPrivacyTarget(
        PrivacyRiskKind kind,
        string processName,
        string product,
        string originalFilename,
        bool signedBySeewo)
    {
        bool seewoProduct = product.Contains("希沃", StringComparison.OrdinalIgnoreCase);
        return kind switch
        {
            PrivacyRiskKind.Camera =>
                processName.Equals("media_capture", StringComparison.OrdinalIgnoreCase) &&
                originalFilename.Equals("media_capture.exe", StringComparison.OrdinalIgnoreCase) &&
                seewoProduct && signedBySeewo,
            PrivacyRiskKind.ScreenCapture =>
                processName.Equals("screenCapture", StringComparison.OrdinalIgnoreCase) &&
                originalFilename.Equals("screenCapture.exe", StringComparison.OrdinalIgnoreCase) &&
                seewoProduct && signedBySeewo,
            PrivacyRiskKind.RemoteControl =>
                processName.Equals("rtcRemoteDesktop", StringComparison.OrdinalIgnoreCase) &&
                originalFilename.Equals("rtcRemoteDesktop.exe", StringComparison.OrdinalIgnoreCase) &&
                seewoProduct && signedBySeewo,
            _ => false,
        };
    }

    static bool IsSeewoCapabilityProcess(TargetProcessInfo info)
        => info.IsSignedBySeewo && info.Product.Contains("希沃", StringComparison.OrdinalIgnoreCase);

    void PublishPrivacyRisk(PrivacyRiskSnapshot risk, bool prompt)
    {
        PluginLog.Info($"[隐私风险] {RiskName(risk.Kind)} {(risk.Active ? "活动" : "结束")}: " +
            $"pid={risk.ProcessId}, {risk.Evidence}");
        RaisePrivacyRiskOnUi(risk);
        if (ShouldPromptPrivacyRisk(Config.PrivacyRiskResponse, prompt, risk.Active, risk.ProcessId))
            QueuePrivacyPrompt(risk);
    }

    internal static bool ShouldPromptPrivacyRisk(
        PrivacyRiskResponseMode mode,
        bool promptRequested,
        bool active,
        int processId)
        => mode == PrivacyRiskResponseMode.Prompt && promptRequested && active && processId > 0;

    void QueuePrivacyPrompt(PrivacyRiskSnapshot risk)
    {
        bool start;
        lock (_privacyGate)
        {
            if (!_promptedPrivacyProcesses.Add((risk.ProcessId, risk.ProcessStartTimeUtc))) return;
            _privacyPromptQueue.Enqueue(risk);
            start = !_privacyPromptShowing;
            if (start) _privacyPromptShowing = true;
        }
        if (start) Dispatcher.UIThread.Post(ShowNextPrivacyPrompt);
    }

    async void ShowNextPrivacyPrompt()
    {
        PrivacyRiskSnapshot? risk;
        lock (_privacyGate)
        {
            if (_privacyPromptQueue.Count == 0)
            {
                _privacyPromptShowing = false;
                return;
            }
            risk = _privacyPromptQueue.Dequeue();
            if (!_privacyRisks.ContainsKey((risk.Kind, risk.ProcessId, risk.ProcessStartTimeUtc)))
            {
                Dispatcher.UIThread.Post(ShowNextPrivacyPrompt);
                return;
            }
        }

        try
        {
            var dialog = new ContentDialog
            {
                Title = "发现" + RiskName(risk.Kind),
                Content = $"进程：{risk.ProcessName}.exe (PID {risk.ProcessId})\n" +
                          $"依据：{risk.Evidence}\n路径：{risk.ExecutablePath}",
                PrimaryButtonText = "结束进程",
                CloseButtonText = "允许本次",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var result = await Task.Run(() => TerminatePrivacyRisk(risk));
                _lastPrivacyOperation = result.Message;
                if (!result.Success)
                {
                    await new ContentDialog
                    {
                        Title = "结束进程失败",
                        Content = result.Message,
                        CloseButtonText = "关闭",
                    }.ShowAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _lastPrivacyOperation = "确认框显示失败：" + ex.Message;
            PluginLog.Warn(_lastPrivacyOperation);
        }
        finally
        {
            ShowNextPrivacyPrompt();
        }
    }

    public PluginOperationResult TerminatePrivacyRisk(PrivacyRiskSnapshot risk)
    {
        if (risk.ProcessId <= 0 || risk.ProcessStartTimeUtc is null || string.IsNullOrWhiteSpace(risk.ExecutablePath))
            return PluginOperationResult.Fail("风险快照没有可安全终止的进程信息");

        try
        {
            using var process = Process.GetProcessById(risk.ProcessId);
            var current = TargetProcessInfo.FromProcess(process);
            if (current.StartTimeUtc != risk.ProcessStartTimeUtc ||
                !string.Equals(current.ExecutablePath, risk.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return PluginOperationResult.Fail("进程身份已变化，已拒绝终止以避免 PID 复用误杀");

            bool verified = IsExpectedPrivacyTarget(
                risk.Kind, current.ProcessName, current.Product, current.OriginalFilename, current.IsSignedBySeewo) ||
                IsSeewoCapabilityProcess(current);
            if (!verified) return PluginOperationResult.Fail("进程未通过希沃数字签名和产品校验，已拒绝终止");

            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
            string message = $"已结束 {current.ProcessName}.exe (pid={current.Pid})";
            PluginLog.Info("[隐私防护] " + message);
            return PluginOperationResult.Ok(message);
        }
        catch (ArgumentException) { return PluginOperationResult.Fail("目标进程已退出"); }
        catch (Exception ex) { return PluginOperationResult.Fail("结束进程失败：" + ex.Message); }
    }

    static string RiskName(PrivacyRiskKind kind) => kind switch
    {
        PrivacyRiskKind.Camera => "摄像头访问",
        PrivacyRiskKind.ScreenCapture => "屏幕采集风险",
        PrivacyRiskKind.RemoteControl => "远程控制风险",
        PrivacyRiskKind.Microphone => "麦克风访问",
        _ => "隐私风险",
    };

    /// <summary>
    /// 融合有效活动态 = hook latch OR（启用融合 &amp;&amp; OS 探测）。hook 沉默但 OS 说在用时补发合成帧，
    /// 让既有提醒/触发器/规则照常工作，而不污染 bridge 的纯 hook latch。
    /// </summary>
    void UpdateFusion(TargetProcessInfo? target, bool osInUse)
    {
        bool hookActive = Bridge?.CameraActive ?? false;
        bool fuseOn = Config.FuseOsProbe;
        bool fused = hookActive || (fuseOn && osInUse);
        _fusedActive = fused;

        // 融合关闭或目标消失：结束可能在进行的合成会话（补 stop 让 start/stop 配对）。
        if (!fuseOn || target is null) { EndSyntheticSession(hookActive); return; }

        var live = Bridge?.GetLiveness();
        bool hookSilent = live is null ||
            live.LastFrameUtc is not DateTime lf || DateTime.UtcNow - lf > TimeSpan.FromSeconds(5);

        // 关键告警：这正是「摄像头已开但插件没获取到」。
        if (osInUse && !hookActive && hookSilent)
            PluginLog.Warn($"检测到 media_capture 正在使用摄像头，但 hook 通道无上报（可能未生效）：pid={target.Pid}");

        if (!_syntheticActive && osInUse && !hookActive && hookSilent)
        {
            _syntheticActive = true;   // OS-only 起：补一帧 start，仅当 hook 未在报，避免与真 hook 帧重复提醒
            RaiseStateOnUi(new CaptureSnapshot(IpcProtocol.StatusStart, 0, "OS 探测：摄像头使用中（hook 未上报）"));
        }
        else if (_syntheticActive && (!osInUse || hookActive))
        {
            EndSyntheticSession(hookActive);   // OS 停 或 真 hook 接管
        }
    }

    /// <summary>结束合成会话：清标志；仅当真 hook 未接管（摄像头确已停）才补一帧 stop 让 start/stop 配对。</summary>
    void EndSyntheticSession(bool hookActive)
    {
        if (!_syntheticActive) return;
        _syntheticActive = false;
        if (!hookActive)   // hook 正报则摄像头仍开（真帧会自行发 stop），不补合成 stop
            RaiseStateOnUi(new CaptureSnapshot(IpcProtocol.StatusStop, 0, "OS 探测：摄像头已停止"));
    }

    /// <summary>已注入 pid 上的自愈：确认 hook 是否真的在跑，未生效/冻结则有上限地重注入。</summary>
    void MaybeSelfHeal(TargetProcessInfo target, bool osInUse)
    {
        var live = Bridge?.GetLiveness();
        if (live is null) return;
        var now = DateTime.UtcNow;

        bool hookAlive = live.ReadySeen ||
            (live.HeartbeatSupported && live.LastHeartbeatChangeUtc is DateTime hb && now - hb < HeartbeatStaleAfter);
        bool recentFrame = live.LastFrameUtc is DateTime lf && now - lf < TimeSpan.FromSeconds(30);

        // A) 从未确认存活：注入宽限期过后仍无 Ready/心跳/帧。
        //    心跳每 5s 一拍且与摄像头是否在用无关，所以正常注入后 hookAlive 很快为真；
        //    仅在 OS 确认摄像头在用时才重注入（正是「已开却没测到」），否则暂不上报是正常的，不折腾（避免目标慢启动期空重注入）。
        if (!hookAlive && !recentFrame && now - _injectedUtc > HookConfirmWindow)
        {
            if (osInUse)
            {
                PluginLog.Warn($"hook 可能未生效（pid={target.Pid}）：OS 探测到在用但无 Ready/心跳/上报。");
                ScheduleReinject(target, "hook 未确认存活");
            }
            return;
        }

        // B) 曾经存活后心跳冻结：DLL 疑似卸载/崩溃 ⇒ 同 pid 重注入。仅在心跳被支持时判定。
        if (live.HeartbeatSupported && live.LastHeartbeatChangeUtc is DateTime hb2 && now - hb2 > HeartbeatStaleAfter)
            ScheduleReinject(target, "心跳冻结（DLL 可能已卸载/崩溃）");
    }

    void ScheduleReinject(TargetProcessInfo target, string why)
    {
        if (_reinjectBudget <= 0) return;                       // 预算封顶，杜绝注入器刷屏
        _reinjectBudget--;
        PluginLog.Warn($"自愈重注入（{why}），剩余预算 {_reinjectBudget}：pid={target.Pid}");
        _lastInjectedPid = 0;                                   // 解闩
        _lastAttemptPid = 0;                                    // 让下一拍立即重注入（对该 pid 的 15s 冷却失效）
        _lastAttemptUtc = DateTime.UtcNow - InjectionRetryInterval;
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
                catch { candidates.Add(new TargetProcessInfo(p.Id, p.ProcessName, "", "", "", "", "", "", null, false, false, null)); }
            }

            return candidates
                .Where(c => c.IsExpectedSeewoMediaCapture)
                .OrderBy(c => c.Pid)
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

    /// <summary>融合后的有效活动态（hook latch 或 OS 探测），供 PrivacyIslandRuntime.CameraActive/规则读取。</summary>
    public bool EffectiveCameraActive => _fusedActive;

    public bool IsPrivacyRiskActive(PrivacyRiskKind kind)
    {
        lock (_privacyGate) return _privacyRisks.Keys.Any(key => key.Kind == kind);
    }

    public IReadOnlyList<PrivacyRiskSnapshot> ActivePrivacyRisks
    {
        get { lock (_privacyGate) return _privacyRisks.Values.ToArray(); }
    }

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
        bool previousEffective;
        bool changed;
        lock (_pauseGate)
        {
            previousEffective = _pauseSources.Count > 0;
            changed = active ? _pauseSources.Add(key) : _pauseSources.Remove(key);
            effective = _pauseSources.Count > 0;
        }
        if (!changed) return;     // 该来源状态未变，避免重复写/刷日志
        ApplyEffectiveToBridge();
        if (effective != previousEffective) PrivacyIslandRuntime.RaiseProtectionPauseChanged(effective);
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
        if (Config.PrivacyRiskResponse == PrivacyRiskResponseMode.NotifyOnly)
        {
            lock (_privacyGate) _privacyPromptQueue.Clear();
        }
        ApplyEffectiveToBridge();
        PluginLog.Info($"设置已保存：延迟 {Config.MinDelaySeconds}-{Config.MaxDelaySeconds}s, 隐身={Config.StealthMode}, 语音={Config.SpeechEnabled}, 隐私风险处理={PrivacyRiskResponseName()}（隐身需重注入生效）");
    }

    /// <summary>把当前生效的延迟（覆盖优先于基准）与暂停态一次性写入共享内存。</summary>
    void ApplyEffectiveToBridge()
    {
        var (min, max) = _delayOverride ?? (Config.MinDelaySeconds, Config.MaxDelaySeconds);
        Bridge?.WriteConfig(min, max, EffectivePaused, Config.StealthMode);
    }

    /// <summary>应用内功能测试：注入一帧合成状态，走真实分发路径（提醒/触发器/规则/统计全联动），无需真注入。</summary>
    public void Simulate(int state, string message) => Bridge?.Simulate(state, message);

    public void SimulatePrivacyRisk(PrivacyRiskKind kind)
    {
        var active = new PrivacyRiskSnapshot(kind, true, 0, null, "simulation", "（模拟）", "应用内模拟");
        lock (_privacyGate) _privacyRisks[(kind, 0, null)] = active;
        PublishPrivacyRisk(active, prompt: false);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            bool removed;
            lock (_privacyGate) removed = _privacyRisks.Remove((kind, 0, null));
            if (removed)
                PublishPrivacyRisk(active with { Active = false, Evidence = "应用内模拟结束" }, prompt: false);
        });
    }

    /// <summary>诊断信息：文件/IPC/目标进程/注入/权限状态，给设置页的功能测试区展示。</summary>
    public string Diagnostics()
    {
        var target = FindTargetProcess();
        string targetSummary = target is null ? "否" : $"是 (pid={target.Pid}, {target.DisplayName})";
        string bootSummary = target is null ? "未检测" : DescribeBootConfig(target.ExecutablePath);
        string portsSummary = target is null ? "未检测" : DescribeListeningPorts(target.Pid);

        var live = Bridge?.GetLiveness();
        bool hookActive = Bridge?.CameraActive == true;
        var inUseApps = _cameraProbe.InUseApps();
        var privacyRisks = ActivePrivacyRisks;
        string privacySummary = privacyRisks.Count == 0
            ? "无"
            : string.Join("; ", privacyRisks.Select(r => $"{RiskName(r.Kind)} pid={r.ProcessId} ({r.Evidence})"));
        string Age(DateTime? t) => t is DateTime u ? $"{(DateTime.UtcNow - u).TotalSeconds:0}s 前" : "从未";
        string hbLine = live is null ? "未知"
            : !live.HeartbeatSupported ? "不支持（DLL 从未写入，退化为无心跳）"
            : $"{live.Heartbeat}（{Age(live.LastHeartbeatChangeUtc)}变化）";
        string mismatch = target is { Is32Bit: false, Machine: { Length: > 0 } m }
            ? $"⚠ 目标非 x86（{m}），x86 注入器/DLL 无法注入" : "无";

        return
            $"注入器存在: {(File.Exists(_injectorPath) ? "是" : "否")}\n" +
            $"hook DLL 存在: {(File.Exists(_dllPath) ? "是" : "否")}\n" +
            $"IPC 就绪: {(Bridge != null ? "是" : "否")}\n" +
            $"以管理员运行: {(IsAdmin() ? "是" : "否（跨进程注入通常需要）")}\n" +
            $"检测到 media_capture.exe: {targetSummary}\n" +
            $"目标路径: {DisplayPath(target?.ExecutablePath)}\n" +
            $"目标版本: {DisplayVersion(target)}\n" +
            $"位数匹配: {mismatch}\n" +
            $"目标 BootConfig: {bootSummary}\n" +
            $"目标监听端口: {portsSummary}\n" +
            $"目标 ESTABLISHED 连接数: {DescribeEstablished(target?.Pid ?? 0)}\n" +
            $"反编译接口: {MediaCaptureProtocol.CapabilitySummary}\n" +
            $"已注入的 pid: {(_lastInjectedPid == 0 ? "无" : _lastInjectedPid.ToString())}\n" +
            $"最近注入结果: {_lastInjectionMessage} (code={_lastInjectionCode})\n" +
            $"最近上报帧: {Age(live?.LastFrameUtc)}（currState={StateName(live?.LastPolledState)}）\n" +
            $"心跳: {hbLine}\n" +
            $"StatusReady: {(live?.ReadySeen == true ? $"已见（{Age(live.ReadySeenUtc)}）" : "未见")}\n" +
            $"hook 摄像头活动: {(hookActive ? "是" : "否")}\n" +
            $"OS 摄像头在用(media_capture): {(_osCameraInUse ? "是" : "否")}\n" +
            $"OS 摄像头在用(任意应用): {(inUseApps.Count == 0 ? "无" : string.Join("; ", inUseApps))}\n" +
            $"融合有效活动: {(_fusedActive ? "是" : "否")}\n" +
            $"hook 与 OS 一致性: {(hookActive == _osCameraInUse ? "一致" : "不一致（其一未捕获）")}\n" +
            $"当前隐私风险: {privacySummary}\n" +
            $"隐私风险处理: {PrivacyRiskResponseName()}\n" +
            $"隐私候选校验: {_privacyScanNote}\n" +
            $"最近隐私操作: {_lastPrivacyOperation}";
    }

    string PrivacyRiskResponseName() => Config.PrivacyRiskResponse == PrivacyRiskResponseMode.NotifyOnly
        ? "仅提示"
        : "询问后处理";

    static string StateName(int? state) => state switch
    {
        IpcProtocol.StatusWaiting => "waiting",
        IpcProtocol.StatusStart => "start",
        IpcProtocol.StatusWatching => "watching",
        IpcProtocol.StatusStop => "stop",
        IpcProtocol.StatusError => "error",
        IpcProtocol.StatusLog => "log",
        IpcProtocol.StatusInfo => "info",
        IpcProtocol.StatusReady => "ready",
        null => "未知",
        _ => state.ToString()!,
    };

    static string DescribeEstablished(int pid)
    {
        if (pid <= 0) return "未检测";
        try { return TcpTable.CountEstablished(pid).ToString(); }
        catch (Exception ex) { return "读取失败：" + ex.Message; }
    }

    /// <summary>当前进程是否以管理员运行。供设置页/Runtime 做结构化判断，替代脆弱的诊断字符串匹配。</summary>
    public static bool IsAdmin()
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
        string ProcessName,
        string ExecutablePath,
        string FileVersion,
        string ProductVersion,
        string Description,
        string Product,
        string OriginalFilename,
        DateTime? StartTimeUtc,
        bool IsSignedBySeewo,
        bool Is32Bit,
        string? Machine)
    {
        public bool IsExpectedSeewoMediaCapture =>
            ProcessName.Equals("media_capture", StringComparison.OrdinalIgnoreCase) &&
            OriginalFilename.Equals("media_capture.exe", StringComparison.OrdinalIgnoreCase) &&
            Product.Contains("希沃", StringComparison.OrdinalIgnoreCase) &&
            IsSignedBySeewo;

        public bool IsLikelySeewo =>
            IsExpectedSeewoMediaCapture ||
            IsSignedBySeewo && (Product.Contains("希沃", StringComparison.OrdinalIgnoreCase) ||
                                Description.Contains("媒体采集", StringComparison.OrdinalIgnoreCase));

        public string DisplayName
        {
            get
            {
                string arch = Machine ?? (Is32Bit ? "x86" : "x64/未知");
                if (!string.IsNullOrWhiteSpace(FileVersion)) arch += ", v" + FileVersion;
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
            DateTime? startTimeUtc = null;
            try { startTimeUtc = process.StartTime.ToUniversalTime(); }
            catch { }
            return new TargetProcessInfo(
                process.Id,
                process.ProcessName,
                path,
                version?.FileVersion ?? "",
                version?.ProductVersion ?? "",
                version?.FileDescription ?? "",
                version?.ProductName ?? "",
                version?.OriginalFilename ?? "",
                startTimeUtc,
                SeewoSignatureVerifier.IsSignedBySeewo(path),
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

            case IpcProtocol.StatusWatching:
                PluginLog.CaptureWatching(s.Message);
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

        RaiseStateOnUi(s);
    }

    /// <summary>在 Avalonia UI 线程分发状态帧（提醒/触发器会触达 UI；读线程与 timer 线程都是后台线程）。</summary>
    static void RaiseStateOnUi(CaptureSnapshot s)
    {
        var ui = Dispatcher.UIThread;
        if (ui.CheckAccess()) PrivacyIslandRuntime.RaiseState(s);
        else ui.Post(() => PrivacyIslandRuntime.RaiseState(s));
    }

    static void RaisePrivacyRiskOnUi(PrivacyRiskSnapshot risk)
    {
        var ui = Dispatcher.UIThread;
        if (ui.CheckAccess()) PrivacyIslandRuntime.RaisePrivacyRisk(risk);
        else ui.Post(() => PrivacyIslandRuntime.RaisePrivacyRisk(risk));
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
        if (ReferenceEquals(PrivacyIslandRuntime.Monitor, this)) PrivacyIslandRuntime.Monitor = null;
        if (Bridge != null)
        {
            Bridge.StateReceived -= OnState;
            Bridge.Dispose();
            Bridge = null;
        }
    }
}
