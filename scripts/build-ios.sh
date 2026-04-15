#!/usr/bin/env bash
#
# iOS 全架构构建 (目前仅 ARM64 真机)
#
set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

echo "========================================"
echo "  iOS 全架构构建"
echo "========================================"
echo ""

"$SCRIPT_DIR/build.sh" ios-arm64 "$@"
