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

## 响应头就绪回调（OnHeadersReceived）

流式下载等场景下，如需在 body 到达前检查状态码：

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
    },
    OnDataReceived = (buf, off, len) => { /* 处理 body */ },
};
using var resp = await client.SendAsync(req);
```

- 回调中 throw 即中止传输，异常透传给 `SendAsync` 的 Task。
- HEAD / 204 等无 body 响应也会触发。
- `EnableResponseHeaders = true` 时回调中 `resp.Headers` 可用；否则为 null，但 `StatusCode` / `ContentType` / `Version` 始终可用。

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
