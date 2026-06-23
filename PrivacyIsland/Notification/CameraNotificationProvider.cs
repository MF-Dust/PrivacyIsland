using Avalonia.Media;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using Microsoft.Extensions.Logging;
using PrivacyIsland.Ipc;
using PrivacyIsland.Logging;

namespace PrivacyIsland.Notification;

/// <summary>
/// 摄像头提醒 provider（替代原生全屏覆盖层）。订阅 IPC 状态，在 start/watching/stop 时
/// 通过 ClassIsland 提醒系统弹出通知，颜色沿用原版语义（红/橙/粉）。
/// </summary>
[NotificationProviderInfo("b1e7c0a2-3d4f-4a6b-9c1d-2e3f4a5b6c7d", "摄像头防护", "希沃摄像头访问提醒")]
[NotificationChannelInfo(ChannelId, "摄像头访问", "", "摄像头开启/监视/关闭时提醒")]
public class CameraNotificationProvider : NotificationProviderBase
{
    const string ChannelId = "c2f8d1b3-4e5a-4b7c-8d2e-3f4a5b6c7d8e";

    readonly ILogger<CameraNotificationProvider>? _logger;

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
        LogInformation("[提醒] provider 已构造并订阅状态");
    }

    void OnState(CaptureSnapshot s)
    {
        var cfg = PrivacyIslandRuntime.Config;
        string text;
        Color color;
        bool enabled;
        switch (s.State)
        {
            case IpcProtocol.StatusStart:
                text = OrDefault(cfg?.TextOnStart, "起风了");
                color = ParseColor(cfg?.ColorOnStart, Color.FromRgb(255, 0, 0));
                enabled = cfg?.NotifyOnStart ?? true;
                break;
            case IpcProtocol.StatusWatching:
                text = OrDefault(cfg?.TextOnWatching, "风好大");
                color = ParseColor(cfg?.ColorOnWatching, Color.FromRgb(255, 165, 0));
                enabled = cfg?.NotifyOnWatching ?? true;
                break;
            case IpcProtocol.StatusStop:
                text = OrDefault(cfg?.TextOnStop, "风停了");
                color = ParseColor(cfg?.ColorOnStop, Color.FromRgb(255, 105, 180));
                enabled = cfg?.NotifyOnStop ?? true;
                break;
            default:
                return;
        }

        if (!enabled) return;

        bool speech = cfg?.SpeechEnabled ?? false;
        var duration = TimeSpan.FromSeconds(cfg?.OverlayDurationSeconds ?? 5);
        var brush = new SolidColorBrush(color);

        try
        {
            Channel(ChannelId).ShowNotification(new NotificationRequest
            {
                MaskContent = NotificationContent.CreateSimpleTextContent(text, c =>
                {
                    c.Color = brush;
                    c.Duration = duration;
                    c.IsSpeechEnabled = speech;
                    c.SpeechContent = text;
                })
            });
            LogInformation("[提醒] 已显示：" + text);
        }
        catch (Exception ex)
        {
            LogError("[提醒] 显示失败：" + ex.Message);
        }
    }

    static string OrDefault(string? s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

    /// <summary>解析 hex 颜色字符串，非法/空则回退默认（容错，不抛异常）。</summary>
    static Color ParseColor(string? hex, Color fallback)
        => !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out var c) ? c : fallback;

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
