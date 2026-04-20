# curl-unity

libcurl 的 Unity3D 封装，通过 P/Invoke 提供 HTTP/2 + HTTP/3 (QUIC) 原生网络能力，优先支持 IL2CPP 运行时。

## 项目结构

```
curl-unity/
├── Packages/
│   └── com.basecity.curl-unity/   # 核心 Unity Package (本项目主体，见下文专节)
├── tests/                         # .NET 测试 (不依赖 Unity Editor)
│   ├── CurlUnity.IntegrationTests/#   xUnit + Kestrel 真网络集成测试
│   └── CurlUnity.UnitTests/       #   xUnit + FakeCurlApi 单元测试
├── deps/                          # 依赖源码 (均为 git submodule)
│   ├── curl/                      #   libcurl
│   ├── openssl/                   #   OpenSSL 3.6.x (https 支持)
│   ├── nghttp2/                   #   HTTP/2
│   ├── nghttp3/                   #   HTTP/3
│   ├── ngtcp2/                    #   QUIC
│   └── zlib/                      #   zlib (Windows和Android依赖)
├── bridge/                        # C bridge 层 (variadic 函数包装)
│   └── curl_unity_bridge.c
├── scripts/                       # 构建脚本
│   ├── build.sh                   #   主构建脚本 (指定平台编译)
│   ├── build-macos.sh             #   macOS 全架构 (ARM64 + x86_64 lipo)
│   ├── build-macos-arm64.sh       #   快捷: macOS ARM64
│   ├── build-macos-x86_64.sh      #   快捷: macOS x86_64
│   ├── build-ios.sh               #   iOS 全架构 (目前仅 ARM64 真机)
│   ├── build-ios-arm64.sh         #   快捷: iOS ARM64
│   ├── build-android.sh           #   Android 全架构 (armv7 + arm64, API 22)
│   ├── build-android-arm64.sh     #   快捷: Android arm64
│   ├── build-android-armv7.sh     #   快捷: Android armv7
│   ├── build-android-x86_64.sh    #   快捷: Android x86_64
│   ├── build-curl-cli.sh          #   编译 curl CLI 用于测试
│   ├── build-windows.bat          #   Windows 主构建脚本 (MSVC)
│   ├── build-windows-x64.bat      #   快捷: Windows x64
│   ├── build-windows-x86.bat      #   快捷: Windows x86
│   ├── sync-plugins.sh            #   把 output/ 同步到 Packages Plugins 目录
│   ├── sync-ci-plugins.sh         #   从 CI 拉取最新产物并同步到 Plugins
│   └── device-test.sh             #   真机自动化测试流水线调度
├── build/                         # 编译工作目录 (gitignore)
├── output/                        # 编译最终产物 (gitignore)
├── demo/
│   └── curl-unity/                # Unity 交互式 Demo 工程 (非回归测试)
└── docs/
    ├── BUILD_GUIDE.md             # 编译指南
    └── DEVICE_TESTING.md          # 真机测试说明
```

## 核心 Package: `Packages/com.basecity.curl-unity/`

项目主体。Unity Package 布局，可被任何 Unity 工程通过 package 引用集成。

```
Packages/com.basecity.curl-unity/
├── package.json
├── THIRD_PARTY_NOTICES.txt
├── Runtime/
│   ├── com.curl.unity.runtime.asmdef
│   ├── Native/                    # P/Invoke & bridge 绑定
│   │   ├── CurlNative.cs          #   常量 + DllImport (libcurl 与 bridge)
│   │   ├── ICurlApi.cs            #   libcurl API 抽象接口 (便于测试替换)
│   │   └── CurlNativeApi.cs       #   ICurlApi 的真实实现
│   ├── Core/                      # libcurl 资源生命周期管理
│   │   ├── CurlGlobal.cs          #   curl_global_init/cleanup 引用计数
│   │   ├── CurlMulti.cs           #   multi handle + 回调注册 + I/O 驱动
│   │   ├── CurlBackgroundWorker.cs#   独占线程驱动 multi.Tick/Poll
│   │   ├── CurlRequest.cs         #   easy handle 封装 (slist/buffer 生命周期)
│   │   ├── CurlResponse.cs        #   完成结果数据
│   │   ├── CurlCookieJar.cs       #   CURLSH 封装 (client 级 cookie 共享)
│   │   ├── CurlCerts.cs           #   平台证书处理 (Windows native CA 等)
│   │   └── CurlLog.cs             #   内部日志 (Unity Debug / stderr 适配)
│   ├── Http/                      # 对外 HTTP API
│   │   ├── IHttpClient.cs / CurlHttpClient.cs
│   │   ├── IHttpRequest.cs / HttpRequest.cs / HttpMethod.cs / HttpVersion.cs
│   │   ├── IHttpResponse.cs / HttpResponse.cs
│   │   └── HttpClientExtensions.cs
│   ├── Diagnostics/               # 可选诊断 (构造时 enableDiagnostics=true 才启用)
│   │   ├── HttpDiagnostics.cs / HttpDiagnosticsSnapshot.cs / HttpRequestTiming.cs
│   └── Plugins/                   # 各平台 native 二进制 (入库)
│       ├── macOS/libcurl_unity.dylib
│       ├── iOS/libcurl_unity.a
│       ├── Android/<abi>/libcurl_unity.so
│       └── Windows/<arch>/libcurl_unity.dll
└── Editor/
    ├── com.basecity.curl-unity.editor.asmdef
    └── CurlPostProcessor.cs       # 构建后处理 (iOS plist 等)
```

