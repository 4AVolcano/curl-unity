# curl for Unity

libcurl 的 Unity3D 原生封装,通过 P/Invoke 提供 **HTTP/2 + HTTP/3 (QUIC)** 网络能力,支持 IL2CPP 运行时。用一套代码替代 `UnityWebRequest`。

## 安装

编辑工程的 `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.basecity.curl-unity": "https://github.com/4AVolcano/curl-unity.git#upm"
  }
}
```

- `#upm` 跟最新正式版
- `#upm/v0.1.0` 锁定到具体版本

支持平台:macOS (Apple Silicon + Intel)、iOS、Android (arm64 / armv7 / x86_64)、Windows (x64 / x86)。全平台预编译二进制已随 package 分发,无需额外构建。

## 最小示例

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
            using var resp = await client.GetAsync("https://api.github.com/");
            if (resp.StatusCode == 200)
                Debug.Log(System.Text.Encoding.UTF8.GetString(resp.Body));
        }
        catch (CurlHttpException ex)
        {
            // 网络 / TLS / 超时 / 协议错。按 ex.ErrorKind 做重试或上报策略。
            Debug.LogError($"Request failed: {ex.ErrorKind} ({ex.CurlCode})");
        }
    }
}
```

## 文档

- **在线文档站**: <https://4avolcano.github.io/curl-unity/>
- [快速开始](https://github.com/4AVolcano/curl-unity/blob/master/docs/articles/getting-started.md)
- [进阶使用](https://github.com/4AVolcano/curl-unity/blob/master/docs/articles/advanced.md) — 流式上传下载、Multipart、代理、Cookie、诊断

## 源码 / Issues

<https://github.com/4AVolcano/curl-unity>

## 许可证

C# 代码和构建脚本:MIT。依赖库各有许可证,详见 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)。
