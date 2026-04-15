#!/usr/bin/env bash
# 快捷脚本: Android arm64-v8a (API 22)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$SCRIPT_DIR/build.sh" android-arm64 --android-api 22 "$@"
