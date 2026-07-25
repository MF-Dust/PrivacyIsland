using System.Diagnostics;
using PrivacyIsland.Ipc;

internal static class LiveChecks
{
    public static void Run()
    {
        string baseDir = AppContext.BaseDirectory;
        string dll = Find(baseDir, "PrivacyIslandHook.dll",
            @"..\..\..\..\PrivacyIsland\Native\PrivacyIslandHook.dll");
        string injector = Find(baseDir, "nmm_injector.exe",
            @"..\..\..\..\PrivacyIsland\Native\nmm_injector.exe");
        SmokeAssert.That(File.Exists(dll), $"找到 hook DLL: {dll}");
        SmokeAssert.That(File.Exists(injector), $"找到注入器: {injector}");

        using var bridge = new SharedMemoryBridge();
        using var got = new AutoResetEvent(false);
        var messages = new List<CaptureSnapshot>();
        bridge.StateReceived += snapshot =>
        {
            lock (messages) messages.Add(snapshot);
            Console.WriteLine($"    DLL-> state={snapshot.State} '{snapshot.Message}'");
            got.Set();
        };
        bridge.Start(3, 8, false);

        using var target = Process.Start(@"C:\Windows\SysWOW64\notepad.exe");
        SmokeAssert.That(target is not null, "启动 32 位 notepad 作为靶子");
        Thread.Sleep(800);

        try
        {
            var startInfo = new ProcessStartInfo(injector, $"--inject {target!.Id} \"{dll}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var injection = Process.Start(startInfo)!;
            injection.WaitForExit(8000);
            SmokeAssert.That(injection.ExitCode == 0, $"注入器返回 0（成功注入 pid={target.Id}）");
            SmokeAssert.That(got.WaitOne(12000), "12s 内收到 DLL 上报的至少一帧状态");
            Console.WriteLine($"  共收到 {messages.Count} 帧");

            Process.Start(injector, $"--eject {target.Id} \"PrivacyIslandHook.dll\"")!
                .WaitForExit(5000);
        }
        finally
        {
            try { target!.Kill(); }
            catch { }
        }

        Console.WriteLine("LIVE PASS");
    }

    static string Find(string baseDir, string name, string fallbackRelativePath)
    {
        string outputPath = Path.Combine(baseDir, name);
        return File.Exists(outputPath)
            ? outputPath
            : Path.GetFullPath(Path.Combine(baseDir, fallbackRelativePath));
    }
}
