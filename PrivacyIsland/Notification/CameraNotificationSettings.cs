using PrivacyIsland.Config;

namespace PrivacyIsland.Notification;

public sealed class CameraNotificationSettings
{
    public bool NotifyOnStart { get; set; } = true;
    public bool NotifyOnWatching { get; set; } = true;
    public bool NotifyOnStop { get; set; } = true;
    public bool SpeechEnabled { get; set; } = false;
    public int OverlayDurationSeconds { get; set; } = 5;

    public string TextOnStart { get; set; } = "起风了";
    public string TextOnWatching { get; set; } = "风好大";
    public string TextOnStop { get; set; } = "风停了";

    public string ColorOnStart { get; set; } = "#FF0000";
    public string ColorOnWatching { get; set; } = "#FFA500";
    public string ColorOnStop { get; set; } = "#FF69B4";

    public bool HasMigratedPluginConfig { get; set; } = false;

    public void Clamp()
    {
        OverlayDurationSeconds = Math.Clamp(OverlayDurationSeconds, 1, 30);
        TextOnStart = Truncate(TextOnStart);
        TextOnWatching = Truncate(TextOnWatching);
        TextOnStop = Truncate(TextOnStop);
        ColorOnStart = OrDefault(ColorOnStart, "#FF0000");
        ColorOnWatching = OrDefault(ColorOnWatching, "#FFA500");
        ColorOnStop = OrDefault(ColorOnStop, "#FF69B4");
    }

    public void ApplyLegacyConfig(PluginConfig cfg)
    {
        NotifyOnStart = cfg.NotifyOnStart;
        NotifyOnWatching = cfg.NotifyOnWatching;
        NotifyOnStop = cfg.NotifyOnStop;
        SpeechEnabled = cfg.SpeechEnabled;
        OverlayDurationSeconds = cfg.OverlayDurationSeconds;
        TextOnStart = cfg.TextOnStart;
        TextOnWatching = cfg.TextOnWatching;
        TextOnStop = cfg.TextOnStop;
        ColorOnStart = cfg.ColorOnStart;
        ColorOnWatching = cfg.ColorOnWatching;
        ColorOnStop = cfg.ColorOnStop;
        HasMigratedPluginConfig = true;
        Clamp();
    }

    static string OrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    static string Truncate(string? value)
    {
        value ??= "";
        return value.Length > PluginConfig.MaxTextLength
            ? value.Substring(0, PluginConfig.MaxTextLength)
            : value;
    }
}
