# curl-unity

libcurl 的 Unity3D 原生封装,通过 P/Invoke 提供 **HTTP/2 + HTTP/3 (QUIC)** 网络能力,支持 IL2CPP 运行时。

用一套代码替代 `UnityWebRequest`,获得更现代的协议支持、更灵活的控制、以及真正可靠的跨平台 HTTPS 验证。

## 特性

- **HTTP/2 + HTTP/3 (QUIC)** — 自动协议协商,可配置偏好
- **跨平台** — macOS (Apple Silicon)、iOS、Android (arm64 + armv7 + x86_64)、Windows (x64 + x86)
- **原生性能** — libcurl + OpenSSL 编译为单个动态库,P/Invoke 直调,零 GC 开销
- **IL2CPP 安全** — 所有回调 `[MonoPInvokeCallback]`,无 lambda 传递给 native
- **系统证书** — 各平台使用原生证书验证 (macOS/iOS SecTrust、Android JNI 提取、Windows CryptoAPI)
- **async/await** — `Task<IHttpResponse>` 接口 + `CancellationToken` 支持
- **自动解压** — 默认透明处理 gzip/deflate 响应
- **流式上传/下载** — 大文件不进内存
- **诊断统计** — DNS/TLS/TTFB 逐请求 timing,连接复用率

## 快速导航

- [快速开始](articles/getting-started.md) — 装包、第一个 GET/POST 请求
- [进阶使用](articles/advanced.md) — 流式上传、multipart、代理、cookie、诊断
- [Server-Sent Events (SSE)](articles/sse.md) — 事件流读取、自动重连、自定义重连
- [API 参考](api/index.md) — 所有 public 类型的详细说明

## 安装

```json
{
  "dependencies": {
    "com.basecity.curl-unity": "https://github.com/4AVolcano/curl-unity.git#upm"
  }
}
```

详见 [README](https://github.com/4AVolcano/curl-unity#安装) 和 [快速开始](articles/getting-started.md)。
