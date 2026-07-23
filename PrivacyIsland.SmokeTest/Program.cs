using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using PrivacyIsland.Ipc;

// 两种自检：
//   dotnet run            -> IPC round-trip（快、确定性、无需注入/管理员）—— 提交进仓库的回归检查
//   dotnet run -- live    -> 真注入：把 hook DLL 注入 32 位 notepad，验证 注入器→DLL加载→共享内存 整条链路
//                            （需要桌面会话；同用户进程注入通常无需管理员）
// 失败抛异常（退出码非 0）；全过打印 PASS。

const int OffLogBuffer = 0;
const int OffCurrState = 2048;
const int OffMinDelay = 2056;
const int OffMaxDelay = 2060;
const int OffHeartbeat = 2064;
const int OffPaused = 2068;
const int OffCaptureCount = 2072;
const int OffStealth = 2076;
const int Size = 2080;
const int StatusStart = 1;
const int StatusStop = 3;

void Assert(bool cond, string what)
{
    if (!cond) throw new Exception("FAIL: " + what);
    Console.WriteLine("  ok: " + what);
}

if (args.Length > 0 && args[0] == "live")
{
    RunLive();
    return;
}
if (args.Length > 0 && args[0] == "privacy")
{
    RunPrivacyChecks();
    Console.WriteLine("PRIVACY PASS");
    return;
}
if (args.Length > 1 && args[0] == "signature")
{
    foreach (string path in args.Skip(1))
        Assert(PrivacyIsland.Native.SeewoSignatureVerifier.IsSignedBySeewo(path), $"希沃数字签名有效: {path}");
    Assert(!PrivacyIsland.Native.SeewoSignatureVerifier.IsSignedBySeewo(typeof(Program).Assembly.Location),
        "未签名的冒烟测试程序集被拒绝");
    Console.WriteLine("SIGNATURE PASS");
    return;
}

// ---------- IPC round-trip ----------
using (var bridge = new SharedMemoryBridge())
{
    var received = new List<CaptureSnapshot>();
    var got = new AutoResetEvent(false);
    bridge.StateReceived += s => { lock (received) received.Add(s); got.Set(); };

    bridge.Start(minDelay: 3, maxDelay: 8, stealth: false);
    Console.WriteLine("bridge started (created Local\\LilithSharedMem + Mutex + Event)");

    using var mmf = MemoryMappedFile.OpenExisting(@"Local\LilithSharedMem", MemoryMappedFileRights.ReadWrite);
    using var view = mmf.CreateViewAccessor(0, Size);
    using var mutex = new Mutex(false, @"Local\LilithMutex");
    using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LilithLogEvent");

    void DllWrite(string msg, int state, uint hb = 0, uint cc = 0)
    {
        Assert(mutex.WaitOne(2000), "DLL 拿到互斥锁");
        try
        {
            byte[] wide = Encoding.Unicode.GetBytes(msg + "\0");
            var buf = new byte[2048];
            Array.Copy(wide, buf, Math.Min(wide.Length, buf.Length));
            view.WriteArray(OffLogBuffer, buf, 0, buf.Length);
            view.Write(OffCurrState, state);
            view.Write(OffHeartbeat, hb);
            view.Write(OffCaptureCount, cc);
        }
        finally { mutex.ReleaseMutex(); }
        evt.Set();
    }

    // 只改 currState 不发信号，模拟「丢了一次 SetEvent」，用于验证读线程轮询补偿。
    void DllSetStateNoSignal(int state)
    {
        Assert(mutex.WaitOne(2000), "DLL 拿到互斥锁(无信号)");
        try { view.Write(OffCurrState, state); }
        finally { mutex.ReleaseMutex(); }
    }

    CaptureSnapshot WaitOne()
    {
        Assert(got.WaitOne(3000), "桥在 3s 内收到一帧");
        lock (received) return received[^1];
    }

    DllWrite("DS capture start!", StatusStart);
    var s1 = WaitOne();
    Assert(s1.State == StatusStart, "解码 state == start");
    Assert(s1.Message == "DS capture start!", $"解码 message 正确 (得到: '{s1.Message}')");
    Assert(bridge.CameraActive, "start 后 CameraActive == true");

    DllWrite("we are safe now", StatusStop);
    var s2 = WaitOne();
    Assert(s2.State == StatusStop, "解码 state == stop");
    Assert(!bridge.CameraActive, "stop 后 CameraActive == false");

    bridge.WriteConfig(minDelay: 5, maxDelay: 9, paused: false, stealth: true);
    Assert(view.ReadInt32(OffMinDelay) == 5, "WriteConfig 写入 min=5");
    Assert(view.ReadInt32(OffMaxDelay) == 9, "WriteConfig 写入 max=9");
    Assert(view.ReadInt32(OffStealth) == 1, "WriteConfig 写入 stealth=1");

    // 暂停标志：分层暂停模型最终经 WriteConfig/SetPaused 落到 OffPaused。
    bridge.WriteConfig(minDelay: 5, maxDelay: 9, paused: true, stealth: true);
    Assert(view.ReadInt32(OffPaused) == 1, "WriteConfig 写入 paused=1");
    bridge.SetPaused(false);
    Assert(view.ReadInt32(OffPaused) == 0, "SetPaused(false) 写入 paused=0");

    // 4) Simulate（应用内功能测试引擎）：应走真实读线程路径触发 StateReceived + CameraActive
    bridge.Simulate(StatusStart, "（模拟）DS capture start!");
    var s3 = WaitOne();
    Assert(s3.State == StatusStart, "Simulate 触发 start 帧");
    Assert(s3.Message == "（模拟）DS capture start!", $"Simulate 解码 message 正确 (得到: '{s3.Message}')");
    Assert(bridge.CameraActive, "Simulate start 后 CameraActive==true");
    bridge.Simulate(StatusStop, "（模拟）stop");
    var s4 = WaitOne();
    Assert(s4.State == StatusStop && !bridge.CameraActive, "Simulate stop 后 CameraActive==false");

    // 5) 读未用字段：heartbeat/captureCount 应被解码进快照。
    DllWrite("DS capture start!", StatusStart, hb: 7, cc: 3);
    var s5 = WaitOne();
    Assert(s5.Heartbeat == 7 && s5.CaptureCount == 3, $"解码 heartbeat/captureCount（得到 hb={s5.Heartbeat}, cc={s5.CaptureCount}）");
    Assert(bridge.GetLiveness().HeartbeatSupported, "收到非零心跳后 HeartbeatSupported==true");

    // 6) 轮询补偿：只改 currState=stop 不发信号，读线程应在 ~1s 轮询里收敛 CameraActive。
    Assert(bridge.CameraActive, "补偿前置：当前为 active");
    DllSetStateNoSignal(StatusStop);
    Assert(got.WaitOne(2500), "轮询在 ~1s 内补偿遗漏的 stop（无事件也收敛）");
    Assert(!bridge.CameraActive, "reconcile 后 CameraActive == false");
}

