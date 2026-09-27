using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using PrivacyIsland.Config;
using PrivacyIsland.Logging;
using PrivacyIsland.Native;

namespace PrivacyIsland.Orchestrator;

/// <summary>维护风险状态、提示队列和安全终止操作；监测采样本身由 MonitoringScanner 负责。</summary>
internal sealed class PrivacyRiskCoordinator
{
    readonly object _gate = new();
    readonly Dictionary<RiskKey, PrivacyRiskSnapshot> _risks = new();
    readonly HashSet<(int Pid, DateTime? StartTimeUtc)> _promptedProcesses = new();
    readonly Dictionary<RiskKey, int> _presentStreak = new();
    readonly Dictionary<RiskKey, int> _absentStreak = new();
    readonly Queue<PrivacyRiskSnapshot> _promptQueue = new();
    readonly Action<PrivacyRiskSnapshot> _publish;
    bool _promptShowing;
    string _scanNote = "无";
    string _lastOperation = "尚无";

    public PrivacyRiskCoordinator(Action<PrivacyRiskSnapshot> publish)
    {
        _publish = publish;
    }

    public IReadOnlyList<PrivacyRiskSnapshot> ActiveRisks
    {
        get { lock (_gate) return _risks.Values.ToArray(); }
    }

    public string ScanNote
    {
        get { lock (_gate) return _scanNote; }
    }

    public string LastOperation
    {
        get { lock (_gate) return _lastOperation; }
    }

    public bool IsActive(PrivacyRiskKind kind)
    {
        lock (_gate) return _risks.Keys.Any(key => key.Kind == kind);
    }

    public void ClearPromptQueue()
    {
        lock (_gate) _promptQueue.Clear();
    }

    public void Update(
        MonitoringSnapshot snapshot,
        bool fusedActive,
        bool hookActive,
        PluginConfig config,
        IReadOnlyList<WindowAnomaly> windowAnomalies)
    {
        var current = new Dictionary<RiskKey, PrivacyRiskSnapshot>();
        var notes = new List<string>(snapshot.ProcessNotes);
        TargetProcessInfo? cameraTarget = snapshot.Target;
        bool cameraOsInUse = snapshot.CameraOsInUse;

        bool cameraTargetValid = cameraTarget is not null && IsExpectedPrivacyTarget(
            PrivacyRiskKind.Camera,
            cameraTarget.ProcessName,
            cameraTarget.Product,
            cameraTarget.OriginalFilename,
            cameraTarget.IsSignedBySeewo);
        if (ShouldTrackCameraPrivacyRisk(fusedActive, cameraTargetValid))
        {
            string evidence = hookActive && cameraOsInUse
                ? "hook 与 Windows 均检测到摄像头正在使用"
                : hookActive
                    ? "hook 检测到摄像头正在使用"
                    : "Windows 检测到摄像头正在使用";
            var risk = ToPrivacyRisk(PrivacyRiskKind.Camera, cameraTarget!, evidence);
            current[KeyOf(risk)] = risk;
        }

        if (config.EnableScreenCaptureMonitoring)
            AddScreenCaptureRisks(current, notes, snapshot);
        if (config.EnableRemoteControlMonitoring)
            AddRemoteControlRisks(current, notes, snapshot);
        if (config.EnableMicrophoneMonitoring)
            AddMicrophoneRisks(current, snapshot);
        if (config.EnableLiveMonitoring)
            AddLiveRisks(current, notes, snapshot);
        if (config.EnableDeviceCameraMonitoring)
            AddDeviceCameraRisks(current, notes, snapshot);
        if (config.EnableWindowChangeMonitoring)
            AddWindowRisks(current, windowAnomalies);

        List<PrivacyRiskSnapshot> changed = new();
        lock (_gate)
        {
            foreach (var (key, risk) in current)
            {
                _absentStreak.Remove(key);
                int seen = _presentStreak.GetValueOrDefault(key) + 1;
                _presentStreak[key] = seen;
                if (seen < RiskConfirmSamples) continue;
                if (_risks.TryAdd(key, risk)) changed.Add(risk);
                else _risks[key] = risk;
            }

            foreach (var (key, old) in _risks.ToArray())
            {
                if (current.ContainsKey(key)) continue;
                _presentStreak[key] = 0;
                int gone = _absentStreak.GetValueOrDefault(key) + 1;
                _absentStreak[key] = gone;
                if (gone < RiskClearSamples) continue;
                _risks.Remove(key);
                _absentStreak.Remove(key);
                changed.Add(old with { Active = false, Evidence = old.Evidence + "；状态已结束" });
            }

            foreach (var key in _presentStreak.Keys.Where(key => !current.ContainsKey(key) && !_risks.ContainsKey(key)).ToArray())
                _presentStreak.Remove(key);

            var activeProcesses = _risks.Keys
                .Where(key => key.Pid > 0)
                .Select(key => (key.Pid, key.StartTimeUtc))
                .ToHashSet();
            _promptedProcesses.RemoveWhere(identity => !activeProcesses.Contains(identity));
            _scanNote = notes.Count == 0 ? "无" : string.Join("; ", notes);
        }

        foreach (var risk in changed) Publish(risk, prompt: true, config);
    }

