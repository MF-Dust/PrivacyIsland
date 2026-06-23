using System.IO;
using System.Text.Json;

namespace PrivacyIsland.Config;

/// <summary>插件配置，持久化到 PluginConfigFolder/config.json（替代原 NoMoreMonitor.ini）。</summary>
public sealed class PluginConfig
{
    public int MinDelaySeconds { get; set; } = 3;
    public int MaxDelaySeconds { get; set; } = 8;
    public bool StealthMode { get; set; } = false;

    // 提醒设置
    public bool NotifyOnStart { get; set; } = true;        // 摄像头启动（延迟开始）时提醒
    public bool NotifyOnWatching { get; set; } = true;     // 延迟结束、进入监视时提醒
    public bool NotifyOnStop { get; set; } = true;         // 摄像头关闭时提醒
    public bool SpeechEnabled { get; set; } = false;       // 通知是否语音播报
    public int OverlayDurationSeconds { get; set; } = 5;   // 通知正文显示时长（秒）

    // 提醒文案（默认与源程序一致）
    public string TextOnStart { get; set; } = "起风了";
    public string TextOnWatching { get; set; } = "风好大";
    public string TextOnStop { get; set; } = "风停了";

    // 提醒颜色（hex，默认与原写死值一致：红/橙/粉）。provider 容错解析，非法值回退默认。
    public string ColorOnStart { get; set; } = "#FF0000";
    public string ColorOnWatching { get; set; } = "#FFA500";
    public string ColorOnStop { get; set; } = "#FF69B4";

    // 课程感知联动（接 ILessonsService）。默认全关，保持现有行为。
    public bool LessonAwareEnabled { get; set; } = false;        // 总开关
    public bool PauseDuringClass { get; set; } = false;          // 上课时自动暂停防护
    public bool StrongerDelayDuringClass { get; set; } = false;  // 上课时改用加强延迟（未暂停时生效）
    public int ClassMinDelaySeconds { get; set; } = 10;          // 上课加强延迟下限
    public int ClassMaxDelaySeconds { get; set; } = 20;          // 上课加强延迟上限

    /// <summary>文案最大长度，超出截断，防止溢出 overlay。</summary>
    public const int MaxTextLength = 50;

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static string PathFor(string folder) => System.IO.Path.Combine(folder, "config.json");

    public static PluginConfig Load(string folder)
    {
        try
        {
            string p = PathFor(folder);
            if (File.Exists(p))
            {
                var cfg = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(p));
                if (cfg != null) { cfg.Clamp(); return cfg; }
            }
        }
        catch { /* 损坏就用默认 */ }
        var def = new PluginConfig();
        def.Save(folder);
        return def;
    }

    public void Save(string folder)
    {
        Clamp();
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(PathFor(folder), JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* 写失败不致命 */ }
    }

    /// <summary>校验：与原版一致，延迟限 1..30，且 max>=min。</summary>
    public void Clamp()
    {
        if (MinDelaySeconds < 1) MinDelaySeconds = 1;
        if (MinDelaySeconds > 30) MinDelaySeconds = 30;
        if (MaxDelaySeconds < 1) MaxDelaySeconds = 1;
        if (MaxDelaySeconds > 30) MaxDelaySeconds = 30;
        if (MaxDelaySeconds < MinDelaySeconds) MaxDelaySeconds = MinDelaySeconds;
        if (OverlayDurationSeconds < 1) OverlayDurationSeconds = 1;
        if (OverlayDurationSeconds > 30) OverlayDurationSeconds = 30;

        // 上课加强延迟：同样限 1..30 且 max>=min。
        if (ClassMinDelaySeconds < 1) ClassMinDelaySeconds = 1;
        if (ClassMinDelaySeconds > 30) ClassMinDelaySeconds = 30;
        if (ClassMaxDelaySeconds < 1) ClassMaxDelaySeconds = 1;
        if (ClassMaxDelaySeconds > 30) ClassMaxDelaySeconds = 30;
        if (ClassMaxDelaySeconds < ClassMinDelaySeconds) ClassMaxDelaySeconds = ClassMinDelaySeconds;

        // 文案截断，防止溢出 overlay。
        TextOnStart = Truncate(TextOnStart);
        TextOnWatching = Truncate(TextOnWatching);
        TextOnStop = Truncate(TextOnStop);
    }

    static string Truncate(string? s)
    {
        s ??= "";
        return s.Length > MaxTextLength ? s.Substring(0, MaxTextLength) : s;
    }
}
