#!/usr/bin/env bash
#
# libcurl 跨平台构建脚本
#
# 用法:
#   ./scripts/build.sh <platform> [options]
#
# 平台:
#   macos-arm64          macOS ARM64
#   macos-x86_64         macOS Intel
#   ios-arm64            iOS 真机
#   ios-sim-arm64        iOS 模拟器 (Apple Silicon)
#   android-arm64        Android arm64-v8a
#   android-armv7        Android armeabi-v7a
#   android-x86_64       Android x86_64
#
# 选项:
#   --clean              清理该平台的 build 目录后重新编译
#   --curl-cli           额外编译 curl 命令行工具（仅 macOS 本机架构有效）
#   --android-api <N>    Android 最低 API 等级 (默认 24)
#   --ios-min <ver>      iOS 最低部署版本 (默认 13.0)
#   --macos-min <ver>    macOS 最低部署版本 (默认 10.14)
#   --jobs <N>           并行编译线程数 (默认自动检测)
#
set -eo pipefail

# ============================================================
# 项目路径
# ============================================================
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEPS_SRC="$PROJECT_ROOT/deps"
CURL_SRC="$DEPS_SRC/curl"
BRIDGE_SRC="$PROJECT_ROOT/bridge/curl_unity_bridge.c"
OUTPUT_DIR="$PROJECT_ROOT/output"

# ============================================================
# 默认参数
# ============================================================
CLEAN=0
BUILD_CURL_CLI=0
ANDROID_MIN_API=24
IOS_MIN_VERSION="13.0"
MACOS_MIN_VERSION="10.14"
JOBS="$(sysctl -n hw.ncpu 2>/dev/null || nproc 2>/dev/null || echo 4)"

# ============================================================
# 参数解析
# ============================================================
PLATFORM=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --clean)        CLEAN=1; shift ;;
    --curl-cli)     BUILD_CURL_CLI=1; shift ;;
    --android-api)  ANDROID_MIN_API="$2"; shift 2 ;;
    --ios-min)      IOS_MIN_VERSION="$2"; shift 2 ;;
    --macos-min)    MACOS_MIN_VERSION="$2"; shift 2 ;;
    --jobs)         JOBS="$2"; shift 2 ;;
    -h|--help)
      sed -n '3,/^$/p' "$0" | sed 's/^#//; s/^ //'
      exit 0
      ;;
    -*)
      echo "错误: 未知选项 $1" >&2; exit 1 ;;
    *)
      if [[ -z "$PLATFORM" ]]; then
        PLATFORM="$1"
      else
        echo "错误: 多余参数 $1" >&2; exit 1
      fi
      shift ;;
  esac
done

if [[ -z "$PLATFORM" ]]; then
  echo "错误: 请指定目标平台。运行 $0 --help 查看用法。" >&2
  exit 1
fi

# ============================================================
# 平台配置
# ============================================================
# 公共变量，由 configure_platform 填充
CMAKE_EXTRA_ARGS=()       # 平台特有的 CMake 参数
OPENSSL_TARGET=""          # OpenSSL Configure target
OPENSSL_EXTRA_ARGS=()      # OpenSSL 额外参数
CURL_SHARED=OFF            # libcurl 统一编译为静态库，由 collect_output 合成最终动态库
CURL_STATIC=ON

