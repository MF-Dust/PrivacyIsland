using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using PrivacyIsland.Config;
using PrivacyIsland.Logging;

namespace PrivacyIsland.Orchestrator;

/// <summary>维护风险状态、提示队列和安全终止操作；监测采样本身由 MonitoringScanner 负责。</summary>
internal sealed class PrivacyRiskCoordinator
{
    readonly object _gate = new();
    readonly Dictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot> _risks = new();
    readonly HashSet<(int Pid, DateTime? StartTimeUtc)> _promptedProcesses = new();
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

    public void Update(MonitoringSnapshot snapshot, bool fusedActive, bool hookActive, PluginConfig config)
    {
        var current = new Dictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot>();
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
            current[(risk.Kind, risk.ProcessId, risk.ProcessStartTimeUtc)] = risk;
        }

        if (config.EnableScreenCaptureMonitoring)
        {
            AddProcessRisks(current, notes, PrivacyRiskKind.ScreenCapture, snapshot.ScreenProcesses,
                "希沃屏幕采集组件已启动（进程信号，不代表已确认每次截图）");
            AddCapabilityRisks(current, PrivacyRiskKind.ScreenCapture, snapshot.ScreenCapabilityProcesses,
                "Windows 检测到无边框屏幕捕获正在使用");
        }
        if (config.EnableRemoteControlMonitoring)
        {
            AddProcessRisks(current, notes, PrivacyRiskKind.RemoteControl, snapshot.RemoteProcesses,
                "希沃远程桌面组件已启动");
        }
        if (config.EnableMicrophoneMonitoring)
        {
            AddCapabilityRisks(current, PrivacyRiskKind.Microphone, snapshot.MicrophoneCapabilityProcesses,
                "Windows 检测到麦克风正在使用");
        }

        List<PrivacyRiskSnapshot> changed = new();
        lock (_gate)
        {
            foreach (var (key, risk) in current)
            {
                if (!_risks.ContainsKey(key)) changed.Add(risk);
                _risks[key] = risk;
            }

            foreach (var (key, old) in _risks.ToArray())
            {
                if (current.ContainsKey(key)) continue;
                _risks.Remove(key);
                changed.Add(old with { Active = false, Evidence = old.Evidence + "；状态已结束" });
            }

            var activeProcesses = current.Keys
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
        lock (_gate) _risks[(kind, 0, null)] = active;
        Publish(active, prompt: false, config);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1200);
            bool removed;
            lock (_gate) removed = _risks.Remove((kind, 0, null));
            if (removed)
                Publish(active with { Active = false, Evidence = "应用内模拟结束" }, prompt: false, config);
        });
    }

    void AddProcessRisks(
        IDictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot> current,
        ICollection<string> notes,
        PrivacyRiskKind kind,
        IEnumerable<TargetProcessInfo> processes,
        string evidence)
    {
        foreach (var info in processes)
        {
            if (!IsExpectedPrivacyTarget(kind, info.ProcessName, info.Product, info.OriginalFilename, info.IsSignedBySeewo))
            {
                notes.Add($"{info.ProcessName}.exe(pid={info.Pid}) 未通过希沃数字签名/产品校验");
                continue;
            }

            var risk = ToPrivacyRisk(kind, info, evidence);
            current[(kind, info.Pid, info.StartTimeUtc)] = risk;
        }
    }

    static void AddCapabilityRisks(
        IDictionary<(PrivacyRiskKind Kind, int Pid, DateTime? StartTimeUtc), PrivacyRiskSnapshot> current,
        PrivacyRiskKind kind,
        IEnumerable<TargetProcessInfo> processes,
        string evidence)
    {
        foreach (var info in processes)
        {
            if (!IsSeewoCapabilityProcess(info)) continue;
            current[(kind, info.Pid, info.StartTimeUtc)] = ToPrivacyRisk(kind, info, evidence);
        }
    }

    void Publish(PrivacyRiskSnapshot risk, bool prompt, PluginConfig config)
    {
        PluginLog.Info($"[隐私风险] {RiskName(risk.Kind)} {(risk.Active ? "活动" : "结束")}: " +
            $"pid={risk.ProcessId}, {risk.Evidence}");
        _publish(risk);
        if (ShouldPromptPrivacyRisk(config.PrivacyRiskResponse, prompt, risk.Active, risk.ProcessId))
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
            if (!_risks.ContainsKey((risk.Kind, risk.ProcessId, risk.ProcessStartTimeUtc)))
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

    internal static bool ShouldTrackCameraPrivacyRisk(bool fusedActive, bool targetVerified)
        => fusedActive && targetVerified;

    internal static bool ShouldPromptPrivacyRisk(
        PrivacyRiskResponseMode mode,
        bool promptRequested,
        bool active,
        int processId)
        => mode == PrivacyRiskResponseMode.Prompt && promptRequested && active && processId > 0;

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

    static PrivacyRiskSnapshot ToPrivacyRisk(PrivacyRiskKind kind, TargetProcessInfo info, string evidence)
        => new(kind, true, info.Pid, info.StartTimeUtc, info.ProcessName, info.ExecutablePath, evidence);

    internal static string RiskName(PrivacyRiskKind kind) => kind switch
    {
        PrivacyRiskKind.Camera => "摄像头访问",
        PrivacyRiskKind.ScreenCapture => "屏幕采集风险",
        PrivacyRiskKind.RemoteControl => "远程控制风险",
        PrivacyRiskKind.Microphone => "麦克风访问",
        _ => "隐私风险",
    };
}
