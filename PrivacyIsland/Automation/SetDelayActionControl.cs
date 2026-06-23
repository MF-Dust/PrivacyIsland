using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;

namespace PrivacyIsland.Automation;

/// <summary>「立即设定延迟」行动的设置界面：两个数值框编辑 Min/Max（代码构建，无 AXAML）。</summary>
public class SetDelayActionControl : ActionSettingsControlBase<DelayActionConfig>
{
    readonly NumericUpDown _numMin = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };
    readonly NumericUpDown _numMax = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };
    bool _loading;

    public SetDelayActionControl()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Row("最小延迟（秒）", _numMin));
        panel.Children.Add(Row("最大延迟（秒）", _numMax));
        Content = panel;

        _numMin.ValueChanged += (_, _) => Save();
        _numMax.ValueChanged += (_, _) => Save();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _loading = true;
        _numMin.Value = Settings.Min;
        _numMax.Value = Settings.Max;
        _loading = false;
    }

    void Save()
    {
        if (_loading) return;
        int min = (int)(_numMin.Value ?? 10);
        int max = (int)(_numMax.Value ?? 20);
        if (max < min)
        {
            max = min;
            _loading = true;
            _numMax.Value = max;
            _loading = false;
        }
        Settings.Min = min;
        Settings.Max = max;
    }

    static Control Row(string label, Control control)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 120 },
                control,
            },
        };
    }
}
