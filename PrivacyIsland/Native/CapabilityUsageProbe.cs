using Microsoft.Win32;

namespace PrivacyIsland.Native;

/// <summary>读取 Windows ConsentStore 中非打包桌面程序的能力使用状态。</summary>
internal sealed class CapabilityUsageProbe
{
    const string BaseSubPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public readonly record struct CapabilityUsage(
        string ExecutablePath,
        long LastUsedStart,
        long LastUsedStop,
        bool MachineScope)
    {
        public bool InUse => LastUsedStop == 0 && LastUsedStart != 0;
    }

    public delegate IEnumerable<CapabilityUsage> ConsentStoreReader();

    readonly string _capability;
    readonly ConsentStoreReader _read;

    public CapabilityUsageProbe(string capability, ConsentStoreReader? reader = null)
    {
        _capability = capability;
        _read = reader ?? ReadFromRegistry;
    }

    public bool IsInUseBy(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        try
        {
            return _read().Any(u => u.InUse &&
                string.Equals(u.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public IReadOnlyList<string> InUseApps()
    {
        try
        {
            return _read().Where(u => u.InUse)
                .Select(u => u.ExecutablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    IEnumerable<CapabilityUsage> ReadFromRegistry()
    {
        string subPath = $@"{BaseSubPath}\{_capability}\NonPackaged";
        foreach (var (hive, machine) in new[] { (RegistryHive.CurrentUser, false), (RegistryHive.LocalMachine, true) })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(subPath);
            if (root is null) continue;

            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key is null) continue;
                yield return new CapabilityUsage(
                    name.Replace('#', '\\'),
                    ToLong(key.GetValue("LastUsedTimeStart")),
                    ToLong(key.GetValue("LastUsedTimeStop")),
                    machine);
            }
        }
    }

    static long ToLong(object? value) => value is long result ? result : 0L;
}
