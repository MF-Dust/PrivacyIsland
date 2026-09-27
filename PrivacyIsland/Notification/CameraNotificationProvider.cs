using Avalonia.Media;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Logging;
using PrivacyIsland.Config;
using PrivacyIsland.Ipc;
using PrivacyIsland.Logging;
using PrivacyIsland.Orchestrator;

namespace PrivacyIsland.Notification;

/// <summary>
/// 摄像头与隐私风险提醒 provider（替代原生全屏覆盖层）。
/// </summary>
[NotificationProviderInfo("b1e7c0a2-3d4f-4a6b-9c1d-2e3f4a5b6c7d", "隐私防护", Icons.ShieldCheckmarkFilled, "希沃摄像头、屏幕、远控、麦克风、直播和窗口异常提醒")]
[NotificationChannelInfo(ChannelId, "隐私事件", Icons.ShieldErrorFilled, "摄像头与隐私风险事件提醒")]
public class CameraNotificationProvider : NotificationProviderBase<CameraNotificationSettings>
{
    const string ChannelId = "c2f8d1b3-4e5a-4b7c-8d2e-3f4a5b6c7d8e";

    readonly ILogger<CameraNotificationProvider>? _logger;
    readonly HashSet<(PrivacyRiskKind Kind, int Pid, long SubjectId)> _openNotices = new();
    int? _lastNotifiedCameraState;

    public CameraNotificationProvider()
    {
        Subscribe();
    }

    public CameraNotificationProvider(ILogger<CameraNotificationProvider> logger)
    {
        _logger = logger;
        Subscribe();
    }

    void Subscribe()
    {
        PrivacyIslandRuntime.StateReceived += OnState;
        PrivacyIslandRuntime.PrivacyRiskReceived += OnPrivacyRisk;
        LogInformation("[提醒] provider 已构造并订阅状态");
    }

    void OnState(CaptureSnapshot s)
    {
        var cfg = Settings;
        TryMigrateLegacyConfig(cfg, PrivacyIslandRuntime.Config);
        cfg.Clamp();

        string text;
        Color color;
        bool enabled;
        switch (s.State)
        {
            case IpcProtocol.StatusStart:
                text = OrDefault(cfg.TextOnStart, "起风了");
                color = ParseColor(cfg.ColorOnStart, Color.FromRgb(255, 0, 0));
                enabled = cfg.NotifyOnStart;
                break;
            case IpcProtocol.StatusWatching:
                text = OrDefault(cfg.TextOnWatching, "风好大");
                color = ParseColor(cfg.ColorOnWatching, Color.FromRgb(255, 165, 0));
                enabled = cfg.NotifyOnWatching;
                break;
            case IpcProtocol.StatusStop:
                text = OrDefault(cfg.TextOnStop, "风停了");
                color = ParseColor(cfg.ColorOnStop, Color.FromRgb(255, 105, 180));
                enabled = cfg.NotifyOnStop;
                break;
            default:
                return;
        }

        if (!enabled) return;
        if (_lastNotifiedCameraState == s.State) return;
        _lastNotifiedCameraState = s.State;

        ShowNotice(text, color, cfg, "[提醒] 已显示：" + text, "[提醒] 显示失败：");
    }

    void OnPrivacyRisk(PrivacyRiskSnapshot risk)
    {
        var cfg = Settings;
        if (!ShouldShowGenericPrivacyNotification(
                risk.Kind, risk.Active, cfg.NotifyOnPrivacyRisk, cfg.NotifyOnPrivacyRiskEnded))
            return;
        var noticeId = (risk.Kind, risk.ProcessId, risk.SubjectId);
        if (risk.Active)
        {
            if (!_openNotices.Add(noticeId)) return;
        }
        else if (!_openNotices.Remove(noticeId))
        {
            return;
        }
        cfg.Clamp();
        string template = risk.Active ? cfg.PrivacyRiskTextTemplate : cfg.PrivacyRiskEndedTextTemplate;
        string text = FormatPrivacyRiskText(template, risk);
        var color = risk.Active
            ? ParseColor(cfg.ColorOnStart, Color.FromRgb(255, 0, 0))
            : ParseColor(cfg.ColorOnStop, Color.FromRgb(255, 105, 180));
        ShowNotice(text, color, cfg, "[提醒] 已显示隐私风险：" + text.Replace('\n', ' '), "[提醒] 隐私风险显示失败：");
    }

