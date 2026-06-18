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
    // ConnectTimeoutMs 可按需调整（默认 30s）；TimeoutMs 由 SSE 内部强制为 0（长连接不设整体超时）
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
    ReconnectDelayInit = TimeSpan.FromSeconds(1),  // 退避基准（首连后用作下次重连起点，建立成功后重置回此值）
    IdleTimeout = TimeSpan.FromSeconds(20),         // 20s 无任何数据(含注释心跳)→ 判死重连；null=不启用
};

var req = new HttpRequest { Url = url };

// 推荐：用带回调参数的重载，在后台循环启动前挂好回调 —— 彻底消除 construct-then-subscribe 竞态，绝不漏首个事件/状态。
using var sse = client.OpenSse(req,
    onEvent:        e => Debug.Log($"[{e.EventType}] {e.Data}"),
    onError:        ex => Debug.LogWarning($"sse error: {ex.GetType().Name}（将自动重连）"),
    onStateChanged: (oldS, newS) => Debug.Log($"sse {oldS} → {newS}"),
    options:        options);

// ...不再需要时：
sse.Dispose();   // 关闭并停止重连，状态置 Closed
```

> 仍可用 `client.OpenSse(req, options)` 的无回调重载并随后 `sse.OnEvent += ...` 订阅；但该写法存在 construct-then-subscribe 竞态（首个事件可能在你订阅前已触发）。**要保证不漏事件，请用上面带回调参数的重载在构造期挂回调。**

需要每轮(重)连前刷新 token / 动态构造请求（async headers）时，用 `requestFactory` 重载——库不碰 token（同样有带回调参数的版本）：

```csharp
using var sse = client.OpenSse(async ct =>
{
    var token = await GetTokenAsync(ct);   // 每轮(重)连前刷新
    return new HttpRequest
    {
        Url = url, Method = HttpMethod.Post,
        Headers = new[] { new KeyValuePair<string, string>("Authorization", $"Bearer {token}") },
    };
}, onEvent: e => Handle(e), options: options);
```

要点：缺省自动注入 `Last-Event-ID`（取自已确认 id）；非 2xx 经 `OnError` 收到 `SseHttpStatusException`、空闲超时收到 `TimeoutException`、网络错收到 `CurlHttpException`，随后都自动重连；**`204 No Content` 视为服务端要求停止，直接关闭、不重连**；`Dispose()` 取消在飞请求、停止重连、状态置 `Closed`。**回调全在后台线程，调用方自行 marshal 到主线程，且回调内不应抛异常（见下「终止与可观测性」）。**

### 退避与重置语义

每轮(重)连的实际等待按以下顺序确定：

1. **基准** = 服务端最近的 `retry:`（若出现过，优先且粘滞）否则当前退避值；
2. 若设了 `MaxReconnectDelay`，clamp 到该上限；
3. 若设了 `ReconnectJitter`（`[0,1]`），在 `[delay*(1-jitter), delay]` 内随机下调（只减不增，打散重连风暴）。

退避值仅在连接**未建立**时按 `ReconnectDelayIncFn`（默认 `t*2` clamp[1s,32s]）递增；连接**建立成功**后重置回 `ReconnectDelayInit`。

「建立成功」的判定由 `BackoffResetThreshold` 控制：

- 默认 `TimeSpan.Zero`：收到**任意字节**即算建立（EventSource 风格）。
- 设为正值：连接需保持至少这么久才算建立——可防御「建连即断」的抖动服务端，避免它每次都以基准间隔无限热循环猛打服务端。
- **完全无字节的空 2xx EOF 永远不算建立**，退避因此会持续递增（修复了旧版「空 EOF 反复重置退避」的热循环问题）。
- 「是否建立」**只看本轮是否收到字节 + 存活时长**，与连接**如何结束无关**——干净 EOF、网络错误、空闲超时一视同仁。这点对生产很关键：SSE 连接绝大多数以网络错误/超时断开而非干净 EOF，若仅干净 EOF 才算建立，退避会几乎永远递增到上限、`MaxReconnectAttempts` 也会误杀健康但短命的连接。

### 生产护栏（默认全关，等价 EventSource 无限重连）

| 字段 | 作用 | 默认 |
| --- | --- | --- |
| `MaxReconnectDelay` | 重连等待上限（含 `retry:` 与抖动后） | `null`（不限） |
| `ReconnectJitter` | 抖动系数 `[0,1]`，打散重连风暴 | `0`（不抖动） |
| `BackoffResetThreshold` | 连接存活多久才算「建立」并重置退避 | `0`（收到字节即算） |
| `MaxReconnectAttempts` | 连续失败达该次数即放弃（建立成功清零） | `0`（不限） |
| `MaxElapsedReconnectTime` | 自首次失败起累计重连耗时超过即放弃 | `null`（不限） |
| `ShouldReconnect` | `Func<Exception,bool>`，自定义是否重连（干净 EOF 入参为 `null`；返回 `false` 优雅停止） | `null`（始终重连） |

> `204` 始终终止，不经过 `ShouldReconnect`。

### 终止与可观测性（Completion）

`ISseConnection.Completion` 是后台循环的终止信号，可 `await` 以感知连接彻底结束并区分原因：

- **优雅完成（RanToCompletion）**：`Dispose()`、外部 `ct` 取消、收到 `204`、或 `ShouldReconnect` 返回 `false`。
- **fault `SseReconnectExhaustedException`**：达到 `MaxReconnectAttempts` / `MaxElapsedReconnectTime` 上限而放弃（`AttemptCount` 为连续失败次数，`InnerException` 为最后一次失败原因）。
- **fault 原始异常**：某个用户回调（`OnEvent`/`OnError`/`OnStateChanged`）抛出，或后台循环遇到未预期异常。

```csharp
try
{
    await sse.Completion;   // 连接生命周期结束
}
catch (SseReconnectExhaustedException ex)
{
    Debug.LogError($"重连放弃，共失败 {ex.AttemptCount} 次：{ex.InnerException?.Message}");
}
catch (Exception ex)
{
    Debug.LogError($"SSE 连接异常终止：{ex}");   // 回调抛异常等会落到这里
}
```

注意：**单次连接失败不会完成 `Completion`**（仍会按策略重连，错误经 `OnError` 报告）。`Completion` 只在整个连接彻底停止时完成。**用户回调一旦抛异常，连接会终止并通过 `Completion` 暴露该异常——既不会被静默吞掉，也不会触发无限重连。**

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
        Url = url,
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
