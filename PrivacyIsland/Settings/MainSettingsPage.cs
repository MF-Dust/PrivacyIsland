using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using FluentAvalonia.UI.Controls;
using PrivacyIsland.Config;

namespace PrivacyIsland.Settings;

[SettingsPageInfo("privacy.island.settings", "隐私防护", Icons.ShieldCheckmarkFilled, Icons.ShieldCheckmarkFilled, ClassIsland.Core.Enums.SettingsWindow.SettingsPageCategory.External)]
public class MainSettingsPage : SettingsPageBase
{
    readonly ProtectionSettingsEditor _editor;
    readonly DiagnosticsTestPanel _diagnosticsPanel;
    readonly InfoBar _infoBar = new()
    {
        Title = "提示",
        Severity = InfoBarSeverity.Informational,
        IsOpen = true,
        IsClosable = false,
    };
    PluginConfig? _config;
    bool _configDirty;

    public MainSettingsPage()
    {
        _editor = new ProtectionSettingsEditor();
        _editor.Changed += OnSettingsChanged;
        _diagnosticsPanel = new DiagnosticsTestPanel(FlushConfig, ShowInfo);

        var tabs = new TabControl
        {
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 0),
        };
        tabs.Items.Add(_editor.ProtectionTab);
        tabs.Items.Add(_editor.LessonTab);
        tabs.Items.Add(SettingsUi.CategoryTab(Icons.ScanFilled, "诊断与测试", _diagnosticsPanel));

        var root = new StackPanel
        {
            Spacing = 8,
            Children = { TitleSection(), _infoBar, tabs },
        };
        root.Classes.Add("settings-container");
        root.Classes.Add("animated-intro");

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadConfig();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        FlushConfig();
        base.OnUnloaded(e);
    }

    void LoadConfig()
    {
        _config = PrivacyIslandRuntime.Config;
        if (_config is null)
        {
            ShowInfo("编排器尚未就绪，配置加载延迟。", InfoBarSeverity.Warning);
            return;
        }

        _editor.Load(_config);
        _configDirty = false;
        bool isAdmin = PrivacyIslandRuntime.IsAdministrator;
        ShowInfo(
            isAdmin
                ? "以管理员身份运行，跨进程注入功能正常。"
                : "未以管理员身份运行，跨进程注入可能失败。请右键以管理员运行 ClassIsland。",
            isAdmin ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    void OnSettingsChanged()
    {
        _configDirty = true;
        SaveConfig();
    }

    void SaveConfig()
    {
        if (!_configDirty || _config is null) return;
        _editor.ApplyTo(_config);
        PrivacyIslandRuntime.Monitor?.SaveAndApply();
        PrivacyIslandRuntime.ReapplyLessonState();
        _configDirty = false;
    }

    void FlushConfig() => SaveConfig();

    void ShowInfo(string message, InfoBarSeverity severity)
    {
        _infoBar.Message = message;
        _infoBar.Severity = severity;
        _infoBar.IsOpen = true;
    }

    static Control TitleSection() => new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 12,
        Margin = new Thickness(0, 4, 0, 4),
        Children =
        {
            new FontIcon
            {
                Glyph = Icons.ShieldCheckmarkFilled,
                FontSize = 28,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
            new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new Label { Content = "隐私防护", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "按场景管理监测、延迟和诊断功能", Opacity = 0.72 },
                },
            },
        },
    };
}
