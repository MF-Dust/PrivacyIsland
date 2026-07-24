using System.Diagnostics;
using System.IO;

namespace PrivacyIsland.Orchestrator;

internal sealed record HookOperationResult(bool Success, int Code);

/// <summary>只负责调用原生注入器；重试、自愈和目标身份由 CaptureMonitor 编排。</summary>
internal sealed class HookInjector
{
    const string DllFileName = "PrivacyIslandHook.dll";
    const string InjectorFileName = "nmm_injector.exe";

    public string DllPath { get; }
    public string InjectorPath { get; }

    public HookInjector(string assemblyDirectory)
    {
        DllPath = Path.Combine(assemblyDirectory, DllFileName);
        InjectorPath = Path.Combine(assemblyDirectory, InjectorFileName);
    }

    public HookOperationResult Inject(int pid)
    {
        if (!File.Exists(InjectorPath)) return new(false, -10);
        if (!File.Exists(DllPath)) return new(false, -11);
        return Run($"--inject {pid} \"{DllPath}\"");
    }

    public HookOperationResult Eject(int pid)
    {
        if (!File.Exists(InjectorPath)) return new(false, -10);
        if (!File.Exists(DllPath)) return new(false, -11);
        return Run($"--eject {pid} \"{DllPath}\"");
    }

    HookOperationResult Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(InjectorPath, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return new(false, -1);
            return process.WaitForExit(8000)
                ? new(process.ExitCode == 0, process.ExitCode)
                : new(false, -2);
        }
        catch
        {
            return new(false, -3);
        }
    }
}
