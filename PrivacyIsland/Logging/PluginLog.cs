using Microsoft.Extensions.Logging;

namespace PrivacyIsland.Logging;

/// <summary>PrivacyIsland 的宿主日志适配器，日志交给 ClassIsland 的 Microsoft.Extensions.Logging 管线。</summary>
internal static class PluginLog
{
    static readonly object Gate = new();
    static ILogger? _logger;

    public static void Init(ILogger logger)
    {
        lock (Gate) _logger = logger;
        Info("PrivacyIsland 启动，监控 media_capture.exe");
    }

    public static void Info(string msg) => Write(LogLevel.Information, msg);
    public static void Warn(string msg) => Write(LogLevel.Warning, msg);
    public static void Error(string msg) => Write(LogLevel.Error, msg);
    public static void CaptureStart(string msg) => Write(LogLevel.Information, $"[CAPTURE_START] {msg}");
    public static void CaptureStop(string msg) => Write(LogLevel.Information, $"[CAPTURE_STOP] {msg}");

    static void Write(LogLevel level, string msg)
    {
        ILogger? logger;
        lock (Gate) logger = _logger;
        logger?.Log(level, "{Message}", msg);
    }
}