configure_platform() {
  case "$PLATFORM" in
    macos-arm64)
      # arm64 Mac 最低支持 macOS 11.0，自动提升低于此值的设置
      local macos_min="$MACOS_MIN_VERSION"
      if [[ "$(printf '%s\n' "11.0" "$macos_min" | sort -V | head -1)" != "11.0" ]]; then
        echo "  注意: arm64 最低支持 macOS 11.0，已将 $macos_min 提升到 11.0"
        macos_min="11.0"
      fi
      OPENSSL_TARGET="darwin64-arm64-cc"
      OPENSSL_EXTRA_ARGS=("-mmacosx-version-min=$macos_min")
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_OSX_ARCHITECTURES=arm64
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$macos_min"
      )
      ;;
    macos-x86_64)
      OPENSSL_TARGET="darwin64-x86_64-cc"
      OPENSSL_EXTRA_ARGS=("-mmacosx-version-min=$MACOS_MIN_VERSION")
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_OSX_ARCHITECTURES=x86_64
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOS_MIN_VERSION"
      )
      ;;
    ios-arm64)
      OPENSSL_TARGET="ios64-xcrun"
      OPENSSL_EXTRA_ARGS=(no-dso "-mios-version-min=$IOS_MIN_VERSION")
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_SYSTEM_NAME=iOS
        -DCMAKE_OSX_ARCHITECTURES=arm64
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$IOS_MIN_VERSION"
      )
      ;;
    ios-sim-arm64)
      OPENSSL_TARGET="iossimulator-xcrun"
      OPENSSL_EXTRA_ARGS=(no-dso "-mios-version-min=$IOS_MIN_VERSION" -arch arm64)
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_SYSTEM_NAME=iOS
        -DCMAKE_OSX_ARCHITECTURES=arm64
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$IOS_MIN_VERSION"
        -DCMAKE_OSX_SYSROOT=iphonesimulator
      )
      ;;
    android-arm64)
      _require_ndk
      OPENSSL_TARGET="android-arm64"
      OPENSSL_EXTRA_ARGS=("-D__ANDROID_API__=$ANDROID_MIN_API")
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
        -DANDROID_ABI=arm64-v8a
        -DANDROID_PLATFORM="android-$ANDROID_MIN_API"
      )
      ;;
    android-armv7)
      _require_ndk
      OPENSSL_TARGET="android-arm"
      OPENSSL_EXTRA_ARGS=("-D__ANDROID_API__=$ANDROID_MIN_API")
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
        -DANDROID_ABI=armeabi-v7a
        -DANDROID_PLATFORM="android-$ANDROID_MIN_API"
      )
      ;;
    android-x86_64)
      _require_ndk
      OPENSSL_TARGET="android-x86_64"
      OPENSSL_EXTRA_ARGS=("-D__ANDROID_API__=$ANDROID_MIN_API")
      CMAKE_EXTRA_ARGS=(
        -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
        -DANDROID_ABI=x86_64
        -DANDROID_PLATFORM="android-$ANDROID_MIN_API"
      )
      ;;
    *)
      echo "错误: 不支持的平台 '$PLATFORM'" >&2
      echo "支持的平台: macos-arm64 macos-x86_64 ios-arm64 ios-sim-arm64 android-arm64 android-armv7 android-x86_64" >&2
      exit 1
      ;;
  esac
}

# ============================================================
# 工具函数
# ============================================================
_ndk_host_tag() {
  # NDK prebuilt 目录名: darwin-x86_64 (macOS) 或 linux-x86_64 (Linux)
  echo "$(uname -s | tr '[:upper:]' '[:lower:]')-x86_64"
}

_require_ndk() {
  # 自动探测 NDK 路径
  if [[ -z "${ANDROID_NDK_HOME:-}" ]]; then
    # macOS 默认路径
    local sdk_ndk_dir="$HOME/Library/Android/sdk/ndk"
    # Linux 默认路径
    [[ ! -d "$sdk_ndk_dir" ]] && sdk_ndk_dir="$HOME/Android/Sdk/ndk"
    # ANDROID_HOME 环境变量
    [[ ! -d "$sdk_ndk_dir" ]] && [[ -n "${ANDROID_HOME:-}" ]] && sdk_ndk_dir="$ANDROID_HOME/ndk"

    if [[ -d "$sdk_ndk_dir" ]]; then
      ANDROID_NDK_HOME="$(ls -d "$sdk_ndk_dir"/*/ 2>/dev/null | sort -V | tail -1)"
      ANDROID_NDK_HOME="${ANDROID_NDK_HOME%/}"
    fi
  fi
  if [[ -z "${ANDROID_NDK_HOME:-}" ]] || [[ ! -d "$ANDROID_NDK_HOME" ]]; then
    echo "错误: 未找到 Android NDK。请设置 ANDROID_NDK_HOME 环境变量。" >&2
    exit 1
  fi
  export ANDROID_NDK_ROOT="$ANDROID_NDK_HOME"
  # 将 NDK 工具链加入 PATH (OpenSSL 需要)
  local toolchain_bin="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/$(_ndk_host_tag)/bin"
  if [[ -d "$toolchain_bin" ]]; then
    export PATH="$toolchain_bin:$PATH"
  fi
  echo "  NDK: $ANDROID_NDK_HOME"
}

