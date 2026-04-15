#!/usr/bin/env bash
# 快捷脚本: macOS Intel (x86_64)
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$SCRIPT_DIR/build.sh" macos-x86_64 "$@"
