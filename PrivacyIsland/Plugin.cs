using ClassIsland.Core.Abstractions;
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

        // 提醒：摄像头开启/监视/关闭弹 ClassIsland 通知（替代原生覆盖层）。
        services.AddNotificationProvider<CameraNotificationProvider>();

        // 自动化触发器：摄像头开启/关闭时。
        services.AddTrigger<CameraStartedTrigger>();
        services.AddTrigger<CameraStoppedTrigger>();

        // 自动化行动：暂停/恢复（可逆）、立即注入/弹射。
        services.AddAction<PauseProtectionAction>();
        services.AddAction<InjectNowAction>();
        services.AddAction<EjectNowAction>();

        // 自动化规则：摄像头当前是否正被访问（无配置，内联处理器）。
        services.AddRule("privacy.island.rule.cameraActive", "摄像头正在被访问", "",
            _ => PrivacyIslandRuntime.CameraActive);

        // 设置页：延迟/隐身/语音/统计/日志。
        services.AddSettingsPage<MainSettingsPage>();
    }
}
