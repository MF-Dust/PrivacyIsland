using CommunityToolkit.Mvvm.ComponentModel;
using PrivacyIsland.Config;

namespace PrivacyIsland.Notification;

public sealed class CameraNotificationSettings : ObservableRecipient
{
    public const string DefaultPrivacyRiskTextTemplate = "检测到 {风险类型}\n{进程名} (PID {PID}) 正在访问相关隐私能力";
    public const int MaxPrivacyRiskTextLength = 120;

    bool _notifyOnStart = true;
    bool _notifyOnWatching = true;
    bool _notifyOnStop = true;
    bool _notifyOnPrivacyRisk = true;
    bool _speechEnabled;
    int _overlayDurationSeconds = 5;
    string _textOnStart = "起风了";
    string _textOnWatching = "风好大";
    string _textOnStop = "风停了";
    string _privacyRiskTextTemplate = DefaultPrivacyRiskTextTemplate;
    string _colorOnStart = "#FF0000";
    string _colorOnWatching = "#FFA500";
    string _colorOnStop = "#FF69B4";
    bool _hasMigratedPluginConfig;

    public bool NotifyOnStart { get => _notifyOnStart; set => SetProperty(ref _notifyOnStart, value); }
    public bool NotifyOnWatching { get => _notifyOnWatching; set => SetProperty(ref _notifyOnWatching, value); }
    public bool NotifyOnStop { get => _notifyOnStop; set => SetProperty(ref _notifyOnStop, value); }
    public bool NotifyOnPrivacyRisk { get => _notifyOnPrivacyRisk; set => SetProperty(ref _notifyOnPrivacyRisk, value); }
    public bool SpeechEnabled { get => _speechEnabled; set => SetProperty(ref _speechEnabled, value); }
    public int OverlayDurationSeconds { get => _overlayDurationSeconds; set => SetProperty(ref _overlayDurationSeconds, value); }

    public string TextOnStart { get => _textOnStart; set => SetProperty(ref _textOnStart, value); }
    public string TextOnWatching { get => _textOnWatching; set => SetProperty(ref _textOnWatching, value); }
    public string TextOnStop { get => _textOnStop; set => SetProperty(ref _textOnStop, value); }
    public string PrivacyRiskTextTemplate { get => _privacyRiskTextTemplate; set => SetProperty(ref _privacyRiskTextTemplate, value); }

    public string ColorOnStart { get => _colorOnStart; set => SetProperty(ref _colorOnStart, value); }
    public string ColorOnWatching { get => _colorOnWatching; set => SetProperty(ref _colorOnWatching, value); }
    public string ColorOnStop { get => _colorOnStop; set => SetProperty(ref _colorOnStop, value); }

    public bool HasMigratedPluginConfig { get => _hasMigratedPluginConfig; set => SetProperty(ref _hasMigratedPluginConfig, value); }

    public void Clamp()
    {
        OverlayDurationSeconds = Math.Clamp(OverlayDurationSeconds, 1, 30);
        TextOnStart = Truncate(TextOnStart);
        TextOnWatching = Truncate(TextOnWatching);
        TextOnStop = Truncate(TextOnStop);
        PrivacyRiskTextTemplate = Truncate(
            OrDefault(PrivacyRiskTextTemplate, DefaultPrivacyRiskTextTemplate),
            MaxPrivacyRiskTextLength);
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

    static string Truncate(string? value, int maxLength = PluginConfig.MaxTextLength)
    {
        value ??= "";
        return value.Length > maxLength
            ? value.Substring(0, maxLength)
            : value;
    }
}
