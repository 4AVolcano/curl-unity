# libcurl 跨平台编译指南 (Unity3D / P/Invoke)

本指南基于 curl 源码 (rc-8.20.0) 编写，目标是将 libcurl 编译为 macOS / iOS / Windows / Android 四个平台的原生库，通过 P/Invoke 在 Unity3D (IL2CPP) 中使用。

编译目标特性：**HTTP/2** (nghttp2) + **HTTP/3** (ngtcp2 + nghttp3) + **OpenSSL** (v3.5.0+)

### 链接策略

本指南默认采用**依赖静态链接**策略：所有依赖库（OpenSSL、nghttp2、ngtcp2、nghttp3、zlib）编译为静态库，最终链接进 libcurl 动态库中。这样每个平台只需分发一个文件（`libcurl.dylib` / `libcurl.dll` / `libcurl.so`），大幅简化 Unity 集成。iOS 平台例外：由于 App Store 限制，libcurl 本身也编译为静态库 `.a`。

> **备注：若需要依赖以动态库形式分发**（例如需要单独更新某个依赖），将依赖库编译时的 `-DBUILD_SHARED_LIBS=OFF` 改为 `ON`，并移除 `-DCMAKE_POSITION_INDEPENDENT_CODE=ON`（动态库默认 PIC）。OpenSSL 对应将 `no-shared` 改为 `shared`。此时每个平台需分发多个库文件，并注意 rpath/install_name 的配置。

---

## 目录

