namespace CurlUnity.Sse
{
    /// <summary>
    /// 一条已解析的 Server-Sent Event。由 <see cref="SseEventParser"/> 在遇到空行
    /// (event 分隔) 且累积过 <c>data</c> 字段时产出。
    /// </summary>
    public readonly struct SseEvent
    {
        /// <summary><c>event</c> 字段值；未指定时为 <c>"message"</c>（SSE 规范默认类型）。</summary>
        public string EventType { get; }

        /// <summary>
        /// <c>data</c> 字段值。多行 <c>data:</c> 以 <c>\n</c> 连接，末尾不含多余换行
        /// (规范要求 dispatch 时去掉最后一个 <c>\n</c>)。
        /// </summary>
        public string Data { get; }

        /// <summary>
        /// dispatch 时刻的 last-event-id 快照（即最近一次有效 <c>id</c> 字段值，跨事件保留）。
        /// 从未出现过 <c>id</c> 时为空字符串。
        /// </summary>
        public string LastEventId { get; }

        public SseEvent(string eventType, string data, string lastEventId)
        {
            EventType = eventType;
            Data = data;
            LastEventId = lastEventId;
        }
    }
}
