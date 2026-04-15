#!/usr/bin/env bash
# 快捷脚本: Android x86_64 (API 22, 模拟器用)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$SCRIPT_DIR/build.sh" android-x86_64 --android-api 22 "$@"
