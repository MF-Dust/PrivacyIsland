using System.IO;
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
using PrivacyIsland.Statistics;

namespace PrivacyIsland.Orchestrator;

/// <summary>
/// 编排器（替代原生 main.c）：轮询 media_capture.exe，发现即用 nmm_injector.exe 注入 hook DLL，
/// 并把 DLL 经共享内存上报的状态分发给提醒/自动化/统计/日志。
/// 注入需要权限——通常要求以管理员身份运行 ClassIsland，否则 OpenProcess 失败。
/// </summary>
public sealed class CaptureMonitor : IHostedService, IDisposable
{
    static readonly TimeSpan InjectionRetryInterval = TimeSpan.FromSeconds(15);
    // ponytail: 3s polling keeps the fallback detector cheap; use process events only if this ceiling becomes too slow.
    static readonly TimeSpan HeavyPollInterval = TimeSpan.FromSeconds(3);

    // 自愈阈值。反汇编确认 hook DLL 有一条专用心跳线程：每 ~5s（WaitForSingleObject 超时 5000ms）
    // 在互斥锁下把 GetTickCount() 写入 heartbeat 偏移；不随捕获活动变化，是纯粹的「DLL 存活」信号。
    static readonly TimeSpan HookConfirmWindow = TimeSpan.FromSeconds(10);   // 注入后多久没 Ready/心跳/帧算「未确认存活」（≈2 拍心跳宽限）
    static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(15); // 心跳多久没变算「冻结」（≈3 拍，5s 一拍）
    const int MaxSelfHealReinjects = 2;                                      // 每个 pid 自愈重注入次数上限

    readonly string _folder;
    readonly ILogger<CaptureMonitor> _logger;
    Timer? _timer;
    int _lastInjectedPid;     // 同一目标进程注入成功后不重复处理
    DateTime? _lastInjectedStartTimeUtc;
    string _lastInjectedPath = "";
    int _lastAttemptPid;      // 注入失败时保留 pid，并按冷却时间重试
    DateTime _lastAttemptUtc;
    int _lastInjectionCode;
    string _lastInjectionMessage = "尚未尝试注入";
    int _polling;             // 防止轮询重入
    DateTime _lastHeavyPollUtc = DateTime.MinValue;
    bool _awaitingDelay;      // 收到 start 后，等首条 "Delay N s" 以统计本次延迟

    // OS 独立探测 + 融合/自愈状态（除标注外仅 timer 线程访问）。
    readonly MonitoringScanner _scanner;
    readonly HookInjector _injector;
    readonly PrivacyRiskCoordinator _privacy;
    volatile bool _osCameraInUse;    // media_capture 的 OS 探测结果（timer 写，诊断读）
    volatile bool _fusedActive;      // 融合后的有效活动态（timer 写，规则/诊断读）
    bool _syntheticActive;           // 是否已补发过合成 start（避免重复）
    DateTime _injectedUtc;           // 本 pid 最近一次成功注入时刻
    int _healthPid;                  // 当前健康跟踪的 pid
    DateTime? _healthStartTimeUtc;
    int _reinjectBudget;             // 该 pid 剩余自愈重注入预算
    bool _loggedTargetPid;           // 发现/消失日志去抖
    bool _hookSilentWarningActive;   // hook 静默告警按一次会话去抖

    readonly object _scanGate = new();
    readonly object _snapshotGate = new();
    MonitoringSnapshot _snapshot = MonitoringSnapshot.Empty;
    MonitoringDiagnostics _diagnostics = MonitoringDiagnostics.Empty;
    int _diagnosticsRefreshRequested;

    // 分层暂停：多个来源（manual/automation/lesson）可各自请求暂停，任一生效即暂停。
    readonly object _pauseGate = new();
    readonly HashSet<string> _pauseSources = new();
    (int min, int max)? _delayOverride;   // 临时延迟覆盖（如上课加强延迟），不写 config.json

