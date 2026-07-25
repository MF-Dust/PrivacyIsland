using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PrivacyIsland.Config;
using PrivacyIsland.Native;

namespace PrivacyIsland.Orchestrator;

/// <summary>后台监测一次采样的不可变结果。只包含持续监测需要的数据。</summary>
internal sealed record MonitoringSnapshot(
    TargetProcessInfo? Target,
    bool CameraOsInUse,
    IReadOnlyList<string> CameraInUseApps,
    IReadOnlyList<TargetProcessInfo> ScreenProcesses,
    IReadOnlyList<TargetProcessInfo> RemoteProcesses,
    IReadOnlyList<TargetProcessInfo> ScreenCapabilityProcesses,
    IReadOnlyList<TargetProcessInfo> MicrophoneCapabilityProcesses,
    IReadOnlyList<string> ProcessNotes,
    DateTime UpdatedUtc)
{
    public static MonitoringSnapshot Empty => new(
        null,
        false,
        Array.Empty<string>(),
        Array.Empty<TargetProcessInfo>(),
        Array.Empty<TargetProcessInfo>(),
        Array.Empty<TargetProcessInfo>(),
        Array.Empty<TargetProcessInfo>(),
        Array.Empty<string>(),
        DateTime.MinValue);
}

/// <summary>诊断页所需的低频数据；不参与每次监测采样。</summary>
internal sealed record MonitoringDiagnostics(
    string BootSummary,
    string ListeningPorts,
    string EstablishedConnections,
    DateTime UpdatedUtc)
{
    public static MonitoringDiagnostics Empty => new("未检测", "未检测", "未检测", DateTime.MinValue);
}

/// <summary>
/// 读取 Windows 能力状态和目标进程，并生成监测快照。
/// 诊断网络/配置读取单独暴露，避免设置页或每次轮询重复触发慢 I/O。
/// </summary>
internal sealed class MonitoringScanner
{
    const string TargetProcessName = "media_capture";

    readonly CapabilityUsageProbe _cameraProbe;
    readonly CapabilityUsageProbe _microphoneProbe;
    readonly CapabilityUsageProbe _screenProbe;

    public MonitoringScanner()
        : this(new CapabilityUsageProbe("webcam"),
               new CapabilityUsageProbe("microphone"),
               new CapabilityUsageProbe("graphicsCaptureWithoutBorder"))
    {
    }

    internal MonitoringScanner(
        CapabilityUsageProbe cameraProbe,
        CapabilityUsageProbe microphoneProbe,
        CapabilityUsageProbe screenProbe)
    {
        _cameraProbe = cameraProbe;
        _microphoneProbe = microphoneProbe;
        _screenProbe = screenProbe;
    }

    public MonitoringSnapshot Scan(PluginConfig config)
    {
        var cameraApps = _cameraProbe.InUseApps();
        var screenApps = config.EnableScreenCaptureMonitoring
            ? _screenProbe.InUseApps()
            : Array.Empty<string>();
        var microphoneApps = config.EnableMicrophoneMonitoring
            ? _microphoneProbe.InUseApps()
            : Array.Empty<string>();

        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TargetProcessName };
        if (config.EnableScreenCaptureMonitoring) candidateNames.Add("screenCapture");
        if (config.EnableRemoteControlMonitoring) candidateNames.Add("rtcRemoteDesktop");
        AddCapabilityProcessNames(candidateNames, screenApps);
        AddCapabilityProcessNames(candidateNames, microphoneApps);

        var candidates = new List<TargetProcessInfo>();
        var notes = new List<string>();

        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                string processName;
                try { processName = process.ProcessName; }
                catch { continue; }
                if (!candidateNames.Contains(processName)) continue;

                TargetProcessInfo info;
                try { info = TargetProcessInfo.FromProcess(process); }
                catch
                {
                    notes.Add($"{processName}.exe(pid={process.Id}) 无法读取元数据");
                    continue;
                }

