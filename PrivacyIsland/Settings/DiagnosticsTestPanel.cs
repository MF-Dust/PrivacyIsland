using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using PrivacyIsland.Ipc;
using PrivacyIsland.Orchestrator;

namespace PrivacyIsland.Settings;

/// <summary>诊断、统计、模拟及手动维护操作；仅在页面可见时订阅事件和刷新。</summary>
internal sealed class DiagnosticsTestPanel : UserControl
{
    readonly TextBlock _stats = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _diagnostics = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
    };
    readonly Action _flushConfig;
    readonly Action<string, InfoBarSeverity> _showInfo;
    DispatcherTimer? _timer;
    bool _subscribed;

    public DiagnosticsTestPanel(Action flushConfig, Action<string, InfoBarSeverity> showInfo)
    {
        _flushConfig = flushConfig;
        _showInfo = showInfo;
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                StatsSection(),
                DiagnosticsSection(),
                SimulationSection(),
            },
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        RefreshStats();
        RefreshDiagnostics(request: true);
        if (!_subscribed)
        {
            PrivacyIslandRuntime.StateReceived += OnRuntimeState;
            PrivacyIslandRuntime.PrivacyRiskReceived += OnPrivacyRisk;
            _subscribed = true;
        }

        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => RefreshDiagnostics();
        }
        _timer.Start();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_subscribed)
        {
            PrivacyIslandRuntime.StateReceived -= OnRuntimeState;
            PrivacyIslandRuntime.PrivacyRiskReceived -= OnPrivacyRisk;
            _subscribed = false;
        }
        _timer?.Stop();
        base.OnUnloaded(e);
    }

    SettingsExpander StatsSection()
    {
        var content = new StackPanel { Spacing = 8, Children = { _stats } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var refresh = SettingsUi.ActionButton(Icons.ScanFilled, "刷新统计");
        refresh.Click += (_, _) =>
        {
            RefreshStats();
            _showInfo("统计信息已刷新。", InfoBarSeverity.Informational);
        };
        var reset = SettingsUi.ActionButton(Icons.DeleteFilled, "重置统计");
        reset.Click += (_, _) => ResetStats();
        buttons.Children.Add(refresh);
        buttons.Children.Add(reset);
        content.Children.Add(buttons);

        var item = SettingsUi.Item(Icons.ChartMultipleFilled, "捕获统计", "自插件加载以来的摄像头访问记录", content);
        return SettingsUi.Expander(Icons.ChartMultipleFilled, "捕获统计", "摄像头捕获历史记录", item);
    }

    SettingsExpander DiagnosticsSection()
    {
        var info = SettingsUi.Item(Icons.InfoFilled, "诊断信息", "当前注入器、DLL、IPC 与权限状态", _diagnostics);
        var refresh = SettingsUi.ActionButton(Icons.ScanFilled, "刷新诊断");
        refresh.Click += (_, _) =>
        {
            RefreshDiagnostics(request: true);
            _showInfo("已请求后台刷新诊断信息。", InfoBarSeverity.Informational);
        };
        var refreshItem = SettingsUi.Item(Icons.SettingsFilled, "刷新诊断信息", "重新检测注入器、DLL、IPC 和目标进程状态", refresh);
        var logs = SettingsUi.ActionButton(Icons.FolderFilled, "查看日志说明");
        logs.Click += (_, _) => ShowOperation(PrivacyIslandRuntime.OpenLogsFolder());
        var logsItem = SettingsUi.Item(Icons.FolderFilled, "ClassIsland 日志", "PrivacyIsland 运行日志写入 ClassIsland 宿主日志", logs);
        return SettingsUi.Expander(Icons.InfoFilled, "运行状态", "查看诊断信息和宿主日志位置", info, refreshItem, logsItem);
    }

    SettingsExpander SimulationSection()
    {
        var camera = new StackPanel { Spacing = 4 };
        camera.Children.Add(SimulateButton("模拟开启", () => SimThenRefresh(IpcProtocol.StatusStart, "模拟 DirectShow 捕获开启 [DS]")));
        camera.Children.Add(SimulateButton("模拟监视", () => SimThenRefresh(IpcProtocol.StatusWatching, "模拟进入监视状态")));
        camera.Children.Add(SimulateButton("模拟关闭", () => SimThenRefresh(IpcProtocol.StatusStop, "模拟捕获关闭")));
        camera.Children.Add(SimulateButton("完整模拟", RunFullSimulation));
        var cameraItem = SettingsUi.Item(Icons.PlayFilled, "模拟摄像头事件", "走完整 IPC 路径触发提醒/触发器/规则/统计，无需真注入", camera);

        var privacy = new StackPanel { Spacing = 4 };
        privacy.Children.Add(SimulateButton("模拟摄像头访问", () => SimulateRisk(PrivacyRiskKind.Camera)));
        privacy.Children.Add(SimulateButton("模拟屏幕采集", () => SimulateRisk(PrivacyRiskKind.ScreenCapture)));
        privacy.Children.Add(SimulateButton("模拟远程控制", () => SimulateRisk(PrivacyRiskKind.RemoteControl)));
        privacy.Children.Add(SimulateButton("模拟麦克风访问", () => SimulateRisk(PrivacyRiskKind.Microphone)));
        var privacyItem = SettingsUi.Item(Icons.ShieldErrorFilled, "模拟隐私风险", "验证隐私事件、触发器和规则，不会结束任何进程", privacy);

        var lesson = new StackPanel { Spacing = 4 };
        lesson.Children.Add(SimulateButton("模拟上课", () => SimLesson(true)));
        lesson.Children.Add(SimulateButton("模拟课间", () => SimLesson(false)));
        var lessonItem = SettingsUi.Item(Icons.CalendarFilled, "模拟课程状态", "按当前课程联动设置应用上课/课间策略，无需等真实课表", lesson);

        var maintenance = new StackPanel { Spacing = 4 };
        var inject = SettingsUi.ActionButton(Icons.PlugConnectedFilled, "立即注入");
        inject.Click += (_, _) =>
        {
            _flushConfig();
            ShowOperation(PrivacyIslandRuntime.InjectNow());
            RefreshDiagnostics(request: true);
        };
        var eject = SettingsUi.ActionButton(Icons.PlugDisconnectedFilled, "立即弹射");
        eject.Click += (_, _) =>
        {
            _flushConfig();
            ShowOperation(PrivacyIslandRuntime.EjectNow());
            RefreshDiagnostics(request: true);
        };
        maintenance.Children.Add(inject);
        maintenance.Children.Add(eject);
        var maintenanceItem = SettingsUi.Item(Icons.PlugConnectedFilled, "手动注入 / 弹射", "立即向 media_capture.exe 注入或弹射防护 DLL", maintenance);

        return SettingsUi.Expander(Icons.PlayFilled, "应用内模拟与操作", "无需真实注入即可验证事件和执行维护操作", cameraItem, privacyItem, lessonItem, maintenanceItem);
    }

    Button SimulateButton(string text, Action onClick)
    {
        var button = SettingsUi.ActionButton(Icons.PlayFilled, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    void SimLesson(bool inClass)
    {
        _flushConfig();
        var controller = PrivacyIslandRuntime.LessonController;
        if (controller is null)
        {
            _showInfo("课程联动控制器未就绪。", InfoBarSeverity.Warning);
            return;
        }

        controller.ApplyLessonState(inClass);
        RefreshDiagnostics();
        _showInfo(inClass
            ? $"已模拟「上课」。当前防护：{(PrivacyIslandRuntime.IsPaused ? "已暂停" : "正常")}。"
            : "已模拟「课间」，已恢复常态。", InfoBarSeverity.Success);
    }

    void SimThenRefresh(int state, string message)
    {
        _flushConfig();
        PrivacyIslandRuntime.Simulate(state, message);
        RefreshStats();
        RefreshDiagnostics();
        _showInfo("已触发模拟摄像头事件。", InfoBarSeverity.Success);
    }

    void SimulateRisk(PrivacyRiskKind kind)
    {
        _flushConfig();
        PrivacyIslandRuntime.SimulatePrivacyRisk(kind);
        RefreshDiagnostics();
        _showInfo("已触发模拟隐私风险。", InfoBarSeverity.Success);
    }

    async void RunFullSimulation()
    {
        _flushConfig();
        PrivacyIslandRuntime.Simulate(IpcProtocol.StatusStart, "模拟 DirectShow 捕获开启 [DS]");
        _showInfo("已开始完整模拟。", InfoBarSeverity.Success);
        await Task.Delay(1500);
        PrivacyIslandRuntime.Simulate(IpcProtocol.StatusWatching, "模拟进入监视状态");
        await Task.Delay(1500);
        PrivacyIslandRuntime.Simulate(IpcProtocol.StatusStop, "模拟捕获关闭");
        RefreshStats();
        RefreshDiagnostics();
        _showInfo("完整模拟已完成。", InfoBarSeverity.Success);
    }

    void ResetStats()
    {
        var stats = PrivacyIslandRuntime.Monitor?.Stats;
        if (stats is null)
        {
            _showInfo("编排器未就绪，无法重置统计。", InfoBarSeverity.Warning);
            return;
        }

        stats.Reset();
        RefreshStats();
        _showInfo("捕获统计已重置。", InfoBarSeverity.Success);
    }

    void RefreshStats()
        => _stats.Text = PrivacyIslandRuntime.Monitor?.Stats.Summary() ?? "（编排器未就绪）";

    void RefreshDiagnostics(bool request = false)
    {
        if (request) PrivacyIslandRuntime.RequestDiagnosticsRefresh();
        _diagnostics.Text = PrivacyIslandRuntime.Diagnostics();
    }

    void OnRuntimeState(CaptureSnapshot _)
        => Dispatcher.UIThread.Post(RefreshStats);

    void OnPrivacyRisk(PrivacyRiskSnapshot _)
        => Dispatcher.UIThread.Post(() => RefreshDiagnostics());

    void ShowOperation(PluginOperationResult result)
        => _showInfo(result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
}
