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
        CheckLiveHostAndWindows();
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
        SmokeAssert.That(!notInUse.IsInUseBy(MediaPath), "OS 探测：Stop 晚于 Start 判为未在用");
        SmokeAssert.That(notInUse.InUseApps().Count == 0, "OS 探测：无在用应用时 InUseApps 为空");
        SmokeAssert.That(CapabilityUsageProbe.IsCurrentlyInUse(8, 3),
            "OS 探测：Start 晚于 Stop 仍判为在用");
    }

    static void CheckConfiguration()
    {
        var config = new PluginConfig();
        SmokeAssert.That(config.EnableLiveMonitoring && config.EnableDeviceCameraMonitoring && config.EnableWindowChangeMonitoring,
            "直播、宿主摄像头和窗口记录默认开启");
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
        SmokeAssert.That(!CaptureMonitor.ShouldConfirmScreenCapture(true, 0),
            "无 TCP 客户端的截图服务不构成风险");
        SmokeAssert.That(CaptureMonitor.ShouldConfirmScreenCapture(true, 1),
            "已有 RPC 客户端的截图服务构成风险");
        SmokeAssert.That(!CaptureMonitor.ShouldConfirmScreenCapture(false, 3),
            "未通过校验的截图服务即使有连接也不构成风险");
        SmokeAssert.That(CaptureMonitor.DescribeRemoteSession(false, false) == "远控组件会话已启动",
            "远控进程会话即可确认");
        SmokeAssert.That(CaptureMonitor.DescribeRemoteSession(true, true) == "远控组件会话已启动，正在采集麦克风和摄像头",
            "远控会话标注麦克风和摄像头占用");

        var started = DateTime.UnixEpoch;
        long fresh = started.ToFileTimeUtc();
        SmokeAssert.That(!CaptureMonitor.ShouldTrackMicrophoneUse(
            true, Product, "media_capture", "media_capture.exe", fresh - 1, 0, started),
            "早于进程启动的麦克风记录不构成风险");
        SmokeAssert.That(CaptureMonitor.ShouldTrackMicrophoneUse(
            true, Product, "media_capture", "media_capture.exe", fresh, 0, started),
            "本次进程启动后的麦克风占用构成风险");
        SmokeAssert.That(CaptureMonitor.ShouldTrackMicrophoneUse(
            true, Product, "rtcRemoteDesktop", "rtcRemoteDesktop.exe", fresh + 10, fresh, started),
            "1.5.5 与 1.6.6 的 rtcRemoteDesktop 麦克风占用构成风险");
        SmokeAssert.That(!CaptureMonitor.ShouldTrackMicrophoneUse(
            true, Product, "audioHost", "audioHost.exe", fresh, 0, started),
            "非采集进程的麦克风记录不构成风险");
        SmokeAssert.That(!CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.Camera, true, true, true),
            "摄像头不重复显示通用隐私通知");
        SmokeAssert.That(CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.ScreenCapture, true, true, false),
            "其他隐私风险继续显示通用通知");
        SmokeAssert.That(CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.ScreenCapture, false, true, true),
            "隐私风险结束时显示结束提醒");
        SmokeAssert.That(!CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.ScreenCapture, false, true, false),
            "关闭结束提醒后不显示结束通知");
        SmokeAssert.That(!CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.Camera, false, true, true),
            "摄像头结束不走通用隐私通知");

        var risk = new PrivacyRiskSnapshot(
            PrivacyRiskKind.ScreenCapture,
            true,
            123,
            null,
            "screenCapture",
            MediaPath,
            "测试");
        SmokeAssert.That(CameraNotificationProvider.FormatPrivacyRiskText(
            "{风险类型}|{进程名}|{PID}|{状态}|{依据}", risk) == "屏幕采集|screenCapture.exe|123|活动|测试",
            "隐私提醒模板替换风险类型、进程名、PID、状态和依据");
        SmokeAssert.That(CameraNotificationProvider.FormatPrivacyRiskText("", risk) ==
            "检测到 屏幕采集（活动）\nscreenCapture.exe (PID 123) 正在访问相关隐私能力\n测试",
            "空隐私提醒模板回退默认文案");
        SmokeAssert.That(CameraNotificationProvider.FormatPrivacyRiskText("", risk with { Active = false }) ==
            "屏幕采集已结束",
            "结束态生成结束提醒文案");
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
        long consentStart = started.ToFileTimeUtc();
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
            new[] { new CapabilityUsageProbe.CapabilityUsage(microphonePath, consentStart, 0, false) },
            new[]
            {
                ProcessInfo(30, "media_capture", MediaPath, "media_capture.exe", started),
                target,
                ProcessInfo(40, "screenCapture", screenPath, "screenCapture.exe", started),
                ProcessInfo(50, "rtcRemoteDesktop", @"C:\Seewo\rtcRemoteDesktop.exe", "rtcRemoteDesktop.exe", started),
                ProcessInfo(60, "audioHost", microphonePath, "audioHost.exe", started),
                ProcessInfo(70, "other", @"C:\Other\other.exe", "other.exe", started),
            },
            new Dictionary<int, int> { [40] = 0 },
            new[] { "note" },
            started);

        SmokeAssert.That(snapshot.Target?.Pid == 20 && snapshot.CameraOsInUse,
            "监测快照：选择最低 PID 的可信目标并忽略路径大小写");
        SmokeAssert.That(snapshot.ScreenProcesses.Count == 1 &&
            snapshot.EstablishedTcpByPid[40] == 0,
            "监测快照：保留截图进程及其 RPC 连接数");
        SmokeAssert.That(snapshot.RemoteProcesses.Count == 0,
            "监测快照：关闭的远程控制监测被过滤");
        SmokeAssert.That(snapshot.MicrophoneProcesses.Select(p => p.Pid).OrderBy(pid => pid).SequenceEqual(new[] { 20, 30, 50 }),
            "监测快照：麦克风候选只保留 media_capture 与 rtcRemoteDesktop");
        SmokeAssert.That(snapshot.MicrophoneUsages.Single().ExecutablePath == microphonePath,
            "监测快照：保留麦克风同意项时间");
        SmokeAssert.That(snapshot.ProcessNotes.SequenceEqual(new[] { "note" }) && snapshot.UpdatedUtc == started,
            "监测快照：保留扫描备注与更新时间");
        SmokeAssert.That(snapshot.LiveProcesses.Count == 0 && snapshot.AbilityProcesses.Count == 0,
            "监测快照：未出现的直播和宿主进程不会被补入");
    }

    static void CheckLiveHostAndWindows()
    {
        SmokeAssert.That(!CaptureMonitor.ShouldConfirmLiveSession(true, false, false, 0),
            "直播进程没有采集或连接时不构成会话");
        SmokeAssert.That(CaptureMonitor.ShouldConfirmLiveSession(true, false, false, 1),
            "直播已有 TCP 连接时构成会话");
        SmokeAssert.That(CaptureMonitor.ShouldConfirmLiveSession(true, true, false, 0),
            "直播占用摄像头时构成会话");
        SmokeAssert.That(!CaptureMonitor.ShouldConfirmLiveSession(false, true, true, 2),
            "未通过校验的直播客户端不构成会话");
        SmokeAssert.That(CaptureMonitor.DescribeLiveSession(true, true, true) ==
            "校园直播客户端已建立连接，正在采集麦克风和摄像头",
            "直播会话同时标注连接和采集");
        SmokeAssert.That(CaptureMonitor.DescribeLiveSession(false, false, true) == "校园直播客户端已建立连接",
            "只有连接时的直播说明");
        SmokeAssert.That(CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.LiveBroadcast, "liveClient", Product, "liveClient.exe", true),
            "隐私目标：接受有效签名的 liveClient");
        SmokeAssert.That(!CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.LiveBroadcast, "liveClient", Product, "liveClient.exe", false),
            "隐私目标：拒绝未签名的 liveClient");
        SmokeAssert.That(CaptureMonitor.IsExpectedPrivacyTarget(
            PrivacyRiskKind.DeviceCamera, "SeewoAbility", Product, "SeewoAbility.exe", true),
            "隐私目标：接受有效签名的业务宿主");

        var started = DateTime.UnixEpoch;
        long fresh = started.ToFileTimeUtc();
        SmokeAssert.That(CaptureMonitor.ShouldTrackHostCameraUse(
            true, Product, "SeewoAbility", "SeewoAbility.exe", fresh, 0, started),
            "本次启动后的宿主摄像头占用构成风险");
        SmokeAssert.That(!CaptureMonitor.ShouldTrackHostCameraUse(
            true, Product, "SeewoAbility", "SeewoAbility.exe", fresh - 1, 0, started),
            "早于宿主启动的摄像头记录不构成风险");
        SmokeAssert.That(CaptureMonitor.ShouldTrackMicrophoneUse(
            true, Product, "SeewoAbility", "SeewoAbility.exe", fresh, 0, started),
            "语音宿主的麦克风占用构成风险");
        SmokeAssert.That(!CaptureMonitor.CanTerminateRisk(PrivacyRiskKind.Microphone, "SeewoAbility"),
            "语音宿主只提醒，不结束进程");
        SmokeAssert.That(!CaptureMonitor.CanTerminateRisk(PrivacyRiskKind.DeviceCamera, "SeewoAbility"),
            "宿主摄像头不结束进程");
        SmokeAssert.That(!CaptureMonitor.CanTerminateRisk(PrivacyRiskKind.WindowChange, "课表"),
            "窗口异常不结束进程");
        SmokeAssert.That(CaptureMonitor.CanTerminateRisk(PrivacyRiskKind.LiveBroadcast, "liveClient"),
            "直播客户端允许确认后结束");
        SmokeAssert.That(!CaptureMonitor.CanTerminateRisk(PrivacyRiskKind.ScreenCapture, "SeewoCore"),
            "管理宿主即使出现在其他风险类型中也不结束");

        var screen = new ScreenBounds(0, 0, 1920, 1080);
        var shown = new WindowSample(7, "课表", "ClassIsland", 100, 100, 400, 200, true, false);
        var baseline = new Dictionary<long, WindowSample> { [shown.Hwnd] = shown };
        var hidden = WindowAnomalyLogic.Detect(baseline, new[] { shown with { Visible = false } }, screen);
        SmokeAssert.That(hidden.Count == 1 && hidden[0].Kind == WindowAnomalyKind.Hidden,
            "可见窗口变为隐藏时记录异常");
        var offScreen = WindowAnomalyLogic.Detect(baseline, new[] { shown with { X = -800 } }, screen);
        SmokeAssert.That(offScreen.Count == 1 && offScreen[0].Kind == WindowAnomalyKind.MovedOffScreen,
            "完全移出虚拟屏幕时记录异常");
        SmokeAssert.That(WindowAnomalyLogic.Detect(baseline, new[] { shown with { Minimized = true } }, screen).Count == 0,
            "最小化不记为窗口异常");
        SmokeAssert.That(WindowAnomalyLogic.Detect(baseline, Array.Empty<WindowSample>(), screen).Count == 0,
            "窗口关闭不记为异常");
        SmokeAssert.That(WindowAnomalyLogic.Detect(baseline, new[] { shown with { X = 1900 } }, screen).Count == 0,
            "仍有一部分在屏幕内时不记为移出");
        var kept = WindowAnomalyLogic.AdvanceBaseline(baseline, new[] { shown with { X = -800 } }, screen);
        SmokeAssert.That(WindowAnomalyLogic.Detect(kept, new[] { shown with { X = -800 } }, screen).Count == 1,
            "窗口停留在屏幕外时异常保持");
        SmokeAssert.That(WindowAnomalyLogic.Detect(kept, new[] { shown }, screen).Count == 0,
            "窗口回到屏幕内后不再报告异常");

        var episode = new WindowEpisodeTracker();
        IReadOnlyList<WindowAnomaly> episodeNow = Array.Empty<WindowAnomaly>();
        for (int i = 0; i < WindowEpisodeTracker.ArmSamples; i++)
            episodeNow = episode.Observe(new[] { shown }, screen);
        SmokeAssert.That(episodeNow.Count == 0, "窗口稳定显示时不产生提醒");
        SmokeAssert.That(episode.Observe(new[] { shown with { Visible = false } }, screen).Count == 0,
            "普通隐藏不产生窗口提醒");
        for (int i = 0; i < WindowEpisodeTracker.ArmSamples; i++)
            episode.Observe(new[] { shown }, screen);
        var onceOff = shown with { X = -800 };
        SmokeAssert.That(episode.Observe(new[] { onceOff }, screen).Count == 0,
            "只移出屏幕一次不产生窗口提醒");
        episodeNow = episode.Observe(new[] { onceOff }, screen);
        SmokeAssert.That(episodeNow.Count == 1, "连续移出屏幕才产生窗口提醒");
        SmokeAssert.That(episode.Observe(new[] { onceOff }, screen).Count == 1,
            "窗口停留在屏幕外时不另起提醒回合");

        var published = new List<PrivacyRiskSnapshot>();
        var coordinator = new PrivacyRiskCoordinator(published.Add);
        var quiet = new PluginConfig
        {
            PrivacyRiskResponse = PrivacyRiskResponseMode.NotifyOnly,
            EnableScreenCaptureMonitoring = false,
            EnableRemoteControlMonitoring = false,
            EnableMicrophoneMonitoring = false,
            EnableLiveMonitoring = false,
            EnableDeviceCameraMonitoring = false,
            EnableWindowChangeMonitoring = true,
        };
        coordinator.Update(MonitoringSnapshot.Empty, false, false, quiet, episodeNow);
        SmokeAssert.That(published.Count == 0, "风险只采样到一次时不提醒");
        coordinator.Update(MonitoringSnapshot.Empty, false, false, quiet, episodeNow);
        SmokeAssert.That(published.Count == 1 && published[0].Active, "风险连续确认后只提醒一次");
        coordinator.Update(MonitoringSnapshot.Empty, false, false, quiet, episodeNow);
        SmokeAssert.That(published.Count == 1, "同一风险持续存在时不重复提醒");
        coordinator.Update(MonitoringSnapshot.Empty, false, false, quiet, Array.Empty<WindowAnomaly>());
        SmokeAssert.That(published.Count == 1, "风险中断一次不发送结束提醒");
        coordinator.Update(MonitoringSnapshot.Empty, false, false, quiet, Array.Empty<WindowAnomaly>());
        SmokeAssert.That(published.Count == 2 && !published[1].Active, "风险连续消失后只发送一次结束提醒");

        var windowRisk = new PrivacyRiskSnapshot(
            PrivacyRiskKind.WindowChange, true, 0, null, "课表", "ClassIsland", "依据", 7);
        SmokeAssert.That(CameraNotificationProvider.FormatPrivacyRiskText("{进程名}|{依据}", windowRisk) == "课表|依据",
            "窗口异常提醒保留窗口标题");
        SmokeAssert.That(CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
            PrivacyRiskKind.LiveBroadcast, true, true, false),
            "校园直播显示通用提醒");

        var liveConfig = new PluginConfig
        {
            EnableScreenCaptureMonitoring = false,
            EnableRemoteControlMonitoring = false,
            EnableMicrophoneMonitoring = false,
            EnableLiveMonitoring = true,
            EnableDeviceCameraMonitoring = false,
        };
        var liveSnapshot = MonitoringScanner.BuildSnapshot(
            liveConfig,
            Array.Empty<string>(),
            Array.Empty<CapabilityUsageProbe.CapabilityUsage>(),
            new[]
            {
                ProcessInfo(80, "liveClient", @"C:\Seewo\liveClient.exe", "liveClient.exe", started),
                ProcessInfo(90, "SeewoAbility", @"C:\Seewo\SeewoAbility.exe", "SeewoAbility.exe", started),
            },
            new Dictionary<int, int> { [80] = 2 },
            Array.Empty<string>(),
            started,
            new[] { new CapabilityUsageProbe.CapabilityUsage(@"C:\Seewo\liveClient.exe", fresh, 0, false) });
        SmokeAssert.That(liveSnapshot.LiveProcesses.Count == 1 && liveSnapshot.AbilityProcesses.Count == 0,
            "监测快照：直播开启且宿主摄像头关闭时只保留 liveClient");
        SmokeAssert.That(liveSnapshot.EstablishedTcpByPid[80] == 2 &&
            liveSnapshot.CameraUsages.Single().ExecutablePath.EndsWith("liveClient.exe", StringComparison.OrdinalIgnoreCase),
            "监测快照：保留直播连接数和摄像头同意项");

        liveConfig.EnableLiveMonitoring = false;
        liveConfig.EnableDeviceCameraMonitoring = true;
        liveConfig.EnableMicrophoneMonitoring = true;
        var hostSnapshot = MonitoringScanner.BuildSnapshot(
            liveConfig,
            Array.Empty<string>(),
            new[] { new CapabilityUsageProbe.CapabilityUsage(@"C:\Seewo\SeewoAbility.exe", fresh, 0, false) },
            new[]
            {
                ProcessInfo(80, "liveClient", @"C:\Seewo\liveClient.exe", "liveClient.exe", started),
                ProcessInfo(90, "SeewoAbility", @"C:\Seewo\SeewoAbility.exe", "SeewoAbility.exe", started),
            },
            new Dictionary<int, int>(),
            Array.Empty<string>(),
            started);
        SmokeAssert.That(hostSnapshot.LiveProcesses.Count == 0 && hostSnapshot.AbilityProcesses.Single().Pid == 90,
            "监测快照：关闭直播后不保留 liveClient，并保留业务宿主");
        SmokeAssert.That(hostSnapshot.MicrophoneProcesses.Select(p => p.Pid).SequenceEqual(new[] { 90 }),
            "监测快照：麦克风候选包含语音宿主");
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
