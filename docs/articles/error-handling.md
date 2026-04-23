# 错误处理

`CurlHttpClient` 的 `SendAsync`(以及 `GetAsync` / `PostAsync` 等扩展) 是 **"成功返回响应, 失败抛异常"** 的契约, 与 .NET 的 `HttpClient` 一致。这篇文档说明一次请求可能走到的全部失败路径, 以及该 catch 什么。

## 异常契约概览

| 场景 | 异常类型 | 是否应 catch |
|---|---|---|
| 主动 `ct.Cancel()` / client 在请求 pending 时被 `Dispose` | `OperationCanceledException` | 通常不 catch, 让它冒泡 |
| 用户回调(`OnDataReceived` 下载 / `BodyStream.Read` 上传) 抛的异常 | **原异常, 保留栈** | 由用户代码自己 catch(知道自己抛了什么) |
| 网络 / TLS / 超时 / 协议错 / libcurl 内部失败 | [`CurlHttpException`](xref:CurlUnity.Http.CurlHttpException) | catch 这一种即可覆盖所有运行时失败 |
| 用法错误: `null` 参数 / 已 `Dispose` 的 client / `Body` 与 `BodyStream` 互斥 / `HEAD` 带 body | 原生 `ArgumentException` / `ObjectDisposedException` / `InvalidOperationException` | **不要 catch**, 这是 bug 应当暴露 |
| 拿到 HTTP 响应(含 4xx / 5xx) | 不抛, 返回 `IHttpResponse` | 业务侧读 `StatusCode` 判断 |

重点: **HTTP 4xx / 5xx 不算失败**, 通过 `IHttpResponse.StatusCode` 处理, 不会抛异常。

## `CurlHttpException`

网络层失败的统一异常类型。两个关键字段:

- `ErrorKind` : [`HttpErrorKind`](xref:CurlUnity.Http.HttpErrorKind) 枚举, 业务代码基于这个做 switch / 重试策略
- `CurlCode` : 原始 libcurl 返回值(`CURLcode` 或 `CURLMcode`), 用于日志和 issue 排查

基本用法:

```csharp
try
{
    using var resp = await client.GetAsync(url);
    // ... 处理 resp
}
catch (CurlHttpException ex)
{
    Debug.LogError($"{ex.ErrorKind}: {ex.Message}");
}
```

## `HttpErrorKind` 分类与处理建议

分类粒度按"使用者会做的响应决策"划分(重试 / 改配置 / 上报)。

| `ErrorKind` | 常见原因 | 处理建议 |
|---|---|---|
| `InvalidUrl` | URL 格式错或 scheme 不支持 | URL 来自用户输入就提示用户; 写死的 URL 是 bug |
| `DnsFailed` | DNS 解析失败(目标主机或代理) | 检查网络 / DNS 服务, 可重试 |
| `ConnectFailed` | TCP 建连失败(端口不通 / 服务器拒绝) | 可重试, 若持续发生则服务不可用 |
| `TlsError` | 证书 / 握手失败 | 检查证书 / 信任链 / 系统时间; 不要盲目关 `VerifySSL` |
| `Timeout` | 超过 `TimeoutMs` 或 `ConnectTimeoutMs` | 考虑重试或放宽超时 |
| `NetworkIo` | 传输中途 I/O 中断(send/recv 被中断、短读) | 瞬态, 可重试 |
| `ProtocolError` | HTTP/2/3 帧错、奇怪响应、编码错 | 降级到 `HttpVersion.Http11` 试试; 或服务器端有问题 |
| `ProxyError` | 代理握手失败 | 检查代理配置和可达性 |
| `TooManyRedirects` | 重定向链超过 libcurl 默认上限 | 目标有重定向死循环 |
| `OutOfMemory` | libcurl 报告内存不足 | 极罕见; 降低并发或减少在内存里缓冲的响应体 |
| `SetupFailed` | libcurl 内部 setup 失败(`curl_multi_*` / 不认识的 option 等) | 几乎只在本库或 libcurl 自身有 bug 时出现; 请提 issue |
| `Unknown` | 未列入已知分类的 `CURLcode`(FTP/LDAP/TFTP/SSH/RTSP 专用码, 或未来 libcurl 新增) | 看 `CurlCode` 原始值 |

典型的重试策略大致像这样:

```csharp
static bool IsTransient(CurlHttpException ex) => ex.ErrorKind switch
{
    HttpErrorKind.DnsFailed      => true,
    HttpErrorKind.ConnectFailed  => true,
    HttpErrorKind.NetworkIo      => true,
    HttpErrorKind.Timeout        => true,
    _                             => false,
};
```

## 用户回调的异常

**下载回调** (`HttpRequest.OnDataReceived`) 和 **流式上传** (`HttpRequest.BodyStream`) 里用户代码抛的异常不会被 `CurlHttpException` 包装, 而是**以原始异常 rethrow, 保留完整栈**。根因在用户代码, 包一层反而让 catch 位置和错误信息跟业务代码语义错位。

```csharp
var req = new HttpRequest
{
    Url = url,
    OnDataReceived = (buf, off, count) =>
    {
        // 假如这里抛了 IOException, client.SendAsync 会以该 IOException 冒出
        _file.Write(buf, off, count);
    },
};

try
{
    using var resp = await client.SendAsync(req);
}
catch (IOException ex)
{
    // 能 catch 到的就是 OnDataReceived 里抛的那个 IOException
}
catch (CurlHttpException ex)
{
    // 真正的网络层失败才是这里
}
```

## 取消

`CancellationToken` 取消和 `CurlHttpClient.Dispose` 时 pending 的请求都统一抛 `OperationCanceledException`, 与 .NET 标准 async API 一致:

```csharp
var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
try
{
    using var resp = await client.SendAsync(req, cts.Token);
}
catch (OperationCanceledException)
{
    // 取消或 client 已关闭; 不需要当作 error 上报
}
catch (CurlHttpException ex)
{
    // 网络失败
}
```

## 诊断统计

启用诊断时([`CurlHttpClient(enableDiagnostics: true)`](xref:CurlUnity.Http.CurlHttpClient.#ctor(System.Boolean))), 成功和失败(`CurlHttpException` / 用户回调异常) 的请求都会计入 `TotalRequests`, 按成功失败分在 `SuccessRequests` / `FailedRequests`。 **取消和用法错误不计入** — 前者不是"网络请求失败", 后者是 bug。

```csharp
using var client = new CurlHttpClient(enableDiagnostics: true);
// ... 一批请求
var snap = client.Diagnostics.GetSnapshot();
Debug.Log($"{snap.SuccessRequests}/{snap.TotalRequests} OK, {snap.FailedRequests} failed");
```

## 一张图总结

```
SendAsync(req, ct)
  │
  ├─ request == null / Body+BodyStream 冲突 / client 已 Dispose / BodyLength<0
  │     └─→ 抛 ArgumentException / IOE / ODE (用法错误, 不要 catch, 让 bug 暴露)
  │
  ├─ ct 已取消 / 执行中被取消 / client Dispose 打断 pending
  │     └─→ 抛 OperationCanceledException
  │
  ├─ 流式上传 Stream.Read / 下载 OnDataReceived 里抛异常
  │     └─→ 原异常 rethrow (保栈)
  │
  ├─ libcurl 返回非 0 (DNS/TCP/TLS/超时/协议/...)
  │     └─→ 抛 CurlHttpException(ErrorKind=Xxx, CurlCode=N)
  │
  └─ 成功拿到响应 (含 4xx / 5xx)
        └─→ 返回 IHttpResponse, StatusCode 里读
```
