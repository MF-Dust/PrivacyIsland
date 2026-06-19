using PrivacyIsland.Config;
using PrivacyIsland.Ipc;
using PrivacyIsland.Orchestrator;

namespace PrivacyIsland;

/// <summary>
/// 静态枢纽：ClassIsland 用 DI 即时实例化 提醒/触发器/规则/行动，它们拿不到我们的服务实例，
/// 故经此静态入口访问正在运行的编排器（与 ExtraIsland 的 GlobalConstants/静态事件同思路）。
/// </summary>
public static class PrivacyIslandRuntime
{
    public static CaptureMonitor? Monitor { get; internal set; }

    /// <summary>每帧 DLL 状态——提醒 provider 与触发器订阅此事件。</summary>
    public static event Action<CaptureSnapshot>? StateReceived;

    internal static void RaiseState(CaptureSnapshot s)
    {
        try { StateReceived?.Invoke(s); } catch { /* 订阅方异常隔离 */ }
    }

    /// <summary>摄像头当前是否正被访问，供规则读取。</summary>
    public static bool CameraActive => Monitor?.Bridge?.CameraActive ?? false;

    public static PluginConfig? Config => Monitor?.Config;

    // 控制面（供自动化行动调用）
    public static void Pause() => Monitor?.SetPaused(true);
    public static void Resume() => Monitor?.SetPaused(false);
    public static void SetDelay(int min, int max) => Monitor?.SetDelay(min, max);
    public static PluginOperationResult InjectNow()
        => Monitor?.InjectNow() ?? PluginOperationResult.Fail("编排器未就绪，无法注入");
    public static PluginOperationResult EjectNow()
        => Monitor?.EjectNow() ?? PluginOperationResult.Fail("编排器未就绪，无法弹射");
    public static PluginOperationResult OpenLogsFolder()
        => Monitor?.OpenLogsFolder() ?? PluginOperationResult.Fail("编排器未就绪，无法查看日志状态");

    // 应用内功能测试
    public static void Simulate(int state, string message) => Monitor?.Simulate(state, message);
    public static string Diagnostics() => Monitor?.Diagnostics() ?? "（编排器未就绪）";
}
