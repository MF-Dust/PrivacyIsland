using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using PrivacyIsland.Ipc;

namespace PrivacyIsland.Automation;

/// <summary>无配置自动化项共用的空设置类型。</summary>
public sealed class EmptyConfig { }

/// <summary>摄像头开始捕获时触发 → 用户可做「希沃开摄像头 → 执行 X」。</summary>
[TriggerInfo("privacy.island.trigger.cameraStarted", "摄像头开启时", "")]
public class CameraStartedTrigger : TriggerBase<EmptyConfig>
{
    void OnState(CaptureSnapshot s)
    {
        if (s.State == IpcProtocol.StatusStart) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}

/// <summary>摄像头停止捕获时触发。</summary>
[TriggerInfo("privacy.island.trigger.cameraStopped", "摄像头关闭时", "")]
public class CameraStoppedTrigger : TriggerBase<EmptyConfig>
{
    void OnState(CaptureSnapshot s)
    {
        if (s.State == IpcProtocol.StatusStop) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}
