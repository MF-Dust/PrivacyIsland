namespace PrivacyIsland.Ipc;

/// <summary>
/// 与原生侧 shared_defs.h 的 <c>struct log_data</c> 对齐的 IPC 契约。
/// 用「固定字节偏移」而非 [StructLayout] 编组，避免 wchar_t[1024] 内嵌缓冲的编组坑。
/// 任何字段调整都要同步改这里和 shared_defs.h。
/// </summary>
internal static class IpcProtocol
{
    // 命名同步对象（Local\ 会话内可见，与原 host/DLL 一致）
    public const string SharedMemName = @"Local\LilithSharedMem";
    public const string MutexName = @"Local\LilithMutex";
    public const string EventName = @"Local\LilithLogEvent";

    // 状态码（同 shared_defs.h 的匿名枚举）
    public const int StatusWaiting = 0;
    public const int StatusStart = 1;
    public const int StatusWatching = 2;
    public const int StatusStop = 3;
    public const int StatusError = -1;
    public const int StatusLog = 4;
    public const int StatusInfo = 5;
    public const int StatusReady = 6;

    // log_buffer 是 wchar_t[1024] = 2048 字节
    public const int LogBufferChars = 1024;
    public const int LogBufferBytes = LogBufferChars * 2;

    // 字段偏移（严格对应 shared_defs.h 的字段顺序）
    public const int OffLogBuffer = 0;                      // wchar_t[1024]
    public const int OffCurrState = OffLogBuffer + LogBufferBytes;   // 2048  int
    public const int OffPotError = OffCurrState + 4;        // 2052  int
    public const int OffMinDelay = OffPotError + 4;         // 2056  int
    public const int OffMaxDelay = OffMinDelay + 4;         // 2060  int
    public const int OffHeartbeat = OffMaxDelay + 4;        // 2064  DWORD
    public const int OffPaused = OffHeartbeat + 4;          // 2068  BOOL(int)
    public const int OffCaptureCount = OffPaused + 4;       // 2072  DWORD
    public const int OffStealth = OffCaptureCount + 4;      // 2076  BOOL(int)

    public const int Size = OffStealth + 4;                 // 2080

    /// <summary>自检：偏移算术自洽且总大小 == 2080。布局错了就早爆，别等到运行期读到垃圾。</summary>
    public static void SelfCheck()
    {
        if (Size != 2080)
            throw new InvalidOperationException($"IPC 布局错误：Size={Size}，应为 2080（与 shared_defs.h 不一致）。");
    }
}
