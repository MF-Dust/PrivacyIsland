using System.Runtime.InteropServices;
using System.Text;
using PrivacyIsland.Logging;

namespace PrivacyIsland.Orchestrator;

internal readonly record struct ScreenBounds(int X, int Y, int Width, int Height);

internal readonly record struct WindowSample(
    long Hwnd,
    string Title,
    string ClassName,
    int X,
    int Y,
    int Width,
    int Height,
    bool Visible,
    bool Minimized);

internal enum WindowAnomalyKind
{
    Hidden,
    MovedOffScreen,
}

internal readonly record struct WindowAnomaly(
    WindowAnomalyKind Kind,
    WindowSample Before,
    WindowSample Current);

/// <summary>判断顶层窗口是否可跟踪、是否仍在屏幕上，以及当前是隐藏还是完全移出虚拟屏幕。</summary>
internal static class WindowAnomalyLogic
{
    public const int MinTrackWidth = 80;
    public const int MinTrackHeight = 40;

    public static bool IsTrackable(WindowSample window)
        => window.Hwnd != 0 &&
           !string.IsNullOrWhiteSpace(window.Title) &&
           window.Width >= MinTrackWidth &&
           window.Height >= MinTrackHeight;

    public static string ShortTitle(string title)
    {
        title = title.Trim();
        return title.Length > 40 ? title[..40] : title;
    }

    public static long IntersectionArea(WindowSample window, ScreenBounds screen)
    {
        long left = Math.Max(window.X, screen.X);
        long top = Math.Max(window.Y, screen.Y);
        long right = Math.Min(window.X + (long)window.Width, screen.X + (long)screen.Width);
        long bottom = Math.Min(window.Y + (long)window.Height, screen.Y + (long)screen.Height);
        long width = right - left;
        long height = bottom - top;
        return width <= 0 || height <= 0 ? 0 : width * height;
    }

    public static bool IsPresented(WindowSample window, ScreenBounds screen)
        => window.Visible && !window.Minimized && IntersectionArea(window, screen) > 0;

    public static WindowAnomalyKind? Classify(WindowSample current, ScreenBounds screen)
    {
        if (current.Minimized || !IsTrackable(current)) return null;
        if (!current.Visible) return WindowAnomalyKind.Hidden;
        if (IntersectionArea(current, screen) == 0) return WindowAnomalyKind.MovedOffScreen;
        return null;
    }

    public static string Describe(WindowAnomaly anomaly)
    {
        var window = anomaly.Current;
        return $"顶层窗口「{ShortTitle(window.Title)}」被移出虚拟屏幕（HWND {window.Hwnd}，类名 {window.ClassName}，现位置 {Rect(window)}）";
    }

    static string Rect(WindowSample window) => $"{window.X},{window.Y} {window.Width}x{window.Height}";
}

/// <summary>
/// 窗口要先在屏幕上连续停留，再连续移出屏幕，才进入风险。普通隐藏、最小化和单次闪动不产生提醒。
/// </summary>
internal sealed class WindowEpisodeTracker
{
    public const int ArmSamples = 3;
    public const int TriggerSamples = 2;
    public const int ClearSamples = 2;

    readonly Dictionary<long, int> _visibleStreak = new();
    readonly Dictionary<long, WindowSample> _armed = new();
    readonly Dictionary<long, int> _offScreenStreak = new();
    readonly Dictionary<long, int> _returnStreak = new();
    readonly Dictionary<long, WindowAnomaly> _active = new();

    public void Reset()
    {
        _visibleStreak.Clear();
        _armed.Clear();
        _offScreenStreak.Clear();
        _returnStreak.Clear();
        _active.Clear();
    }

