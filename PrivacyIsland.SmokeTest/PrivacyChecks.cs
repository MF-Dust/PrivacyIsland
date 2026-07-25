using PrivacyIsland.Config;
using PrivacyIsland.Native;
using PrivacyIsland.Notification;
using PrivacyIsland.Orchestrator;
using PrivacyIsland;

internal static class PrivacyChecks
{
    const string Product = "希沃管家";
    const string MediaPath = @"C:\Seewo\media_capture.exe";

    public static void Run()
    {
        CheckPollingAndCapabilityUsage();
        CheckConfiguration();
        CheckPrivacyRules();
        CheckRefactoringRegressions();
    }

    static void CheckPollingAndCapabilityUsage()
    {
        var pollAt = DateTime.UtcNow;
        SmokeAssert.That(!CaptureMonitor.IsHeavyPollDue(pollAt.AddMilliseconds(2999), pollAt),
            "重扫描闸门：3 秒内不重复扫描");
        SmokeAssert.That(CaptureMonitor.IsHeavyPollDue(pollAt.AddMilliseconds(3000), pollAt),
            "重扫描闸门：到达 3 秒时扫描");

        var inUse = new CapabilityUsageProbe("webcam",
            () => new[] { new CapabilityUsageProbe.CapabilityUsage(MediaPath, 1, 0, false) });
        SmokeAssert.That(inUse.IsInUseBy(MediaPath), "OS 探测：Stop==0 且 Start!=0 判为在用");
        SmokeAssert.That(inUse.IsInUseBy(MediaPath.ToUpperInvariant()), "OS 探测：路径大小写不敏感");
        SmokeAssert.That(!inUse.IsInUseBy(@"C:\other.exe"), "OS 探测：其他路径不误报");
        SmokeAssert.That(inUse.InUseApps().Contains(MediaPath), "OS 探测：InUseApps 含在用路径");

        var notInUse = new CapabilityUsageProbe("microphone",
            () => new[] { new CapabilityUsageProbe.CapabilityUsage(MediaPath, 1, 5, false) });
        SmokeAssert.That(!notInUse.IsInUseBy(MediaPath), "OS 探测：Stop!=0 判为未在用");
        SmokeAssert.That(notInUse.InUseApps().Count == 0, "OS 探测：无在用应用时 InUseApps 为空");
    }

    static void CheckConfiguration()
    {
        var config = new PluginConfig();
        SmokeAssert.That(config.PrivacyRiskResponse == PrivacyRiskResponseMode.Prompt,
            "隐私风险默认询问后处理");
        config.PrivacyRiskResponse = PrivacyRiskResponseMode.NotifyOnly;
        config.Clamp();
        SmokeAssert.That(config.PrivacyRiskResponse == PrivacyRiskResponseMode.NotifyOnly,
            "隐私风险可切换为仅提示");

        config.MinDelaySeconds = -10;
        config.MaxDelaySeconds = 99;
        config.OverlayDurationSeconds = 0;
        config.ClassMinDelaySeconds = 31;
        config.ClassMaxDelaySeconds = -1;
        config.PrivacyRiskResponse = (PrivacyRiskResponseMode)99;
        config.Clamp();
        SmokeAssert.That(config.MinDelaySeconds == 1 && config.MaxDelaySeconds == 30,
            "配置边界：基础延迟限制在 1..30");
        SmokeAssert.That(config.OverlayDurationSeconds == 1,
            "配置边界：通知时长限制在 1..30");
        SmokeAssert.That(config.ClassMinDelaySeconds == 30 && config.ClassMaxDelaySeconds == 30,
            "配置边界：课程延迟限制范围且 max>=min");
        SmokeAssert.That(config.PrivacyRiskResponse == PrivacyRiskResponseMode.Prompt,
            "配置边界：非法风险处理模式回退默认值");
    }