    public PluginConfig Config { get; private set; }
    public SharedMemoryBridge? Bridge { get; private set; }
    public CaptureStats Stats { get; private set; }

    public CaptureMonitor(string pluginConfigFolder, ILogger<CaptureMonitor> logger)
    {
        _folder = pluginConfigFolder;
        _logger = logger;
        PluginLog.Init(_logger);
        string dir = Path.GetDirectoryName(typeof(CaptureMonitor).Assembly.Location) ?? AppContext.BaseDirectory;
        _scanner = new MonitoringScanner();
        _injector = new HookInjector(dir);
        _privacy = new PrivacyRiskCoordinator(RaisePrivacyRiskOnUi);
        Config = new PluginConfig();
        Stats = new CaptureStats();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Config = PluginConfig.Load(_folder);
        Stats = CaptureStats.Load(_folder);

        if (!File.Exists(_injector.DllPath)) PluginLog.Error($"找不到 hook DLL：{_injector.DllPath}");
        if (!File.Exists(_injector.InjectorPath)) PluginLog.Error($"找不到注入器：{_injector.InjectorPath}");

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
        DateTime now = DateTime.UtcNow;
        bool heavyRefresh = false;
        if (IsHeavyPollDue(now, _lastHeavyPollUtc)) RefreshMonitoringSnapshot(out heavyRefresh);

        var snapshot = GetMonitoringSnapshot();
        var target = snapshot.Target;
        bool osInUse = snapshot.CameraOsInUse;
        UpdateFusion(target, osInUse);
        if (heavyRefresh) _privacy.Update(snapshot, _fusedActive, Bridge?.CameraActive == true, Config);

        if (target is null)
        {
            if (_loggedTargetPid) { PluginLog.Info("media_capture.exe 已退出，等待下次出现"); _loggedTargetPid = false; }
            if (Bridge?.CameraActive == true) Bridge.ForceInactive("media_capture 已退出");
            _fusedActive = false;   // 目标已消失，融合态立即置否，不等下一拍
            _lastInjectedPid = 0;
            _lastInjectedStartTimeUtc = null;
            _lastInjectedPath = "";
            _lastAttemptPid = 0;
            _healthPid = 0;
            _healthStartTimeUtc = null;
            return;
        }

        if (!_loggedTargetPid) { PluginLog.Info($"发现 media_capture.exe (pid={target.Pid}, {target.DisplayName})"); _loggedTargetPid = true; }

        if (IsSameInjectedInstance(target))
        {
            if (heavyRefresh) MaybeSelfHeal(target, osInUse);
            return;
        }   // 已处理——但仍监控 hook 是否真活着
        if (!heavyRefresh) return; // 缓存尚未刷新，不对可能已退出的旧 PID 发起注入
        if (target.Pid == _lastAttemptPid &&
            DateTime.UtcNow - _lastAttemptUtc < InjectionRetryInterval)
            return;                                     // 失败后冷却，避免每秒刷日志/拉起注入器

        _lastAttemptPid = target.Pid;
        _lastAttemptUtc = DateTime.UtcNow;
        if (_healthPid != target.Pid || _healthStartTimeUtc != target.StartTimeUtc)
        {
            _healthPid = target.Pid;
            _healthStartTimeUtc = target.StartTimeUtc;
            _reinjectBudget = MaxSelfHealReinjects;
        }
        var result = Inject(target);
        if (result.Success)
        {
            _lastInjectedPid = target.Pid;
            _lastInjectedStartTimeUtc = target.StartTimeUtc;
            _lastInjectedPath = target.ExecutablePath;
            _injectedUtc = DateTime.UtcNow;
        }
    }

    bool IsSameInjectedInstance(TargetProcessInfo target)
        => target.Pid == _lastInjectedPid && SameProcessIdentity(target, _lastInjectedStartTimeUtc, _lastInjectedPath);

