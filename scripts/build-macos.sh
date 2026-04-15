#!/usr/bin/env bash
#
# macOS 全架构构建: ARM64 + x86_64，最后合成 universal binary
#
set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="$PROJECT_ROOT/output/macOS"

echo "========================================"
echo "  macOS 全架构构建 (ARM64 + x86_64)"
echo "========================================"
echo ""

# 构建两个架构
"$SCRIPT_DIR/build.sh" macos-arm64 "$@"
"$SCRIPT_DIR/build.sh" macos-x86_64 "$@"

# 合成 universal binary
ARM64="$OUTPUT_DIR/arm64/libcurl_unity.dylib"
X86_64="$OUTPUT_DIR/x86_64/libcurl_unity.dylib"
UNIVERSAL="$OUTPUT_DIR/libcurl_unity.dylib"

if [[ -f "$ARM64" && -f "$X86_64" ]]; then
  echo ""
  echo "合成 universal binary..."
  lipo -create "$ARM64" "$X86_64" -output "$UNIVERSAL"
  echo "  -> $UNIVERSAL"
  lipo -info "$UNIVERSAL"
else
  echo ""
  echo "警告: 无法合成 universal binary (缺少架构产物)"
  [[ ! -f "$ARM64" ]] && echo "  缺失: $ARM64"
  [[ ! -f "$X86_64" ]] && echo "  缺失: $X86_64"
fi