log() {
  echo ""
  echo "========================================"
  echo "  $1"
  echo "========================================"
  echo ""
}

step_time() {
  date +%s
}

elapsed() {
  local start=$1
  local end
  end=$(date +%s)
  echo "$(( end - start ))s"
}

# ============================================================
# 构建函数
# ============================================================

# 确保 git 子模块已初始化
# 用法: ensure_submodules <repo_dir> [submodule_path ...]
#   不指定路径时初始化所有子模块，指定路径时只初始化指定的
ensure_submodules() {
  local repo_dir="$1"
  shift
  if [[ ! -f "$repo_dir/.gitmodules" ]]; then return; fi

  pushd "$repo_dir" > /dev/null
  if [[ $# -gt 0 ]]; then
    for path in "$@"; do
      if [[ ! -e "$path/.git" ]]; then
        echo "  初始化子模块: $(basename "$repo_dir")/$path"
        git submodule update --init --depth 1 -- "$path"
      fi
    done
  else
    if git submodule status | grep -q '^-'; then
      echo "  初始化子模块: $(basename "$repo_dir")"
      git submodule update --init --depth 1
    fi
  fi
  popd > /dev/null
}

build_openssl() {
  if [[ -f "$PREFIX/lib/libssl.a" && -f "$PREFIX/lib/libcrypto.a" ]]; then
    echo "  [跳过] OpenSSL 已编译"
    return
  fi

  log "[$PLATFORM] 编译 OpenSSL"
  local start
  start=$(step_time)

  local build_dir="$PROJECT_ROOT/build/$PLATFORM/openssl"
  rm -rf "$build_dir"
  mkdir -p "$build_dir"
  cd "$build_dir"

  "$DEPS_SRC/openssl/Configure" "$OPENSSL_TARGET" \
    --prefix="$PREFIX" \
    --libdir=lib \
    no-shared \
    no-tests \
    no-apps \
    -fPIC \
    "${OPENSSL_EXTRA_ARGS[@]}"

  make -j"$JOBS"
  make install_sw

  echo "  OpenSSL 完成 ($(elapsed "$start"))"
}

build_nghttp2() {
  if [[ -f "$PREFIX/lib/libnghttp2.a" ]]; then
    echo "  [跳过] nghttp2 已编译"
    return
  fi

  log "[$PLATFORM] 编译 nghttp2"
  local start
  start=$(step_time)

  # nghttp2: ENABLE_LIB_ONLY 模式不需要嵌套子模块

  # 清理 install 目录中可能残留的旧动态库
  rm -f "$PREFIX"/lib/libnghttp2*

  local build_dir="$PROJECT_ROOT/build/$PLATFORM/nghttp2"
  rm -rf "$build_dir"

  cmake -B "$build_dir" -S "$DEPS_SRC/nghttp2" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DENABLE_LIB_ONLY=ON \
    -DBUILD_TESTING=OFF \
    -DBUILD_SHARED_LIBS=OFF \
    -DBUILD_STATIC_LIBS=ON \
    "${CMAKE_EXTRA_ARGS[@]}"

  cmake --build "$build_dir" -j "$JOBS"
  cmake --install "$build_dir"

  echo "  nghttp2 完成 ($(elapsed "$start"))"
}

build_nghttp3() {
  if [[ -f "$PREFIX/lib/libnghttp3.a" ]]; then
    echo "  [跳过] nghttp3 已编译"
    return
  fi

  log "[$PLATFORM] 编译 nghttp3"
  local start
  start=$(step_time)

  ensure_submodules "$DEPS_SRC/nghttp3" "lib/sfparse"

  rm -f "$PREFIX"/lib/libnghttp3*

  local build_dir="$PROJECT_ROOT/build/$PLATFORM/nghttp3"
  rm -rf "$build_dir"

  cmake -B "$build_dir" -S "$DEPS_SRC/nghttp3" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DENABLE_LIB_ONLY=ON \
    -DBUILD_TESTING=OFF \
    -DENABLE_SHARED_LIB=OFF \
    -DENABLE_STATIC_LIB=ON \
    "${CMAKE_EXTRA_ARGS[@]}"

  cmake --build "$build_dir" -j "$JOBS"
  cmake --install "$build_dir"

  echo "  nghttp3 完成 ($(elapsed "$start"))"
}

build_ngtcp2() {
  if [[ -f "$PREFIX/lib/libngtcp2.a" ]]; then
    echo "  [跳过] ngtcp2 已编译"
    return
  fi

  log "[$PLATFORM] 编译 ngtcp2"
  local start
  start=$(step_time)

  # ngtcp2: ENABLE_LIB_ONLY 模式不需要嵌套子模块

  rm -f "$PREFIX"/lib/libngtcp2*

  local build_dir="$PROJECT_ROOT/build/$PLATFORM/ngtcp2"
  rm -rf "$build_dir"

  cmake -B "$build_dir" -S "$DEPS_SRC/ngtcp2" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DCMAKE_PREFIX_PATH="$PREFIX" \
    -DOPENSSL_ROOT_DIR="$PREFIX" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    -DENABLE_OPENSSL=ON \
    -DENABLE_LIB_ONLY=ON \
    -DBUILD_TESTING=OFF \
    -DENABLE_SHARED_LIB=OFF \
    -DENABLE_STATIC_LIB=ON \
    "${CMAKE_EXTRA_ARGS[@]}"

  cmake --build "$build_dir" -j "$JOBS"
  cmake --install "$build_dir"

  echo "  ngtcp2 完成 ($(elapsed "$start"))"
}

_curl_common_args() {
  # libcurl 公共 CMake 参数（库和 CLI 共用）
  echo \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_PREFIX_PATH="$PREFIX" \
    -DOPENSSL_ROOT_DIR="$PREFIX" \
    -DCURL_ENABLE_SSL=ON \
    -DCURL_USE_OPENSSL=ON \
    -DUSE_NGHTTP2=ON \
    -DUSE_NGTCP2=ON \
    -DHTTP_ONLY=ON \
    -DCURL_USE_LIBPSL=OFF \
    -DCURL_USE_LIBSSH2=OFF \
    -DCURL_USE_LIBSSH=OFF \
    -DCURL_BROTLI=OFF \
    -DCURL_ZSTD=OFF \
    -DUSE_LIBIDN2=OFF \
    -DCURL_DISABLE_LDAP=ON \
    -DCURL_DISABLE_LDAPS=ON \
    -DCURL_DISABLE_AWS=ON \
    -DCURL_DISABLE_KERBEROS_AUTH=ON \
    -DCURL_DISABLE_NEGOTIATE_AUTH=ON \
    -DCURL_DISABLE_VERBOSE_STRINGS=OFF \
    -DCURL_LTO=ON

  # Apple 平台使用系统证书链
  case "$PLATFORM" in
    macos-*|ios-*)
      echo -DUSE_APPLE_SECTRUST=ON
      ;;
  esac
}

build_libcurl() {
  # cache 命中时 $PREFIX/lib/libcurl.a 已存在, 且依赖 (openssl/nghttp2/nghttp3/ngtcp2)
  # 因为共用同一个 actions/cache key (基于 submodule hash + 脚本 hash) 一起失效 /
  # 一起恢复, 所以 libcurl.a 的存在 = 依赖也是匹配的版本, 安全跳过。
  if [[ -f "$PREFIX/lib/libcurl.a" ]]; then
    echo "  [跳过] libcurl 已编译"
    return
  fi

  log "[$PLATFORM] 编译 libcurl"
  local start
  start=$(step_time)

  # 清除依赖库安装的 cmake package config，避免与 curl 的 FindXXX 模块冲突
  rm -rf "$PREFIX/lib/cmake/nghttp2" "$PREFIX/lib/cmake/nghttp3" "$PREFIX/lib/cmake/ngtcp2"

  local build_dir="$PROJECT_ROOT/build/$PLATFORM/curl"
  rm -rf "$build_dir"

  cmake -B "$build_dir" -S "$CURL_SRC" -G Ninja \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" \
    -DBUILD_CURL_EXE=OFF \
    -DBUILD_SHARED_LIBS="$CURL_SHARED" \
    -DBUILD_STATIC_LIBS="$CURL_STATIC" \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    $(_curl_common_args) \
    "${CMAKE_EXTRA_ARGS[@]}"

  cmake --build "$build_dir" -j "$JOBS"
  cmake --install "$build_dir"

  echo "  libcurl 完成 ($(elapsed "$start"))"
}

build_curl_cli() {
  # 在独立目录中编译全静态链接的 curl 可执行文件，不影响库的编译产物
  log "[$PLATFORM] 编译 curl CLI (静态链接)"
  local start
  start=$(step_time)

  local build_dir="$PROJECT_ROOT/build/$PLATFORM/curl-cli"
  local cli_prefix="$PROJECT_ROOT/build/$PLATFORM/curl-cli-install"
  rm -rf "$build_dir"
  mkdir -p "$cli_prefix"

  cmake -B "$build_dir" -S "$CURL_SRC" -G Ninja \
    -DCMAKE_INSTALL_PREFIX="$cli_prefix" \
    -DBUILD_CURL_EXE=ON \
    -DBUILD_SHARED_LIBS=OFF \
    -DBUILD_STATIC_LIBS=ON \
    -DBUILD_STATIC_CURL=ON \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
    $(_curl_common_args) \
    "${CMAKE_EXTRA_ARGS[@]}"

  cmake --build "$build_dir" -j "$JOBS"
  cmake --install "$build_dir"

  # 收集 CLI 产物
  local out="$OUTPUT_DIR/macOS"
  mkdir -p "$out"
  cp -f "$cli_prefix/bin/curl" "$out/curl"
  echo "  -> $out/curl"
  echo "  curl CLI 完成 ($(elapsed "$start"))"
  echo ""
  echo "  验证: $out/curl --version"
  echo "  测试: $out/curl -v --http3 https://cloudflare-quic.com/"
}

# ============================================================
# 产物收集 — bridge.c + 所有静态依赖 → 单个动态/静态库
# ============================================================

# 所有平台共用的静态库列表
_all_static_libs() {
  echo \
    "$PREFIX/lib/libcurl.a" \
    "$PREFIX/lib/libssl.a" \
    "$PREFIX/lib/libcrypto.a" \
    "$PREFIX/lib/libnghttp2.a" \
    "$PREFIX/lib/libngtcp2.a" \
    "$PREFIX/lib/libngtcp2_crypto_ossl.a" \
    "$PREFIX/lib/libnghttp3.a"
}

collect_output() {
  log "[$PLATFORM] 编译 bridge + 收集产物"

  case "$PLATFORM" in
    macos-*)
      _collect_macos
      ;;
    ios-arm64)
      _collect_ios "$OUTPUT_DIR/iOS"
      ;;
    ios-sim-*)
      _collect_ios "$OUTPUT_DIR/iOS-Simulator"
      ;;
    android-arm64)
      _collect_android "$OUTPUT_DIR/Android/arm64-v8a" aarch64-linux-android
      ;;
    android-armv7)
      _collect_android "$OUTPUT_DIR/Android/armeabi-v7a" armv7a-linux-androideabi
      ;;
    android-x86_64)
      _collect_android "$OUTPUT_DIR/Android/x86_64" x86_64-linux-android
      ;;
  esac
}