    static bool SameProcessIdentity(
        TargetProcessInfo target,
        DateTime? expectedStartTimeUtc,
        string expectedPath)
        => target.StartTimeUtc is DateTime start && expectedStartTimeUtc is DateTime expectedStart
            ? start == expectedStart
            : string.Equals(target.ExecutablePath, expectedPath, StringComparison.OrdinalIgnoreCase);

    static bool SameProcessInstance(TargetProcessInfo? left, TargetProcessInfo? right)
        => left is null && right is null ||
           left is not null && right is not null &&
           left.Pid == right.Pid &&
           SameProcessIdentity(right, left.StartTimeUtc, left.ExecutablePath);

    internal static bool IsHeavyPollDue(DateTime nowUtc, DateTime lastPollUtc)
        => nowUtc - lastPollUtc >= HeavyPollInterval;

    MonitoringSnapshot GetMonitoringSnapshot()
    {
        lock (_snapshotGate) return _snapshot;
    }
    MonitoringSnapshot RefreshMonitoringSnapshot(out bool updated)
    {
        updated = false;
        lock (_scanGate)
        {
            MonitoringSnapshot snapshot;
            try
            {
                var previous = GetMonitoringSnapshot();
                snapshot = _scanner.Scan(Config);
                bool diagnosticsDue = !SameProcessInstance(previous.Target, snapshot.Target) ||
                    Interlocked.Exchange(ref _diagnosticsRefreshRequested, 0) != 0;
                if (diagnosticsDue) _diagnostics = _scanner.ScanDiagnostics(snapshot.Target);
            }
            catch (Exception ex)
            {
                _lastHeavyPollUtc = DateTime.UtcNow;
                PluginLog.Error("监测扫描异常：" + ex.Message);
                return GetMonitoringSnapshot();
            }

            _lastHeavyPollUtc = DateTime.UtcNow;
            lock (_snapshotGate) _snapshot = snapshot;
            _osCameraInUse = snapshot.CameraOsInUse;
            updated = true;
            return snapshot;
        }
    }

    internal void RequestDiagnosticsRefresh()
        => Interlocked.Exchange(ref _diagnosticsRefreshRequested, 1);

    internal static bool ShouldTrackCameraPrivacyRisk(bool fusedActive, bool targetVerified)
        => PrivacyRiskCoordinator.ShouldTrackCameraPrivacyRisk(fusedActive, targetVerified);

    internal static bool ShouldPromptPrivacyRisk(
        PrivacyRiskResponseMode mode,
        bool promptRequested,
        bool active,
        int processId)
        => PrivacyRiskCoordinator.ShouldPromptPrivacyRisk(mode, promptRequested, active, processId);

    internal static bool IsExpectedPrivacyTarget(
        PrivacyRiskKind kind,
        string processName,
        string product,
        string originalFilename,
        bool signedBySeewo)
        => PrivacyRiskCoordinator.IsExpectedPrivacyTarget(
            kind, processName, product, originalFilename, signedBySeewo);

    internal static string RiskName(PrivacyRiskKind kind)
        => PrivacyRiskCoordinator.RiskName(kind);

