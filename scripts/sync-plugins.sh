#!/usr/bin/env bash
#
# 将 output/ 中的编译产物同步到 Unity Package 的 Plugins 目录
#
# 用法:
#   ./scripts/sync-plugins.sh
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/output"
PLUGINS_DIR="$PROJECT_ROOT/Packages/com.basecity.curl-unity/Runtime/Plugins"

copied=0
skipped=0

sync_file() {
  local src="$1"
  local dst="$2"

  if [[ ! -f "$src" ]]; then
    echo "  [缺失] $src"
    skipped=$((skipped + 1))
    return
  fi

  mkdir -p "$(dirname "$dst")"
  cp -f "$src" "$dst"
  local size
  size=$(du -h "$dst" | cut -f1 | xargs)
  echo "  [同步] $(basename "$dst")  ($size)  -> $(dirname "$dst")/"
  copied=$((copied + 1))
}

echo "同步编译产物到 Unity Package..."
echo ""

# macOS (prefer universal, fallback to arm64)
if [[ -f "$OUTPUT_DIR/macOS/libcurl_unity.dylib" ]]; then
  sync_file "$OUTPUT_DIR/macOS/libcurl_unity.dylib" "$PLUGINS_DIR/macOS/libcurl_unity.dylib"
elif [[ -f "$OUTPUT_DIR/macOS/arm64/libcurl_unity.dylib" ]]; then
  sync_file "$OUTPUT_DIR/macOS/arm64/libcurl_unity.dylib" "$PLUGINS_DIR/macOS/libcurl_unity.dylib"
else
  echo "  [缺失] macOS libcurl_unity.dylib"
  skipped=$((skipped + 1))
fi

# iOS
sync_file "$OUTPUT_DIR/iOS/libcurl_unity.a" "$PLUGINS_DIR/iOS/libcurl_unity.a"

# Android
sync_file "$OUTPUT_DIR/Android/arm64-v8a/libcurl_unity.so" "$PLUGINS_DIR/Android/arm64-v8a/libcurl_unity.so"
sync_file "$OUTPUT_DIR/Android/armeabi-v7a/libcurl_unity.so" "$PLUGINS_DIR/Android/armeabi-v7a/libcurl_unity.so"
sync_file "$OUTPUT_DIR/Android/x86_64/libcurl_unity.so" "$PLUGINS_DIR/Android/x86_64/libcurl_unity.so"

# Windows (if exists)
sync_file "$OUTPUT_DIR/Windows/x64/libcurl_unity.dll" "$PLUGINS_DIR/Windows/x86_64/libcurl_unity.dll" 2>/dev/null || true
sync_file "$OUTPUT_DIR/Windows/x86/libcurl_unity.dll" "$PLUGINS_DIR/Windows/x86/libcurl_unity.dll" 2>/dev/null || true

echo ""
echo "完成: $copied 个文件已同步, $skipped 个缺失跳过"