    public IReadOnlyList<WindowAnomaly> Observe(IReadOnlyList<WindowSample> current, ScreenBounds screen)
    {
        var seen = new HashSet<long>();
        foreach (var window in current)
        {
            if (!WindowAnomalyLogic.IsTrackable(window)) continue;
            seen.Add(window.Hwnd);
            if (WindowAnomalyLogic.IsPresented(window, screen))
            {
                _offScreenStreak[window.Hwnd] = 0;
                int streak = _visibleStreak.GetValueOrDefault(window.Hwnd) + 1;
                _visibleStreak[window.Hwnd] = streak;
                if (streak >= ArmSamples) _armed[window.Hwnd] = window;
                if (_active.ContainsKey(window.Hwnd))
                {
                    int back = _returnStreak.GetValueOrDefault(window.Hwnd) + 1;
                    _returnStreak[window.Hwnd] = back;
                    if (back >= ClearSamples)
                    {
                        _active.Remove(window.Hwnd);
                        _returnStreak.Remove(window.Hwnd);
                        _armed.Remove(window.Hwnd);
                        _visibleStreak[window.Hwnd] = back;
                    }
                }
                continue;
            }

            _returnStreak[window.Hwnd] = 0;
            _visibleStreak[window.Hwnd] = 0;
            bool movedOff = WindowAnomalyLogic.Classify(window, screen) == WindowAnomalyKind.MovedOffScreen;
            if (!movedOff || !_armed.ContainsKey(window.Hwnd))
            {
                _offScreenStreak[window.Hwnd] = 0;
                if (!movedOff) _armed.Remove(window.Hwnd);
                continue;
            }

            int off = _offScreenStreak.GetValueOrDefault(window.Hwnd) + 1;
            _offScreenStreak[window.Hwnd] = off;
            if (off >= TriggerSamples)
            {
                _active[window.Hwnd] = new WindowAnomaly(
                    WindowAnomalyKind.MovedOffScreen,
                    _armed[window.Hwnd],
                    window);
            }
        }

        foreach (var hwnd in _active.Keys.Where(hwnd => !seen.Contains(hwnd)).ToArray())
            _active.Remove(hwnd);
        foreach (var hwnd in _armed.Keys.Where(hwnd => !seen.Contains(hwnd)).ToArray())
        {
            _armed.Remove(hwnd);
            _visibleStreak.Remove(hwnd);
            _offScreenStreak.Remove(hwnd);
        }

        return _active.Values.ToArray();
    }
}

internal sealed class WindowChangeTracker
{
    readonly WindowEpisodeTracker _episodes = new();
    IReadOnlyList<WindowAnomaly> _lastAnomalies = Array.Empty<WindowAnomaly>();

    public IReadOnlyList<WindowAnomaly> Collect(bool enabled)
    {
        if (!enabled)
        {
            _episodes.Reset();
            _lastAnomalies = Array.Empty<WindowAnomaly>();
            return _lastAnomalies;
        }

        ScreenBounds screen;
        IReadOnlyList<WindowSample> current;
        try
        {
            screen = WindowChangeProbe.VirtualScreen();
            current = WindowChangeProbe.ReadCurrentProcessWindows();
        }
        catch (Exception ex)
        {
            PluginLog.Warn("窗口观察失败：" + ex.Message);
            return _lastAnomalies;
        }

        _lastAnomalies = _episodes.Observe(current, screen);
        return _lastAnomalies;
    }
}

internal static class WindowChangeProbe
{
    const int SmCxScreen = 0;
    const int SmCyScreen = 1;
    const int SmXVirtualScreen = 76;
    const int SmYVirtualScreen = 77;
    const int SmCxVirtualScreen = 78;
    const int SmCyVirtualScreen = 79;

    public static ScreenBounds VirtualScreen()
    {
        int width = GetSystemMetrics(SmCxVirtualScreen);
        int height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
            return new ScreenBounds(0, 0, Math.Max(GetSystemMetrics(SmCxScreen), 1), Math.Max(GetSystemMetrics(SmCyScreen), 1));
        return new ScreenBounds(
            GetSystemMetrics(SmXVirtualScreen),
            GetSystemMetrics(SmYVirtualScreen),
            width,
            height);
    }

    public static IReadOnlyList<WindowSample> ReadCurrentProcessWindows()
    {
        var windows = new List<WindowSample>();
        uint pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid == pid) windows.Add(Read(hwnd));
            return true;
        }, 0);
        return windows;
    }

    static WindowSample Read(IntPtr hwnd)
    {
        var title = new StringBuilder(256);
        _ = GetWindowText(hwnd, title, title.Capacity);
        var className = new StringBuilder(128);
        _ = GetClassName(hwnd, className, className.Capacity);
        _ = GetWindowRect(hwnd, out RECT rect);
        return new WindowSample(
            hwnd.ToInt64(),
            title.ToString(),
            className.ToString(),
            rect.Left,
            rect.Top,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top,
            IsWindowVisible(hwnd),
            IsIconic(hwnd));
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
