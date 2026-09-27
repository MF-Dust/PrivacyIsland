using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using PrivacyIsland.Config;

namespace PrivacyIsland.Settings;

/// <summary>防护、延迟和课程联动控件及其配置映射。</summary>
internal sealed class ProtectionSettingsEditor
{
    readonly NumericUpDown _numMin = NumberBox();
    readonly NumericUpDown _numMax = NumberBox();
    readonly ToggleSwitch _swStealth = new();
    readonly ToggleSwitch _swFuseOsProbe = new();
    readonly ToggleSwitch _swScreenCapture = new();
    readonly ToggleSwitch _swRemoteControl = new();
    readonly ToggleSwitch _swMicrophone = new();
    readonly ToggleSwitch _swLive = new();
    readonly ToggleSwitch _swDeviceCamera = new();
    readonly ToggleSwitch _swWindowChange = new();
    readonly ComboBox _privacyResponse = new() { Width = 180 };
    readonly ToggleSwitch _swLessonAware = new();
    readonly ToggleSwitch _swPauseInClass = new();
    readonly ToggleSwitch _swStrongDelayInClass = new();
    readonly NumericUpDown _numClassMin = NumberBox();
    readonly NumericUpDown _numClassMax = NumberBox();
    bool _loading;

    public event Action? Changed;

    public TabItem ProtectionTab { get; }
    public TabItem LessonTab { get; }

    public ProtectionSettingsEditor()
    {
        _privacyResponse.Items.Add("询问后处理");
        _privacyResponse.Items.Add("仅提示");
        WireChanges();

        ProtectionTab = SettingsUi.CategoryTab(
            Icons.ShieldCheckmarkFilled,
            "防护设置",
            PrivacySection(),
            DelaySection());
        LessonTab = SettingsUi.CategoryTab(
            Icons.CalendarFilled,
            "课程联动",
            LessonSection());
    }

    public void Load(PluginConfig config)
    {
        _loading = true;
        try
        {
            _numMin.Value = config.MinDelaySeconds;
            _numMax.Value = config.MaxDelaySeconds;
            _swStealth.IsChecked = config.StealthMode;
            _swFuseOsProbe.IsChecked = config.FuseOsProbe;
            _swScreenCapture.IsChecked = config.EnableScreenCaptureMonitoring;
            _swRemoteControl.IsChecked = config.EnableRemoteControlMonitoring;
            _swMicrophone.IsChecked = config.EnableMicrophoneMonitoring;
            _swLive.IsChecked = config.EnableLiveMonitoring;
            _swDeviceCamera.IsChecked = config.EnableDeviceCameraMonitoring;
            _swWindowChange.IsChecked = config.EnableWindowChangeMonitoring;
            _privacyResponse.SelectedIndex = config.PrivacyRiskResponse == PrivacyRiskResponseMode.NotifyOnly ? 1 : 0;
            _swLessonAware.IsChecked = config.LessonAwareEnabled;
            _swPauseInClass.IsChecked = config.PauseDuringClass;
            _swStrongDelayInClass.IsChecked = config.StrongerDelayDuringClass;
            _numClassMin.Value = config.ClassMinDelaySeconds;
            _numClassMax.Value = config.ClassMaxDelaySeconds;
        }
        finally
        {
            _loading = false;
        }
    }

    public void ApplyTo(PluginConfig config)
    {
        int min = (int)(_numMin.Value ?? 3);
        int max = Math.Max(min, (int)(_numMax.Value ?? 8));
        int classMin = (int)(_numClassMin.Value ?? 10);
        int classMax = Math.Max(classMin, (int)(_numClassMax.Value ?? 20));

        _loading = true;
        try
        {
            _numMax.Value = max;
            _numClassMax.Value = classMax;
        }
        finally
        {
            _loading = false;
        }

        config.MinDelaySeconds = min;
        config.MaxDelaySeconds = max;
        config.StealthMode = _swStealth.IsChecked == true;
        config.FuseOsProbe = _swFuseOsProbe.IsChecked == true;
        config.EnableScreenCaptureMonitoring = _swScreenCapture.IsChecked == true;
        config.EnableRemoteControlMonitoring = _swRemoteControl.IsChecked == true;
        config.EnableMicrophoneMonitoring = _swMicrophone.IsChecked == true;
        config.EnableLiveMonitoring = _swLive.IsChecked == true;
        config.EnableDeviceCameraMonitoring = _swDeviceCamera.IsChecked == true;
        config.EnableWindowChangeMonitoring = _swWindowChange.IsChecked == true;
        config.PrivacyRiskResponse = _privacyResponse.SelectedIndex == 1
            ? PrivacyRiskResponseMode.NotifyOnly
            : PrivacyRiskResponseMode.Prompt;
        config.LessonAwareEnabled = _swLessonAware.IsChecked == true;
        config.PauseDuringClass = _swPauseInClass.IsChecked == true;
        config.StrongerDelayDuringClass = _swStrongDelayInClass.IsChecked == true;
        config.ClassMinDelaySeconds = classMin;
        config.ClassMaxDelaySeconds = classMax;
    }

