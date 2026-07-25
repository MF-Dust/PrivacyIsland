using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace PrivacyIsland.Ipc;

/// <summary>
/// DLL 经共享内存上报的一帧状态快照。
/// 反汇编确认：Heartbeat 由 DLL 专用线程每 ~5s 写入 GetTickCount()（存活信号，与捕获无关）；
/// CaptureCount 当前 DLL 从不写（保留字段，读出恒为 0）。
/// </summary>
public sealed record CaptureSnapshot(int State, int Error, string Message, uint Heartbeat = 0, uint CaptureCount = 0);

/// <summary>共享内存存活快照，供编排器判断 hook 是否真的在跑（区别于「注入器退出码 0」）。</summary>
public sealed record IpcLiveness(
    bool CameraActive,
    int LastPolledState,
    DateTime? LastFrameUtc,
    uint Heartbeat,
    DateTime? LastHeartbeatChangeUtc,
    bool HeartbeatSupported,
    uint CaptureCount,
    DateTime? LastCaptureCountChangeUtc,
    bool ReadySeen,
    DateTime? ReadySeenUtc);

/// <summary>
/// 主机侧（C#）的共享内存 IPC，替代原生 messager.c。
/// 插件现在是「创建方」：先建好 MMF/Mutex/Event，原生 DLL 启动时再去 Open。
/// 因此 <see cref="Start"/> 必须在注入 DLL 之前调用。
/// </summary>
public sealed class SharedMemoryBridge : IDisposable
{
    readonly object _gate = new();
    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    Mutex? _mutex;
    EventWaitHandle? _dataEvent;
    readonly ManualResetEvent _quit = new(false);
    Thread? _reader;
    volatile bool _running;

    /// <summary>每收到一帧 DLL 状态时触发（在后台读线程上）。</summary>
    public event Action<CaptureSnapshot>? StateReceived;

    /// <summary>摄像头当前是否正被访问（start→stop 之间为真），供自动化规则读取。</summary>
    // 主写者是读线程；ForceInactive 由编排器线程置回；读者是规则/timer/UI，容忍陈旧一拍。
    volatile bool _cameraActive;
    public bool CameraActive => _cameraActive;

    // ---- 存活跟踪（全部在 _liveGate 下读写；heartbeat/captureCount 是原本从未读的字段）----
    readonly object _liveGate = new();
    int _lastPolledState = IpcProtocol.StatusWaiting;
    DateTime? _lastFrameUtc;            // 最近一次「真实 DLL 事件」（非轮询）
    uint _lastHeartbeat;
    DateTime? _lastHeartbeatChangeUtc;
    bool _heartbeatEverNonZero;        // DLL 从未写非零心跳→视为不支持，下游心跳判据全部跳过
    uint _lastCaptureCount;
    DateTime? _lastCaptureCountChangeUtc;
    bool _readySeen;
    DateTime? _readySeenUtc;

    /// <summary>快照当前存活信息，供编排器融合/自愈判断。</summary>
    public IpcLiveness GetLiveness()
    {
        lock (_liveGate)
            return new IpcLiveness(
                _cameraActive, _lastPolledState, _lastFrameUtc,
                _lastHeartbeat, _lastHeartbeatChangeUtc, _heartbeatEverNonZero,
                _lastCaptureCount, _lastCaptureCountChangeUtc, _readySeen, _readySeenUtc);
    }