层级约定：`Native` ← `Core` ← `Http`/`Diagnostics`。`Http` 层对用户可见，下层内部。

修改 bridge 后，编译产物在 `output/<platform>/<arch>/`，需要 `scripts/sync-plugins.sh` 同步到 `Runtime/Plugins/` 才能被 Unity / 测试项目加载。

## 构建

### 前提

- macOS (Apple Silicon)，Xcode 15+，CMake，Ninja
- Android NDK (通过 ANDROID_NDK_HOME 指定)
- Windows 平台需在 Windows 上用 MSVC 编译 (VS 2019+, CMake, Ninja, Perl)

### 常用命令

```bash
# 编译单平台 (macOS/iOS/Android — 在 macOS 上执行)
./scripts/build.sh macos-arm64
./scripts/build.sh ios-arm64
./scripts/build.sh android-arm64 --android-api 22

# 快捷脚本
./scripts/build-macos-arm64.sh          # macOS ARM64
./scripts/build-ios-arm64.sh            # iOS ARM64
./scripts/build-android.sh              # Android armv7 + arm64 (API 22)
./scripts/build-curl-cli.sh             # 编译 curl CLI 用于测试

# 清理重编
./scripts/build-macos-arm64.sh --clean

# 编译产物同步到 Unity Package Plugins 目录
# (改 bridge 或 CurlNative 里的 DllImport 后必跑，否则 Unity / tests 加载的是旧 dylib)
./scripts/sync-plugins.sh
```

```cmd
:: Windows — 在 Windows 上执行
scripts\build-windows-x64.bat           :: 自动定位 VS 环境编译 x64
scripts\build-windows-x86.bat           :: 自动定位 VS 环境编译 x86
scripts\build-windows.bat               :: 从 VS Native Tools Command Prompt 手动运行
scripts\build-windows.bat --clean       :: 清理重编
```

### 构建架构

- 所有依赖 (OpenSSL/nghttp2/nghttp3/ngtcp2) 编译为**静态库**
- bridge.c + 所有静态库合成**单个动态库** `libcurl_unity.dylib/.so`（iOS 为静态库 `.a`）
- 最终每个平台只分发一个文件

### 关键注意事项

- **依赖库的 CMake 变量名不统一**: nghttp2 用 `BUILD_SHARED_LIBS`，nghttp3/ngtcp2 用 `ENABLE_SHARED_LIB`
- **交叉编译路径隔离**: iOS/Android 必须加 `-DCMAKE_FIND_ROOT_PATH=$PREFIX`
- **依赖有 git submodule**: nghttp2/nghttp3/ngtcp2 需要 `git submodule update --init`
- **禁用宿主机可选依赖**: 必须设置 `CURL_BROTLI=OFF CURL_ZSTD=OFF USE_LIBIDN2=OFF`，否则会链接 Homebrew 动态库

### bridge 层

ARM64 ABI 对 variadic 和 fixed 参数使用不同传参方式。libcurl 的 `curl_easy_setopt`/`curl_easy_getinfo`/`curl_multi_setopt`/`curl_share_setopt` 是 variadic 函数，不能通过 P/Invoke 直接调用。`bridge/curl_unity_bridge.c` 将其包装为 fixed-argument 函数。非 variadic 函数可直接 P/Invoke 调用。

## 常量定义规范

- CURLOPT/CURLINFO 常量值 = `type_base + N`，N 为**十进制**
- CURLINFO 类型前缀：STRING=`0x100000`, LONG=`0x200000`, DOUBLE=`0x300000`, SLIST=`0x400000`, **OFF_T=`0x600000`**（不是 0x300000！）
- 添加新常量时务必对照 `deps/curl/include/curl/curl.h` 中的定义

## 修改脚本时的规范

- 修改 `scripts/` 下的构建脚本后，必须同步更新 `docs/BUILD_GUIDE.md` 对应内容
