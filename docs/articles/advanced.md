# 进阶使用

## Multipart / Form-Data 上传

头像、截图、日志上传等场景。`MultipartFormData` 按 RFC 7578 构造 body:

```csharp
var form = new MultipartFormData();
form.AddText("userId", "42");
form.AddText("desc", "avatar upload");
form.AddFile("avatar", "photo.jpg", fileBytes, "image/jpeg");

using var resp = await client.PostMultipartAsync("https://api.example.com/upload", form);
```

**大文件用 Stream 版本**(不进内存):

```csharp
using var fs = File.OpenRead(path);
var form = new MultipartFormData();
form.AddText("name", "big-video");
form.AddFile("file", Path.GetFileName(path), fs, fs.Length, "video/mp4");

// PostMultipartAsync 检测到 Stream part, 自动走流式上传(BodyStream 通路)
using var resp = await client.PostMultipartAsync(url, form);
```

[MultipartFormData 完整 API 参考](../api/CurlUnity.Http.MultipartFormData.yml)

## 流式上传 raw body

不用 multipart,直接流式发整个 body:

```csharp
using var fs = File.OpenRead(largeFilePath);
var req = new HttpRequest
{
    Method = HttpMethod.Post,
    Url = "https://api.example.com/upload-raw",
    BodyStream = fs,
    BodyLength = fs.Length,  // 已知长度 → 发 Content-Length;
                             // null → Transfer-Encoding: chunked
};
using var resp = await client.SendAsync(req);
```

## 流式下载

下载大文件 / 边下边处理:

```csharp
using var outFile = File.OpenWrite(destPath);
var req = new HttpRequest
{
    Url = bigFileUrl,
    OnDataReceived = (buffer, offset, length) =>
    {
        // 注意: 这个回调在 libcurl 的 worker 线程,不要阻塞
        outFile.Write(buffer, offset, length);
    },
};
using var resp = await client.SendAsync(req);
// 此时 resp.Body == null (流式下载不缓冲到内存)
```

## Server-Sent Events (SSE)

SSE (`text/event-stream`) 在 `CurlUnity.Sse` 命名空间，按「协议核心 / 工程便利」分层：

- **`SseEventParser`** — 纯协议解析器，喂字节出事件，零网络依赖，可独立复用（Layer 0）。
- **`ReadServerSentEventsAsync`** — `IHttpClient` 扩展方法，在一个 `IHttpRequest` 上读**一段** SSE 连接（连接结束即返回，**不重连**），单连接原语（Layer 1）。
- **`OpenSse` / `ISseConnection`** — 带**自动重连 / 退避 / 心跳 / 状态机 / Last-Event-ID** 的便利层（Layer 2）。

多数业务直接用 `OpenSse`（见下「自动重连」）即可开箱即用；只需「读一段就结束」时用 `ReadServerSentEventsAsync`；要完全自定义重连策略时用 `SseEventParser` + `SendAsync`。

```csharp
using CurlUnity.Http;
using CurlUnity.Sse;

var req = new HttpRequest
{
    Url = "https://example.com/events",
    TimeoutMs = 0,        // SSE 长连接不设整体超时
    TcpNoDelay = true,    // 事件不被 Nagle 合并,降低推送延迟
    TcpKeepAlive = true,  // OS 周期探活,尽早发现死连接
};

// onEvent 在 worker 线程触发,禁止阻塞 —— 要碰 Unity API 时自行 marshal 到主线程
using var resp = await client.ReadServerSentEventsAsync(req, evt =>
{
    Debug.Log($"[{evt.EventType}] {evt.Data} (id={evt.LastEventId})");
});
// 连接结束后返回;HTTP 4xx/5xx 不抛,按 resp.StatusCode 判断
```

要点:

- `Accept: text/event-stream` 由库缺省补上(用户已设 Accept 则不覆盖),且**不修改**传入的 `request`。
- 支持任意 method + body:SSE 订阅常用 `POST` + JSON 参数,直接用 `HttpRequest.Method` / `Body`。
- 回调在 **worker 线程**,与 `OnDataReceived` 一致;`request` 上不能再设 `OnDataReceived`(SSE 需接管,已设会 throw)。

### 自动重连（推荐：OpenSse / ISseConnection）

需要断线自动重连时用 `OpenSse`——内部跑重连循环，叠加指数退避、`Last-Event-ID` 续传、`retry:` 响应、空闲/心跳超时、连接状态机：

```csharp
var options = new SseConnectionOptions
{
    ReconnectDelayInit = TimeSpan.FromSeconds(1),  // 首次重连延迟（连成功后重置）；ReconnectDelayIncFn 默认 t*2 clamp[1s,32s]
    IdleTimeout = TimeSpan.FromSeconds(20),         // 20s 无任何数据(含注释心跳)→ 判死重连；null=不启用
};

var req = new HttpRequest { Url = url, TimeoutMs = 0, TcpNoDelay = true, TcpKeepAlive = true };
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
        Url = url, Method = HttpMethod.Post, TimeoutMs = 0, TcpNoDelay = true, TcpKeepAlive = true,
        Headers = new[] { new KeyValuePair<string, string>("Authorization", $"Bearer {token}") },
    };
}, options);
```

要点：缺省自动注入 `Last-Event-ID`（取自已确认 id）；非 2xx 经 `OnError` 收到 `SseHttpStatusException`、空闲超时收到 `TimeoutException`、网络错收到 `CurlHttpException`，随后都自动重连；**`204 No Content` 视为服务端要求停止，直接关闭、不重连**；`Dispose()` 取消在飞请求、停止重连、状态置 `Closed`。**回调全在后台线程，调用方自行 marshal 到主线程。**