    SettingsExpander PrivacySection()
    {
        var cameraStatus = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon { Glyph = Icons.CheckmarkCircleFilled, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14 },
                new TextBlock { Text = "始终启用", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var camera = SettingsUi.Item(Icons.CameraFilled, "监测摄像头访问", "通过 hook 与 Windows 占用状态确认希沃摄像头访问", cameraStatus);
        var fuse = SettingsUi.Item(Icons.EyeFilled, "融合系统摄像头探测", "hook 未上报但系统显示摄像头在用时，也判定为活动并触发提醒和规则", _swFuseOsProbe);
        var screen = SettingsUi.Item(Icons.EyeFilled, "监测屏幕采集", "希沃 1.5.5 与 1.6.6 的 screenCapture 使用 GDI 截图 RPC，仅在已有客户端连接时提醒", _swScreenCapture);
        var remote = SettingsUi.Item(Icons.ShieldErrorFilled, "监测远程控制", "希沃 1.5.5 与 1.6.6 的 rtcRemoteDesktop 会话，并标注麦克风或摄像头占用", _swRemoteControl);
        var microphone = SettingsUi.Item(Icons.MegaphoneFilled, "监测麦克风", "确认 media_capture、rtcRemoteDesktop 和语音宿主 SeewoAbility 的占用，并忽略早于进程启动的记录。宿主只提醒", _swMicrophone);
        var live = SettingsUi.Item(Icons.PlayFilled, "监测校园直播", "1.5.5、1.6.6 与 1.6.7 的 liveClient。仅在占用摄像头、麦克风或已有 TCP 连接时提醒，确认后可结束该进程", _swLive);
        var deviceCamera = SettingsUi.Item(Icons.CameraFilled, "监测宿主摄像头", "SeewoAbility 被 Windows 记为正在使用摄像头时提醒，对应设备采集和视频展台。只提醒，不结束业务宿主", _swDeviceCamera);
        var windowChange = SettingsUi.Item(Icons.WarningFilled, "记录窗口异常", "窗口先在屏幕上稳定显示，再被连续完全移出屏幕时才提醒。普通隐藏、最小化和单次闪动不提醒，也不恢复窗口", _swWindowChange);
        var response = SettingsUi.Item(Icons.WarningFilled, "风险处理方式", "仅提示不会弹出确认框，也不会自动结束进程", _privacyResponse);
        return SettingsUi.Expander(Icons.ShieldCheckmarkFilled, "隐私风险监测", "统一管理摄像头、屏幕、远控、麦克风、直播、宿主摄像头和窗口异常", response, camera, fuse, screen, remote, microphone, live, deviceCamera, windowChange);
    }

    SettingsExpander DelaySection()
    {
        var minItem = SettingsUi.Item(Icons.TimerFilled, "最小延迟（秒）", "摄像头捕获开始后的最短随机等待时间", _numMin);
        var maxItem = SettingsUi.Item(Icons.TimerFilled, "最大延迟（秒）", "摄像头捕获开始后的最长随机等待时间", _numMax);
        var stealthItem = SettingsUi.Item(Icons.EyeOffFilled, "隐身模式", "降低 hook 日志输出，减少被检测的风险", _swStealth);
        return SettingsUi.Expander(Icons.TimerFilled, "捕获延迟", "控制摄像头捕获开始前的随机等待时间", minItem, maxItem, stealthItem);
    }

    SettingsExpander LessonSection()
    {
        var enableItem = SettingsUi.Item(Icons.CalendarFilled, "启用课程联动", "按 ClassIsland 课程状态自动调整防护（总开关）", _swLessonAware);
        var pauseItem = SettingsUi.Item(Icons.PauseFilled, "上课时自动暂停", "进入上课时段时暂停摄像头延迟防护，课间自动恢复", _swPauseInClass);
        var strongItem = SettingsUi.Item(Icons.TimerFilled, "上课时加强延迟", "上课时改用下方加强延迟（未勾选自动暂停时生效），课间恢复基准", _swStrongDelayInClass);
        var minItem = SettingsUi.Item(Icons.TimerFilled, "上课最小延迟（秒）", "上课加强延迟的下限", _numClassMin);
        var maxItem = SettingsUi.Item(Icons.TimerFilled, "上课最大延迟（秒）", "上课加强延迟的上限", _numClassMax);
        return SettingsUi.Expander(Icons.CalendarFilled, "课程联动", "接入课程表，上课/课间自动切换防护策略", enableItem, pauseItem, strongItem, minItem, maxItem);
    }

    void WireChanges()
    {
        _numMin.PropertyChanged += (_, e) => Notify(e.Property == NumericUpDown.ValueProperty);
        _numMax.PropertyChanged += (_, e) => Notify(e.Property == NumericUpDown.ValueProperty);
        _swStealth.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swFuseOsProbe.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swScreenCapture.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swRemoteControl.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swMicrophone.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swLive.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swDeviceCamera.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swWindowChange.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _privacyResponse.SelectionChanged += (_, _) => Notify(true);
        _swLessonAware.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swPauseInClass.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _swStrongDelayInClass.PropertyChanged += (_, e) => Notify(e.Property == ToggleSwitch.IsCheckedProperty);
        _numClassMin.PropertyChanged += (_, e) => Notify(e.Property == NumericUpDown.ValueProperty);
        _numClassMax.PropertyChanged += (_, e) => Notify(e.Property == NumericUpDown.ValueProperty);
    }

    void Notify(bool changed)
    {
        if (changed && !_loading) Changed?.Invoke();
    }

    static NumericUpDown NumberBox() => new()
    {
        Minimum = 1,
        Maximum = 30,
        Increment = 1,
        Width = 120,
    };
}