    static void CheckPrivacyRules()
    {
        SmokeAssert.That(!CaptureMonitor.ShouldPromptPrivacyRisk(
            PrivacyRiskResponseMode.NotifyOnly, true, true, 123),
            "仅提示模式不排队确认框");
        SmokeAssert.That(CaptureMonitor.ShouldPromptPrivacyRisk(
            PrivacyRiskResponseMode.Prompt, true, true, 123),
            "询问模式为有效风险排队确认框");
        SmokeAssert.That(CaptureMonitor.ShouldTrackCameraPrivacyRisk(true, true),
            "摄像头活动且目标可信时纳入隐私风险");
        SmokeAssert.That(!CaptureMonitor.ShouldTrackCameraPrivacyRisk(true, false),
            "摄像头目标未通过校验时不纳入隐私风险");
        SmokeAssert.That(CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.Camera, "media_capture", Product, "media_capture.exe", true),
            "隐私目标：接受有效签名的 media_capture 摄像头组件");
        SmokeAssert.That(!CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.Camera, "media_capture", Product, "wrong.exe", true),
            "隐私目标：拒绝原始文件名错误的摄像头组件");
        SmokeAssert.That(!CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.Camera, "media_capture", Product, "media_capture.exe", false),
            "隐私目标：拒绝未签名的摄像头组件");
        SmokeAssert.That(!CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.Camera, true, true),
            "摄像头不重复显示通用隐私通知");
        SmokeAssert.That(CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.ScreenCapture, true, true),
            "其他隐私风险继续显示通用通知");

        var risk = new PrivacyRiskSnapshot(
            PrivacyRiskKind.ScreenCapture,
            true,
            123,
            null,
            "screenCapture",
            MediaPath,
            "测试");
        SmokeAssert.That(CameraNotificationProvider.FormatPrivacyRiskText(
            "{风险类型}|{进程名}|{PID}", risk) == "屏幕采集|screenCapture.exe|123",
            "隐私提醒模板替换风险类型、进程名和 PID");
        SmokeAssert.That(CameraNotificationProvider.FormatPrivacyRiskText("", risk) ==
            "检测到 屏幕采集\nscreenCapture.exe (PID 123) 正在访问相关隐私能力",
            "空隐私提醒模板回退默认文案");
        SmokeAssert.That(CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.ScreenCapture, "screenCapture", Product, "screenCapture.exe", true),
            "隐私目标：接受有效签名的 screenCapture");
        SmokeAssert.That(CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.RemoteControl, "rtcRemoteDesktop", Product, "rtcRemoteDesktop.exe", true),
            "隐私目标：接受有效签名的远控组件");
        SmokeAssert.That(!CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.ScreenCapture, "screenCapture", Product, "screenCapture.exe", false),
            "隐私目标：拒绝未签名的同名组件");
    }

    static void CheckRefactoringRegressions()
    {
        int delivered = 0;
        Action<int> handlers = _ => throw new InvalidOperationException("expected");
        handlers += value => delivered = value;
        EventDispatch.Invoke(handlers, 7);
        SmokeAssert.That(delivered == 7, "事件分发：单个订阅者异常不阻断后续订阅者");

        var started = DateTime.UnixEpoch;
        var target = ProcessInfo(20, "media_capture", MediaPath, "media_capture.exe", started);
        SmokeAssert.That(!CaptureMonitor.ShouldRefreshDiagnostics(target, target with { }, false),
            "诊断刷新闸门：目标实例未变时不重复慢扫描");
        SmokeAssert.That(CaptureMonitor.ShouldRefreshDiagnostics(target, target with { }, true),
            "诊断刷新闸门：显式请求强制刷新");
        SmokeAssert.That(CaptureMonitor.ShouldRefreshDiagnostics(
            target, target with { StartTimeUtc = started.AddSeconds(1) }, false),
            "诊断刷新闸门：目标实例变化时自动刷新");

        const string screenPath = @"C:\Seewo\screenCapture.exe";
        const string microphonePath = @"C:\Seewo\audioHost.exe";
        var config = new PluginConfig
        {
            EnableScreenCaptureMonitoring = true,
            EnableRemoteControlMonitoring = false,
            EnableMicrophoneMonitoring = true,
        };
        var snapshot = MonitoringScanner.BuildSnapshot(
            config,
            new[] { MediaPath.ToUpperInvariant() },
            new[] { screenPath.ToUpperInvariant() },
            new[] { microphonePath.ToUpperInvariant() },
            new[]
            {
                ProcessInfo(30, "media_capture", MediaPath, "media_capture.exe", started),
                target,
                ProcessInfo(40, "screenCapture", screenPath, "screenCapture.exe", started),
                ProcessInfo(50, "rtcRemoteDesktop", @"C:\Seewo\rtcRemoteDesktop.exe", "rtcRemoteDesktop.exe", started),
                ProcessInfo(60, "audioHost", microphonePath, "audioHost.exe", started),
                ProcessInfo(70, "other", @"C:\Other\other.exe", "other.exe", started),
            },
            new[] { "note" },
            started);

        SmokeAssert.That(snapshot.Target?.Pid == 20 && snapshot.CameraOsInUse,
            "监测快照：选择最低 PID 的可信目标并忽略路径大小写");
        SmokeAssert.That(snapshot.ScreenProcesses.Count == 1 &&
            snapshot.ScreenCapabilityProcesses.Single().Pid == 40,
            "监测快照：屏幕组件与能力路径正确匹配");
        SmokeAssert.That(snapshot.RemoteProcesses.Count == 0,
            "监测快照：关闭的远程控制监测被过滤");
        SmokeAssert.That(snapshot.MicrophoneCapabilityProcesses.Single().Pid == 60,
            "监测快照：麦克风能力只匹配对应可执行路径");
        SmokeAssert.That(snapshot.ProcessNotes.SequenceEqual(new[] { "note" }) && snapshot.UpdatedUtc == started,
            "监测快照：保留扫描备注与更新时间");
    }

    static TargetProcessInfo ProcessInfo(
        int pid,
        string processName,
        string path,
        string originalFilename,
        DateTime startTimeUtc)
        => new(
            pid,
            processName,
            path,
            "1.0",
            "1.0",
            processName,
            Product,
            originalFilename,
            startTimeUtc,
            true,
            true,
            "x86");
}
