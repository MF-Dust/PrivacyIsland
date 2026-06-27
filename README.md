# PrivacyIsland

PrivacyIsland 是一个面向 ClassIsland v2 的摄像头防护插件，用于在检测到希沃摄像头进程访问摄像头时，接入 ClassIsland 的提醒、自动化、课程联动和诊断能力。

## 功能

- 摄像头捕获开始、进入监视、关闭时显示 ClassIsland 提醒。
- 支持 1-30 秒随机延迟，可在设置页调整最小值和最大值。
- 支持隐身模式，以及 ClassIsland 提醒设置中的语音播报、提醒文案、提醒颜色和显示时长配置。
- 支持课程联动：上课时自动暂停防护，或切换到更强的延迟范围；课间自动恢复。
- 提供自动化触发器：摄像头启动时、开始监视时、摄像头关闭时。
- 提供自动化行动：暂停摄像头防护、立即注入防护、立即弹射防护、立即设定延迟。
- 提供自动化规则：摄像头正在被访问。
- 设置页内置诊断、模拟摄像头事件、模拟课程状态、手动注入和弹射。

## 环境要求

- Windows
- .NET 8 SDK
- ClassIsland v2
- 管理员权限：跨进程注入通常需要以管理员身份运行 ClassIsland。

## 项目结构

```text
PrivacyIsland/                 ClassIsland 插件主体
PrivacyIsland/Native/          随插件分发的原生 DLL 与注入器
PrivacyIsland.SmokeTest/       共享内存桥接与 IPC 流程烟雾测试
GUIDE.md                       ClassIsland 插件开发速查
```

## 构建

```powershell
dotnet build PrivacyIsland\PrivacyIsland.csproj -c Release -p:CreateCipx=true
```

构建完成后，插件包位于：

```text
PrivacyIsland\cipx\PrivacyIsland.cipx
```

## 安装

1. 构建或下载 `PrivacyIsland.cipx`。
2. 在 ClassIsland 的插件管理中导入该 `.cipx` 文件。
3. 以管理员身份重新启动 ClassIsland。
4. 打开 ClassIsland 设置中的“摄像头防护”页面，检查诊断信息并按需调整配置。

## 使用

安装后插件会随 ClassIsland 启动并监控 `media_capture.exe`。检测到目标进程后，插件会尝试注入防护组件，并通过共享内存接收摄像头状态。

防护配置位于“摄像头防护”设置页：

- 捕获延迟：调整随机延迟范围与隐身模式。
- 课程联动：按上课/课间状态自动切换防护策略。
- 捕获统计：查看摄像头访问记录。
- 功能测试：查看诊断信息，模拟事件，手动注入或弹射。

提醒配置位于 ClassIsland 的提醒/通知设置中，在“摄像头防护”提醒提供方里调整通知开关、语音、文案、颜色和显示时长。

## 自动化

插件注册了以下 ClassIsland 自动化能力：

- 触发器：摄像头启动时、开始监视时、摄像头关闭时。
- 行动：暂停摄像头防护、立即注入防护、立即弹射防护、立即设定延迟。
- 规则：摄像头正在被访问。

可以用这些能力组合课程表、时间段或其他 ClassIsland 规则，实现按场景切换防护策略。

## 测试

运行烟雾测试：

```powershell
dotnet run --project PrivacyIsland.SmokeTest\PrivacyIsland.SmokeTest.csproj
```

设置页中的“模拟摄像头事件”会走完整 IPC 分发路径，可用于验证提醒、自动化触发器、规则和统计，不需要真实注入。

## 排障

- 提示注入失败：确认 ClassIsland 已以管理员身份运行。
- 诊断显示缺少 DLL 或注入器：确认 `PrivacyIsland.cipx` 中包含 `PrivacyIslandHook.dll` 和 `nmm_injector.exe`。
- 找不到目标进程：确认希沃相关摄像头进程 `media_capture.exe` 正在运行。
- 修改隐身模式后未生效：重新注入或重启 ClassIsland。
- 日志位置：插件日志写入 ClassIsland 宿主日志，不单独生成日志目录。

## 致谢

感谢 ClassIsland 项目及其插件生态提供的基础能力。

感谢 NoMoreMonitor 项目提供的原始思路与原生 Hook 实现。