1. [依赖总览](#1-依赖总览)
2. [基础环境配置](#2-基础环境配置)
3. [公共编译参数](#3-公共编译参数)
4. [依赖库编译](#4-依赖库编译)
5. [各平台编译步骤](#5-各平台编译步骤)
   - [macOS (x86_64 / arm64)](#51-macos)
   - [iOS (arm64 / arm64-simulator / x86_64-simulator)](#52-ios)
   - [Windows (x86_64 / x86)](#53-windows)
   - [Android (arm64-v8a / armeabi-v7a / x86_64)](#54-android)
6. [产物整合与 Unity 集成](#6-产物整合与-unity-集成)
7. [常见问题与注意事项](#7-常见问题与注意事项)

---

## 1. 依赖总览

libcurl 需要以下依赖库，每个依赖都需要针对目标平台分别交叉编译：

| 依赖库 | 版本要求 | 用途 |
|--------|---------|------|
| **OpenSSL** | >= 3.5.0 | TLS 后端 + QUIC 支持 |
| **nghttp2** | >= 1.x | HTTP/2 支持 |
| **ngtcp2** | >= 1.12.0 | QUIC 协议实现 |
| **nghttp3** | >= 1.0.0 | HTTP/3 协议实现 |
| **zlib** | >= 1.x | gzip/deflate 压缩 |

> **重要约束**：HTTP/3 需要 QUIC，QUIC 需要 TLS。OpenSSL 3.5.0+ 原生支持 QUIC，无需使用 quictls 分支。此外 HTTP/3 + QUIC 与 MultiSSL 特性互斥，不能同时启用多个 SSL 后端。

### 依赖关系链

```
libcurl (动态库, 唯一分发产物)
├── OpenSSL (>= 3.5.0)    ← 静态链接, TLS + QUIC TLS
├── nghttp2               ← 静态链接, HTTP/2
├── ngtcp2                ← 静态链接, QUIC (依赖 OpenSSL + nghttp3)
│   ├── OpenSSL
│   └── nghttp3
├── nghttp3               ← 静态链接, HTTP/3
└── zlib                  ← 静态链接, 压缩
```

---

## 2. 基础环境配置

### 2.1 macOS 宿主机通用工具

所有平台的交叉编译均在 macOS 上执行（Windows 除外，见下文），需要安装以下工具：

```bash
# Homebrew 基础工具
brew install cmake ninja autoconf automake libtool pkg-config

# 确认 Xcode Command Line Tools 已安装
xcode-select --install
```

### 2.2 各平台特殊环境

#### macOS / iOS

- **Xcode** >= 15.0（包含 Apple Clang 和 iOS SDK）
- 确认 SDK 路径可用：

  ```bash
  xcrun --sdk macosx --show-sdk-path
  xcrun --sdk iphoneos --show-sdk-path
  xcrun --sdk iphonesimulator --show-sdk-path
  ```

#### Windows

推荐在 Windows 机器上使用 **Visual Studio 2019+** 原生编译（MSVC）。原因：

- Unity IL2CPP 在 Windows 上使用 MSVC 工具链，MinGW 编译的 DLL 会混用不同的 CRT，可能导致堆内存管理等问题
- MSVC 编译的产物兼容性和稳定性更好

所需环境：

- **Visual Studio 2022**（或 2019），安装"使用 C++ 的桌面开发"工作负载
- **Perl**（编译 OpenSSL 需要，推荐 Strawberry Perl）
- **NASM**（OpenSSL 汇编优化需要）
- **CMake** + **Ninja**（可通过 VS Installer 安装）

> **macOS 交叉编译备选方案**：可使用 MinGW-w64（`brew install mingw-w64`）在 macOS 上交叉编译，但不推荐用于生产环境。如确需使用，后文有 MinGW 交叉编译参考。

#### Android

- **Android NDK**：通过 Android Studio SDK Manager 安装，或手动下载

  ```bash
  # 确认 NDK 路径（以 r26 为例）
  export ANDROID_NDK_HOME=~/Library/Android/sdk/ndk/26.1.10909125
  ```

- 需要 NDK r21+ 以支持 CMake toolchain 文件

---

## 3. 公共编译参数

### 3.1 构建系统选择

推荐使用 **CMake**，因为它对交叉编译支持更好，且所有目标平台都支持。

### 3.2 依赖库公共编译策略

所有依赖库统一编译为**静态库 + PIC**，以便链接进 libcurl 动态库：

```cmake
# 依赖库公共参数
-DCMAKE_BUILD_TYPE=Release
-DCMAKE_POSITION_INDEPENDENT_CODE=ON   # PIC，链接进动态库必须
-DENABLE_LIB_ONLY=ON                   # 只编译库，不编译工具/示例/测试
```

> **注意：不同依赖库控制静态/动态的 CMake 变量不同：**
>
> | 依赖库 | 静态库参数 |
> |--------|-----------|
> | **nghttp2** | `-DBUILD_SHARED_LIBS=OFF -DBUILD_STATIC_LIBS=ON` （标准 CMake 变量）|
> | **nghttp3** | `-DENABLE_SHARED_LIB=OFF -DENABLE_STATIC_LIB=ON` |
> | **ngtcp2** | `-DENABLE_SHARED_LIB=OFF -DENABLE_STATIC_LIB=ON` |

OpenSSL 使用自己的构建系统（Configure + make），对应参数为 `no-shared`（静态）。

### 3.3 交叉编译路径隔离

iOS 和 Android 的交叉编译会触发 CMake 的路径隔离机制（`CMAKE_FIND_ROOT_PATH_MODE_*`），`find_package` 默认**只搜索 sysroot 内的路径**。即使设置了 `CMAKE_PREFIX_PATH` 或 `OPENSSL_ROOT_DIR`，也可能被忽略。

**解决方式**：对 iOS / Android 平台，在所有 cmake 调用中额外指定：

```cmake
-DCMAKE_FIND_ROOT_PATH=$PREFIX     # 将依赖安装目录标记为"目标平台路径"
```

这样 `FindOpenSSL`、`FindNGHTTP2` 等模块才能正确找到我们自行编译的依赖。macOS 本机编译不需要此参数。

### 3.4 libcurl 公共 CMake 参数

以下参数在所有平台编译 libcurl 时统一使用：

```cmake
# === 构建类型 ===
-DCMAKE_BUILD_TYPE=Release

# === 输出控制 ===
-DBUILD_CURL_EXE=OFF              # 不编译 curl 命令行工具
-DBUILD_SHARED_LIBS=ON            # libcurl 编译为动态库（P/Invoke 需要）
-DBUILD_STATIC_LIBS=OFF           # 不额外生成静态库

# === SSL/TLS ===
-DCURL_ENABLE_SSL=ON
-DCURL_USE_OPENSSL=ON             # 使用 OpenSSL 后端

# === HTTP/2 + HTTP/3 ===
-DUSE_NGHTTP2=ON                  # 启用 HTTP/2
-DUSE_NGTCP2=ON                   # 启用 HTTP/3 (ngtcp2 + nghttp3)

# === 禁用不需要的协议（仅保留 HTTP/HTTPS）===
-DHTTP_ONLY=ON                    # 禁用 FTP/DICT/GOPHER/IMAP/POP3 等所有非 HTTP 协议

# === 禁用不需要的特性和可选依赖 ===
-DCURL_DISABLE_LDAP=ON            # 禁用 LDAP
-DCURL_DISABLE_LDAPS=ON           # 禁用 LDAPS
-DCURL_USE_LIBPSL=OFF             # 禁用 Public Suffix List
-DCURL_USE_LIBSSH2=OFF            # 禁用 SSH
-DCURL_USE_LIBSSH=OFF             # 禁用 SSH
-DCURL_BROTLI=OFF                 # 禁用 Brotli 压缩（避免链接系统动态库）
-DCURL_ZSTD=OFF                   # 禁用 Zstd 压缩（同上）
-DUSE_LIBIDN2=OFF                 # 禁用 IDN 国际化域名（同上）

# === 禁用不需要的认证方式（按需保留）===
-DCURL_DISABLE_AWS=ON             # 禁用 AWS Sigv4
-DCURL_DISABLE_KERBEROS_AUTH=ON   # 禁用 Kerberos
-DCURL_DISABLE_NEGOTIATE_AUTH=ON  # 禁用 Negotiate (SPNEGO)
-DCURL_DISABLE_NTLM=ON           # 禁用 NTLM（如需 NTLM 代理认证则保留）

# === 体积优化 ===
-DCURL_DISABLE_VERBOSE_STRINGS=OFF # 去除冗余字符串（减小体积）
-DCURL_DISABLE_MANUAL=ON          # 去除内置手册
-DCURL_LTO=ON                     # 启用 Link Time Optimization
-DENABLE_CURL_MANUAL=OFF          # 不生成手册
```

> **注意**：`HTTP_ONLY=ON` 已经会自动禁用非 HTTP 协议（FTP/DICT/GOPHER/IMAP/POP3/SMTP/TELNET/TFTP/RTSP/MQTT 等），无需再逐个设置对应的 `CURL_DISABLE_*`。

### 3.5 可选保留的特性

根据实际需求，以下特性可以选择保留或禁用：

| CMake 变量 | 默认 | 建议 | 说明 |
|-----------|------|------|------|
| `CURL_DISABLE_COOKIES` | OFF | **保留** | HTTP Cookie 支持，大多数场景需要 |
| `CURL_DISABLE_PROXY` | OFF | **保留** | HTTP 代理支持 |
| `CURL_DISABLE_DOH` | OFF | 可禁用 | DNS-over-HTTPS |
| `CURL_DISABLE_HSTS` | OFF | 可禁用 | HTTP Strict Transport Security |
| `CURL_DISABLE_ALTSVC` | OFF | **保留** | Alt-Svc 头支持（HTTP/3 升级需要）|
| `CURL_DISABLE_BASIC_AUTH` | OFF | **保留** | Basic 认证 |
| `CURL_DISABLE_BEARER_AUTH` | OFF | **保留** | Bearer 认证 |
| `CURL_DISABLE_DIGEST_AUTH` | OFF | 按需 | Digest 认证 |
| `CURL_DISABLE_WEBSOCKETS` | OFF | 按需 | WebSocket 支持 |
| `ENABLE_IPV6` | ON | **保留** | IPv6 支持 |

---

## 4. 依赖库编译

每个依赖库都需要为每个目标平台（及架构）分别编译。建议建立如下目录结构：

```
curl-unity/
├── deps/                      # 依赖源码 (git submodule)
│   ├── curl/
│   ├── openssl/
│   ├── nghttp2/
│   ├── ngtcp2/
│   └── nghttp3/
├── build/                     # 编译工作目录 (各库 build 产物均在此，不污染 deps/)
│   └── <platform>/            # 如 macos-arm64, ios-arm64, android-arm64 等
│       ├── openssl/           #   OpenSSL 构建目录
│       ├── nghttp2/           #   nghttp2 构建目录
│       ├── nghttp3/           #   nghttp3 构建目录
│       ├── ngtcp2/            #   ngtcp2 构建目录
│       ├── curl/              #   curl 构建目录
│       └── install/           #   所有库的安装产物 (PREFIX)
├── output/                    # 最终产物
│   ├── macOS/
│   ├── iOS/
│   ├── Android/
│   └── Windows/
└── BUILD_GUIDE.md             # 本文件
```

### 4.1 下载依赖源码

```bash
mkdir -p deps && cd deps

# OpenSSL 3.5.0+
git clone --depth 1 -b openssl-3.6 https://github.com/openssl/openssl

# nghttp2
git clone --depth 1 -b v1.68.x https://github.com/nghttp2/nghttp2

# nghttp3
git clone --depth 1 -b v1.15.0 https://github.com/ngtcp2/nghttp3

# ngtcp2
git clone --depth 1 -b v1.22.0 https://github.com/ngtcp2/ngtcp2

# zlib（大部分平台系统自带，Android 需要）
git clone --depth 1 -b v1.3.2 https://github.com/madler/zlib
```

> 版本号请根据实际情况调整，确保 OpenSSL >= 3.5.0，ngtcp2 >= 1.12.0，nghttp3 >= 1.0.0。

### 4.2 通用编译顺序

对于每个目标平台，依赖的编译顺序为：

1. **zlib**（如需）
2. **OpenSSL**
3. **nghttp2**
4. **nghttp3**
5. **ngtcp2**（依赖 OpenSSL + nghttp3）
6. **libcurl**（依赖以上全部）

每个依赖安装到平台独立的 prefix 目录，例如：

```bash
PREFIX=/path/to/curl-unity/build/<platform>-<arch>/install
```

---

## 5. 各平台编译步骤

### 环境变量约定

以下示例中使用的环境变量：

```bash
export PROJECT_ROOT=/path/to/curl-unity
export DEPS_SRC=$PROJECT_ROOT/deps
export OUTPUT_DIR=$PROJECT_ROOT/output
```

---

### 5.1 macOS

**目标架构**：arm64 (Apple Silicon)、x86_64 (Intel)
**产物格式**：`libcurl.dylib`（单文件，依赖已静态链接）

可以分别编译两个架构，最后用 `lipo` 合并为 Universal Binary。

#### 5.1.1 编译 OpenSSL (macOS arm64 示例)

```bash
PLATFORM=macos-arm64
PREFIX=$PROJECT_ROOT/build/$PLATFORM/install

mkdir -p $PROJECT_ROOT/build/$PLATFORM/openssl
cd $PROJECT_ROOT/build/$PLATFORM/openssl

MACOS_MIN_VERSION=10.14

$DEPS_SRC/openssl/Configure darwin64-arm64-cc \
  --prefix=$PREFIX \
  --libdir=lib \
  no-shared \
  no-tests \
  no-apps \
  -fPIC \
  -mmacosx-version-min=$MACOS_MIN_VERSION

make -j$(sysctl -n hw.ncpu)
make install_sw   # install_sw 跳过文档安装
```

> 对于 x86_64，将 `darwin64-arm64-cc` 改为 `darwin64-x86_64-cc`。
> `-mmacosx-version-min` 确保 OpenSSL 生成兼容 macOS 10.14+ 的代码。

#### 5.1.2 编译 nghttp2

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/nghttp2 -S $DEPS_SRC/nghttp2 -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOS_MIN_VERSION \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DENABLE_LIB_ONLY=ON \
  -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_STATIC_LIBS=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/nghttp2
cmake --install $PROJECT_ROOT/build/$PLATFORM/nghttp2
```

#### 5.1.3 编译 nghttp3

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/nghttp3 -S $DEPS_SRC/nghttp3 -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOS_MIN_VERSION \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DENABLE_LIB_ONLY=ON \
  -DENABLE_SHARED_LIB=OFF \
  -DENABLE_STATIC_LIB=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/nghttp3
cmake --install $PROJECT_ROOT/build/$PLATFORM/nghttp3
```

#### 5.1.4 编译 ngtcp2

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/ngtcp2 -S $DEPS_SRC/ngtcp2 -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOS_MIN_VERSION \
  -DCMAKE_PREFIX_PATH=$PREFIX \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DENABLE_OPENSSL=ON \
  -DENABLE_LIB_ONLY=ON \
  -DENABLE_SHARED_LIB=OFF \
  -DENABLE_STATIC_LIB=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/ngtcp2
cmake --install $PROJECT_ROOT/build/$PLATFORM/ngtcp2
```

#### 5.1.5 编译 libcurl

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/curl -S $PROJECT_ROOT/curl -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=$MACOS_MIN_VERSION \
  -DCMAKE_PREFIX_PATH=$PREFIX \
  -DOPENSSL_ROOT_DIR=$PREFIX \
  -DBUILD_CURL_EXE=OFF \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_STATIC_LIBS=OFF \
  -DCURL_ENABLE_SSL=ON \
  -DCURL_USE_OPENSSL=ON \
  -DUSE_NGHTTP2=ON \
  -DUSE_NGTCP2=ON \
  -DHTTP_ONLY=ON \
  -DCURL_USE_LIBPSL=OFF \
  -DCURL_USE_LIBSSH2=OFF \
  -DCURL_DISABLE_LDAP=ON \
  -DCURL_DISABLE_LDAPS=ON \
  -DCURL_DISABLE_AWS=ON \
  -DCURL_DISABLE_KERBEROS_AUTH=ON \
  -DCURL_DISABLE_NEGOTIATE_AUTH=ON \
  -DCURL_DISABLE_VERBOSE_STRINGS=OFF \
  -DCURL_LTO=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/curl
cmake --install $PROJECT_ROOT/build/$PLATFORM/curl
```

> x86_64 架构重复以上步骤，将 `PLATFORM=macos-x86_64`，`CMAKE_OSX_ARCHITECTURES=x86_64`，OpenSSL target 改为 `darwin64-x86_64-cc`。

#### 5.1.6 合并 Universal Binary

```bash
mkdir -p $OUTPUT_DIR/macOS

lipo -create \
  $PROJECT_ROOT/build/macos-arm64/install/lib/libcurl.dylib \
  $PROJECT_ROOT/build/macos-x86_64/install/lib/libcurl.dylib \
  -output $OUTPUT_DIR/macOS/libcurl.dylib

# 修正 install_name
install_name_tool -id @rpath/libcurl.dylib $OUTPUT_DIR/macOS/libcurl.dylib
```

> 由于依赖已静态链接，只需要合并 `libcurl.dylib` 一个文件。

#### 5.1.7 macOS 特殊注意事项

- **最低部署版本**：默认 macOS 10.14 (Mojave)，与 Unity 2022 支持的最低 macOS 版本对齐。可通过 `--macos-min` 参数调整。所有依赖（OpenSSL、nghttp2 等）和最终 dylib 链接均使用统一的 deployment target。
- **install_name**：使用 `install_name_tool -id @rpath/libcurl.dylib` 确保 Unity 能正确加载。
- **代码签名**：macOS 可能要求动态库签名，发布前需 `codesign --force --sign - libcurl.dylib`。

---

### 5.2 iOS

**目标架构**：arm64（真机）、arm64 + x86_64（模拟器）
**产物格式**：`libcurl.a`（静态库）

> **iOS 特殊限制**：iOS App Store 限制第三方动态库加载。libcurl 及其所有依赖均编译为静态库，最终在 Unity IL2CPP 的 Xcode 编译阶段链接。

#### 5.2.1 iOS Toolchain 设置

```bash
export IOS_MIN_VERSION=13.0

PLATFORM=ios-arm64
PREFIX=$PROJECT_ROOT/build/$PLATFORM/install
```

#### 5.2.2 编译 OpenSSL (iOS arm64)

OpenSSL 对 iOS 有专门的 Configure target：

```bash
mkdir -p $PROJECT_ROOT/build/$PLATFORM/openssl
cd $PROJECT_ROOT/build/$PLATFORM/openssl

$DEPS_SRC/openssl/Configure ios64-xcrun \
  --prefix=$PREFIX \
  --libdir=lib \
  no-shared \
  no-tests \
  no-apps \
  no-dso \
  -mios-version-min=$IOS_MIN_VERSION

make -j$(sysctl -n hw.ncpu)
make install_sw
```

> - iOS 模拟器 (arm64)：使用 `iossimulator-xcrun` target 并设置 `CFLAGS="-arch arm64"`
> - iOS 模拟器 (x86_64)：使用 `iossimulator-xcrun` target 并设置 `CFLAGS="-arch x86_64"`

#### 5.2.3 编译 nghttp2 / nghttp3 / ngtcp2 (iOS)

使用 CMake 交叉编译，指定 `CMAKE_SYSTEM_NAME=iOS`：

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/nghttp2 -S $DEPS_SRC/nghttp2 -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=$IOS_MIN_VERSION \
  -DCMAKE_FIND_ROOT_PATH=$PREFIX \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DENABLE_LIB_ONLY=ON \
  -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_STATIC_LIBS=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/nghttp2
cmake --install $PROJECT_ROOT/build/$PLATFORM/nghttp2
```

nghttp3、ngtcp2 同理，但注意静态库参数不同：nghttp3/ngtcp2 使用 `-DENABLE_SHARED_LIB=OFF -DENABLE_STATIC_LIB=ON`。ngtcp2 还额外需要：

```bash
  -DCMAKE_PREFIX_PATH=$PREFIX \
  -DOPENSSL_ROOT_DIR=$PREFIX \
  -DENABLE_OPENSSL=ON
```

#### 5.2.4 编译 libcurl (iOS)

iOS 上 libcurl 本身也编译为静态库：

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/curl -S $PROJECT_ROOT/curl -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=$IOS_MIN_VERSION \
  -DCMAKE_PREFIX_PATH=$PREFIX \
  -DCMAKE_FIND_ROOT_PATH=$PREFIX \
  -DOPENSSL_ROOT_DIR=$PREFIX \
  -DBUILD_CURL_EXE=OFF \
  -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_STATIC_LIBS=ON \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DCURL_ENABLE_SSL=ON \
  -DCURL_USE_OPENSSL=ON \
  -DUSE_NGHTTP2=ON \
  -DUSE_NGTCP2=ON \
  -DHTTP_ONLY=ON \
  -DCURL_USE_LIBPSL=OFF \
  -DCURL_USE_LIBSSH2=OFF \
  -DCURL_DISABLE_LDAP=ON \
  -DCURL_DISABLE_LDAPS=ON \
  -DCURL_DISABLE_AWS=ON \
  -DCURL_DISABLE_KERBEROS_AUTH=ON \
  -DCURL_DISABLE_NEGOTIATE_AUTH=ON \
  -DCURL_DISABLE_VERBOSE_STRINGS=OFF \
  -DCURL_LTO=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/curl
cmake --install $PROJECT_ROOT/build/$PLATFORM/curl
```

#### 5.2.5 合并静态库并创建 XCFramework

由于 iOS 上是全静态链接，建议将所有 `.a` 合并为一个 fat 静态库，简化 Unity 集成：

```bash
# 将所有依赖合并为单个静态库（以 ios-arm64 为例）
libtool -static -o $PROJECT_ROOT/build/ios-arm64/libcurl-all.a \
  $PREFIX/lib/libcurl.a \
  $PREFIX/lib/libssl.a \
  $PREFIX/lib/libcrypto.a \
  $PREFIX/lib/libnghttp2.a \
  $PREFIX/lib/libngtcp2.a \
  $PREFIX/lib/libnghttp3.a

# 对模拟器架构也做同样操作，然后创建 XCFramework
xcodebuild -create-xcframework \
  -library $PROJECT_ROOT/build/ios-arm64/libcurl-all.a \
  -headers $PREFIX/include \
  -library $PROJECT_ROOT/build/ios-sim-arm64/libcurl-all.a \
  -headers $PROJECT_ROOT/build/ios-sim-arm64/install/include \
  -output $OUTPUT_DIR/iOS/libcurl.xcframework
```

#### 5.2.6 iOS 特殊注意事项

- **CA 证书**：iOS 没有默认的 CA bundle 文件路径。两种方案：
  - 使用 `-DUSE_APPLE_SECTRUST=ON` 调用 Apple Security.framework 验证证书（推荐）
  - 或将 CA bundle 文件打包进 App，运行时通过 `CURLOPT_CAINFO` 指定
- **Bitcode**：Xcode 14+ 已废弃 Bitcode，无需添加 `-fembed-bitcode`。
- **最低部署版本**：建议设为 iOS 13.0+，与大部分 Unity 项目一致。
- **模拟器**：Apple Silicon Mac 的模拟器也是 arm64，需要单独编译（使用 `iphonesimulator` SDK），与真机 arm64 产物不同，不能 lipo 合并，需要用 XCFramework 区分。

---

### 5.3 Windows

**目标架构**：x86_64 (64位) + x86 (32位)
**产物格式**：`libcurl.dll`（单文件，依赖已静态链接）

推荐在 Windows 上使用 MSVC 原生编译，分别编译 64 位和 32 位版本。

#### 5.3.1 MSVC 原生编译（推荐）

##### x86_64 (64位)

打开 **"x64 Native Tools Command Prompt for VS 2022"**：

```cmd
set PROJECT_ROOT=C:\curl-build
set DEPS_SRC=%PROJECT_ROOT%\deps
set PLATFORM=windows-x64
set PREFIX=%PROJECT_ROOT%\build\%PLATFORM%\install
set WIN_MIN_VER=0x0601
```

**编译 OpenSSL：**

```cmd
mkdir %PROJECT_ROOT%\build\%PLATFORM%\openssl
cd %PROJECT_ROOT%\build\%PLATFORM%\openssl
perl %DEPS_SRC%\openssl\Configure VC-WIN64A --prefix=%PREFIX% --libdir=lib no-shared no-tests no-apps -D_WIN32_WINNT=%WIN_MIN_VER%
nmake
nmake install_sw
```

**编译 nghttp2 / nghttp3 / ngtcp2：**

```cmd
cmake -B %PROJECT_ROOT%\build\%PLATFORM%\nghttp2 -S %DEPS_SRC%\nghttp2 -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_INSTALL_PREFIX=%PREFIX% ^
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON ^
  -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%" ^
  -DENABLE_LIB_ONLY=ON ^
  -DBUILD_SHARED_LIBS=OFF ^
  -DBUILD_STATIC_LIBS=ON
cmake --build %PROJECT_ROOT%\build\%PLATFORM%\nghttp2
cmake --install %PROJECT_ROOT%\build\%PLATFORM%\nghttp2

:: nghttp3 同理，额外加 -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%"
:: ngtcp2 额外需要 -DCMAKE_PREFIX_PATH=%PREFIX% -DENABLE_OPENSSL=ON -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%"
```

**编译 libcurl：**

```cmd
cmake -B %PROJECT_ROOT%\build\%PLATFORM%\curl -S %DEPS_SRC%\curl -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_INSTALL_PREFIX=%PREFIX% ^
  -DCMAKE_PREFIX_PATH=%PREFIX% ^
  -DOPENSSL_ROOT_DIR=%PREFIX% ^
  -DBUILD_CURL_EXE=OFF ^
  -DBUILD_SHARED_LIBS=ON ^
  -DBUILD_STATIC_LIBS=OFF ^
  -DCMAKE_C_FLAGS="/D_WIN32_WINNT=%WIN_MIN_VER%" ^
  -DCURL_TARGET_WINDOWS_VERSION=%WIN_MIN_VER% ^
  -DCURL_ENABLE_SSL=ON ^
  -DCURL_USE_OPENSSL=ON ^
  -DUSE_NGHTTP2=ON ^
  -DUSE_NGTCP2=ON ^
  -DHTTP_ONLY=ON ^
  -DCURL_USE_LIBPSL=OFF ^
  -DCURL_USE_LIBSSH2=OFF ^
  -DCURL_DISABLE_LDAP=ON ^
  -DCURL_DISABLE_LDAPS=ON ^
  -DCURL_DISABLE_AWS=ON ^
  -DCURL_DISABLE_KERBEROS_AUTH=ON ^
  -DCURL_DISABLE_NEGOTIATE_AUTH=ON ^
  -DCURL_DISABLE_VERBOSE_STRINGS=OFF ^
  -DCURL_LTO=ON
cmake --build %PROJECT_ROOT%\build\%PLATFORM%\curl
cmake --install %PROJECT_ROOT%\build\%PLATFORM%\curl

:: 产物位于 %PREFIX%\bin\libcurl.dll
```

##### x86 (32位)

打开 **"x86 Native Tools Command Prompt for VS 2022"**（注意是 x86 不是 x64）：

```cmd
set PROJECT_ROOT=C:\curl-build
set DEPS_SRC=%PROJECT_ROOT%\deps
set PLATFORM=windows-x86
set PREFIX=%PROJECT_ROOT%\build\%PLATFORM%\install
```

**编译 OpenSSL (32位)：**

```cmd
mkdir %PROJECT_ROOT%\build\%PLATFORM%\openssl
cd %PROJECT_ROOT%\build\%PLATFORM%\openssl
perl %DEPS_SRC%\openssl\Configure VC-WIN32 --prefix=%PREFIX% --libdir=lib no-shared no-tests no-apps -D_WIN32_WINNT=%WIN_MIN_VER%
nmake
nmake install_sw
```

> 关键区别：OpenSSL target 从 `VC-WIN64A` 改为 **`VC-WIN32`**。

**编译 nghttp2 / nghttp3 / ngtcp2 (32位)：**

参数与 64 位完全相同 —— 在 x86 Native Tools Command Prompt 中执行即可，编译器自动生成 32 位代码。如果使用 Visual Studio Generator 替代 Ninja，需要将 `-A x64` 改为 `-A Win32`：

```cmd
:: 使用 VS Generator 时
cmake -B build -G "Visual Studio 17 2022" -A Win32 ...
```

**编译 libcurl (32位)：**

参数与 64 位完全相同，在 x86 Native Tools Command Prompt 中执行。

##### 产物位置

```
C:\curl-build\windows-x64\install\bin\libcurl.dll   (64位)
C:\curl-build\windows-x86\install\bin\libcurl.dll   (32位)
```

#### 5.3.2 MinGW-w64 交叉编译（备选）

如需在 macOS 上交叉编译（不推荐用于生产）：

```bash
brew install mingw-w64

# 64位
MINGW_PREFIX=x86_64-w64-mingw32
OPENSSL_TARGET=mingw64

# 32位
MINGW_PREFIX=i686-w64-mingw32
OPENSSL_TARGET=mingw
```

```bash
# OpenSSL
mkdir -p $PROJECT_ROOT/build/$PLATFORM/openssl
cd $PROJECT_ROOT/build/$PLATFORM/openssl
$DEPS_SRC/openssl/Configure $OPENSSL_TARGET \
  --prefix=$PREFIX --libdir=lib \
  --cross-compile-prefix=$MINGW_PREFIX- \
  no-shared no-tests no-apps

# CMake 依赖（以 nghttp2 为例）
cmake -B $PROJECT_ROOT/build/$PLATFORM/nghttp2 -S $DEPS_SRC/nghttp2 -G Ninja \
  -DCMAKE_SYSTEM_NAME=Windows \
  -DCMAKE_C_COMPILER=$MINGW_PREFIX-gcc \
  -DCMAKE_CXX_COMPILER=$MINGW_PREFIX-g++ \
  -DCMAKE_RC_COMPILER=$MINGW_PREFIX-windres \
  ... # 其余参数同上
```

> **警告**：MinGW 编译的 DLL 使用 `msvcrt.dll` 作为 CRT，而 Unity IL2CPP 使用 MSVC 的 `ucrt`。混用不同 CRT 可能导致跨 DLL 边界的内存分配/释放崩溃。如果必须使用 MinGW 产物，确保所有内存分配和释放都在 libcurl 内部完成（避免调用方 free libcurl malloc 的内存）。

#### 5.3.3 Windows 特殊注意事项

- **最低 Windows 版本**：默认 Windows 7 SP1 (`_WIN32_WINNT=0x0601`)，与 Unity 2022 支持的最低 Windows 版本对齐。可通过 `--win-min` 参数调整。libcurl 硬性要求最低 Windows Vista (0x0600)。
- **CRT 链接**：libcurl 动态库应使用动态 CRT（`/MD`，CMake 默认），不要使用静态 CRT（`/MT`）。CMake 变量：`-DCURL_STATIC_CRT=OFF`（默认即 OFF）。
- **Winsock**：libcurl 在 Windows 上依赖 `ws2_32.lib` 和 `crypt32.lib`，CMake 会自动处理。
- **32 位必要性**：虽然现代 Windows 系统绝大多数是 64 位，但部分用户可能以 32 位模式运行 Unity 应用。建议同时提供 x86 和 x64 两个版本。Unity 会根据 Player Settings 中的 Architecture 设置自动选择。

---

### 5.4 Android

**目标架构**：arm64-v8a、armeabi-v7a、x86_64
**产物格式**：`libcurl.so`（单文件，依赖已静态链接）

#### 5.4.1 环境变量设置

```bash
export ANDROID_NDK_HOME=~/Library/Android/sdk/ndk/26.1.10909125
export TOOLCHAIN=$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake
export ANDROID_MIN_API=24   # Android 7.0+，Unity 最低要求

# 目标架构（以 arm64-v8a 为例）
ANDROID_ABI=arm64-v8a
PLATFORM=android-arm64
PREFIX=$PROJECT_ROOT/build/$PLATFORM/install
```

#### 5.4.2 编译 OpenSSL (Android)

OpenSSL 对 Android 有专门支持：

```bash
export ANDROID_NDK_ROOT=$ANDROID_NDK_HOME
export PATH=$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/bin:$PATH

mkdir -p $PROJECT_ROOT/build/$PLATFORM/openssl
cd $PROJECT_ROOT/build/$PLATFORM/openssl

$DEPS_SRC/openssl/Configure android-arm64 \
  --prefix=$PREFIX \
  --libdir=lib \
  -D__ANDROID_API__=$ANDROID_MIN_API \
  no-shared \
  no-tests \
  no-apps

make -j$(sysctl -n hw.ncpu)
make install_sw
```

> 各架构对应的 OpenSSL target：
>
> | Android ABI | OpenSSL Target |
> |-------------|----------------|
> | arm64-v8a | `android-arm64` |
> | armeabi-v7a | `android-arm` |
> | x86_64 | `android-x86_64` |

#### 5.4.3 编译 nghttp2 / nghttp3 / ngtcp2 (Android)

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/nghttp2 -S $DEPS_SRC/nghttp2 -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_TOOLCHAIN_FILE=$TOOLCHAIN \
  -DANDROID_ABI=$ANDROID_ABI \
  -DANDROID_PLATFORM=android-$ANDROID_MIN_API \
  -DCMAKE_FIND_ROOT_PATH=$PREFIX \
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DENABLE_LIB_ONLY=ON \
  -DBUILD_SHARED_LIBS=OFF \
  -DBUILD_STATIC_LIBS=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/nghttp2
cmake --install $PROJECT_ROOT/build/$PLATFORM/nghttp2

# nghttp3、ngtcp2 同理，但静态库参数使用 -DENABLE_SHARED_LIB=OFF -DENABLE_STATIC_LIB=ON
# ngtcp2 额外需要：-DCMAKE_PREFIX_PATH=$PREFIX -DOPENSSL_ROOT_DIR=$PREFIX -DENABLE_OPENSSL=ON
```

#### 5.4.4 编译 libcurl (Android)

```bash
cmake -B $PROJECT_ROOT/build/$PLATFORM/curl -S $PROJECT_ROOT/curl -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_INSTALL_PREFIX=$PREFIX \
  -DCMAKE_TOOLCHAIN_FILE=$TOOLCHAIN \
  -DANDROID_ABI=$ANDROID_ABI \
  -DANDROID_PLATFORM=android-$ANDROID_MIN_API \
  -DCMAKE_PREFIX_PATH=$PREFIX \
  -DCMAKE_FIND_ROOT_PATH=$PREFIX \
  -DOPENSSL_ROOT_DIR=$PREFIX \
  -DBUILD_CURL_EXE=OFF \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_STATIC_LIBS=OFF \
  -DCURL_ENABLE_SSL=ON \
  -DCURL_USE_OPENSSL=ON \
  -DUSE_NGHTTP2=ON \
  -DUSE_NGTCP2=ON \
  -DHTTP_ONLY=ON \
  -DCURL_USE_LIBPSL=OFF \
  -DCURL_USE_LIBSSH2=OFF \
  -DCURL_DISABLE_LDAP=ON \
  -DCURL_DISABLE_LDAPS=ON \
  -DCURL_DISABLE_AWS=ON \
  -DCURL_DISABLE_KERBEROS_AUTH=ON \
  -DCURL_DISABLE_NEGOTIATE_AUTH=ON \
  -DCURL_DISABLE_VERBOSE_STRINGS=OFF \
  -DCURL_LTO=ON

cmake --build $PROJECT_ROOT/build/$PLATFORM/curl
cmake --install $PROJECT_ROOT/build/$PLATFORM/curl
```

> 对 armeabi-v7a 和 x86_64 重复以上所有步骤，调整 `ANDROID_ABI` 和 OpenSSL target 即可。

#### 5.4.5 Strip 调试符号

```bash
$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip \
  --strip-debug $PREFIX/lib/libcurl.so
```

> 使用 `--strip-debug` 而非 `--strip-all`，避免 strip 动态符号表导致 P/Invoke 找不到导出函数。

#### 5.4.6 Android 特殊注意事项

- **最低 API 等级**：建议 android-24 (Android 7.0)，与 Unity 2022+ 的最低要求对齐。
- **CA 证书**：Android 没有标准的 CA bundle 文件路径。两种方案：
  - 将 CA bundle（如 `cacert.pem`，可从 <https://curl.se/ca/cacert.pem> 下载）打包到 `StreamingAssets`，运行时通过 `CURLOPT_CAINFO` 设置路径
  - 通过 JNI 调用 Android 系统证书存储（较复杂）
- **多架构**：需要为 `arm64-v8a`、`armeabi-v7a`（32位 ARM 设备）、`x86_64`（模拟器/Chromebook）分别编译。

---

## 6. 产物整合与 Unity 集成

### 6.1 产物位置汇总

由于依赖已静态链接，每个平台只需一个文件：

| 平台 | 架构 | 产物文件 | 编译产物路径 |
|------|------|---------|-------------|
| macOS | arm64 | `libcurl_unity.dylib` | `output/macOS/arm64/libcurl_unity.dylib` |
| macOS | x86_64 | `libcurl_unity.dylib` | `output/macOS/x86_64/libcurl_unity.dylib` |
| macOS | Universal | `libcurl_unity.dylib` | `output/macOS/libcurl_unity.dylib` (build-macos.sh 生成) |
| iOS | arm64 | `libcurl-all.a` | `build/ios-arm64/libcurl-all.a` |
| iOS | XCFramework | `libcurl.xcframework` | `output/iOS/libcurl.xcframework` |
| Windows | x64 | `libcurl.dll` | `windows-x64/install/bin/libcurl.dll` |
| Windows | x86 | `libcurl.dll` | `windows-x86/install/bin/libcurl.dll` |
| Android | arm64-v8a | `libcurl.so` | `build/android-arm64/install/lib/libcurl.so` |
| Android | armeabi-v7a | `libcurl.so` | `build/android-armv7/install/lib/libcurl.so` |
| Android | x86_64 | `libcurl.so` | `build/android-x86_64/install/lib/libcurl.so` |

### 6.2 Unity 目录结构

```
Assets/Plugins/
├── macOS/
│   └── libcurl.bundle          # 或 libcurl.dylib
├── iOS/
│   └── libcurl.a               # 合并后的静态库（或 XCFramework）
├── Windows/
│   ├── x86_64/
│   │   └── libcurl.dll
│   └── x86/
│       └── libcurl.dll
└── Android/
    ├── arm64-v8a/
    │   └── libcurl.so
    ├── armeabi-v7a/
    │   └── libcurl.so
    └── x86_64/
        └── libcurl.so
```

### 6.3 Unity Plugin 设置

在 Unity Inspector 中为每个库文件设置正确的平台选项：

- **macOS**：Platform = Editor + Standalone, CPU = AnyCPU (Universal)
- **iOS**：Platform = iOS, CPU = ARM64
- **Windows x86_64**：Platform = Editor + Standalone, CPU = x86_64
- **Windows x86**：Platform = Standalone, CPU = x86
- **Android**：Platform = Android, CPU = 对应架构（按放置目录自动识别）

### 6.4 P/Invoke 声明示例

```csharp
using System;
using System.Runtime.InteropServices;

public static class CurlNative
{
    #if UNITY_IOS && !UNITY_EDITOR
    private const string CURL_LIB = "__Internal";  // iOS 静态链接
    #elif UNITY_ANDROID && !UNITY_EDITOR
    private const string CURL_LIB = "curl";        // Android: libcurl.so
    #elif UNITY_STANDALONE_WIN && !UNITY_EDITOR
    private const string CURL_LIB = "libcurl";     // Windows: libcurl.dll
    #else
    private const string CURL_LIB = "curl";        // macOS: libcurl.dylib
    #endif

    [DllImport(CURL_LIB, CallingConvention = CallingConvention.Cdecl)]
    public static extern int curl_global_init(long flags);

    [DllImport(CURL_LIB, CallingConvention = CallingConvention.Cdecl)]
    public static extern void curl_global_cleanup();

    [DllImport(CURL_LIB, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr curl_easy_init();

    [DllImport(CURL_LIB, CallingConvention = CallingConvention.Cdecl)]
    public static extern int curl_easy_setopt(IntPtr curl, int option, string value);

    [DllImport(CURL_LIB, CallingConvention = CallingConvention.Cdecl)]
    public static extern int curl_easy_perform(IntPtr curl);

    [DllImport(CURL_LIB, CallingConvention = CallingConvention.Cdecl)]
    public static extern void curl_easy_cleanup(IntPtr curl);
}
```

---

## 7. 常见问题与注意事项

### 7.1 IL2CPP 兼容性

- IL2CPP 使用 P/Invoke 调用原生库，与 Mono 运行时的机制相同，无需特殊处理。
- iOS 上 IL2CPP 是静态链接，所以 `DllImport("__Internal")` 即可。
- 确保所有回调函数使用 `[MonoPInvokeCallback]` 特性标记（IL2CPP 需要）。
- 避免在 P/Invoke 中传递复杂的 C struct，优先使用 `IntPtr` + 手动 Marshal。

### 7.2 线程安全

- 必须在主线程调用一次 `curl_global_init()` 后，才能在其他线程使用 curl。
- `curl_easy_*` 句柄不是线程安全的，不要跨线程共享。
- `curl_multi_*` 接口更适合在游戏引擎中使用（非阻塞）。

### 7.3 内存管理

- libcurl 分配的内存必须由 libcurl 释放（如 `curl_free()`），不能跨库使用 `Marshal.FreeHGlobal()`。
- Windows 上这一点尤其重要，因为不同 CRT 有独立的堆。

### 7.4 CA 证书策略

| 平台 | 推荐方案 |
|------|---------|
| macOS | `USE_APPLE_SECTRUST=ON`，使用系统证书链 |
| iOS | `USE_APPLE_SECTRUST=ON`，使用系统证书链 |
| Windows | 打包 `cacert.pem` 或通过代码加载系统证书 |
| Android | 打包 `cacert.pem` 到 StreamingAssets |

### 7.5 编译问题排查

- **交叉编译时找不到 OpenSSL（iOS/Android）**：CMake 交叉编译会启用路径隔离，`CMAKE_PREFIX_PATH` 和 `OPENSSL_ROOT_DIR` 可能被忽略。必须同时设置 `-DCMAKE_FIND_ROOT_PATH=$PREFIX`，见 [3.3 交叉编译路径隔离](#33-交叉编译路径隔离)。
- **找不到 OpenSSL（macOS 本机）**：检查 `CMAKE_PREFIX_PATH` 或 `OPENSSL_ROOT_DIR` 是否指向正确路径。
- **找不到 nghttp2/ngtcp2**：确保 `CMAKE_PREFIX_PATH` 包含所有依赖的安装路径。
- **链接了系统 Homebrew 的动态库**：如果 `otool -L` 显示链接了 `/opt/homebrew/...` 的 dylib，检查：1) 依赖是否确实编译为静态库（nghttp2 用 `BUILD_SHARED_LIBS`，nghttp3/ngtcp2 用 `ENABLE_SHARED_LIB`，变量名不同）；2) install 目录中是否有残留的旧 `.dylib` 文件需要清理；3) libcurl 的 CMake 参数是否设置了 `CURL_BROTLI=OFF`、`CURL_ZSTD=OFF`、`USE_LIBIDN2=OFF`。
- **依赖库的 git submodule 缺失**：nghttp2/nghttp3/ngtcp2 都有子模块（sfparse、munit 等）。如果 `git clone --depth 1` 下载的源码，需要执行 `git submodule update --init` 初始化子模块，否则编译会报 "Cannot find source file" 错误。
- **链接错误 undefined symbol**：检查依赖编译顺序，ngtcp2 依赖 OpenSSL 和 nghttp3。
- **静态链接时符号缺失**：确保依赖库编译时加了 `-DCMAKE_POSITION_INDEPENDENT_CODE=ON`（或 OpenSSL 的 `-fPIC`）。
- **iOS 模拟器 arm64 vs 真机 arm64 冲突**：两者不能 lipo 合并，必须使用 XCFramework 区分。
- **Android strip 后 P/Invoke 失败**：只 strip 调试符号（`--strip-debug`），不要 strip 所有符号。
- **Windows 32 位编译报错**：确认是在 "x86 Native Tools Command Prompt" 中执行，而非 x64 环境。

### 7.6 版本验证

编译完成后，可临时开启 `BUILD_CURL_EXE=ON` 编译 curl 命令行工具来验证特性支持：

```bash
./curl --version

# 应该看到类似输出：
# curl 8.20.0 (aarch64-apple-darwin) libcurl/8.20.0 OpenSSL/3.5.0 nghttp2/1.65.0 ngtcp2/1.12.0 nghttp3/1.7.0
# Protocols: http https
# Features: alt-svc AsynchDNS HSTS HTTP2 HTTP3 HTTPS-proxy IPv6 Largefile libz SSL threadsafe UnixSockets
```

### 7.7 关于动态链接依赖的备选方案

本指南默认将所有依赖静态链接进 libcurl，如果你需要将依赖作为独立动态库分发（例如多个原生插件共享同一份 OpenSSL），需要调整：

1. 依赖库编译参数改为 `-DBUILD_SHARED_LIBS=ON`，OpenSSL 去掉 `no-shared`
2. libcurl 编译参数不变（仍为 `BUILD_SHARED_LIBS=ON`）
3. 所有 `.dylib` / `.dll` / `.so` 都需要放入 Unity Plugins 目录
4. macOS 需要用 `install_name_tool` 修正每个库的 rpath
5. Windows 需要确保所有 DLL 在同一目录下（Unity 默认满足）
6. Android 需要确保所有 `.so` 在同一个 ABI 目录下