RunPrivacyChecks();

Console.WriteLine("PASS");
return;

void RunPrivacyChecks()
{
    const string path = @"C:\x\media_capture.exe";
    var inUse = new PrivacyIsland.Native.CapabilityUsageProbe("webcam",
        () => new[] { new PrivacyIsland.Native.CapabilityUsageProbe.CapabilityUsage(path, 1, 0, false) });
    Assert(inUse.IsInUseBy(path), "OS 探测：Stop==0 且 Start!=0 判为在用");
    Assert(inUse.IsInUseBy(path.ToUpperInvariant()), "OS 探测：路径大小写不敏感");
    Assert(!inUse.IsInUseBy(@"C:\other.exe"), "OS 探测：其他路径不误报");
    Assert(inUse.InUseApps().Contains(path), "OS 探测：InUseApps 含在用路径");

    var notInUse = new PrivacyIsland.Native.CapabilityUsageProbe("microphone",
        () => new[] { new PrivacyIsland.Native.CapabilityUsageProbe.CapabilityUsage(path, 1, 5, false) });
    Assert(!notInUse.IsInUseBy(path), "OS 探测：Stop!=0 判为未在用");
    Assert(notInUse.InUseApps().Count == 0, "OS 探测：无在用应用时 InUseApps 为空");

    const string product = "希沃管家";
    var config = new PrivacyIsland.Config.PluginConfig();
    Assert(config.PrivacyRiskResponse == PrivacyIsland.Config.PrivacyRiskResponseMode.Prompt,
        "隐私风险默认询问后处理");
    config.PrivacyRiskResponse = PrivacyIsland.Config.PrivacyRiskResponseMode.NotifyOnly;
    config.Clamp();
    Assert(config.PrivacyRiskResponse == PrivacyIsland.Config.PrivacyRiskResponseMode.NotifyOnly,
        "隐私风险可切换为仅提示");
    Assert(!PrivacyIsland.Orchestrator.CaptureMonitor.ShouldPromptPrivacyRisk(
        config.PrivacyRiskResponse, true, true, 123),
        "仅提示模式不排队确认框");
    Assert(PrivacyIsland.Orchestrator.CaptureMonitor.ShouldPromptPrivacyRisk(
        PrivacyIsland.Config.PrivacyRiskResponseMode.Prompt, true, true, 123),
        "询问模式为有效风险排队确认框");
    Assert(PrivacyIsland.Orchestrator.CaptureMonitor.ShouldTrackCameraPrivacyRisk(true, true),
        "摄像头活动且目标可信时纳入隐私风险");
    Assert(!PrivacyIsland.Orchestrator.CaptureMonitor.ShouldTrackCameraPrivacyRisk(true, false),
        "摄像头目标未通过校验时不纳入隐私风险");
    Assert(PrivacyIsland.Orchestrator.CaptureMonitor.IsExpectedPrivacyTarget(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.Camera, "media_capture", product, "media_capture.exe", true),
        "隐私目标：接受有效签名的 media_capture 摄像头组件");
    Assert(!PrivacyIsland.Orchestrator.CaptureMonitor.IsExpectedPrivacyTarget(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.Camera, "media_capture", product, "wrong.exe", true),
        "隐私目标：拒绝原始文件名错误的摄像头组件");
    Assert(!PrivacyIsland.Orchestrator.CaptureMonitor.IsExpectedPrivacyTarget(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.Camera, "media_capture", product, "media_capture.exe", false),
        "隐私目标：拒绝未签名的摄像头组件");
    Assert(!PrivacyIsland.Notification.CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.Camera, true, true),
        "摄像头不重复显示通用隐私通知");
    Assert(PrivacyIsland.Notification.CameraNotificationProvider.ShouldShowGenericPrivacyNotification(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.ScreenCapture, true, true),
        "其他隐私风险继续显示通用通知");
    var risk = new PrivacyIsland.Orchestrator.PrivacyRiskSnapshot(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.ScreenCapture,
        true, 123, null, "screenCapture", path, "测试");
    Assert(PrivacyIsland.Notification.CameraNotificationProvider.FormatPrivacyRiskText(
        "{风险类型}|{进程名}|{PID}", risk) == "屏幕采集|screenCapture.exe|123",
        "隐私提醒模板替换风险类型、进程名和 PID");
    Assert(PrivacyIsland.Notification.CameraNotificationProvider.FormatPrivacyRiskText("", risk) ==
        "检测到 屏幕采集\nscreenCapture.exe (PID 123) 正在访问相关隐私能力",
        "空隐私提醒模板回退默认文案");
    Assert(PrivacyIsland.Orchestrator.CaptureMonitor.IsExpectedPrivacyTarget(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.ScreenCapture, "screenCapture", product, "screenCapture.exe", true),
        "隐私目标：接受有效签名的 screenCapture");
    Assert(PrivacyIsland.Orchestrator.CaptureMonitor.IsExpectedPrivacyTarget(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.RemoteControl, "rtcRemoteDesktop", product, "rtcRemoteDesktop.exe", true),
        "隐私目标：接受有效签名的远控组件");
    Assert(!PrivacyIsland.Orchestrator.CaptureMonitor.IsExpectedPrivacyTarget(
        PrivacyIsland.Orchestrator.PrivacyRiskKind.ScreenCapture, "screenCapture", product, "screenCapture.exe", false),
        "隐私目标：拒绝未签名的同名组件");
}

