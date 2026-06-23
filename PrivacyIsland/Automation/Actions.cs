using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PrivacyIsland.Automation;

/// <summary>暂停防护（可逆）：invoke 暂停摄像头延迟，规则不再满足时 revert 自动恢复。</summary>
[ActionInfo("privacy.island.action.pause", "暂停摄像头防护", "")]
public class PauseProtectionAction : ActionBase<EmptyConfig>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PrivacyIslandRuntime.Pause();
    }

    protected override async Task OnRevert()
    {
        await base.OnRevert();
        PrivacyIslandRuntime.Resume();
    }
}

/// <summary>立即向 media_capture.exe 注入防护 DLL。</summary>
[ActionInfo("privacy.island.action.injectNow", "立即注入防护", "")]
public class InjectNowAction : ActionBase<EmptyConfig>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PrivacyIslandRuntime.InjectNow();
    }
}

/// <summary>立即从 media_capture.exe 弹射防护 DLL。</summary>
[ActionInfo("privacy.island.action.ejectNow", "立即弹射防护", "")]
public class EjectNowAction : ActionBase<EmptyConfig>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PrivacyIslandRuntime.EjectNow();
    }
}

/// <summary>"立即设定延迟"行动的配置：写入的随机延迟上下限（秒）。</summary>
public class DelayActionConfig : ObservableObject
{
    int _min = 10;
    int _max = 20;

    public int Min { get => _min; set => SetProperty(ref _min, value); }
    public int Max { get => _max; set => SetProperty(ref _max, value); }
}

/// <summary>把基准随机延迟设为指定上下限（持久化）。可在自动化里「上课 → 设定 10-20s」。</summary>
[ActionInfo("privacy.island.action.setDelay", "立即设定延迟", "")]
public class SetDelayAction : ActionBase<DelayActionConfig>
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        PrivacyIslandRuntime.SetDelay(Settings.Min, Settings.Max);
    }
}
