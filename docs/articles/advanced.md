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

SSE (`text/event-stream`) 在 `CurlUnity.Sse` 命名空间，按「协议核心 / 工程策略」分两层：

- **`SseEventParser`** — 纯协议解析器，喂字节出事件，零网络依赖，可独立复用。
- **`ReadServerSentEventsAsync`** — `IHttpClient` 扩展方法，在一个 `IHttpRequest` 上读**一段** SSE 连接，把响应体增量解析成 `SseEvent` 回调出来；连接结束(服务端关流 / 网络错误 / 取消)即返回。

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

### 重连 / Last-Event-ID

`ReadServerSentEventsAsync` 是**单连接原语,不重连** —— 重连 / 退避 / 心跳属工程策略,按需自行组合。`SseEventParser` 暴露 `LastEventId` / `RetryMilliseconds`,可据此用 `SendAsync` 实现断线续传(复用同一个 parser 跨连接):

```csharp
var parser = new SseEventParser();
while (!ct.IsCancellationRequested)
{
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