    void UpdateFusion(TargetProcessInfo? target, bool osInUse)
    {
        bool hookActive = Bridge?.CameraActive ?? false;
        bool fuseOn = Config.FuseOsProbe;
        bool fused = hookActive || (fuseOn && osInUse);
        _fusedActive = fused;

        // 融合关闭或目标消失：结束可能在进行的合成会话（补 stop 让 start/stop 配对）。
        if (!fuseOn || target is null)
        {
            if (_hookSilentWarningActive) _hookSilentWarningActive = false;
            EndSyntheticSession(hookActive);
            return;
        }

        var live = Bridge?.GetLiveness();
        bool hookSilent = live is null ||
            live.LastFrameUtc is not DateTime lf || DateTime.UtcNow - lf > TimeSpan.FromSeconds(5);

        // 关键告警：这正是「摄像头已开但插件没获取到」。只在状态边沿记录，避免日志 I/O 造成额外卡顿。
        bool hookSilentNow = osInUse && !hookActive && hookSilent;
        if (hookSilentNow && !_hookSilentWarningActive)
        {
            _hookSilentWarningActive = true;
            PluginLog.Warn($"检测到 media_capture 正在使用摄像头，但 hook 通道无上报（可能未生效）：pid={target.Pid}");
        }
        else if (!hookSilentNow && _hookSilentWarningActive)
        {
            _hookSilentWarningActive = false;
            PluginLog.Info($"media_capture hook 通道已恢复或摄像头已停止：pid={target.Pid}");
        }

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
        _lastInjectedStartTimeUtc = null;
        _lastInjectedPath = "";
        _lastAttemptPid = 0;                                    // 让下一次重扫描立即重注入（对该 pid 的 15s 冷却失效）
        _lastAttemptUtc = DateTime.UtcNow - InjectionRetryInterval;
    }