_collect_macos() {
  local arch
  case "$PLATFORM" in
    macos-arm64)  arch=arm64 ;;
    macos-x86_64) arch=x86_64 ;;
  esac

  local out="$OUTPUT_DIR/macOS/$arch"
  mkdir -p "$out"

  # bridge.c + 所有静态库 → 单个 dylib
  cc -arch "$arch" -mmacosx-version-min="$MACOS_MIN_VERSION" \
    -shared -o "$out/libcurl_unity.dylib" \
    -I"$PREFIX/include" \
    "$BRIDGE_SRC" \
    -Wl,-force_load,$PREFIX/lib/libcurl.a \
    $(_all_static_libs | sed "s|$PREFIX/lib/libcurl.a||") \
    -framework SystemConfiguration \
    -framework CoreFoundation \
    -framework CoreServices \
    -framework Security \
    -lz \
    -install_name @rpath/libcurl_unity.dylib

  echo "  -> $out/libcurl_unity.dylib"
}

_collect_ios() {
  local out="$1"
  mkdir -p "$out"

  local sdk arch
  case "$PLATFORM" in
    ios-arm64)     sdk=iphoneos;       arch=arm64 ;;
    ios-sim-arm64) sdk=iphonesimulator; arch=arm64 ;;
  esac

  # bridge.c → bridge.o
  xcrun -sdk "$sdk" cc -arch "$arch" -c -O2 \
    -I"$PREFIX/include" \
    -o "$PREFIX/lib/curl_unity_bridge.o" \
    "$BRIDGE_SRC"

  # 合并所有静态库 + bridge.o
  libtool -static -o "$out/libcurl_unity.a" \
    "$PREFIX/lib/curl_unity_bridge.o" \
    $(_all_static_libs) \
    2>/dev/null

  echo "  -> $out/libcurl_unity.a"

  # 复制头文件
  rm -rf "$out/include"
  cp -R "$PREFIX/include" "$out/include"
  echo "  -> $out/include/"
}