// ---------- live inject ----------
void RunLive()
{
    string baseDir = AppContext.BaseDirectory;
    string dll = Find(baseDir, "PrivacyIslandHook.dll",
        @"..\..\..\..\PrivacyIsland\Native\PrivacyIslandHook.dll");
    string injector = Find(baseDir, "nmm_injector.exe",
        @"..\..\..\..\PrivacyIsland\Native\nmm_injector.exe");
    Assert(File.Exists(dll), $"找到 hook DLL: {dll}");
    Assert(File.Exists(injector), $"找到注入器: {injector}");

    using var bridge = new SharedMemoryBridge();
    var got = new AutoResetEvent(false);
    var msgs = new List<CaptureSnapshot>();
    bridge.StateReceived += s => { lock (msgs) msgs.Add(s); Console.WriteLine($"    DLL-> state={s.State} '{s.Message}'"); got.Set(); };
    bridge.Start(3, 8, false);

    var target = Process.Start(@"C:\Windows\SysWOW64\notepad.exe");
    Assert(target != null, "启动 32 位 notepad 作为靶子");
    Thread.Sleep(800); // 等进程初始化

    try
    {
        var psi = new ProcessStartInfo(injector, $"--inject {target!.Id} \"{dll}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using var inj = Process.Start(psi)!;
        inj.WaitForExit(8000);
        Assert(inj.ExitCode == 0, $"注入器返回 0（成功注入 pid={target.Id}）");

        // DLL 加载后会 OpenFileMapping 并上报状态（找不到 media_framework_device.dll 时最终报 error）。
        // 收到任意一帧即证明：注入成功 + DLL 运行 + IPC 通。
        Assert(got.WaitOne(12000), "12s 内收到 DLL 上报的至少一帧状态");
        Console.WriteLine($"  共收到 {msgs.Count} 帧");

        Process.Start(injector, $"--eject {target.Id} \"PrivacyIslandHook.dll\"")!.WaitForExit(5000);
    }
    finally
    {
        try { target!.Kill(); } catch { }
    }
    Console.WriteLine("LIVE PASS");
}

static string Find(string baseDir, string name, string fallbackRel)
{
    string a = Path.Combine(baseDir, name);
    if (File.Exists(a)) return a;
    return Path.GetFullPath(Path.Combine(baseDir, fallbackRel));
}
