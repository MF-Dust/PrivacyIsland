using Microsoft.Win32;

namespace PrivacyIsland.Native;

/// <summary>
/// OS 级摄像头占用探测：读 CapabilityAccessManager ConsentStore\webcam\NonPackaged，
/// 判断某 exe 当前是否正在使用摄像头。作为注入 hook 通道的独立印证，不作唯一依据。
/// 反汇编确认：media_framework_device.dll 同时具备两条采集后端——
///   · Media Foundation（MFEnumDeviceSources + MFCreateSourceReaderFromMediaSource）→ 经帧服务器，登记 ConsentStore；
///   · DirectShow（CLSID_FilterGraph / CLSID_SystemDeviceEnum / CLSID_VideoInputDeviceCategory / IID_IBaseFilter，经 CoCreateInstance）→
///     Win11 默认帧服务器模式下通常也登记，但传统 KsProxy 直连在部分配置可能不登记。
/// 故本通道对 MF 后端可靠、对 DS 后端不保证；注入 hook（同时挂 DS/MF 两套序号）才是主检测，本通道为兜底印证。
/// </summary>
internal sealed class CameraUsageProbe
{
    const string SubPath =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged";

    /// <summary>一条占用记录。in-use 判据：Stop==0 &amp;&amp; Start!=0（FILETIME QWORD）。</summary>
    public readonly record struct WebcamUsage(string ExecutablePath, long LastUsedStart, long LastUsedStop, bool MachineScope)
    {
        public bool InUse => LastUsedStop == 0 && LastUsedStart != 0;
    }

    /// <summary>ConsentStore 读取委托，供测试注入假数据（无需真摄像头/注册表）。</summary>
    public delegate IEnumerable<WebcamUsage> ConsentStoreReader();

    readonly ConsentStoreReader _read;

    public CameraUsageProbe(ConsentStoreReader? reader = null) => _read = reader ?? ReadFromRegistry;

    /// <summary>指定 exe 是否当前在用摄像头。按解码后路径大小写不敏感精确匹配；查不到→false。</summary>
    public bool IsWebcamInUseBy(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        try
        {
            foreach (var u in _read())
                if (u.InUse && string.Equals(u.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { /* 读注册表失败不致命，退化为「未探测到」 */ }
        return false;
    }

    /// <summary>诊断：当前所有在用摄像头的应用路径（去重）。</summary>
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

    static IEnumerable<WebcamUsage> ReadFromRegistry()
    {
        // HKCU（当前用户）+ HKLM（机器/桌面应用）都要看；用 Registry64 视图避开 WOW6432 重定向（宿主是 x64）。
        foreach (var (hive, machine) in new[] { (RegistryHive.CurrentUser, false), (RegistryHive.LocalMachine, true) })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(SubPath);
            if (root is null) continue;

            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k is null) continue;
                long start = ToLong(k.GetValue("LastUsedTimeStart"));
                long stop = ToLong(k.GetValue("LastUsedTimeStop"));
                // 子键名是把可执行文件全路径里的 '\' 换成 '#' 的编码，解码回来匹配。
                yield return new WebcamUsage(name.Replace('#', '\\'), start, stop, machine);
            }
        }
    }

    static long ToLong(object? v) => v is long l ? l : 0L;   // REG_QWORD 装箱为 Int64
}
