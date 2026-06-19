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

    void DllWrite(string msg, int state)
    {
        Assert(mutex.WaitOne(2000), "DLL 拿到互斥锁");
        try
        {
            byte[] wide = Encoding.Unicode.GetBytes(msg + "\0");
            var buf = new byte[2048];
            Array.Copy(wide, buf, Math.Min(wide.Length, buf.Length));
            view.WriteArray(OffLogBuffer, buf, 0, buf.Length);
            view.Write(OffCurrState, state);
        }
        finally { mutex.ReleaseMutex(); }
        evt.Set();
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

    // 4) Simulate（应用内功能测试引擎）：应走真实读线程路径触发 StateReceived + CameraActive
    bridge.Simulate(StatusStart, "（模拟）DS capture start!");
    var s3 = WaitOne();
    Assert(s3.State == StatusStart, "Simulate 触发 start 帧");
    Assert(s3.Message == "（模拟）DS capture start!", $"Simulate 解码 message 正确 (得到: '{s3.Message}')");
    Assert(bridge.CameraActive, "Simulate start 后 CameraActive==true");
    bridge.Simulate(StatusStop, "（模拟）stop");
    var s4 = WaitOne();
    Assert(s4.State == StatusStop && !bridge.CameraActive, "Simulate stop 后 CameraActive==false");
}

Console.WriteLine("PASS");
return;

// ---------- live inject ----------
void RunLive()
{
    string baseDir = AppContext.BaseDirectory;
    string dll = Find(baseDir, "NoMoreMonitor_Dll.dll",
        @"..\..\..\..\NoMoreMonitor_Dll\Release\NoMoreMonitor_Dll.dll");
    string injector = Find(baseDir, "nmm_injector.exe",
        @"..\..\..\..\NoMoreMonitor_Injector\Release\nmm_injector.exe");
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

        Process.Start(injector, $"--eject {target.Id} \"NoMoreMonitor_Dll.dll\"")!.WaitForExit(5000);
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