    PluginOperationResult Inject(TargetProcessInfo target)
    {
        var operation = _injector.Inject(target.Pid);
        _lastInjectionCode = operation.Code;
        if (operation.Code == -10)
        {
            string message = $"找不到注入器：{_injector.InjectorPath}";
            PluginLog.Error(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Fail(message);
        }
        if (operation.Code == -11)
        {
            string message = $"找不到 hook DLL：{_injector.DllPath}";
            PluginLog.Error(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Fail(message);
        }
        if (operation.Code == 0)
        {
            string message = $"已注入 media_capture.exe (pid={target.Pid}, {target.DisplayName})";
            PluginLog.Info(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Ok(message);
        }

        string failure = $"注入失败 (pid={target.Pid}, code={operation.Code}, {target.DisplayName})。将在 {InjectionRetryInterval.TotalSeconds:0}s 后重试；请确认 ClassIsland 以管理员身份运行。";
        PluginLog.Warn(failure);
        _lastInjectionMessage = failure;
        return PluginOperationResult.Fail(failure);
    }

    // ---- 控制面（供自动化行动 / 设置页 / 课程控制器调用）----

    /// <summary>融合后的有效活动态（hook latch 或 OS 探测），供 PrivacyIslandRuntime.CameraActive/规则读取。</summary>
    public bool EffectiveCameraActive => _fusedActive;

    public bool IsPrivacyRiskActive(PrivacyRiskKind kind)
        => _privacy.IsActive(kind);

    public IReadOnlyList<PrivacyRiskSnapshot> ActivePrivacyRisks
        => _privacy.ActiveRisks;

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
            _privacy.ClearPromptQueue();
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
        => _privacy.Simulate(kind, Config);

    /// <summary>诊断信息：文件/IPC/目标进程/注入/权限状态，给设置页的功能测试区展示。</summary>
    public string Diagnostics()
    {
        var snapshot = GetMonitoringSnapshot();
        var target = snapshot.Target;
        string targetSummary = target is null ? "否" : $"是 (pid={target.Pid}, {target.DisplayName})";

        var live = Bridge?.GetLiveness();
        bool hookActive = Bridge?.CameraActive == true;
        var inUseApps = snapshot.CameraInUseApps;
        var privacyRisks = _privacy.ActiveRisks;
        string privacySummary = privacyRisks.Count == 0
            ? "无"
            : string.Join("; ", privacyRisks.Select(r => $"{RiskName(r.Kind)} pid={r.ProcessId} ({r.Evidence})"));
        string Age(DateTime? t) => t is DateTime u ? $"{(DateTime.UtcNow - u).TotalSeconds:0}s 前" : "从未";
        string scanAge = snapshot.UpdatedUtc == DateTime.MinValue ? "从未" : Age(snapshot.UpdatedUtc);
        string hbLine = live is null ? "未知"
            : !live.HeartbeatSupported ? "不支持（DLL 从未写入，退化为无心跳）"
            : $"{live.Heartbeat}（{Age(live.LastHeartbeatChangeUtc)}变化）";
        string mismatch = target is { Is32Bit: false, Machine: { Length: > 0 } m }
            ? $"⚠ 目标非 x86（{m}），x86 注入器/DLL 无法注入" : "无";

        return
            $"注入器存在: {(File.Exists(_injector.InjectorPath) ? "是" : "否")}\n" +
            $"hook DLL 存在: {(File.Exists(_injector.DllPath) ? "是" : "否")}\n" +
            $"IPC 就绪: {(Bridge != null ? "是" : "否")}\n" +
            $"以管理员运行: {(IsAdmin() ? "是" : "否（跨进程注入通常需要）")}\n" +
            $"检测到 media_capture.exe: {targetSummary}\n" +
            $"目标路径: {DisplayPath(target?.ExecutablePath)}\n" +
            $"目标版本: {DisplayVersion(target)}\n" +
            $"位数匹配: {mismatch}\n" +
            $"监测快照: {scanAge}\n" +
            $"目标 BootConfig: {_diagnostics.BootSummary}\n" +
            $"目标监听端口: {_diagnostics.ListeningPorts}\n" +
            $"目标 ESTABLISHED 连接数: {_diagnostics.EstablishedConnections}\n" +
            $"反汇编接口: {MediaCaptureProtocol.CapabilitySummary}\n" +
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
            $"隐私候选校验: {_privacy.ScanNote}\n" +
            $"最近隐私操作: {_privacy.LastOperation}";
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
    public PluginOperationResult TerminatePrivacyRisk(PrivacyRiskSnapshot risk)
        => _privacy.Terminate(risk);

    public PluginOperationResult OpenLogsFolder()
    {
        const string message = "PrivacyIsland 日志已写入 ClassIsland 日志，不再生成独立日志文件。";
        PluginLog.Info(message);
        return PluginOperationResult.Ok(message);
    }

    public PluginOperationResult InjectNow()
    {
        var target = RefreshMonitoringSnapshot(out bool refreshed).Target;
        if (!refreshed)
        {
            const string message = "监测扫描失败，未执行手动注入";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }
        if (target is null)
        {
            const string message = "未找到 media_capture.exe，无法注入";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }

        _lastAttemptPid = target.Pid;
        _lastAttemptUtc = DateTime.UtcNow;
        var result = Inject(target);
        if (result.Success)
        {
            _lastInjectedPid = target.Pid;
            _lastInjectedStartTimeUtc = target.StartTimeUtc;
            _lastInjectedPath = target.ExecutablePath;
            _healthPid = target.Pid;
            _healthStartTimeUtc = target.StartTimeUtc;
            _reinjectBudget = MaxSelfHealReinjects;
            _injectedUtc = DateTime.UtcNow;
        }
        return result;
    }

    public PluginOperationResult EjectNow()
    {
        var target = RefreshMonitoringSnapshot(out bool refreshed).Target;
        if (!refreshed)
        {
            const string message = "监测扫描失败，未执行手动弹射";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }
        if (target is null)
        {
            const string message = "未找到 media_capture.exe，无法弹射";
            PluginLog.Warn(message);
            return PluginOperationResult.Fail(message);
        }

        var operation = _injector.Eject(target.Pid);
        _lastInjectedPid = 0;
        _lastInjectedStartTimeUtc = null;
        _lastInjectedPath = "";
        _healthPid = 0;
        _healthStartTimeUtc = null;
        _lastInjectionCode = operation.Code;
        if (operation.Code == 0)
        {
            const string message = "已弹射 hook DLL";
            PluginLog.Info(message);
            _lastInjectionMessage = message;
            return PluginOperationResult.Ok(message);
        }

        string failure = $"弹射失败 (code={operation.Code})";
        PluginLog.Warn(failure);
        _lastInjectionMessage = failure;
        return PluginOperationResult.Fail(failure);
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
