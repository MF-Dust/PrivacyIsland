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

    /// <summary>防护暂停状态变化——自动化触发器订阅此事件。</summary>
    public static event Action<bool>? ProtectionPauseChanged;

    internal static void RaiseState(CaptureSnapshot s)
    {
        try { StateReceived?.Invoke(s); } catch { /* 订阅方异常隔离 */ }
    }

    internal static void RaiseProtectionPauseChanged(bool paused)
    {
        try { ProtectionPauseChanged?.Invoke(paused); } catch { /* 订阅方异常隔离 */ }
    }

    /// <summary>摄像头当前是否正被访问，供规则读取。</summary>
    public static bool CameraActive => Monitor?.Bridge?.CameraActive ?? false;

    /// <summary>防护当前是否处于暂停态（任一暂停来源生效），供规则读取。</summary>
    public static bool IsPaused => Monitor?.EffectivePaused ?? false;

    public static PluginConfig? Config => Monitor?.Config;

    /// <summary>课程联动控制器（由其自身注册时挂上），供设置页"模拟上课/课间"调用。</summary>
    public static LessonAwareController? LessonController { get; internal set; }

    // 控制面（供自动化行动调用）。automation 暂停源与 manual/lesson 互不冲突。
    public static void Pause() => Monitor?.SetPauseSource("automation", true);
    public static void Resume() => Monitor?.SetPauseSource("automation", false);
    public static void SetDelay(int min, int max) => Monitor?.SetDelay(min, max);
    public static void ApplyDelayOverride(int min, int max) => Monitor?.ApplyDelayOverride(min, max);
    public static void ClearDelayOverride() => Monitor?.ApplyDelayOverride(null, null);

    /// <summary>让课程联动按当前配置与课程状态重新评估（设置页改动后调用，立即生效）。</summary>
    public static void ReapplyLessonState() => LessonController?.Reapply();
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
