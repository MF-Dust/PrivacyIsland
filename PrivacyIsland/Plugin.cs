using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrivacyIsland.Automation;
using PrivacyIsland.Notification;
using PrivacyIsland.Orchestrator;
using PrivacyIsland.Settings;

namespace PrivacyIsland;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 编排器：进程监控 + 注入 + IPC，用 PluginConfigFolder 定位配置/统计/日志。
        services.AddSingleton(sp => new CaptureMonitor(
            PluginConfigFolder,
            sp.GetRequiredService<ILogger<CaptureMonitor>>()));
        services.AddHostedService(sp => sp.GetRequiredService<CaptureMonitor>());

        // 课程感知联动：注入可空 ILessonsService（宿主未提供时降级为空操作，不阻断加载）。
        services.AddSingleton(sp => new LessonAwareController(sp.GetService<ILessonsService>()));
        services.AddHostedService(sp => sp.GetRequiredService<LessonAwareController>());

        // 提醒：摄像头开启/监视/关闭弹 ClassIsland 通知（替代原生覆盖层）。
        services.AddNotificationProvider<CameraNotificationProvider>();

        // 自动化触发器：摄像头开启/关闭时。
        services.AddTrigger<CameraStartedTrigger>();
        services.AddTrigger<CameraWatchingTrigger>();
        services.AddTrigger<CameraStoppedTrigger>();

        // 自动化行动：暂停/恢复（可逆）、立即注入/弹射。
        services.AddAction<PauseProtectionAction>();
        services.AddAction<InjectNowAction>();
        services.AddAction<EjectNowAction>();
        services.AddAction<SetDelayAction, SetDelayActionControl>();

        // 自动化规则：摄像头当前是否正被访问（无配置，内联处理器）。
        services.AddRule("privacy.island.rule.cameraActive", "摄像头正在被访问", "",
            _ => PrivacyIslandRuntime.CameraActive);
        services.AddRule("privacy.island.rule.paused", "防护已暂停", "",
            _ => PrivacyIslandRuntime.IsPaused);

        // 设置页：延迟/隐身/语音/统计/日志。
        services.AddSettingsPage<MainSettingsPage>();
    }
}