_collect_android() {
  local out="$1"
  local target="$2"
  mkdir -p "$out"

  local toolchain_bin="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/$(_ndk_host_tag)/bin"
  local clang="$toolchain_bin/${target}${ANDROID_MIN_API}-clang"

  # bridge.c + 所有静态库 → 单个 .so
  "$clang" -shared -o "$out/libcurl_unity.so" \
    -I"$PREFIX/include" \
    "$BRIDGE_SRC" \
    -Wl,--whole-archive "$PREFIX/lib/libcurl.a" -Wl,--no-whole-archive \
    $(_all_static_libs | sed "s|$PREFIX/lib/libcurl.a||") \
    -lz

  # strip
  local strip_bin="$toolchain_bin/llvm-strip"
  if [[ -x "$strip_bin" ]]; then
    "$strip_bin" --strip-debug "$out/libcurl_unity.so"
    echo "  (已 strip 调试符号)"
  fi

  echo "  -> $out/libcurl_unity.so"
}

# ============================================================
# 主流程
# ============================================================
main() {
  echo "========================================"
  echo "  libcurl 构建脚本"
  echo "  平台: $PLATFORM"
  echo "  项目: $PROJECT_ROOT"
  echo "========================================"

  configure_platform

  PREFIX="$PROJECT_ROOT/build/$PLATFORM/install"

  # 交叉编译时 CMake 会限制搜索路径，需要将 PREFIX 加入 CMAKE_FIND_ROOT_PATH
  # 这样 FindOpenSSL / FindNGHTTP2 等模块才能找到我们编译的依赖
  case "$PLATFORM" in
    ios-*|android-*)
      CMAKE_EXTRA_ARGS+=(-DCMAKE_FIND_ROOT_PATH="$PREFIX")
      ;;
  esac

  if [[ "$CLEAN" == "1" ]]; then
    echo "清理 build/$PLATFORM ..."
    rm -rf "$PROJECT_ROOT/build/$PLATFORM"
  fi

  mkdir -p "$PREFIX"

  local total_start
  total_start=$(step_time)

  build_openssl
  build_nghttp2
  build_nghttp3
  build_ngtcp2

  if [[ "$BUILD_CURL_CLI" == "1" ]]; then
    build_curl_cli
  else
    build_libcurl
    collect_output
  fi

  log "全部完成! 总耗时: $(elapsed "$total_start")"
  echo "安装目录: $PREFIX"
  echo "产物目录: $OUTPUT_DIR"
}

main
