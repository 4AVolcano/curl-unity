#!/usr/bin/env bash
# 快捷脚本: Android armeabi-v7a (API 22)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$SCRIPT_DIR/build.sh" android-armv7 --android-api 22 "$@"
