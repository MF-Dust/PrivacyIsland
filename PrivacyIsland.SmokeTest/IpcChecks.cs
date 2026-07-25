using System.IO.MemoryMappedFiles;
using System.Text;
using PrivacyIsland.Ipc;

internal static class IpcChecks
{
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

    public static void Run()
    {
        using var bridge = new SharedMemoryBridge();
        var received = new List<CaptureSnapshot>();
        using var got = new AutoResetEvent(false);
        bridge.StateReceived += snapshot =>
        {
            lock (received) received.Add(snapshot);
            got.Set();
        };

        bridge.Start(minDelay: 3, maxDelay: 8, stealth: false);
        Console.WriteLine("bridge started (created Local\\LilithSharedMem + Mutex + Event)");

        using var mmf = MemoryMappedFile.OpenExisting(@"Local\LilithSharedMem", MemoryMappedFileRights.ReadWrite);
        using var view = mmf.CreateViewAccessor(0, Size);
        using var mutex = new Mutex(false, @"Local\LilithMutex");
        using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LilithLogEvent");

        void DllWrite(string message, int state, uint heartbeat = 0, uint captureCount = 0)
        {
            SmokeAssert.That(mutex.WaitOne(2000), "DLL 拿到互斥锁");
            try
            {
                byte[] wide = Encoding.Unicode.GetBytes(message + "\0");
                var buffer = new byte[2048];
                Array.Copy(wide, buffer, Math.Min(wide.Length, buffer.Length));
                view.WriteArray(OffLogBuffer, buffer, 0, buffer.Length);
                view.Write(OffCurrState, state);
                view.Write(OffHeartbeat, heartbeat);
                view.Write(OffCaptureCount, captureCount);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
            evt.Set();
        }

        void DllSetStateNoSignal(int state)
        {
            SmokeAssert.That(mutex.WaitOne(2000), "DLL 拿到互斥锁(无信号)");
            try { view.Write(OffCurrState, state); }
            finally { mutex.ReleaseMutex(); }
        }

        CaptureSnapshot WaitForSnapshot()
        {
            SmokeAssert.That(got.WaitOne(3000), "桥在 3s 内收到一帧");
            lock (received) return received[^1];
        }

        DllWrite("DS capture start!", StatusStart);
        var start = WaitForSnapshot();
        SmokeAssert.That(start.State == StatusStart, "解码 state == start");
        SmokeAssert.That(start.Message == "DS capture start!", $"解码 message 正确 (得到: '{start.Message}')");
        SmokeAssert.That(bridge.CameraActive, "start 后 CameraActive == true");

        DllWrite("we are safe now", StatusStop);
        var stop = WaitForSnapshot();
        SmokeAssert.That(stop.State == StatusStop, "解码 state == stop");
        SmokeAssert.That(!bridge.CameraActive, "stop 后 CameraActive == false");

        bridge.WriteConfig(minDelay: 5, maxDelay: 9, paused: false, stealth: true);
        SmokeAssert.That(view.ReadInt32(OffMinDelay) == 5, "WriteConfig 写入 min=5");
        SmokeAssert.That(view.ReadInt32(OffMaxDelay) == 9, "WriteConfig 写入 max=9");
        SmokeAssert.That(view.ReadInt32(OffStealth) == 1, "WriteConfig 写入 stealth=1");

        bridge.WriteConfig(minDelay: 5, maxDelay: 9, paused: true, stealth: true);
        SmokeAssert.That(view.ReadInt32(OffPaused) == 1, "WriteConfig 写入 paused=1");
        bridge.SetPaused(false);
        SmokeAssert.That(view.ReadInt32(OffPaused) == 0, "SetPaused(false) 写入 paused=0");

        bridge.Simulate(StatusStart, "（模拟）DS capture start!");
        var simulatedStart = WaitForSnapshot();
        SmokeAssert.That(simulatedStart.State == StatusStart, "Simulate 触发 start 帧");
        SmokeAssert.That(simulatedStart.Message == "（模拟）DS capture start!",
            $"Simulate 解码 message 正确 (得到: '{simulatedStart.Message}')");
        SmokeAssert.That(bridge.CameraActive, "Simulate start 后 CameraActive==true");
        bridge.Simulate(StatusStop, "（模拟）stop");
        var simulatedStop = WaitForSnapshot();
        SmokeAssert.That(simulatedStop.State == StatusStop && !bridge.CameraActive,
            "Simulate stop 后 CameraActive==false");

        DllWrite("DS capture start!", StatusStart, heartbeat: 7, captureCount: 3);
        var liveness = WaitForSnapshot();
        SmokeAssert.That(liveness.Heartbeat == 7 && liveness.CaptureCount == 3,
            $"解码 heartbeat/captureCount（得到 hb={liveness.Heartbeat}, cc={liveness.CaptureCount}）");
        SmokeAssert.That(bridge.GetLiveness().HeartbeatSupported,
            "收到非零心跳后 HeartbeatSupported==true");

        SmokeAssert.That(bridge.CameraActive, "补偿前置：当前为 active");
        DllSetStateNoSignal(StatusStop);
        SmokeAssert.That(got.WaitOne(2500), "轮询在 ~1s 内补偿遗漏的 stop（无事件也收敛）");
        SmokeAssert.That(!bridge.CameraActive, "reconcile 后 CameraActive == false");
    }
}