> 已知限制：HTTP 状态码要到连接结束才能确认（核心层暂无「响应头就绪」回调），故非 2xx 响应体若恰为 SSE 格式，可能在 `OnError` 前先触发少量 `OnEvent` 并短暂置 `Open`（退避不受影响，仅 2xx 才重置）。真实 SSE 服务器罕见；后续版本将补响应头就绪回调彻底解决。

### 自定义重连（低层：parser + SendAsync）

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
        Url = url, TimeoutMs = 0, TcpNoDelay = true, TcpKeepAlive = true,
        Headers = headers,
        OnDataReceived = (b, o, l) => parser.Feed(b, o, l, OnEvent),
    };
    try { using var _ = await client.SendAsync(req, ct); }
    catch (CurlHttpException) { /* 网络错,下面退避后重连 */ }

    var delay = parser.RetryMilliseconds ?? 1000; // 服务端 retry: 优先,否则默认
    await Task.Delay(delay, ct);
}
```

## 代理

```csharp
// 设置代理(client 级,对后续请求生效)
client.SetProxy(new HttpProxy("http://127.0.0.1:7890"));

// 带认证
client.SetProxy(new HttpProxy(
    "http://proxy.example.com:8080",
    new NetworkCredential("user", "pwd")));

// SOCKS5 走 URL scheme
client.SetProxy(new HttpProxy("socks5://socks-proxy:1080"));
// 或 socks5h:// (DNS 在 proxy 侧解析)

// 关闭代理
client.ClearProxy();
```

**注意 HTTP/3 限制**:QUIC 无法通过 HTTP CONNECT 隧道,启用代理后即使 `PreferredVersion` 是 `PreferH3`,libcurl 也会回退到 HTTP/2 over TCP。

## Cookie 跨请求共享

```csharp
var req1 = new HttpRequest
{
    Url = "https://example.com/login",
    Method = HttpMethod.Post,
    Body = loginBody,
    EnableCookies = true,   // 把 Set-Cookie 写入 client 的 jar
};
using var r1 = await client.SendAsync(req1);

var req2 = new HttpRequest
{
    Url = "https://example.com/profile",
    EnableCookies = true,   // 自动带上之前 jar 里匹配的 cookie
};
using var r2 = await client.SendAsync(req2);
```

jar 绑在 `CurlHttpClient` 实例上,纯内存存储,`Dispose()` 后清空。

## HTTP 版本控制

```csharp
// 默认: PreferH3 (优先 H3,server 不支持会降级到 H2/H1.1)
client.PreferredVersion = HttpVersion.PreferH3;

// 强制 HTTP/2 (调试某些老 server 对 H2 有兼容问题时)
client.PreferredVersion = HttpVersion.Http2;

// 强制 H3 不降级(调试用,生产别这么做)
client.PreferredVersion = HttpVersion.Http3Only;
```

响应里 `resp.Version` 告诉实际协商出的协议:

```csharp
Debug.Log($"Protocol: {resp.Version}");  // e.g. Http3 / Http2 / Http11
```

## 自动响应解压

默认开启(`AutoDecompressResponse = true`),libcurl 发 `Accept-Encoding: gzip, deflate`,自动解压 `resp.Body`。对 JSON/HTML 下行流量降 3-5x。

```csharp
var req = new HttpRequest
{
    Url = "...",
    AutoDecompressResponse = false,  // 不常见, 比如想看原始压缩字节
};
```

## 诊断统计

构造时开启:

```csharp
using var client = new CurlHttpClient(enableDiagnostics: true);

// ...跑一些请求...
using var resp = await client.GetAsync("https://api.example.com/");

// 单个请求的 timing
var timing = client.Diagnostics.GetTiming(resp);
Debug.Log($"DNS: {timing.DnsTimeUs}μs, TLS: {timing.TlsTimeUs}μs, TTFB: {timing.FirstByteTimeUs}μs");

// 聚合快照
var snapshot = client.Diagnostics.GetSnapshot();
Debug.Log(snapshot);  // Requests=N (ok=X fail=Y) Connections=Z Reuse=W%...
```

- 耗时单位均为**微秒** (μs)
- 连接复用率 `ConnectionReuseRate` 基于 libcurl 内部 connection ID 去重
- 需要分阶段采样时调 `Diagnostics.Reset()` 清零

[HttpDiagnostics API](../api/CurlUnity.Diagnostics.HttpDiagnostics.yml)

## SSL 证书验证

默认开启,各平台走原生证书库(macOS/iOS SecTrust、Android JNI 提取、Windows CryptoAPI)。**开发调试** 期间接入抓包工具(Charles/mitmproxy)时可以临时关闭:

```csharp
client.VerifySSL = false;  // 仅调试!生产环境务必保持 true
```

## 默认 User-Agent

client 级默认:

```csharp
client.UserAgent = "MyGame/1.2.3";  // 覆盖默认 "CurlUnity/0.1.0"
```

单个请求覆盖:

```csharp
var req = new HttpRequest
{
    Url = "...",
    Headers = new[]
    {
        new KeyValuePair<string, string>("User-Agent", "CrashReporter/1.0"),
    },
};
```

请求级 header 优先于 client 级 UA(libcurl 的 slist 优先于 `CURLOPT_USERAGENT`)。

## 线程模型要点

- `SendAsync` 是真正异步,I/O 在专属 worker 线程驱动,不会卡 Unity 主线程
- 完成后 Task 的 continuation 默认回原 SynchronizationContext(Unity 主线程)
- `OnDataReceived` 回调在 **worker 线程** 执行,不要碰 Unity API
- `CurlHttpClient` 实例长期持有,不要为每次请求重建
