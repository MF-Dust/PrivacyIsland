using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using FluentAvalonia.UI.Controls;
using PrivacyIsland.Config;
using PrivacyIsland.Ipc;
using PrivacyIsland.Orchestrator;

namespace PrivacyIsland.Settings;

[SettingsPageInfo("privacy.island.settings", "摄像头防护", "", "", ClassIsland.Core.Enums.SettingsWindow.SettingsPageCategory.External)]
public class MainSettingsPage : SettingsPageBase
{
    readonly NumericUpDown _numMin = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };
    readonly NumericUpDown _numMax = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };
    readonly ToggleSwitch _swStealth = new();

    // 课程联动
    readonly ToggleSwitch _swLessonAware = new();
    readonly ToggleSwitch _swPauseInClass = new();
    readonly ToggleSwitch _swStrongDelayInClass = new();
    readonly NumericUpDown _numClassMin = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };
    readonly NumericUpDown _numClassMax = new() { Minimum = 1, Maximum = 30, Increment = 1, Width = 120 };

    readonly TextBlock _lblStats = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _lblDiag = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    readonly InfoBar _infoBar = new()
    {
        Title = "提示",
        Severity = InfoBarSeverity.Informational,
        IsOpen = true,
        IsClosable = false,
    };

    PluginConfig? _cfg;
    bool _loading;
    bool _configDirty;
    bool _stateSubscribed;

    public MainSettingsPage()
    {
        var root = new StackPanel { Spacing = 8 };
        root.Classes.Add("settings-container");
        root.Classes.Add("animated-intro");

        root.Children.Add(TitleSection());
        root.Children.Add(_infoBar);
        root.Children.Add(DelaySection());
        root.Children.Add(LessonSection());
        root.Children.Add(StatsSection());
        root.Children.Add(TestSection());

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
        };

        WireAutosave();
        LoadConfig();
    }

    // ---- title ----

    Control TitleSection()
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(0, 4, 0, 4),
            Children =
            {
                new FontIcon { Glyph = Icons.CameraFilled, FontSize = 28, FontFamily = new FontFamily("Segoe Fluent Icons") },
                new Label { Content = "摄像头防护", FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            }
        };
    }

    // ---- section builders ----

    SettingsExpander DelaySection()
    {
        var minItem = Item(Icons.TimerFilled, "最小延迟（秒）", "摄像头捕获开始后的最短随机等待时间", _numMin);
        var maxItem = Item(Icons.TimerFilled, "最大延迟（秒）", "摄像头捕获开始后的最长随机等待时间", _numMax);
        var stealthItem = Item(Icons.EyeOffFilled, "隐身模式", "降低 hook 日志输出，减少被检测的风险", _swStealth);

        return Expander(Icons.TimerFilled, "捕获延迟", "控制摄像头捕获开始前的随机等待时间", minItem, maxItem, stealthItem);
    }

    SettingsExpander LessonSection()
    {
        var enableItem = Item(Icons.CalendarFilled, "启用课程联动", "按 ClassIsland 课程状态自动调整防护（总开关）", _swLessonAware);
        var pauseItem = Item(Icons.PauseFilled, "上课时自动暂停", "进入上课时段时暂停摄像头延迟防护，课间自动恢复", _swPauseInClass);
        var strongItem = Item(Icons.TimerFilled, "上课时加强延迟", "上课时改用下方加强延迟（未勾选自动暂停时生效），课间恢复基准", _swStrongDelayInClass);
        var minItem = Item(Icons.TimerFilled, "上课最小延迟（秒）", "上课加强延迟的下限", _numClassMin);
        var maxItem = Item(Icons.TimerFilled, "上课最大延迟（秒）", "上课加强延迟的上限", _numClassMax);

        return Expander(Icons.CalendarFilled, "课程联动", "接入课程表，上课/课间自动切换防护策略",
            enableItem, pauseItem, strongItem, minItem, maxItem);
    }

    SettingsExpander StatsSection()
    {
        var spStats = new StackPanel { Spacing = 8 };
        spStats.Children.Add(_lblStats);

        var spButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var btnRefresh = new Button { Content = "刷新统计" };
        btnRefresh.Click += (_, _) =>
        {
            RefreshStats();
            ShowInfo("统计信息已刷新。", InfoBarSeverity.Informational);
        };
        var btnReset = new Button { Content = "重置统计" };
        btnReset.Click += (_, _) => ResetStats();
        spButtons.Children.Add(btnRefresh);
        spButtons.Children.Add(btnReset);
        spStats.Children.Add(spButtons);

        var statsItem = Item(Icons.ChartMultipleFilled, "捕获统计", "自插件加载以来的摄像头访问记录", spStats);

        return Expander(Icons.ChartMultipleFilled, "捕获统计", "摄像头捕获历史记录", statsItem);
    }

    SettingsExpander TestSection()
    {
        var diagItem = Item(Icons.InfoFilled, "诊断信息", "当前注入器、DLL、IPC 与权限状态", _lblDiag);

        var spSim = new StackPanel { Spacing = 4 };
        spSim.Children.Add(SimulateButton("模拟开启", () => SimThenRefresh(Ipc.IpcProtocol.StatusStart, "模拟 DirectShow 捕获开启 [DS]")));
        spSim.Children.Add(SimulateButton("模拟监视", () => SimThenRefresh(Ipc.IpcProtocol.StatusWatching, "模拟进入监视状态")));
        spSim.Children.Add(SimulateButton("模拟关闭", () => SimThenRefresh(Ipc.IpcProtocol.StatusStop, "模拟捕获关闭")));
        spSim.Children.Add(SimulateButton("完整模拟", RunFullSimulation));
        var simItem = Item(Icons.PlayFilled, "模拟摄像头事件", "走完整 IPC 路径触发提醒/触发器/规则/统计，无需真注入", spSim);

        var spLesson = new StackPanel { Spacing = 4 };
        spLesson.Children.Add(SimulateButton("模拟上课", () => SimLesson(true)));
        spLesson.Children.Add(SimulateButton("模拟课间", () => SimLesson(false)));
        var lessonItem = Item(Icons.CalendarFilled, "模拟课程状态", "按当前课程联动设置应用上课/课间策略，无需等真实课表", spLesson);

        var btnRefresh = new Button { Content = "刷新诊断", Margin = new Thickness(0, 4, 0, 0) };
        btnRefresh.Click += (_, _) =>
        {
            RefreshDiagnostics();
            ShowInfo("诊断信息已刷新。", InfoBarSeverity.Informational);
        };
        var refreshItem = Item(Icons.SettingsFilled, "刷新诊断信息", "重新检测注入器、DLL、IPC 和目标进程状态", btnRefresh);

        var btnLogs = new Button { Content = "查看日志说明", Margin = new Thickness(0, 4, 0, 0) };
        btnLogs.Click += (_, _) => ShowOperation(PrivacyIslandRuntime.OpenLogsFolder());
        var logsItem = Item(Icons.FolderFilled, "ClassIsland 日志", "PrivacyIsland 运行日志写入 ClassIsland 宿主日志", btnLogs);

        var spActions = new StackPanel { Spacing = 4 };
        var btnInject = new Button { Content = "立即注入", Margin = new Thickness(0, 2, 0, 0) };
        btnInject.Click += (_, _) =>
        {
            FlushConfig();
            ShowOperation(PrivacyIslandRuntime.InjectNow());
            RefreshDiagnostics();
        };
        var btnEject = new Button { Content = "立即弹射", Margin = new Thickness(0, 2, 0, 0) };
        btnEject.Click += (_, _) =>
        {
            FlushConfig();
            ShowOperation(PrivacyIslandRuntime.EjectNow());
            RefreshDiagnostics();
        };
        spActions.Children.Add(btnInject);
        spActions.Children.Add(btnEject);
        var actionItem = Item(Icons.PlugConnectedFilled, "手动注入 / 弹射", "立即向 media_capture.exe 注入或弹射防护 DLL", spActions);

        return Expander(Icons.ScanFilled, "功能测试", "诊断与应用内模拟，无需真实注入即可验证", diagItem, simItem, lessonItem, refreshItem, logsItem, actionItem);
    }

    void SimLesson(bool inClass)
    {
        FlushConfig();
        var controller = PrivacyIslandRuntime.LessonController;
        if (controller == null)
        {
            ShowInfo("课程联动控制器未就绪。", InfoBarSeverity.Warning);
            return;
        }
        controller.ApplyLessonState(inClass);
        RefreshDiagnostics();
        bool paused = PrivacyIslandRuntime.IsPaused;
        ShowInfo(inClass
            ? $"已模拟「上课」。当前防护：{(paused ? "已暂停" : "正常")}。"
            : "已模拟「课间」，已恢复常态。", InfoBarSeverity.Success);
    }

    // ---- helpers ----

    static SettingsExpander Expander(string glyph, string header, string description, params Control[] items)
    {
        var expander = new SettingsExpander
        {
            Header = header,
            Description = description,
            IconSource = new FluentIconSource(glyph),
            Margin = new Thickness(0, 0, 0, 4),
        };
        foreach (var item in items) expander.Items.Add(item);
        return expander;
    }

    static SettingsExpanderItem Item(string glyph, string content, string description, Control footer)
    {
        return new SettingsExpanderItem
        {
            Content = content,
            Description = description,
            IconSource = new FluentIconSource(glyph),
            Footer = footer,
        };
    }

    Button SimulateButton(string text, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 0),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    void WireAutosave()
    {
        _numMin.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == NumericUpDown.ValueProperty);
        _numMax.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == NumericUpDown.ValueProperty);
        _swStealth.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == ToggleSwitch.IsCheckedProperty);

        _swLessonAware.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == ToggleSwitch.IsCheckedProperty);
        _swPauseInClass.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == ToggleSwitch.IsCheckedProperty);
        _swStrongDelayInClass.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == ToggleSwitch.IsCheckedProperty);
        _numClassMin.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == NumericUpDown.ValueProperty);
        _numClassMax.PropertyChanged += (_, e) => MarkConfigDirty(e.Property == NumericUpDown.ValueProperty);
    }

    void MarkConfigDirty(bool changed)
    {
        if (!changed || _loading) return;
        _configDirty = true;
        SaveConfig();
    }

    // ---- data binding ----

    void LoadConfig()
    {
        _loading = true;
        _cfg = PrivacyIslandRuntime.Config;
        if (_cfg == null)
        {
            _loading = false;
            _infoBar.Message = "编排器尚未就绪，配置加载延迟。";
            _infoBar.Severity = InfoBarSeverity.Warning;
            return;
        }

        _numMin.Value = _cfg.MinDelaySeconds;
        _numMax.Value = _cfg.MaxDelaySeconds;
        _swStealth.IsChecked = _cfg.StealthMode;
        _swLessonAware.IsChecked = _cfg.LessonAwareEnabled;
        _swPauseInClass.IsChecked = _cfg.PauseDuringClass;
        _swStrongDelayInClass.IsChecked = _cfg.StrongerDelayDuringClass;
        _numClassMin.Value = _cfg.ClassMinDelaySeconds;
        _numClassMax.Value = _cfg.ClassMaxDelaySeconds;
        _configDirty = false;
        _loading = false;

        RefreshDiagnostics();
        RefreshStats();

        var isAdmin = PrivacyIslandRuntime.Monitor?.Diagnostics()?.Contains("以管理员运行: 是") == true;
        _infoBar.Message = isAdmin
            ? "以管理员身份运行，跨进程注入功能正常。"
            : "未以管理员身份运行，跨进程注入可能失败。请右键以管理员运行 ClassIsland。";
        _infoBar.Severity = isAdmin ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadConfig();
        if (!_stateSubscribed)
        {
            PrivacyIslandRuntime.StateReceived += OnRuntimeState;
            _stateSubscribed = true;
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        FlushConfig();
        if (_stateSubscribed)
        {
            PrivacyIslandRuntime.StateReceived -= OnRuntimeState;
            _stateSubscribed = false;
        }

        base.OnUnloaded(e);
    }

    void SaveConfig()
    {
        if (_loading || !_configDirty || _cfg == null) return;

        int min = (int)(_numMin.Value ?? 3);
        int max = (int)(_numMax.Value ?? 8);
        if (max < min)
        {
            max = min;
            _loading = true;
            _numMax.Value = max;
            _loading = false;
        }

        _cfg.MinDelaySeconds = min;
        _cfg.MaxDelaySeconds = max;
        _cfg.StealthMode = _swStealth.IsChecked == true;

        int classMin = (int)(_numClassMin.Value ?? 10);
        int classMax = (int)(_numClassMax.Value ?? 20);
        if (classMax < classMin)
        {
            classMax = classMin;
            _loading = true;
            _numClassMax.Value = classMax;
            _loading = false;
        }
        _cfg.LessonAwareEnabled = _swLessonAware.IsChecked == true;
        _cfg.PauseDuringClass = _swPauseInClass.IsChecked == true;
        _cfg.StrongerDelayDuringClass = _swStrongDelayInClass.IsChecked == true;
        _cfg.ClassMinDelaySeconds = classMin;
        _cfg.ClassMaxDelaySeconds = classMax;

        PrivacyIslandRuntime.Monitor?.SaveAndApply();
        // 课程联动设置可能变了，立即按当前课程状态重评估（开/关、改加强延迟值都即时生效）。
        PrivacyIslandRuntime.ReapplyLessonState();
        _configDirty = false;
    }

    void FlushConfig()
    {
        SaveConfig();
    }

    void RefreshStats()
    {
        var stats = PrivacyIslandRuntime.Monitor?.Stats;
        _lblStats.Text = stats?.Summary() ?? "（编排器未就绪）";
    }

    void RefreshDiagnostics()
    {
        _lblDiag.Text = PrivacyIslandRuntime.Diagnostics();
    }

    void SimThenRefresh(int state, string message)
    {
        FlushConfig();
        PrivacyIslandRuntime.Simulate(state, message);
        RefreshStats();
        RefreshDiagnostics();
        ShowInfo("已触发模拟摄像头事件。", InfoBarSeverity.Success);
    }

    async void RunFullSimulation()
    {
        FlushConfig();
        PrivacyIslandRuntime.Simulate(Ipc.IpcProtocol.StatusStart, "模拟 DirectShow 捕获开启 [DS]");
        ShowInfo("已开始完整模拟。", InfoBarSeverity.Success);
        await Task.Delay(1500);
        PrivacyIslandRuntime.Simulate(Ipc.IpcProtocol.StatusWatching, "模拟进入监视状态");
        await Task.Delay(1500);
        PrivacyIslandRuntime.Simulate(Ipc.IpcProtocol.StatusStop, "模拟捕获关闭");
        RefreshStats();
        RefreshDiagnostics();
        ShowInfo("完整模拟已完成。", InfoBarSeverity.Success);
    }

    void ResetStats()
    {
        var stats = PrivacyIslandRuntime.Monitor?.Stats;
        if (stats == null)
        {
            ShowInfo("编排器未就绪，无法重置统计。", InfoBarSeverity.Warning);
            return;
        }

        stats.Reset();
        RefreshStats();
        ShowInfo("捕获统计已重置。", InfoBarSeverity.Success);
    }

    void OnRuntimeState(CaptureSnapshot _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            RefreshStats();
            RefreshDiagnostics();
        });
    }

    void ShowOperation(PluginOperationResult result)
    {
        ShowInfo(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    void ShowInfo(string message, InfoBarSeverity severity)
    {
        _infoBar.Message = message;
        _infoBar.Severity = severity;
        _infoBar.IsOpen = true;
    }
}
