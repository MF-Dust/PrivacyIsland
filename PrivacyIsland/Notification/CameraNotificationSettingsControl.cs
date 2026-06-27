using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using PrivacyIsland.Config;

namespace PrivacyIsland.Notification;

public class CameraNotificationSettingsControl : NotificationProviderControlBase<CameraNotificationSettings>
{
    readonly ToggleSwitch _swStart = new();
    readonly ToggleSwitch _swWatching = new();
    readonly ToggleSwitch _swStop = new();
    readonly ToggleSwitch _swSpeech = new();
    readonly NumericUpDown _numDuration = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };
    readonly TextBox _txtStart = new() { Width = 180, MaxLength = PluginConfig.MaxTextLength };
    readonly TextBox _txtWatching = new() { Width = 180, MaxLength = PluginConfig.MaxTextLength };
    readonly TextBox _txtStop = new() { Width = 180, MaxLength = PluginConfig.MaxTextLength };
    readonly TextBox _txtColorStart = new() { Width = 110 };
    readonly TextBox _txtColorWatching = new() { Width = 110 };
    readonly TextBox _txtColorStop = new() { Width = 110 };
    readonly Border _swatchStart = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(4) };
    readonly Border _swatchWatching = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(4) };
    readonly Border _swatchStop = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(4) };

    bool _loading;

    public CameraNotificationSettingsControl()
    {
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Row("摄像头启动时提醒", _swStart),
                Row("进入监视时提醒", _swWatching),
                Row("摄像头关闭时提醒", _swStop),
                Row("语音播报", _swSpeech),
                Row("通知显示时长（秒）", _numDuration),
                Row("启动提醒文案", _txtStart),
                Row("监视提醒文案", _txtWatching),
                Row("关闭提醒文案", _txtStop),
                Row("启动提醒颜色", ColorFooter(_txtColorStart, _swatchStart)),
                Row("监视提醒颜色", ColorFooter(_txtColorWatching, _swatchWatching)),
                Row("关闭提醒颜色", ColorFooter(_txtColorStop, _swatchStop)),
            }
        };

        WireAutosave();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadSettings();
    }

    void WireAutosave()
    {
        _swStart.PropertyChanged += (_, e) => SaveIfChanged(e.Property == ToggleSwitch.IsCheckedProperty);
        _swWatching.PropertyChanged += (_, e) => SaveIfChanged(e.Property == ToggleSwitch.IsCheckedProperty);
        _swStop.PropertyChanged += (_, e) => SaveIfChanged(e.Property == ToggleSwitch.IsCheckedProperty);
        _swSpeech.PropertyChanged += (_, e) => SaveIfChanged(e.Property == ToggleSwitch.IsCheckedProperty);
        _numDuration.PropertyChanged += (_, e) => SaveIfChanged(e.Property == NumericUpDown.ValueProperty);

        WireTextAutosave(_txtStart);
        WireTextAutosave(_txtWatching);
        WireTextAutosave(_txtStop);
        WireColorAutosave(_txtColorStart, _swatchStart);
        WireColorAutosave(_txtColorWatching, _swatchWatching);
        WireColorAutosave(_txtColorStop, _swatchStop);
    }

    void LoadSettings()
    {
        Settings.Clamp();

        _loading = true;
        _swStart.IsChecked = Settings.NotifyOnStart;
        _swWatching.IsChecked = Settings.NotifyOnWatching;
        _swStop.IsChecked = Settings.NotifyOnStop;
        _swSpeech.IsChecked = Settings.SpeechEnabled;
        _numDuration.Value = Settings.OverlayDurationSeconds;
        _txtStart.Text = Settings.TextOnStart;
        _txtWatching.Text = Settings.TextOnWatching;
        _txtStop.Text = Settings.TextOnStop;
        _txtColorStart.Text = Settings.ColorOnStart;
        _txtColorWatching.Text = Settings.ColorOnWatching;
        _txtColorStop.Text = Settings.ColorOnStop;
        UpdateSwatch(_txtColorStart, _swatchStart);
        UpdateSwatch(_txtColorWatching, _swatchWatching);
        UpdateSwatch(_txtColorStop, _swatchStop);
        _loading = false;
    }

    void SaveIfChanged(bool changed)
    {
        if (!changed || _loading) return;

        Settings.NotifyOnStart = _swStart.IsChecked == true;
        Settings.NotifyOnWatching = _swWatching.IsChecked == true;
        Settings.NotifyOnStop = _swStop.IsChecked == true;
        Settings.SpeechEnabled = _swSpeech.IsChecked == true;
        Settings.OverlayDurationSeconds = (int)(_numDuration.Value ?? 5);
        Settings.TextOnStart = _txtStart.Text ?? "起风了";
        Settings.TextOnWatching = _txtWatching.Text ?? "风好大";
        Settings.TextOnStop = _txtStop.Text ?? "风停了";
        Settings.ColorOnStart = string.IsNullOrWhiteSpace(_txtColorStart.Text) ? "#FF0000" : _txtColorStart.Text!.Trim();
        Settings.ColorOnWatching = string.IsNullOrWhiteSpace(_txtColorWatching.Text) ? "#FFA500" : _txtColorWatching.Text!.Trim();
        Settings.ColorOnStop = string.IsNullOrWhiteSpace(_txtColorStop.Text) ? "#FF69B4" : _txtColorStop.Text!.Trim();
        Settings.HasMigratedPluginConfig = true;
        Settings.Clamp();
    }

    void WireTextAutosave(TextBox textBox)
    {
        textBox.PropertyChanged += (_, e) => SaveIfChanged(e.Property == TextBox.TextProperty);
        textBox.LostFocus += (_, _) => SaveIfChanged(true);
    }

    void WireColorAutosave(TextBox box, Border swatch)
    {
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            UpdateSwatch(box, swatch);
            SaveIfChanged(true);
        };
        box.LostFocus += (_, _) => SaveIfChanged(true);
    }

    static Control ColorFooter(TextBox box, Border swatch)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { box, swatch },
        };
    }

    static Control Row(string label, Control control)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Width = 150 },
                control,
            },
        };
    }

    static void UpdateSwatch(TextBox box, Border swatch)
    {
        var hex = box.Text?.Trim();
        swatch.Background = !string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var c)
            ? new SolidColorBrush(c)
            : new SolidColorBrush(Colors.Gray);
    }
}
