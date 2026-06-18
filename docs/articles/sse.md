# Server-Sent Events (SSE)

SSE (`text/event-stream`) 在 `CurlUnity.Sse` 命名空间，按「协议核心 / 工程便利」分层：

- **`SseEventParser`** — 纯协议解析器，喂字节出事件，零网络依赖，可独立复用（Layer 0）。
- **`ReadServerSentEventsAsync`** — `IHttpClient` 扩展方法，在一个 `IHttpRequest` 上读**一段** SSE 连接（连接结束即返回，**不重连**），单连接原语（Layer 1）。
- **`OpenSse` / `ISseConnection`** — 带**自动重连 / 退避 / 心跳 / 状态机 / Last-Event-ID** 的便利层（Layer 2）。

多数业务直接用 `OpenSse`（见下「自动重连」）即可开箱即用；只需「读一段就结束」时用 `ReadServerSentEventsAsync`；要完全自定义重连策略时用 `SseEventParser` + `SendAsync`。

## 单连接读取（Layer 1）

```csharp
using CurlUnity.Http;
using CurlUnity.Sse;

var req = new HttpRequest
{
    Url = "https://example.com/events",
    TimeoutMs = 0,        // SSE 长连接不设整体超时（keep-alive 由本方法内部开启）
};

// onEvent 在 worker 线程触发,禁止阻塞 —— 要碰 Unity API 时自行 marshal 到主线程
using var resp = await client.ReadServerSentEventsAsync(req, evt =>
{
    Debug.Log($"[{evt.EventType}] {evt.Data} (id={evt.LastEventId})");
});
// 连接结束后返回; 2xx 正常返回, 非 2xx 抛 SseHttpStatusException
```

要点:

- `Accept: text/event-stream` 由库缺省补上(用户已设 Accept 则不覆盖),且**不修改**传入的 `request`。
- 支持任意 method + body:SSE 订阅常用 `POST` + JSON 参数,直接用 `HttpRequest.Method` / `Body`。
- 回调在 **worker 线程**,与 `OnDataReceived` 一致。
- `request` 上不能再设 `OnDataReceived` 或 `OnHeadersReceived`(SSE 需接管,已设会 throw)。
- **非 2xx 状态码在 body 到达前**通过 `OnHeadersReceived` 以 `SseHttpStatusException` 抛出,不会解析无效的事件流。

## 自动重连（推荐：OpenSse / ISseConnection）

需要断线自动重连时用 `OpenSse`——内部跑重连循环，叠加指数退避、`Last-Event-ID` 续传、`retry:` 响应、空闲/心跳超时、连接状态机：

```csharp
var options = new SseConnectionOptions
{
    ReconnectDelayInit = TimeSpan.FromSeconds(1),  // 首次重连延迟（连成功后重置）；ReconnectDelayIncFn 默认 t*2 clamp[1s,32s]
    IdleTimeout = TimeSpan.FromSeconds(20),         // 20s 无任何数据(含注释心跳)→ 判死重连；null=不启用
};

var req = new HttpRequest { Url = url, TimeoutMs = 0 }; // keep-alive 由 OpenSse 内部为长连接开启
var sse = client.OpenSse(req, options);   // 构造即开始连接
// 请立即挂回调（全在 worker 线程触发，禁止阻塞）
sse.OnEvent += e => Debug.Log($"[{e.EventType}] {e.Data}");
sse.OnError += ex => Debug.LogWarning($"sse error: {ex.GetType().Name}（将自动重连）");
sse.OnStateChanged += (oldS, newS) => Debug.Log($"sse {oldS} → {newS}");
// ...不再需要时：
sse.Dispose();   // 关闭并停止重连，状态置 Closed
```

需要每轮(重)连前刷新 token / 动态构造请求（async headers）时，用 `requestFactory` 重载——库不碰 token：

```csharp
var sse = client.OpenSse(async ct =>
{
    var token = await GetTokenAsync(ct);   // 每轮(重)连前刷新
    return new HttpRequest
    {
        Url = url, Method = HttpMethod.Post, TimeoutMs = 0,
        Headers = new[] { new KeyValuePair<string, string>("Authorization", $"Bearer {token}") },
    };
}, options);
```

要点：缺省自动注入 `Last-Event-ID`（取自已确认 id）；非 2xx 经 `OnError` 收到 `SseHttpStatusException`、空闲超时收到 `TimeoutException`、网络错收到 `CurlHttpException`，随后都自动重连；**`204 No Content` 视为服务端要求停止，直接关闭、不重连**；`Dispose()` 取消在飞请求、停止重连、状态置 `Closed`。**回调全在后台线程，调用方自行 marshal 到主线程。**

## 自定义重连（低层：parser + SendAsync）

若 `OpenSse` 的策略不满足需求，可用 `SseEventParser` + `SendAsync` 完全自定义。`ReadServerSentEventsAsync` 是**单连接原语,不重连**；`SseEventParser` 暴露 `LastEventId` / `RetryMilliseconds`,可据此实现断线续传(复用同一个 parser 跨连接,每轮先 `Reset()`):

```csharp
var parser = new SseEventParser();
while (!ct.IsCancellationRequested)
{
    parser.Reset(); // 新连接边界：清上一连接的半行/半事件并重启 BOM 检查（保留已确认的 LastEventId/Retry）
    var headers = new List<KeyValuePair<string, string>>
    {
        new KeyValuePair<string, string>("Accept", "text/event-stream"),
    };
    if (!string.IsNullOrEmpty(parser.LastEventId)) // 断线续传:带上最后事件 id
        headers.Add(new KeyValuePair<string, string>("Last-Event-ID", parser.LastEventId));

    var req = new HttpRequest
    {
        Url = url, TimeoutMs = 0,
        Headers = headers,
        OnDataReceived = (b, o, l) => parser.Feed(b, o, l, OnEvent),
    };
    try { using var _ = await client.SendAsync(req, ct); }
    catch (CurlHttpException) { /* 网络错,下面退避后重连 */ }

    var delay = parser.RetryMilliseconds ?? 1000; // 服务端 retry: 优先,否则默认
    await Task.Delay(delay, ct);
}
```

## 响应头就绪回调（OnHeadersReceived）

`OnHeadersReceived` 是核心 HTTP 层的通用能力，SSE 内部用它实现非 2xx 快速失败。业务代码如需在流式下载等场景下提前检查状态码，也可直接使用：

```csharp
var req = new HttpRequest
{
    Url = streamUrl,
    OnHeadersReceived = resp =>
    {
        // 所有响应头到达、body 尚未开始时触发一次
        // resp 与 SendAsync 返回的是同一实例，此时 Body 为 null
        if (resp.StatusCode != 200)
            throw new Exception($"unexpected {resp.StatusCode}");
        Log($"content-type: {resp.ContentType}");
    },
    OnDataReceived = (buf, off, len) => { /* 处理 body */ },
};
using var resp = await client.SendAsync(req);
```

- 回调中 throw 即中止传输，异常透传给 `SendAsync` 的 Task。
- HEAD / 204 等无 body 的响应也会触发。
- `EnableResponseHeaders = true` 时回调中 `resp.Headers` 可用；否则为 null，但 `StatusCode` / `ContentType` / `Version` 等 getinfo 属性始终可用。
- **SSE 层已接管此回调**，使用 `ReadServerSentEventsAsync` / `OpenSse` 时不能再设 `OnHeadersReceived`（与 `OnDataReceived` 同策略）。
