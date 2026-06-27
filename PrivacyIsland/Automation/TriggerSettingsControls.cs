using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using PrivacyIsland.Ipc;

namespace PrivacyIsland.Automation;

public class CameraStateTriggerControl : TriggerSettingsControlBase<CameraStateTriggerConfig>
{
    readonly ComboBox _state = new() { Width = 180 };
    bool _loading;

    public CameraStateTriggerControl()
    {
        _state.Items.Add(new StateOption(IpcProtocol.StatusStart, "摄像头启动"));
        _state.Items.Add(new StateOption(IpcProtocol.StatusWatching, "开始监视"));
        _state.Items.Add(new StateOption(IpcProtocol.StatusStop, "摄像头关闭"));
        _state.Items.Add(new StateOption(IpcProtocol.StatusError, "DLL 错误"));
        _state.Items.Add(new StateOption(IpcProtocol.StatusReady, "DLL 就绪"));
        _state.Items.Add(new StateOption(IpcProtocol.StatusInfo, "信息日志"));

        Content = Row("触发状态", _state);
        _state.SelectionChanged += (_, _) => Save();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _loading = true;
        Select(Settings.State);
        _loading = false;
    }

    void Save()
    {
        if (_loading || _state.SelectedItem is not StateOption option) return;
        Settings.State = option.State;
    }

    void Select(int state)
    {
        foreach (var item in _state.Items)
        {
            if (item is StateOption option && option.State == state)
            {
                _state.SelectedItem = option;
                return;
            }
        }

        _state.SelectedIndex = 0;
    }

    static Control Row(string label, Control control)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 90 },
                control,
            },
        };
    }

    sealed record StateOption(int State, string Name)
    {
        public override string ToString() => Name;
    }
}

public class ProtectionPauseTriggerControl : TriggerSettingsControlBase<ProtectionPauseTriggerConfig>
{
    readonly ComboBox _state = new() { Width = 180 };
    bool _loading;

    public ProtectionPauseTriggerControl()
    {
        _state.Items.Add(new PauseOption(true, "防护暂停时"));
        _state.Items.Add(new PauseOption(false, "防护恢复时"));

        Content = Row("触发状态", _state);
        _state.SelectionChanged += (_, _) => Save();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _loading = true;
        _state.SelectedIndex = Settings.TriggerWhenPaused ? 0 : 1;
        _loading = false;
    }

    void Save()
    {
        if (_loading || _state.SelectedItem is not PauseOption option) return;
        Settings.TriggerWhenPaused = option.Paused;
    }

    static Control Row(string label, Control control)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 90 },
                control,
            },
        };
    }

    sealed record PauseOption(bool Paused, string Name)
    {
        public override string ToString() => Name;
    }
}
