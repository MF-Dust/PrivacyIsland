namespace PrivacyIsland;

/// <summary>逐个隔离事件订阅者，避免一个扩展异常阻断后续通知。</summary>
internal static class EventDispatch
{
    public static void Invoke<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch { }
        }
    }
}