    void ShowNotice(string text, Color color, CameraNotificationSettings cfg, string successLog, string failureLog)
    {
        try
        {
            Channel(ChannelId).ShowNotification(new NotificationRequest
            {
                MaskContent = NotificationContent.CreateSimpleTextContent(text, c =>
                {
                    c.Color = new SolidColorBrush(color);
                    c.Duration = TimeSpan.FromSeconds(cfg.OverlayDurationSeconds);
                    c.IsSpeechEnabled = cfg.SpeechEnabled;
                    c.SpeechContent = text;
                })
            });
            LogInformation(successLog);
        }
        catch (Exception ex)
        {
            LogError(failureLog + ex.Message);
        }
    }

    static string RiskName(PrivacyRiskKind kind) => kind switch
    {
        PrivacyRiskKind.Camera => "摄像头访问",
        PrivacyRiskKind.ScreenCapture => "屏幕采集",
        PrivacyRiskKind.RemoteControl => "远程控制",
        PrivacyRiskKind.Microphone => "麦克风访问",
        PrivacyRiskKind.LiveBroadcast => "校园直播",
        PrivacyRiskKind.DeviceCamera => "宿主摄像头",
        PrivacyRiskKind.WindowChange => "窗口异常",
        _ => "未知风险",
    };

    internal static bool ShouldShowGenericPrivacyNotification(
        PrivacyRiskKind kind,
        bool active,
        bool notifyOnStart,
        bool notifyOnEnd)
        => kind != PrivacyRiskKind.Camera && (active ? notifyOnStart : notifyOnEnd);

    internal static string FormatPrivacyRiskText(string? template, PrivacyRiskSnapshot risk)
    {
        string processName = risk.Kind == PrivacyRiskKind.WindowChange ||
            risk.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? risk.ProcessName
            : risk.ProcessName + ".exe";
        string fallback = risk.Active
            ? CameraNotificationSettings.DefaultPrivacyRiskTextTemplate
            : CameraNotificationSettings.DefaultPrivacyRiskEndedTextTemplate;
        return OrDefault(template, fallback)
            .Replace("{风险类型}", RiskName(risk.Kind), StringComparison.Ordinal)
            .Replace("{进程名}", processName, StringComparison.Ordinal)
            .Replace("{PID}", risk.ProcessId.ToString(), StringComparison.Ordinal)
            .Replace("{依据}", risk.Evidence, StringComparison.Ordinal)
            .Replace("{状态}", risk.Active ? "活动" : "已结束", StringComparison.Ordinal);
    }

    static string OrDefault(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

    /// <summary>解析 hex 颜色字符串，非法/空则回退默认（容错，不抛异常）。</summary>
    static Color ParseColor(string? hex, Color fallback)
        => !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out var c) ? c : fallback;

    static void TryMigrateLegacyConfig(CameraNotificationSettings settings, PluginConfig? legacyConfig)
    {
        if (settings.HasMigratedPluginConfig || legacyConfig == null) return;

        settings.ApplyLegacyConfig(legacyConfig);
        PluginLog.Info("[提醒] 已从旧插件配置迁移提醒设置到 ClassIsland 提醒提供方设置");
    }

    void LogInformation(string message)
    {
        if (_logger != null) _logger.LogInformation("{Message}", message);
        else PluginLog.Info(message);
    }

    void LogError(string message)
    {
        if (_logger != null) _logger.LogError("{Message}", message);
        else PluginLog.Error(message);
    }
}
