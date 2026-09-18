namespace CurlUnity.Sse
{
    /// <summary>
    /// <see cref="ISseConnection"/> 的连接状态。迁移顺序：
    /// <c>Connecting</c>（初始/首连）→ <c>Open</c>（已接受非 204 的 2xx 响应头）→ <c>Reconnecting</c>（断后重连中）→
    /// <c>Closed</c>（终态，Dispose/取消）。
    /// </summary>
    public enum SseConnectionState
    {
        Connecting = 0,
        Open = 1,
        Reconnecting = 2,
        Closed = 3,
    }
}
