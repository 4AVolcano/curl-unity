#!/usr/bin/env bash
#
# Android 全架构构建: armv7 + arm64 + x86_64 (min API 22)
#
set -eo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "========================================"
echo "  Android 全架构构建"
echo "========================================"

echo ""
echo "========== Android armeabi-v7a =========="
"$SCRIPT_DIR/build.sh" android-armv7 --android-api 22 "$@"

echo ""
echo "========== Android arm64-v8a =========="
"$SCRIPT_DIR/build.sh" android-arm64 --android-api 22 "$@"

echo ""
echo "========== Android x86_64 =========="
"$SCRIPT_DIR/build.sh" android-x86_64 --android-api 22 "$@"

echo ""
echo "===== Android 全部完成 ====="
echo "产物:"
echo "  output/Android/armeabi-v7a/libcurl_unity.so"
echo "  output/Android/arm64-v8a/libcurl_unity.so"
echo "  output/Android/x86_64/libcurl_unity.so"