    /// <summary>创建 IPC 对象并写入初始配置，然后启动读线程。注入 DLL 之前调用。</summary>
    public void Start(int minDelay, int maxDelay, bool stealth)
    {
        IpcProtocol.SelfCheck();
        lock (_gate)
        {
            if (_running) return;

            // 插件是创建方；若已存在（例如旧 host 还在跑）则打开它，保持鲁棒。
            try
            {
                _mmf = MemoryMappedFile.CreateNew(IpcProtocol.SharedMemName, IpcProtocol.Size);
            }
            catch (IOException)
            {
                _mmf = MemoryMappedFile.OpenExisting(IpcProtocol.SharedMemName, MemoryMappedFileRights.ReadWrite);
            }

            _view = _mmf.CreateViewAccessor(0, IpcProtocol.Size);
            _mutex = new Mutex(false, IpcProtocol.MutexName);
            _dataEvent = new EventWaitHandle(false, EventResetMode.AutoReset, IpcProtocol.EventName);

            // 初始化共享区：状态置 waiting，写入延迟/隐身配置（DLL 在 hook 初始化时读取）。
            WriteConfig(minDelay, maxDelay, paused: false, stealth);
            WriteUnderMutex(() =>
            {
                _view!.Write(IpcProtocol.OffCurrState, IpcProtocol.StatusWaiting);
                _view!.Write(IpcProtocol.OffHeartbeat, (uint)0);
            });

            _running = true;
            _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "PrivacyIsland.IpcReader" };
            _reader.Start();
        }
    }

    // 读线程轮询周期：DLL 每次上报会 SetEvent 走快路径；超时则主动读一次共享区，
    // 补偿丢失的事件信号、刷新心跳/计数存活、校正漂移的 currState。
    const int PollIntervalMs = 1000;

    void ReaderLoop()
    {
        var handles = new WaitHandle[] { _dataEvent!, _quit };
        while (_running)
        {
            int idx;
            try { idx = WaitHandle.WaitAny(handles, PollIntervalMs); }
            catch { break; }
            if (idx == 1) break;                 // quit
            bool isEvent = idx == 0;             // 否则 WaitHandle.WaitTimeout（258）→ 轮询

            CaptureSnapshot? snap = TryReadSnapshot();
            if (snap is null) continue;

            UpdateLiveness(snap, isEvent);
            if (isEvent) DispatchFrame(snap);    // 真实 DLL 帧——快路径不变
            else ReconcileOnPoll(snap);          // 超时——静默校正，不重复分发已处理的帧
        }
    }

    void UpdateLiveness(CaptureSnapshot s, bool isEvent)
    {
        var now = DateTime.UtcNow;
        lock (_liveGate)
        {
            _lastPolledState = s.State;
            if (isEvent) _lastFrameUtc = now;
            if (s.Heartbeat != 0) _heartbeatEverNonZero = true;
            if (s.Heartbeat != _lastHeartbeat) { _lastHeartbeat = s.Heartbeat; _lastHeartbeatChangeUtc = now; }
            if (s.CaptureCount != _lastCaptureCount) { _lastCaptureCount = s.CaptureCount; _lastCaptureCountChangeUtc = now; }
            if (s.State == IpcProtocol.StatusReady) { _readySeen = true; _readySeenUtc = now; }
        }
    }

    void DispatchFrame(CaptureSnapshot s)
    {
        switch (s.State)
        {
            case IpcProtocol.StatusStart:
            case IpcProtocol.StatusWatching:
                _cameraActive = true;
                break;
            case IpcProtocol.StatusStop:
                _cameraActive = false;
                break;
        }
        EventDispatch.Invoke(StateReceived, s);
    }

    // 轮询路径：currState 是「最后写入值」，只在与 latch 真分歧（丢了 SetEvent）时翻转并补一帧，
    // 因此不会每秒重发已处理的帧；Log/Info/Ready/Error/Waiting 不含活动真相，latch 不动。
    void ReconcileOnPoll(CaptureSnapshot s)
    {
        if (s.State == IpcProtocol.StatusStop && _cameraActive)
        {
            _cameraActive = false;
            EmitReconciled(s, "(补偿) 检测到 currState=stop，修正遗漏的关闭");
        }
        else if ((s.State == IpcProtocol.StatusStart || s.State == IpcProtocol.StatusWatching) && !_cameraActive)
        {
            _cameraActive = true;
            EmitReconciled(s, "(补偿) 检测到 currState=active，修正遗漏的开启");
        }
    }

    void EmitReconciled(CaptureSnapshot s, string message)
        => EventDispatch.Invoke(StateReceived, s with { Message = message });

    CaptureSnapshot? TryReadSnapshot()
    {
        if (_view is null) return null;
        CaptureSnapshot? snapshot = null;
        if (!WithMutex(() =>
        {
            var buf = new byte[IpcProtocol.LogBufferBytes];
            _view.ReadArray(IpcProtocol.OffLogBuffer, buf, 0, buf.Length);
            string msg = DecodeWide(buf);
            int state = _view.ReadInt32(IpcProtocol.OffCurrState);
            int err = _view.ReadInt32(IpcProtocol.OffPotError);
            uint hb = _view.ReadUInt32(IpcProtocol.OffHeartbeat);
            uint cc = _view.ReadUInt32(IpcProtocol.OffCaptureCount);
            snapshot = new CaptureSnapshot(state, err, msg, hb, cc);
        })) return null;
        return snapshot;
    }

    /// <summary>写配置（host→DLL 的字段）。只动 min/max/paused/stealth，不碰 DLL 写的状态字段。</summary>
    public void WriteConfig(int minDelay, int maxDelay, bool paused, bool stealth)
    {
        WriteUnderMutex(() =>
        {
            _view!.Write(IpcProtocol.OffMinDelay, minDelay);
            _view!.Write(IpcProtocol.OffMaxDelay, maxDelay);
            _view!.Write(IpcProtocol.OffPaused, paused ? 1 : 0);
            _view!.Write(IpcProtocol.OffStealth, stealth ? 1 : 0);
        });
    }

    /// <summary>单独切换暂停标志（DLL 在每次 capture 前读取它来决定是否跳过延迟）。</summary>
    public void SetPaused(bool paused)
        => WriteUnderMutex(() => _view!.Write(IpcProtocol.OffPaused, paused ? 1 : 0));

    /// <summary>
    /// 应用内功能测试用：写一帧合成状态并触发事件，让读线程按「真 DLL 事件」处理。
    /// 走的是与真实注入完全相同的 IPC + 分发路径（含 CameraActive/统计/提醒/触发器），
    /// 因此无需真注入/管理员即可验证 ClassIsland 侧整条链路。
    /// </summary>
    public void Simulate(int state, string message)
    {
        WriteUnderMutex(() =>
        {
            var buf = new byte[IpcProtocol.LogBufferBytes];
            byte[] wide = Encoding.Unicode.GetBytes(message + "\0");
            Array.Copy(wide, buf, Math.Min(wide.Length, buf.Length));
            _view!.WriteArray(IpcProtocol.OffLogBuffer, buf, 0, buf.Length);
            _view!.Write(IpcProtocol.OffCurrState, state);
        });
        _dataEvent?.Set();
    }

    /// <summary>
    /// 目标进程消失但 hook 未发 stop 导致 latch 卡在 true 时，由编排器强制置回并补一帧 stop。
    /// 仅在当前为 true 时动作，避免重复分发。
    /// </summary>
    public void ForceInactive(string reason)
    {
        if (!_cameraActive) return;
        _cameraActive = false;
        var snap = new CaptureSnapshot(IpcProtocol.StatusStop, 0, "(补偿) " + reason);
        EventDispatch.Invoke(StateReceived, snap);
    }

    void WriteUnderMutex(Action write)
    {
        if (_view is null) return;
        WithMutex(write);
    }

    bool WithMutex(Action action)
    {
        if (_mutex is null) return false;
        bool held = false;
        try
        {
            try { held = _mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return false;
            action();
            return true;
        }
        finally
        {
            if (held) { try { _mutex!.ReleaseMutex(); } catch { } }
        }
    }

    static string DecodeWide(byte[] buf)
    {
        string s = Encoding.Unicode.GetString(buf);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
            _quit.Set();
            _reader?.Join(3000);
            _view?.Dispose();
            _mmf?.Dispose();
            _mutex?.Dispose();
            _dataEvent?.Dispose();
            _quit.Dispose();
            _view = null; _mmf = null; _mutex = null; _dataEvent = null;
        }
    }
}