    public void Simulate(PrivacyRiskKind kind, PluginConfig config)
    {
        var active = new PrivacyRiskSnapshot(kind, true, 0, null, "simulation", "（模拟）", "应用内模拟");
        lock (_gate) _risks[KeyOf(active)] = active;
        Publish(active, prompt: false, config);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            bool removed;
            lock (_gate) removed = _risks.Remove(KeyOf(active));
            if (removed)
                Publish(active with { Active = false, Evidence = "应用内模拟结束" }, prompt: false, config);
        });
    }

    void AddScreenCaptureRisks(
        IDictionary<RiskKey, PrivacyRiskSnapshot> current,
        ICollection<string> notes,
        MonitoringSnapshot snapshot)
    {
        foreach (var info in snapshot.ScreenProcesses)
        {
            if (!IsExpectedPrivacyTarget(
                    PrivacyRiskKind.ScreenCapture, info.ProcessName, info.Product, info.OriginalFilename, info.IsSignedBySeewo))
            {
                NoteUnverified(notes, info);
                continue;
            }

            int established = snapshot.EstablishedTcpByPid.TryGetValue(info.Pid, out int count) ? count : 0;
            if (!ShouldConfirmScreenCapture(true, established))
            {
                notes.Add(ScreenCaptureIdleNote(info.ProcessName, info.Pid));
                continue;
            }

            var risk = ToPrivacyRisk(PrivacyRiskKind.ScreenCapture, info, ScreenCaptureConfirmedEvidence);
            current[KeyOf(risk)] = risk;
        }
    }

    void AddRemoteControlRisks(
        IDictionary<RiskKey, PrivacyRiskSnapshot> current,
        ICollection<string> notes,
        MonitoringSnapshot snapshot)
    {
        foreach (var info in snapshot.RemoteProcesses)
        {
            if (!IsExpectedPrivacyTarget(
                    PrivacyRiskKind.RemoteControl, info.ProcessName, info.Product, info.OriginalFilename, info.IsSignedBySeewo))
            {
                NoteUnverified(notes, info);
                continue;
            }

            bool microphone = IsMicrophoneActive(snapshot, info);
            bool camera = CapabilityPathMatches(snapshot.CameraInUseApps, info.ExecutablePath);
            var risk = ToPrivacyRisk(PrivacyRiskKind.RemoteControl, info, DescribeRemoteSession(microphone, camera));
            current[KeyOf(risk)] = risk;
        }
    }

    void AddMicrophoneRisks(
        IDictionary<RiskKey, PrivacyRiskSnapshot> current,
        MonitoringSnapshot snapshot)
    {
        foreach (var info in snapshot.MicrophoneProcesses)
        {
            if (!IsMicrophoneActive(snapshot, info)) continue;
            var risk = ToPrivacyRisk(PrivacyRiskKind.Microphone, info, MicrophoneEvidence);
            current[KeyOf(risk)] = risk;
        }
    }

    static bool IsMicrophoneActive(MonitoringSnapshot snapshot, TargetProcessInfo info)
        => snapshot.MicrophoneUsages.Any(usage =>
            string.Equals(usage.ExecutablePath, info.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            ShouldTrackMicrophoneUse(
                info.IsSignedBySeewo,
                info.Product,
                info.ProcessName,
                info.OriginalFilename,
                usage.LastUsedStart,
                usage.LastUsedStop,
                info.StartTimeUtc));

    static bool CapabilityPathMatches(IEnumerable<string> paths, string executablePath)
        => !string.IsNullOrWhiteSpace(executablePath) &&
           paths.Any(path => string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase));

    void AddLiveRisks(
        IDictionary<RiskKey, PrivacyRiskSnapshot> current,
        ICollection<string> notes,
        MonitoringSnapshot snapshot)
    {
        foreach (var info in snapshot.LiveProcesses)
        {
            if (!IsExpectedPrivacyTarget(
                    PrivacyRiskKind.LiveBroadcast, info.ProcessName, info.Product, info.OriginalFilename, info.IsSignedBySeewo))
            {
                NoteUnverified(notes, info);
                continue;
            }

            bool camera = HasFreshCapability(snapshot.CameraUsages, info);
            bool microphone = HasFreshCapability(snapshot.MicrophoneUsages, info);
            int established = snapshot.EstablishedTcpByPid.TryGetValue(info.Pid, out int count) ? count : 0;
            if (!ShouldConfirmLiveSession(true, camera, microphone, established))
            {
                notes.Add(LiveIdleNote(info.ProcessName, info.Pid));
                continue;
            }

            var risk = ToPrivacyRisk(
                PrivacyRiskKind.LiveBroadcast,
                info,
                DescribeLiveSession(microphone, camera, established > 0));
            current[KeyOf(risk)] = risk;
        }
    }

    void AddDeviceCameraRisks(
        IDictionary<RiskKey, PrivacyRiskSnapshot> current,
        ICollection<string> notes,
        MonitoringSnapshot snapshot)
    {
        foreach (var info in snapshot.AbilityProcesses)
        {
            var usage = snapshot.CameraUsages.FirstOrDefault(item =>
                string.Equals(item.ExecutablePath, info.ExecutablePath, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(usage.ExecutablePath)) continue;
            if (!IsExpectedPrivacyTarget(
                    PrivacyRiskKind.DeviceCamera, info.ProcessName, info.Product, info.OriginalFilename, info.IsSignedBySeewo))
            {
                NoteUnverified(notes, info);
                continue;
            }

            if (!IsFreshCapabilityUse(usage.LastUsedStart, usage.LastUsedStop, info.StartTimeUtc))
            {
                notes.Add($"{info.ProcessName}.exe(pid={info.Pid}) 摄像头记录早于本次进程启动");
                continue;
            }

            var risk = ToPrivacyRisk(PrivacyRiskKind.DeviceCamera, info, DeviceCameraEvidence);
            current[KeyOf(risk)] = risk;
        }
    }

    static void AddWindowRisks(
        IDictionary<RiskKey, PrivacyRiskSnapshot> current,
        IReadOnlyList<WindowAnomaly> windowAnomalies)
    {
        foreach (var anomaly in windowAnomalies)
        {
            if (anomaly.Kind != WindowAnomalyKind.MovedOffScreen) continue;
            var window = anomaly.Current;
            var risk = new PrivacyRiskSnapshot(
                PrivacyRiskKind.WindowChange,
                true,
                0,
                null,
                WindowAnomalyLogic.ShortTitle(window.Title),
                window.ClassName,
                WindowAnomalyLogic.Describe(anomaly),
                window.Hwnd);
            current[KeyOf(risk)] = risk;
        }
    }

    static void NoteUnverified(ICollection<string> notes, TargetProcessInfo info)
        => notes.Add($"{info.ProcessName}.exe(pid={info.Pid}) 未通过希沃数字签名/产品校验");

    static bool HasFreshCapability(
        IEnumerable<CapabilityUsageProbe.CapabilityUsage> usages,
        TargetProcessInfo info)
        => usages.Any(usage =>
            string.Equals(usage.ExecutablePath, info.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            IsFreshCapabilityUse(usage.LastUsedStart, usage.LastUsedStop, info.StartTimeUtc));

    void Publish(PrivacyRiskSnapshot risk, bool prompt, PluginConfig config)
    {
        PluginLog.Info($"[隐私风险] {RiskName(risk.Kind)} {(risk.Active ? "活动" : "结束")}: " +
            $"pid={risk.ProcessId}, {risk.Evidence}");
        _publish(risk);
        if (ShouldPromptPrivacyRisk(config.PrivacyRiskResponse, prompt, risk.Active, risk.ProcessId) &&
            CanTerminateRisk(risk.Kind, risk.ProcessName))
            QueuePrompt(risk);
    }

    void QueuePrompt(PrivacyRiskSnapshot risk)
    {
        bool start;
        lock (_gate)
        {
            if (!_promptedProcesses.Add((risk.ProcessId, risk.ProcessStartTimeUtc))) return;
            _promptQueue.Enqueue(risk);
            start = !_promptShowing;
            if (start) _promptShowing = true;
        }
        if (start) Dispatcher.UIThread.Post(ShowNextPrompt);
    }

    async void ShowNextPrompt()
    {
        PrivacyRiskSnapshot? risk;
        lock (_gate)
        {
            if (_promptQueue.Count == 0)
            {
                _promptShowing = false;
                return;
            }
            risk = _promptQueue.Dequeue();
            if (!_risks.ContainsKey(KeyOf(risk)))
            {
                Dispatcher.UIThread.Post(ShowNextPrompt);
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
                var result = await Task.Run(() => Terminate(risk));
                lock (_gate) _lastOperation = result.Message;
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
            lock (_gate) _lastOperation = "确认框显示失败：" + ex.Message;
            PluginLog.Warn(LastOperation);
        }
        finally
        {
            ShowNextPrompt();
        }
    }

    public PluginOperationResult Terminate(PrivacyRiskSnapshot risk)
    {
        if (!CanTerminateRisk(risk.Kind, risk.ProcessName))
            return PluginOperationResult.Fail("该风险只记录和提醒，不会结束进程");
        if (risk.ProcessId <= 0 || risk.ProcessStartTimeUtc is null || string.IsNullOrWhiteSpace(risk.ExecutablePath))
            return PluginOperationResult.Fail("风险快照没有可安全终止的进程信息");

        try
        {
            using var process = Process.GetProcessById(risk.ProcessId);
            var current = TargetProcessInfo.FromProcess(process);
            if (current.StartTimeUtc != risk.ProcessStartTimeUtc ||
                !string.Equals(current.ExecutablePath, risk.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return PluginOperationResult.Fail("进程身份已变化，已拒绝终止以避免 PID 复用误杀");

            bool verified = risk.Kind == PrivacyRiskKind.Microphone
                ? IsMicrophoneCaptureTarget(
                    current.ProcessName, current.Product, current.OriginalFilename, current.IsSignedBySeewo)
                : IsExpectedPrivacyTarget(
                    risk.Kind, current.ProcessName, current.Product, current.OriginalFilename, current.IsSignedBySeewo);
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

    internal const int RiskConfirmSamples = 2;
    internal const int RiskClearSamples = 2;

    internal static bool ShouldTrackCameraPrivacyRisk(bool fusedActive, bool targetVerified)
        => fusedActive && targetVerified;

    internal static bool ShouldPromptPrivacyRisk(
        PrivacyRiskResponseMode mode,
        bool promptRequested,
        bool active,
        int processId)
        => mode == PrivacyRiskResponseMode.Prompt && promptRequested && active && processId > 0;

    /// <summary>希沃 1.5.5 与 1.6.6 的 screenCapture 都是 GDI 截图 RPC，仅在已有客户端连接时确认。</summary>
    internal static bool ShouldConfirmScreenCapture(bool targetVerified, int establishedTcpCount)
        => targetVerified && establishedTcpCount > 0;

    internal const string ScreenCaptureConfirmedEvidence = "希沃截图服务已有 RPC 客户端连接";
    internal const string MicrophoneEvidence = "Windows 检测到麦克风正在使用";

    internal static string ScreenCaptureIdleNote(string processName, int pid)
        => $"{processName}.exe(pid={pid}) 截图服务在监听，尚无 RPC 客户端";

    /// <summary>两个版本的 rtcRemoteDesktop 都用 WebRTC，进程即会话；UDP 不作为必要条件。</summary>
    internal static string DescribeRemoteSession(bool microphoneInUse, bool cameraInUse) => (microphoneInUse, cameraInUse) switch
    {
        (true, true) => "远控组件会话已启动，正在采集麦克风和摄像头",
        (true, false) => "远控组件会话已启动，正在采集麦克风",
        (false, true) => "远控组件会话已启动，正在采集摄像头",
        _ => "远控组件会话已启动",
    };

    internal static bool IsMicrophoneCaptureProcess(string processName, string originalFilename)
        => processName.Equals("media_capture", StringComparison.OrdinalIgnoreCase) &&
           originalFilename.Equals("media_capture.exe", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("rtcRemoteDesktop", StringComparison.OrdinalIgnoreCase) &&
           originalFilename.Equals("rtcRemoteDesktop.exe", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("SeewoAbility", StringComparison.OrdinalIgnoreCase) &&
           originalFilename.Equals("SeewoAbility.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMicrophoneCaptureTarget(
        string processName,
        string product,
        string originalFilename,
        bool signedBySeewo)
        => signedBySeewo &&
           product.Contains("希沃", StringComparison.OrdinalIgnoreCase) &&
           IsMicrophoneCaptureProcess(processName, originalFilename);

    /// <summary>
    /// 麦克风认 media_capture、rtcRemoteDesktop，以及承载语音模块的 SeewoAbility。
    /// 同意项开始时间早于本次进程启动的记录视为上次残留。
    /// </summary>
    internal static bool ShouldTrackMicrophoneUse(
        bool signedBySeewo,
        string product,
        string processName,
        string originalFilename,
        long consentStart,
        long consentStop,
        DateTime? processStartUtc)
    {
        if (!IsMicrophoneCaptureTarget(processName, product, originalFilename, signedBySeewo)) return false;
        return IsFreshCapabilityUse(consentStart, consentStop, processStartUtc);
    }

    internal static bool IsFreshCapabilityUse(long consentStart, long consentStop, DateTime? processStartUtc)
    {
        if (!CapabilityUsageProbe.IsCurrentlyInUse(consentStart, consentStop)) return false;
        if (processStartUtc is DateTime start && consentStart < start.ToFileTimeUtc()) return false;
        return true;
    }

    /// <summary>liveClient 进程本身不代表会话；摄像头、麦克风或已建立的 TCP 连接至少要有一项。</summary>
    internal static bool ShouldConfirmLiveSession(bool targetVerified, bool cameraInUse, bool microphoneInUse, int establishedTcpCount)
        => targetVerified && (cameraInUse || microphoneInUse || establishedTcpCount > 0);

    internal static string DescribeLiveSession(bool microphoneInUse, bool cameraInUse, bool connected)
    {
        string media = (microphoneInUse, cameraInUse) switch
        {
            (true, true) => "正在采集麦克风和摄像头",
            (true, false) => "正在采集麦克风",
            (false, true) => "正在采集摄像头",
            _ => "",
        };
        if (connected && media.Length > 0) return "校园直播客户端已建立连接，" + media;
        if (connected) return "校园直播客户端已建立连接";
        if (media.Length > 0) return "校园直播客户端" + media;
        return "校园直播客户端在运行";
    }

    internal static string LiveIdleNote(string processName, int pid)
        => $"{processName}.exe(pid={pid}) 在运行，尚未发现摄像头、麦克风或 TCP 连接";

    internal const string DeviceCameraEvidence = "Windows 检测到希沃业务宿主正在使用摄像头";

    internal static bool ShouldTrackHostCameraUse(
        bool signedBySeewo,
        string product,
        string processName,
        string originalFilename,
        long consentStart,
        long consentStop,
        DateTime? processStartUtc)
    {
        if (!IsExpectedPrivacyTarget(PrivacyRiskKind.DeviceCamera, processName, product, originalFilename, signedBySeewo))
            return false;
        return IsFreshCapabilityUse(consentStart, consentStop, processStartUtc);
    }

    internal static bool CanTerminateRisk(PrivacyRiskKind kind, string processName)
        => kind is not (PrivacyRiskKind.DeviceCamera or PrivacyRiskKind.WindowChange) &&
           !IsManagementHost(processName);

    internal static bool IsManagementHost(string processName)
        => processName.Equals("SeewoAbility", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("SeewoCore", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("SeewoHugoLauncher", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("SeewoServiceAssistant", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("DriverService", StringComparison.OrdinalIgnoreCase) ||
           processName.Equals("proxyLayerService", StringComparison.OrdinalIgnoreCase);

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
            PrivacyRiskKind.LiveBroadcast =>
                processName.Equals("liveClient", StringComparison.OrdinalIgnoreCase) &&
                originalFilename.Equals("liveClient.exe", StringComparison.OrdinalIgnoreCase) &&
                seewoProduct && signedBySeewo,
            PrivacyRiskKind.DeviceCamera =>
                processName.Equals("SeewoAbility", StringComparison.OrdinalIgnoreCase) &&
                originalFilename.Equals("SeewoAbility.exe", StringComparison.OrdinalIgnoreCase) &&
                seewoProduct && signedBySeewo,
            _ => false,
        };
    }

    static PrivacyRiskSnapshot ToPrivacyRisk(PrivacyRiskKind kind, TargetProcessInfo info, string evidence)
        => new(kind, true, info.Pid, info.StartTimeUtc, info.ProcessName, info.ExecutablePath, evidence);

    internal static string RiskName(PrivacyRiskKind kind) => kind switch
    {
        PrivacyRiskKind.Camera => "摄像头访问",
        PrivacyRiskKind.ScreenCapture => "屏幕采集风险",
        PrivacyRiskKind.RemoteControl => "远程控制风险",
        PrivacyRiskKind.Microphone => "麦克风访问",
        PrivacyRiskKind.LiveBroadcast => "校园直播",
        PrivacyRiskKind.DeviceCamera => "宿主摄像头",
        PrivacyRiskKind.WindowChange => "窗口异常",
        _ => "隐私风险",
    };

    static RiskKey KeyOf(PrivacyRiskSnapshot risk)
        => new(risk.Kind, risk.ProcessId, risk.ProcessStartTimeUtc, risk.SubjectId);

    readonly record struct RiskKey(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc, long SubjectId);
}
