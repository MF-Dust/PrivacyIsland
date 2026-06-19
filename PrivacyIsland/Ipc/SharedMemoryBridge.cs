using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace PrivacyIsland.Ipc;

/// <summary>DLL 经共享内存上报的一帧状态快照。</summary>
public sealed record CaptureSnapshot(int State, int Error, string Message);

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
    public bool CameraActive { get; private set; }

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

    void ReaderLoop()
    {
        var handles = new WaitHandle[] { _dataEvent!, _quit };
        while (_running)
        {
            int idx;
            try { idx = WaitHandle.WaitAny(handles); }
            catch { break; }
            if (idx == 1) break; // quit

            CaptureSnapshot? snap = TryReadSnapshot();
            if (snap is null) continue;

            switch (snap.State)
            {
                case IpcProtocol.StatusStart:
                case IpcProtocol.StatusWatching:
                    CameraActive = true;
                    break;
                case IpcProtocol.StatusStop:
                    CameraActive = false;
                    break;
            }

            try { StateReceived?.Invoke(snap); }
            catch { /* 订阅方异常不拖垮读线程 */ }
        }
    }

    CaptureSnapshot? TryReadSnapshot()
    {
        if (_mutex is null || _view is null) return null;
        bool held = false;
        try
        {
            try { held = _mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { held = true; } // DLL 进程持锁而亡——锁已归我们
            if (!held) return null;

            var buf = new byte[IpcProtocol.LogBufferBytes];
            _view.ReadArray(IpcProtocol.OffLogBuffer, buf, 0, buf.Length);
            string msg = DecodeWide(buf);
            int state = _view.ReadInt32(IpcProtocol.OffCurrState);
            int err = _view.ReadInt32(IpcProtocol.OffPotError);
            return new CaptureSnapshot(state, err, msg);
        }
        finally
        {
            if (held) { try { _mutex!.ReleaseMutex(); } catch { } }
        }
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

    void WriteUnderMutex(Action write)
    {
        if (_mutex is null || _view is null) return;
        bool held = false;
        try
        {
            try { held = _mutex.WaitOne(2000); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return;
            write();
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
