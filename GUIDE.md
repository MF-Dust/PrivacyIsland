# ClassIsland 插件开发要点（PrivacyIsland 速查）

> 摘自 ClassIsland 简体中文开发文档 <https://docs.classisland.tech/dev/> 与本仓库现有源码核对。
> 目标 API 版本 **2.0.0.0**（ClassIsland 2.x，**UI 已迁移到 Avalonia / FluentAvalonia**，不再是 WPF）。
> API 参考：<https://api.docs.classisland.tech/>

## 0. 技术栈与关键差异

- 运行时 **.NET 8**，语言 C#，DI 基于 `Microsoft.Extensions.Hosting`。
- **UI 是 Avalonia**（搭配 FluentAvalonia 控件，如 `SettingsExpander`、`InfoBar`、`NumericUpDown`、`ToggleSwitch`、`FontIcon`）。
  - 资源引用用 **`avares://程序集名/资源路径`**（不是 WPF 的 `pack://`）。
  - 英文版文档很多页仍写 WPF/`pack://`，**以中文版/2.x 为准**。
- 部分中文页标注"还在编写中"，**规则集 / UI / 部分 IPC 子页仍是占位 stub**，缺实现细节，需结合 [API 参考](https://api.docs.classisland.tech/) 或现有源码补全（本文已把项目里验证过的写法并入对应章节）。

## 1. 插件项目结构

用官方模板创建（推荐）：

```bash
dotnet new install ClassIsland.PluginTemplate.Packaging   # 安装模板
mkdir MyPlugin && cd MyPlugin
dotnet new cipx-template -n MyPlugin                       # 生成项目
```

核心文件：`MyPlugin.csproj`、`Plugin.cs`（入口）、`manifest.yml`、`icon.png`、`README.md`。

csproj 要点（动态加载插件）：
- `<EnableDynamicLoading>True</EnableDynamicLoading>`
- 对 SDK 引用设 `ExcludeAssets="runtime"`（依赖 `ClassIsland.PluginSdk`，宿主已提供运行时，避免重复加载）。

### manifest.yml 字段

| 字段 | 必填 | 说明 |
|------|------|------|
| `id` | 是 | 插件唯一标识（如 `privacy.island`） |
| `entranceAssembly` | 是 | 入口程序集 DLL 文件名（如 `PrivacyIsland.dll`） |
| `apiVersion` | 是 | 目标 ClassIsland 版本（本项目 `2.0.0.0`） |
| `name` / `description` / `author` / `version` / `url` / `icon` / `readme` | 否 | 元数据 |

发布上架追加：`repoOwner`、`repoName`、`assetsRoot`（`<默认分支>/<路径>`）、`supportedOSPlatforms`、`tagPattern`（多版本兼容，如 `1.*.*.*`）。

## 2. 插件入口类

`[PluginEntrance]` 标注、继承 `PluginBase`，在 `Initialize` 里注册服务（标准 .NET DI）：

```csharp
[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton(sp => new CaptureMonitor(PluginConfigFolder, sp.GetRequiredService<ILogger<CaptureMonitor>>()));
        services.AddHostedService(sp => sp.GetRequiredService<CaptureMonitor>()); // 后台常驻服务
        services.AddNotificationProvider<CameraNotificationProvider>();
        services.AddTrigger<CameraStartedTrigger>();
        services.AddAction<PauseProtectionAction>();
        services.AddRule("privacy.island.rule.cameraActive", "摄像头正在被访问", "", _ => PrivacyIslandRuntime.CameraActive);
        services.AddSettingsPage<MainSettingsPage>();
    }
}
```

常用扩展：`AddSettingsPage<T>`、`AddNotificationProvider<T[,SettingsControl]>`、`AddComponent<T[,SettingsControl]>`、`AddTrigger<T>`、`AddAction<T>`、`AddRule(id,name,desc,Func<cfg?,bool>)`、`AddHostedService`、`AddSingleton`。

## 3. 基础知识（重要约束）

- **程序集隔离**：每个插件加载到独立 `AssemblyLoadContext`，可用不同依赖版本；**两个插件中同名类型不是同一类型**（除非来自同一 Assembly 实例）。
- **配置目录**：用 `PluginConfigFolder` 定位配置，**绝不要写到插件安装目录**（更新时会被删、不备份）。可用 `ConfigureFileHelper.LoadConfig<T>(Path.Combine(PluginConfigFolder, "Settings.json"))`。
- **应用实例**：`AppBase.Current` 订阅生命周期、`Restart()` / `Stop()`。

## 4. 提醒 / 通知（Notification）

继承 `NotificationProviderBase`（或带设置的 `NotificationProviderBase<TSettings>`），用特性声明信息，可再声明通知通道：

```csharp
[NotificationProviderInfo("GUID", "摄像头防护", "希沃摄像头访问提醒")]
[NotificationChannelInfo(ChannelId, "摄像头访问", "", "摄像头开启/监视/关闭时提醒")]
public class CameraNotificationProvider : NotificationProviderBase
{
    void Show(string text, IBrush brush, TimeSpan duration, bool speech)
    {
        Channel(ChannelId).ShowNotification(new NotificationRequest
        {
            MaskContent = NotificationContent.CreateSimpleTextContent(text, c =>
            {
                c.Color = brush;
                c.Duration = duration;
                c.IsSpeechEnabled = speech;
                c.SpeechContent = text;
            }),
            // OverlayContent = NotificationContent.CreateSimpleTextContent(...) // 可选，遮罩后显示
        });
    }
}
```

- `NotificationRequest`：`MaskContent`（必需，先吸睛）、`OverlayContent`（可选）、`Mask/OverlayDuration`、`Mask/OverlaySpeechContent`。
- 内容工厂：`NotificationContent.CreateSimpleTextContent(...)`、`CreateTwoIconsMask(...)`；可配 `Color`、`Duration`、`IsSpeechEnabled`、`SpeechContent`。
- 注册：`services.AddNotificationProvider<T>()`，带设置界面用 `AddNotificationProvider<T, TSettingsControl>()`。
- 提供方本质是托管服务（随宿主启动），可在构造函数注入 `ILessonsService` 等订阅事件。

## 5. 自动化：触发器 / 行动 / 规则

文档的"规则集"页只讲概念（调用方 + 提供方），实现以现有源码为准：

**触发器** —— 继承 `TriggerBase<TConfig>`，`[TriggerInfo(id,name,desc)]`，在 `Loaded`/`UnLoaded` 订阅外部事件，满足条件时调用 `Trigger()`：

```csharp
[TriggerInfo("privacy.island.trigger.cameraStarted", "摄像头开启时", "")]
public class CameraStartedTrigger : TriggerBase<EmptyConfig>
{
    void OnState(CaptureSnapshot s) { if (s.State == IpcProtocol.StatusStart) Trigger(); }
    public override void Loaded()   => PrivacyIslandRuntime.StateReceived += OnState;
    public override void UnLoaded() => PrivacyIslandRuntime.StateReceived -= OnState;
}
```

**行动** —— 继承 `ActionBase<TConfig>`，`[ActionInfo(id,name,desc)]`，重写 `OnInvoke`；可逆行动再重写 `OnRevert`（规则不再满足时自动回退）：

```csharp
[ActionInfo("privacy.island.action.pause", "暂停摄像头防护", "")]
public class PauseProtectionAction : ActionBase<EmptyConfig>
{
    protected override async Task OnInvoke() { await base.OnInvoke(); PrivacyIslandRuntime.Pause(); }
    protected override async Task OnRevert() { await base.OnRevert(); PrivacyIslandRuntime.Resume(); }
}
```

**规则** —— 简单布尔规则可在 `Initialize` 内联：`services.AddRule(id, name, desc, cfg => bool)`。
无配置项统一用空类型：`public sealed class EmptyConfig { }`，泛型写 `<EmptyConfig>`。

规则集架构（文档"规则集"页目前是占位 stub，仅概念）：分**规则集调用方**（消费用户配置的规则集）与**规则提供方**（判断单条规则是否成立）；当规则状态变化，规则提供方通知规则集服务，后者通过 **`StatusUpdated`** 事件通知各调用方。自动化整体逻辑：**触发器触发 → 校验规则集是否成立（若配置）→ 执行行动**。`AddRule` 注册的就是一个规则提供方，回调随被查询时求值；要做"状态变化即时通知"，让规则依赖的运行时状态在变化时能被重新评估即可。

## 6. 设置页面（SettingsPage）

继承 `SettingsPageBase`，用 `[SettingsPageInfo]` 声明（id、名称、图标、分类）。分类用 `SettingsWindow.SettingsPageCategory`：**插件用 `External`**（其余 `Internal`/`About`/`Debug`）。

```csharp
[SettingsPageInfo("privacy.island.settings", "摄像头防护", "", "",
    ClassIsland.Core.Enums.SettingsWindow.SettingsPageCategory.External)]
public class MainSettingsPage : SettingsPageBase
{
    public MainSettingsPage()
    {
        Content = new ScrollViewer { Content = /* Avalonia 控件树 */ };
        LoadConfig();
    }
}
```

- 纯代码构建 UI 用 Avalonia 控件；推荐 FluentAvalonia 的 `SettingsExpander` / `SettingsExpanderItem` 组织分组项。
- 注册：`services.AddSettingsPage<MainSettingsPage>()`。
- 若用 XAML，加主题资源保持一致：`Foreground="{DynamicResource ...}"` 等（具体键以 Avalonia/FluentAvalonia 主题为准）。

## 7. 组件（主界面信息单元）

继承 `ComponentBase`（有配置用 `ComponentBase<TSettings>`），`[ComponentInfo(GUID,name,icon,desc)]`，注册 `AddComponent<T>()` 或 `AddComponent<T, TSettingsControl>()`。
注意：`Settings` 属性**初始化后才可用，勿在构造函数访问**；配置控件无需 `ComponentInfo`。

## 8. 事件 & 课程服务（ILessonsService）

通过构造函数注入 `ILessonsService`（或 `GetService<ILessonsService>()`）。

- 生命周期：`AppBase.Current.AppStarted` / `AppStopping`。
- 主循环（每 ~50ms）：`PreMainTimerTicked` / `PostMainTimerTicked`。
- 课表状态：`OnClass` / `OnBreakingTime` / `OnAfterSchool` / `CurrentTimeStateChanged`。
- 常用属性：`CurrentSubject`、`CurrentState`（`TimeState`）、`CurrentClassPlan`、`OnClassLeftTime`、`OnBreakingTimeLeftTime`、`IsClassPlanLoaded`。
- **务必在 `UnLoaded`/`Unloaded` 取消订阅外部服务事件，否则内存泄漏。**

## 9. URI 导航

注入 `IUriNavigationService`，插件用 `HandlePluginsNavigation("foo/bar", args => { ... })` 注册，对应 `classisland://plugins/foo/bar`。导航时先精确匹配，再逐级向父路径回退。程序内导航用 `Navigate(new Uri(...))`，UI 用 `NavHyperlink`。插件无法用 `HandleAppNavigation`（仅核心）。

## 10. 跨进程通信（ClassIsland IPC）

> 注意区分：本项目 `Ipc/` 是**自有的**命名管道协议（插件 ↔ 注入到 `media_capture.exe` 的防护 DLL），与下面 ClassIsland 自带的 IPC 是两套东西。需要让外部程序读取课表/课程状态时才用 ClassIsland IPC。

- 基于**命名管道**，使用 `dotnetCampus.Ipc` 通讯库，封装在 **`ClassIsland.Shared.Ipc`**（兼容 .NET Framework 4.7.2+ 与 .NET 8.0+）。
- 外部程序可：获取当前科目、按课程动态调整配置、响应上下课事件等。
- 公开接口示例：`IPublicLessonsService`（跨进程版课程服务，对应进程内 `ILessonsService`）。
- 文档分三块：**使用 IPC**（客户端连接）、**扩展接口**（插件自定义 IPC 服务）、**API 参考**（可调用的公开服务与事件 ID）。ClassIsland 自身也作为 IPC 客户端用于 URL 协议路由。

## 11. 发布插件

1. 打包为 `.cipx`（仓库已含 `cipx` 工具目录）。
2. 上架前置：内容合规、开源许可证、代码托管 GitHub。
3. 上传 `.cipx` 到仓库 Release，Tag 严格 `a.b.c.d`（如 `1.2.3.4`）。
4. manifest 重命名为插件 id，提交到 [PluginIndex](https://github.com/ClassIsland/PluginIndex) 的 `index/plugins-v2` 并发 PR 审核。
5. 多版本兼容用 `tagPattern`。

## 附录 A：API 参考关键类型成员

> 摘自 <https://api.docs.classisland.tech/>。签名以文档为准，少数页缺失处用本项目验证写法补注。

### NotificationContent — `ClassIsland.Core.Models.Notification`（继承 `ObservableRecipient`）

属性：
- `object? Content` —— 提醒内容（可放任意可视元素/数据对象）
- `DataTemplate? ContentTemplate` / `object? ContentTemplateResourceKey` —— 自定义渲染模板
- `IBrush? Color` —— 涟漪/主题色
- `TimeSpan Duration` —— 本段显示时长
- `DateTime? EndTime` —— 指定结束时间点（动态算时长）
- `bool IsSpeechEnabled` / `string SpeechContent` —— 语音播报（超总时长会被截断）

静态工厂（均返回 `NotificationContent`）：
```csharp
NotificationContent.CreateSimpleTextContent(string text, Action<NotificationContent>? factory = null);
NotificationContent.CreateRollingTextContent(string text, TimeSpan? duration = null, int repeatCount = 2, Action<NotificationContent>? factory = null);
NotificationContent.CreateTwoIconsMask(string text, string leftIcon = "lucide()", string rightIcon = "lucide()", bool hasRightIcon = true, Action<NotificationContent>? factory = null);
```
（图标用 lucide 语法，`factory` 回调里设 `Duration`/`Color`/`IsSpeechEnabled` 等。**时长与语音配置在 `NotificationContent` 上，而非 `NotificationRequest`。**）

### NotificationRequest — `ClassIsland.Core.Models.Notification`

- `NotificationContent MaskContent { get; set; }` —— 遮罩内容（**必需**）
- `NotificationContent? OverlayContent { get; set; }` —— 正文内容（可空，遮罩后显示）
- `Guid ChannelId { get; set; }` —— 发送渠道 ID
- `NotificationSettings RequestNotificationSettings { get; set; }` —— 本次提醒特殊设置
- `CancellationToken CancellationToken { get; }` —— 被取消令牌
- `CancellationToken CompletedToken { get; }` —— 显示完成令牌

### NotificationProviderBase — `ClassIsland.Core.Abstractions.Services.NotificationProviders`

- `void ShowNotification(NotificationRequest request)`
- `Task ShowNotificationAsync(NotificationRequest request)`
- `void ShowChainedNotifications(params NotificationRequest[] requests)` —— 连续提醒（一起取消）
- `Task ShowChainedNotificationsAsync(NotificationRequest[] requests)`
- `protected NotificationChannel Channel(string id)` / `Channel(Guid id)` —— 取通知渠道
- 属性：`Name` / `Description` / `Guid ProviderGuid` / `object? SettingsElement`
- 泛型 `NotificationProviderBase<TSettings>` 额外提供注入的 `Settings` 属性。

### ActionBase&lt;TSettings&gt; — `ClassIsland.Core.Abstractions.Automation`（`where TSettings : class`）

- 可重写：`Task OnInvoke()`、`Task OnRevert()`、`OnInterrupted()`
- `protected TSettings Settings { get; }`
- 其它：`IsRevertable`、`InterruptCancellationToken`、`ActionItem`、`ActionSet`；`InvokeAsync()`、`RevertAsync()`、`GetInstance()`

### TriggerBase&lt;T&gt; — `ClassIsland.Core.Abstractions.Automation`（`where T : class`）

- `Trigger()` —— 触发 ／ `TriggerRevert()` —— 回退
- `Loaded()` / `UnLoaded()` —— 加载/卸载（在此订阅/退订外部事件）
- `protected T Settings { get; }`

### ILessonsService — `ClassIsland.Core.Abstractions.Services`

继承 `INotifyPropertyChanged`、`INotifyPropertyChanging`、`IPublicLessonsService`。

方法：
- `ClassPlan? GetClassPlanByDate(DateTime date, out Guid? guid)`
- `ObservableCollection<int> GetCyclePositionsByDate(DateTime? referenceTime = null)`
- `void StartMainTimer()` / `void StopMainTimer()`

事件（均 `EventHandler?`）：`PreMainTimerTicked`、`PostMainTimerTicked`、`OnClass`、`OnBreakingTime`、`OnAfterSchool`、`CurrentTimeStateChanged`。

属性（来自 `IPublicLessonsService`）：`Subject? CurrentSubject`、`TimeState CurrentState`、`TimeLayoutItem CurrentTimeLayoutItem`、`ClassPlan? CurrentClassPlan`、`TimeSpan OnClassLeftTime`、`TimeSpan OnBreakingTimeLeftTime`、`bool IsClassPlanLoaded`、`bool IsLessonConfirmed`、`bool IsClassPlanEnabled`。

### SettingsPageCategory（枚举）— `ClassIsland.Core.Enums.SettingsWindow`

| 值 | 含义 |
|----|------|
| `Internal = 0` | 内部设置页（**不建议**插件用） |
| `External = 1` | 扩展设置页（**插件用这个**） |
| `About = 2` | 关于页 |
| `Debug = 3` | 调试页 |

## 参考链接

- 开发文档总览 <https://docs.classisland.tech/dev/>
- 插件基础 <https://docs.classisland.tech/dev/plugins/basics.html>
- 创建项目 <https://docs.classisland.tech/dev/plugins/create-project.html>
- 提醒 <https://docs.classisland.tech/dev/notifications/>
- 设置页面 <https://docs.classisland.tech/dev/settings-page.html>
- 事件 <https://docs.classisland.tech/dev/events.html>
- 组件 <https://docs.classisland.tech/dev/components.html>
- 课程服务 <https://docs.classisland.tech/dev/lessons-service.html>
- URI 导航 <https://docs.classisland.tech/dev/uri-navigation.html>
- 规则集（概念 stub） <https://docs.classisland.tech/dev/ruleset/>
- 跨进程通信 <https://docs.classisland.tech/dev/ipc/>
- 发布 <https://docs.classisland.tech/dev/plugins/publishing.html>
- 提醒内容 <https://docs.classisland.tech/dev/notifications/notification-content.html>
- API 参考首页 <https://api.docs.classisland.tech/>
  - NotificationContent <https://api.docs.classisland.tech/api/ClassIsland.Core.Models.Notification.NotificationContent.html>
  - NotificationRequest <https://api.docs.classisland.tech/api/ClassIsland.Core.Models.Notification.NotificationRequest.html>
  - NotificationProviderBase <https://api.docs.classisland.tech/api/ClassIsland.Core.Abstractions.Services.NotificationProviders.NotificationProviderBase.html>
  - ActionBase&lt;T&gt; <https://api.docs.classisland.tech/api/ClassIsland.Core.Abstractions.Automation.ActionBase-1.html>
  - TriggerBase&lt;T&gt; <https://api.docs.classisland.tech/api/ClassIsland.Core.Abstractions.Automation.TriggerBase-1.html>
  - ILessonsService <https://api.docs.classisland.tech/api/ClassIsland.Core.Abstractions.Services.ILessonsService.html>
  - SettingsPageCategory <https://api.docs.classisland.tech/api/ClassIsland.Core.Enums.SettingsWindow.SettingsPageCategory.html>
- 插件模板 <https://github.com/ClassIsland/PluginTemplate>
