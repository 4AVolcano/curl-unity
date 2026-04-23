# 快速开始

## 1. 安装

编辑 Unity 工程的 `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.basecity.curl-unity": "https://github.com/4AVolcano/curl-unity.git#upm"
  }
}
```

`#upm` 跟最新正式版。锁定具体版本用 `#upm/v0.2.0`。

## 2. 第一个 GET 请求

```csharp
using System;
using CurlUnity.Http;
using UnityEngine;

public class Example : MonoBehaviour
{
    async void Start()
    {
        using var client = new CurlHttpClient();
        try
        {
            using var resp = await client.GetAsync("https://api.github.com/repos/4AVolcano/curl-unity");
            if (resp.StatusCode == 200)
            {
                var json = System.Text.Encoding.UTF8.GetString(resp.Body);
                Debug.Log(json);
            }
            else
            {
                Debug.LogError($"HTTP {resp.StatusCode}");
            }
        }
        catch (CurlHttpException ex)
        {
            // 网络 / TLS / 超时 / 协议错
            Debug.LogError($"Request failed: {ex.ErrorKind} (curl {ex.CurlCode})");
        }
    }
}
```

几个关键点:

- `CurlHttpClient` 实例**线程安全,长期存活** — 不要每次请求都 new。在 MonoBehaviour / ScriptableObject / singleton 里持有
- `IHttpResponse` 必须 `Dispose()` — 最简单就是 `using var resp = ...`
- 只要 `await` 成功返回,就拿到了 HTTP 响应(含 4xx / 5xx);网络/TLS/超时等失败会抛 `CurlHttpException`,详见[错误处理](error-handling.md)
- 响应体 `resp.Body` 是 `byte[]`,用 `Encoding.UTF8.GetString` 解码文本

## 3. POST JSON

```csharp
var body = @"{""name"":""alice"",""age"":30}";
using var resp = await client.PostJsonAsync("https://api.example.com/users", body);
```

## 4. POST 表单 (application/x-www-form-urlencoded)

OAuth token 端点等场景:

```csharp
var fields = new Dictionary<string, string>
{
    ["grant_type"] = "password",
    ["username"]   = "alice",
    ["password"]   = "s3cret",
};
using var resp = await client.PostFormAsync("https://api.example.com/token", fields);
```

支持重复 key (OAuth scope 等):

```csharp
var fields = new[]
{
    new KeyValuePair<string, string>("scope", "read"),
    new KeyValuePair<string, string>("scope", "write"),
};
```

## 5. 自定义 header 和认证

```csharp
var req = new HttpRequest
{
    Method = HttpMethod.Get,
    Url = "https://api.example.com/me",
}.WithBearerToken("eyJhbGc...");   // 链式添加 Authorization: Bearer

using var resp = await client.SendAsync(req);
```

Basic 认证:

```csharp
var req = new HttpRequest { Url = "..." }.WithBasicAuth("alice", "s3cret");
```

任意自定义 header:

```csharp
var req = new HttpRequest
{
    Url = "...",
    Headers = new[]
    {
        new KeyValuePair<string, string>("X-Request-Id", "abc123"),
        new KeyValuePair<string, string>("X-Client-Version", "1.2.3"),
    },
};
```

## 6. 超时 / 取消

```csharp
var req = new HttpRequest
{
    Url = "...",
    ConnectTimeoutMs = 5000,   // TCP 建连 5s
    TimeoutMs        = 30000,  // 整体 30s
};

var cts = new CancellationTokenSource();
// 别的地方可调 cts.Cancel() 主动取消
using var resp = await client.SendAsync(req, cts.Token);
```

## 7. 错误处理

```csharp
try
{
    using var resp = await client.SendAsync(req, ct);
    if (resp.StatusCode >= 400)
    {
        // 拿到响应,HTTP 状态是错误
        Debug.LogError($"HTTP {resp.StatusCode}");
        return;
    }
    // 正常处理 resp.Body
}
catch (CurlHttpException ex)
{
    // 网络 / TLS / 超时 / 协议错。按 ex.ErrorKind 分支处理
    if (ex.ErrorKind == HttpErrorKind.Timeout || ex.ErrorKind == HttpErrorKind.NetworkIo)
    {
        // 瞬态错误,考虑重试
    }
    Debug.LogError($"{ex.ErrorKind} (curl {ex.CurlCode})");
}
catch (OperationCanceledException)
{
    // 主动取消或 client 被 Dispose
}
```

完整的错误分类和异常契约见[错误处理](error-handling.md)。

## 下一步

- [进阶使用](advanced.md) — 流式上传、multipart、代理、cookie、诊断
- [API 参考](../api/index.md) — 所有类型的详细说明
