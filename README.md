# curl-unity

[![Test](https://github.com/4AVolcano/curl-unity/actions/workflows/test.yml/badge.svg?branch=master)](https://github.com/4AVolcano/curl-unity/actions/workflows/test.yml)
[![Build](https://github.com/4AVolcano/curl-unity/actions/workflows/build.yml/badge.svg)](https://github.com/4AVolcano/curl-unity/actions/workflows/build.yml)
[![Docs](https://github.com/4AVolcano/curl-unity/actions/workflows/docs.yml/badge.svg)](https://4avolcano.github.io/curl-unity/)

libcurl 的 Unity3D 原生封装，通过 P/Invoke 提供 **HTTP/2 + HTTP/3 (QUIC)** 网络能力，支持 IL2CPP 运行时。

用一套代码替代 UnityWebRequest，获得更现代的协议支持、更灵活的控制、以及真正可靠的跨平台 HTTPS 验证。

## 特性

- **HTTP/2 + HTTP/3 (QUIC)** — 自动协议协商，可配置偏好
- **跨平台** — macOS (Apple Silicon)、iOS、Android (arm64 + armv7)、Windows (x64 + x86)
- **原生性能** — libcurl + OpenSSL 编译为单个动态库，P/Invoke 直调，零 GC 开销
- **IL2CPP 安全** — 所有回调 `[MonoPInvokeCallback]`，无 lambda 传递给 native
- **系统证书** — 各平台使用原生证书验证（macOS/iOS SecTrust、Android JNI 提取、Windows CryptoAPI）
- **async/await** — `Task<IHttpResponse>` 接口 + `CancellationToken` 支持
- **诊断统计** — DNS/TLS/TTFB 逐请求 timing，连接复用率

## 安装

作为 Unity Package Manager 的 git package 引用。编辑工程的 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.basecity.curl-unity": "https://github.com/4AVolcano/curl-unity.git#upm"
  }
}
```

引用形式：

| 引用 | 说明 |
|------|------|
| `...curl-unity.git#upm` | 跟最新正式版（upm 分支 HEAD） |
| `...curl-unity.git#upm/v0.2.0` | 锁定到具体版本 |

`upm` 分支由 CI 在打 `v*` tag 时自动发布，扁平到根目录，包含 `Runtime/`、`Editor/`、`package.json` 和全平台预编译二进制，**不含 `deps/` 源码**，体积小。

## 文档

- **在线文档站**: <https://4avolcano.github.io/curl-unity/> — 完整 API 参考 + 教程(master 每次更新自动部署)
- [快速开始](docs/articles/getting-started.md) — 第一个 GET/POST 请求、常用场景、错误处理
- [进阶使用](docs/articles/advanced.md) — 流式上传/下载、Multipart、代理、Cookie、诊断统计
- 本地预览:`./scripts/build-docs.sh --serve` → http://localhost:8080

## 支持平台

| 平台 | 架构 | 产物 | 最低版本 |
|------|------|------|---------|
| macOS | ARM64 | libcurl_unity.dylib | macOS 11.0+ (Big Sur) |
| macOS | x86_64 | libcurl_unity.dylib | macOS 10.14+ (Mojave) |
| iOS | ARM64 | libcurl_unity.a (静态库) | iOS 12.0 |
| Android | arm64-v8a, armeabi-v7a, x86_64 | libcurl_unity.so | API 22 (Android 5.1) |
| Windows | x64, x86 | libcurl_unity.dll | Windows 7 SP1+ |

## 依赖

所有依赖编译为静态库，合入单个动态库分发，使用者无需额外安装。

| 库 | 版本 | 用途 |
|---|---|---|
| [curl](https://curl.se/) | 8.19.0 | HTTP 客户端核心 |
| [OpenSSL](https://www.openssl.org/) | 3.6.2 | TLS / HTTPS |
| [nghttp2](https://nghttp2.org/) | 1.68.1 | HTTP/2 |
| [nghttp3](https://nghttp2.org/nghttp3/) | 1.15.0 | HTTP/3 |
| [ngtcp2](https://nghttp2.org/ngtcp2/) | 1.21.0 | QUIC |
| [zlib](https://zlib.net/) | 1.3.2 | 压缩 (Windows/Android) |

## 项目结构

```
curl-unity/
├── deps/                       # 依赖源码 (git submodules)
│   ├── curl/                   #   libcurl
│   ├── openssl/                #   OpenSSL
│   ├── nghttp2/                #   HTTP/2
│   ├── nghttp3/                #   HTTP/3
│   ├── ngtcp2/                 #   QUIC
│   └── zlib/                   #   zlib
├── bridge/                     # C bridge 层
│   ├── curl_unity_bridge.c     #   variadic 函数包装
│   └── exports.def             #   Windows DLL 导出定义
├── scripts/                    # 构建 & 工具脚本
│   ├── build.sh                #   主构建脚本 (macOS/iOS/Android)
│   ├── build-windows.bat       #   Windows 构建 (MSVC)
│   └── ...
├── Packages/
│   └── com.basecity.curl-unity/  # Unity Package (待发布)
├── demo/
│   └── curl-unity/             # Unity 测试工程
├── tests/                      # 测试
│   ├── verify_build.c          #   C smoke test (CI)
│   └── CurlUnity.IntegrationTests/  # xUnit 集成测试
└── docs/                       # 文档 (DocFX 站点源 + 构建/真机测试指南)
```

## 构建

### 前提

- **macOS/iOS/Android**: macOS (Apple Silicon), Xcode 15+, CMake, Ninja
- **Android**: 另需 Android NDK (通过 ANDROID_NDK_HOME 或自动探测)
- **Windows**: Windows 上用 MSVC 编译 (VS 2017+, CMake, Ninja, Perl)

### 快速开始

```bash
# 全平台聚合构建
./scripts/build-macos.sh            # macOS ARM64 + x86_64 → universal binary
./scripts/build-ios.sh              # iOS ARM64
./scripts/build-android.sh          # Android armv7 + arm64 + x86_64
scripts\build-windows-x64.bat       # Windows x64
scripts\build-windows-x86.bat       # Windows x86

# 单架构构建
./scripts/build-macos-arm64.sh      # macOS ARM64 only
./scripts/build-macos-x86_64.sh     # macOS x86_64 only
./scripts/build-android-arm64.sh    # Android arm64 only
./scripts/build-android-x86_64.sh   # Android x86_64 only (模拟器)
./scripts/build.sh <platform>       # 任意平台 (底层入口)
```

产物输出到 `output/` 目录。详细构建指南见 [docs/BUILD_GUIDE.md](docs/BUILD_GUIDE.md)。

### 构建架构

所有依赖编译为**静态库**，与 `bridge.c` 链接成**单个动态库**（iOS 为静态库 `.a`）。最终每个平台只分发一个文件。

Bridge 层解决了 ARM64 ABI 上 variadic 函数不能通过 P/Invoke 直接调用的问题（`curl_easy_setopt` / `curl_easy_getinfo` / `curl_multi_setopt`）。

### CI

GitHub Actions 提供全平台自动构建：

| 事件 | Workflow | 说明 |
|------|----------|------|
| push tag `v*` | `build.yml` | 全平台构建 + 验证 + 上传 artifacts + 发布到 `upm` 分支（打 `upm/v*` tag） |
| push / PR to master | `test.yml` | 运行测试 |
| 手动触发 | `build.yml` | workflow_dispatch（只构建，不发布） |

## 开发计划

### Phase 1: Native 库构建 ✅

libcurl + 全部依赖的跨平台编译管线。

- [x] macOS ARM64 + x86_64 构建
- [x] iOS ARM64 构建
- [x] Android arm64 + armv7 + x86_64 构建
- [x] Windows x64 + x86 构建 (MSVC)
- [x] Bridge 层 (variadic 函数包装)
- [x] CI 全平台自动构建 (GitHub Actions)
- [x] 构建验证 (smoke test + 符号检查)

### Phase 2: Unity Package 🚧

C# 封装层 + Unity 集成，开发中。

- [x] P/Invoke 绑定 (CurlNative)
- [x] Core 封装 (CurlMulti / BackgroundWorker / Request / Response)
- [x] Http 公开 API (IHttpClient / CurlHttpClient / HttpRequest / HttpResponse)
- [x] 扩展方法 (GetAsync / PostJsonAsync / ...)
- [x] 诊断模块 (HttpDiagnostics / HttpRequestTiming)
- [x] CA 证书 (Apple SecTrust / Android JNI / Windows CryptoAPI)
- [x] iOS 构建后处理 (CurlPostProcessor)
- [x] Cookie 跨请求共享
- [x] Proxy 支持
- [x] Multipart / Form-Data 上传
- [x] 流式上传 (READFUNCTION)
- [ ] 完善单元测试 & CI 测试流程
- [x] 发布为 UPM 包（CI 自动发布到 `upm` 分支）

## 许可证

本项目的 C# 代码和构建脚本采用 MIT 许可证。

所使用的开源依赖库各有其许可证（curl License、Apache 2.0、MIT、zlib License），详见 [THIRD_PARTY_NOTICES.txt](Packages/com.basecity.curl-unity/THIRD_PARTY_NOTICES.txt)。分发包含本库的应用时，需附带这些许可声明。