                candidates.Add(info);
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return BuildSnapshot(
            config,
            cameraApps,
            screenApps,
            microphoneApps,
            candidates,
            notes,
            DateTime.UtcNow);
    }

    internal static MonitoringSnapshot BuildSnapshot(
        PluginConfig config,
        IEnumerable<string> cameraApps,
        IEnumerable<string> screenApps,
        IEnumerable<string> microphoneApps,
        IEnumerable<TargetProcessInfo> processes,
        IEnumerable<string> notes,
        DateTime updatedUtc)
    {
        var processList = processes.ToArray();
        var cameraAppList = cameraApps.ToArray();
        var cameraPaths = cameraAppList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var screenPaths = screenApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var microphonePaths = microphoneApps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var target = processList
            .Where(c => c.ProcessName.Equals(TargetProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(c => c.IsExpectedSeewoMediaCapture)
            .OrderBy(c => c.Pid)
            .FirstOrDefault();
        bool cameraOsInUse = target?.ExecutablePath is { Length: > 0 } path &&
            cameraPaths.Contains(path);

        return new MonitoringSnapshot(
            target,
            cameraOsInUse,
            cameraAppList,
            config.EnableScreenCaptureMonitoring
                ? processList.Where(p => p.ProcessName.Equals("screenCapture", StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<TargetProcessInfo>(),
            config.EnableRemoteControlMonitoring
                ? processList.Where(p => p.ProcessName.Equals("rtcRemoteDesktop", StringComparison.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<TargetProcessInfo>(),
            config.EnableScreenCaptureMonitoring
                ? processList.Where(p => screenPaths.Contains(p.ExecutablePath)).ToArray()
                : Array.Empty<TargetProcessInfo>(),
            config.EnableMicrophoneMonitoring
                ? processList.Where(p => microphonePaths.Contains(p.ExecutablePath)).ToArray()
                : Array.Empty<TargetProcessInfo>(),
            notes.ToArray(),
            updatedUtc);
    }

    public MonitoringDiagnostics ScanDiagnostics(TargetProcessInfo? target)
    {
        if (target is null)
            return MonitoringDiagnostics.Empty with { UpdatedUtc = DateTime.UtcNow };

        return new MonitoringDiagnostics(
            DescribeBootConfig(target.ExecutablePath),
            DescribeListeningPorts(target.Pid),
            DescribeEstablished(target.Pid),
            DateTime.UtcNow);
    }

    static void AddCapabilityProcessNames(ISet<string> names, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            catch { }
        }
    }

    static string DescribeEstablished(int pid)
    {
        if (pid <= 0) return "未检测";
        try { return TcpTable.CountEstablished(pid).ToString(); }
        catch (Exception ex) { return "读取失败：" + ex.Message; }
    }

    static string DescribeListeningPorts(int pid)
    {
        try
        {
            var ports = TcpTable.GetListeningPorts(pid);
            return ports.Count == 0 ? "未发现（RPC/HTTP 可能尚未初始化）" : string.Join(", ", ports);
        }
        catch (Exception ex)
        {
            return "读取失败：" + ex.Message;
        }
    }

    static string DescribeBootConfig(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return "未知（无法读取目标路径）";
        try
        {
            string? dir = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(dir)) return "未知（目标路径无目录）";
            string path = Path.Combine(dir, "BootConfig.json");
            if (!File.Exists(path)) return "未找到";

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("default", out var root)) return "已找到（无 default 节）";
            string launcher = root.TryGetProperty("launcher", out var launcherElement) ? launcherElement.GetString() ?? "" : "";
            string needGuard = root.TryGetProperty("needGuard", out var needGuardElement) && needGuardElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (needGuardElement.GetBoolean() ? "true" : "false")
                : "未知";
            string order = root.TryGetProperty("order", out var orderElement) ? orderElement.ToString() : "未知";
            return $"launcher={launcher}, needGuard={needGuard}, order={order}";
        }
        catch (Exception ex)
        {
            return "读取失败：" + ex.Message;
        }
    }
}

internal sealed record TargetProcessInfo(
    int Pid,
    string ProcessName,
    string ExecutablePath,
    string FileVersion,
    string ProductVersion,
    string Description,
    string Product,
    string OriginalFilename,
    DateTime? StartTimeUtc,
    bool IsSignedBySeewo,
    bool Is32Bit,
    string? Machine)
{
    public bool IsExpectedSeewoMediaCapture =>
        ProcessName.Equals("media_capture", StringComparison.OrdinalIgnoreCase) &&
        OriginalFilename.Equals("media_capture.exe", StringComparison.OrdinalIgnoreCase) &&
        Product.Contains("希沃", StringComparison.OrdinalIgnoreCase) &&
        IsSignedBySeewo;

    public bool IsLikelySeewo =>
        IsExpectedSeewoMediaCapture ||
        IsSignedBySeewo && (Product.Contains("希沃", StringComparison.OrdinalIgnoreCase) ||
                            Description.Contains("媒体采集", StringComparison.OrdinalIgnoreCase));

    public string DisplayName
    {
        get
        {
            string arch = Machine ?? (Is32Bit ? "x86" : "x64/未知");
            if (!string.IsNullOrWhiteSpace(FileVersion)) arch += ", v" + FileVersion;
            return arch;
        }
    }

    public static TargetProcessInfo FromProcess(Process process)
    {
        string path = "";
        try { path = process.MainModule?.FileName ?? ""; }
        catch { }

        FileVersionInfo? version = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { version = FileVersionInfo.GetVersionInfo(path); }
            catch { }
        }

        string? machine = TryReadPeMachine(path);
        DateTime? startTimeUtc = null;
        try { startTimeUtc = process.StartTime.ToUniversalTime(); }
        catch { }
        return new TargetProcessInfo(
            process.Id,
            process.ProcessName,
            path,
            version?.FileVersion ?? "",
            version?.ProductVersion ?? "",
            version?.FileDescription ?? "",
            version?.ProductName ?? "",
            version?.OriginalFilename ?? "",
            startTimeUtc,
            SeewoSignatureVerifier.IsSignedBySeewo(path),
            string.Equals(machine, "x86", StringComparison.OrdinalIgnoreCase),
            machine);
    }

    static string? TryReadPeMachine(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return null;
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 6) return null;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null;
            ushort machine = br.ReadUInt16();
            return machine switch
            {
                0x014C => "x86",
                0x8664 => "x64",
                0x01C4 => "ARM",
                0xAA64 => "ARM64",
                _ => "0x" + machine.ToString("X4"),
            };
        }
        catch { return null; }
    }
}
