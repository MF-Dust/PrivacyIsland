using System.Threading;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.Hosting;
using PrivacyIsland.Logging;

namespace PrivacyIsland.Orchestrator;

/// <summary>
/// 课程感知联动：订阅 ClassIsland 的 <see cref="ILessonsService"/> 课程状态事件，
/// 按配置在上课时自动暂停防护或切换"加强延迟"，课间/放学自动恢复。
/// 全部行为受 <see cref="Config.PluginConfig"/> 开关控制，默认关闭即完全不介入。
/// </summary>
public sealed class LessonAwareController : IHostedService, IDisposable
{
    readonly ILessonsService? _lessons;   // 可空：宿主未提供时本控制器降级为空操作，不拖垮插件加载
    bool _inClass;                         // 最近一次已知的课程状态（true=上课中）
    bool _subscribed;

    public LessonAwareController(ILessonsService? lessons)
    {
        _lessons = lessons;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        PrivacyIslandRuntime.LessonController = this;

        if (_lessons != null && !_subscribed)
        {
            _lessons.OnClass += OnClass;
            _lessons.OnBreakingTime += OnBreak;
            _lessons.OnAfterSchool += OnAfterSchool;
            _subscribed = true;
            PluginLog.Info("[课程联动] 已订阅课程状态事件");
        }
        else if (_lessons == null)
        {
            PluginLog.Warn("[课程联动] 未获取到课程服务（ILessonsService），课程联动不可用");
        }

        // 启动时按当前配置评估一次：默认视为非上课，确保未启用时清掉 lesson 暂停源/延迟覆盖。
        // 限制：若启动时正处于上课时段，要到下一次状态切换才会应用（偏保守，倾向保留防护）。
        Reapply();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    void OnClass(object? sender, EventArgs e) => ApplyLessonState(true);
    void OnBreak(object? sender, EventArgs e) => ApplyLessonState(false);
    void OnAfterSchool(object? sender, EventArgs e) => ApplyLessonState(false);

    /// <summary>切换已知课程状态并应用。供事件与设置页"模拟上课/课间"调用。</summary>
    public void ApplyLessonState(bool inClass)
    {
        _inClass = inClass;
        ApplyInternal();
    }

    /// <summary>按当前已知课程状态与最新配置重新评估（设置页改动后调用，立即生效）。</summary>
    public void Reapply() => ApplyInternal();

    void ApplyInternal()
    {
        var monitor = PrivacyIslandRuntime.Monitor;
        var cfg = PrivacyIslandRuntime.Config;
        if (monitor == null || cfg == null) return;

        // 未启用：撤掉本联动的一切影响。
        if (!cfg.LessonAwareEnabled)
        {
            monitor.SetPauseSource("lesson", false);
            monitor.ApplyDelayOverride(null, null);
            return;
        }

        if (_inClass)
        {
            if (cfg.PauseDuringClass)
            {
                // 暂停优先于加强延迟。
                monitor.ApplyDelayOverride(null, null);
                monitor.SetPauseSource("lesson", true);
            }
            else if (cfg.StrongerDelayDuringClass)
            {
                monitor.SetPauseSource("lesson", false);
                monitor.ApplyDelayOverride(cfg.ClassMinDelaySeconds, cfg.ClassMaxDelaySeconds);
            }
            else
            {
                monitor.SetPauseSource("lesson", false);
                monitor.ApplyDelayOverride(null, null);
            }
        }
        else
        {
            // 非上课：恢复常态。
            monitor.SetPauseSource("lesson", false);
            monitor.ApplyDelayOverride(null, null);
        }
    }

    public void Dispose()
    {
        if (_lessons != null && _subscribed)
        {
            _lessons.OnClass -= OnClass;
            _lessons.OnBreakingTime -= OnBreak;
            _lessons.OnAfterSchool -= OnAfterSchool;
            _subscribed = false;
        }
        // 退出时撤掉本联动的暂停/覆盖，避免残留影响。
        PrivacyIslandRuntime.Monitor?.SetPauseSource("lesson", false);
        PrivacyIslandRuntime.Monitor?.ApplyDelayOverride(null, null);
    }
}
