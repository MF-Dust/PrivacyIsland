using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using CommunityToolkit.Mvvm.ComponentModel;
using PrivacyIsland.Ipc;

namespace PrivacyIsland.Automation;

/// <summary>无配置自动化项共用的空设置类型。</summary>
public sealed class EmptyConfig { }

public sealed class CameraStateTriggerConfig : ObservableObject
{
    int _state = IpcProtocol.StatusStart;

    public int State { get => _state; set => SetProperty(ref _state, value); }
}

public sealed class ProtectionPauseTriggerConfig : ObservableObject
{
    bool _triggerWhenPaused = true;

    public bool TriggerWhenPaused { get => _triggerWhenPaused; set => SetProperty(ref _triggerWhenPaused, value); }
}

/// <summary>摄像头开始捕获时触发（进入延迟阶段）→ 用户可做「希沃开摄像头 → 执行 X」。</summary>
[TriggerInfo("privacy.island.trigger.cameraStarted", "摄像头启动时", "")]
public class CameraStartedTrigger : TriggerBase<EmptyConfig>
{
    void OnState(CaptureSnapshot s)
    {
        if (s.State == IpcProtocol.StatusStart) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}

/// <summary>延迟结束、摄像头真正开始工作（监视）时触发。</summary>
[TriggerInfo("privacy.island.trigger.cameraWatching", "开始监视时", "")]
public class CameraWatchingTrigger : TriggerBase<EmptyConfig>
{
    void OnState(CaptureSnapshot s)
    {
        if (s.State == IpcProtocol.StatusWatching) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}

/// <summary>摄像头停止捕获时触发。</summary>
[TriggerInfo("privacy.island.trigger.cameraStopped", "摄像头关闭时", "")]
public class CameraStoppedTrigger : TriggerBase<EmptyConfig>
{
    void OnState(CaptureSnapshot s)
    {
        if (s.State == IpcProtocol.StatusStop) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}

/// <summary>按用户选择的 IPC 状态触发。</summary>
[TriggerInfo("privacy.island.trigger.cameraState", "摄像头状态变化时", "")]
public class CameraStateTrigger : TriggerBase<CameraStateTriggerConfig>
{
    void OnState(CaptureSnapshot s)
    {
        if (s.State == Settings.State) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}

/// <summary>防护暂停/恢复时触发。</summary>
[TriggerInfo("privacy.island.trigger.protectionPauseChanged", "防护状态变化时", "")]
public class ProtectionPauseChangedTrigger : TriggerBase<ProtectionPauseTriggerConfig>
{
    void OnPauseChanged(bool paused)
    {
        if (paused == Settings.TriggerWhenPaused) Trigger();
    }

    public override void Loaded() => PrivacyIslandRuntime.ProtectionPauseChanged += OnPauseChanged;
    public override void UnLoaded() => PrivacyIslandRuntime.ProtectionPauseChanged -= OnPauseChanged;
}
