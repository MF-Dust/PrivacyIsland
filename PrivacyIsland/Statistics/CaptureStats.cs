using System.IO;
using System.Text.Json;

namespace PrivacyIsland.Statistics;

/// <summary>捕获统计，持久化到 PluginConfigFolder/statistics.json（替代原 statistics.dat）。</summary>
public sealed class CaptureStats
{
    public int TotalCaptures { get; set; }
    public int DirectShowCaptures { get; set; }
    public int MediaFoundationCaptures { get; set; }
    public DateTime? FirstCapture { get; set; }
    public DateTime? LastCapture { get; set; }
    public int TotalDelaySeconds { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    string _folder = "";
    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static string PathFor(string folder) => Path.Combine(folder, "statistics.json");

    public static CaptureStats Load(string folder)
    {
        CaptureStats stats;
        try
        {
            string p = PathFor(folder);
            stats = File.Exists(p)
                ? JsonSerializer.Deserialize<CaptureStats>(File.ReadAllText(p)) ?? new CaptureStats()
                : new CaptureStats();
        }
        catch { stats = new CaptureStats(); }
        stats._folder = folder;
        return stats;
    }

    public void RecordCapture(bool isDirectShow)
    {
        lock (Gate)
        {
            TotalCaptures++;
            if (isDirectShow) DirectShowCaptures++; else MediaFoundationCaptures++;
            FirstCapture ??= DateTime.Now;
            LastCapture = DateTime.Now;
            Save();
        }
    }

    public void AddDelay(int seconds)
    {
        lock (Gate) { TotalDelaySeconds += seconds; Save(); }
    }

    public void Reset()
    {
        lock (Gate)
        {
            TotalCaptures = DirectShowCaptures = MediaFoundationCaptures = TotalDelaySeconds = 0;
            FirstCapture = LastCapture = null;
            Save();
        }
    }

    /// <summary>给设置页展示的多行摘要。</summary>
    public string Summary()
    {
        lock (Gate)
        {
            string f = FirstCapture?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
            string l = LastCapture?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";
            return $"总捕获次数: {TotalCaptures}\n" +
                   $"DirectShow: {DirectShowCaptures}\n" +
                   $"Media Foundation: {MediaFoundationCaptures}\n" +
                   $"首次捕获: {f}\n" +
                   $"最近捕获: {l}\n" +
                   $"总延迟时间: {TotalDelaySeconds} 秒";
        }
    }

    void Save()
    {
        if (string.IsNullOrEmpty(_folder)) return;
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(PathFor(_folder), JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 写失败不致命 */ }
    }
}
