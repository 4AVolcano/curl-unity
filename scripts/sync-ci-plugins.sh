#!/usr/bin/env bash
#
# Download latest CI build artifacts and sync to Unity Package Plugins
#
# Usage:
#   ./scripts/sync-ci-plugins.sh              # latest successful build
#   ./scripts/sync-ci-plugins.sh --run <id>   # specific run ID
#
# Requires: gh (GitHub CLI), authenticated
#
set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PLUGINS_DIR="$PROJECT_ROOT/Packages/com.basecity.curl-unity/Runtime/Plugins"
REPO="4AVolcano/curl-unity"
WORKFLOW="build.yml"
TEMP_DIR=""

# ============================================================
# Parse args
# ============================================================
RUN_ID=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --run) RUN_ID="$2"; shift ;;
        *) echo "Unknown arg: $1"; exit 1 ;;
    esac
    shift
done

# ============================================================
# Prereqs
# ============================================================
if ! command -v gh &>/dev/null; then
    echo "ERROR: gh (GitHub CLI) not found"
    echo "Install: brew install gh"
    echo "Auth:    gh auth login"
    exit 1
fi

if ! gh auth status &>/dev/null; then
    echo "ERROR: gh not authenticated"
    echo "Run: gh auth login"
    exit 1
fi

# ============================================================
# Find run
# ============================================================
if [[ -z "$RUN_ID" ]]; then
    echo "Finding latest successful build..."
    RUN_ID=$(gh run list --repo "$REPO" --workflow "$WORKFLOW" --status success \
        --limit 1 --json databaseId --jq '.[0].databaseId')
    if [[ -z "$RUN_ID" ]]; then
        echo "ERROR: No successful runs found"
        exit 1
    fi
fi

# Get run info
RUN_INFO=$(gh run view "$RUN_ID" --repo "$REPO" \
    --json headSha,displayTitle,createdAt \
    --jq '"\(.displayTitle) (\(.headSha[0:8]), \(.createdAt))"')
echo "Run #$RUN_ID: $RUN_INFO"

# ============================================================
# Download artifacts
# ============================================================
TEMP_DIR=$(mktemp -d)
trap "rm -rf '$TEMP_DIR'" EXIT

echo ""
echo "Downloading artifacts..."

# Download all non-log artifacts
ARTIFACTS=$(gh api "repos/$REPO/actions/runs/$RUN_ID/artifacts" \
    --jq '.artifacts[] | select(.name | test("^log-") | not) | .name')

for name in $ARTIFACTS; do
    echo -n "  $name ... "
    # Each artifact gets its own subdirectory (avoids filename collisions)
    gh run download "$RUN_ID" --repo "$REPO" --name "$name" --dir "$TEMP_DIR/$name" 2>/dev/null \
        && echo "ok" \
        || echo "FAILED"
done

# ============================================================
# Sync to Plugins
# ============================================================
echo ""
echo "Syncing to Plugins..."

copied=0
sync_file() {
    local src="$1"
    local dst="$2"
    if [[ ! -f "$src" ]]; then
        echo "  [skip] $(basename "$src") not found"
        return
    fi
    mkdir -p "$(dirname "$dst")"
    cp -f "$src" "$dst"
    local size
    size=$(du -h "$dst" | cut -f1 | xargs)
    echo "  [sync] $(basename "$dst") ($size) -> $(dirname "$dst")/"
    copied=$((copied + 1))
}

# macOS: CI 按架构出 artifact（macos-arm64 / macos-x86_64），这里 lipo 合成
# universal 再入库——Plugins 的 .meta 声明 OSXUniversal/AnyCPU，若只同步
# arm64 单架构，Intel Mac（玩家或编辑器）上会 DllNotFoundException。
MACOS_ARM64="$TEMP_DIR/macos-arm64/libcurl_unity.dylib"
MACOS_X86_64="$TEMP_DIR/macos-x86_64/libcurl_unity.dylib"
if [[ -f "$MACOS_ARM64" && -f "$MACOS_X86_64" ]] && command -v lipo &>/dev/null; then
    lipo -create "$MACOS_ARM64" "$MACOS_X86_64" \
        -output "$TEMP_DIR/libcurl_unity_universal.dylib"
    echo "  [lipo] $(lipo -archs "$TEMP_DIR/libcurl_unity_universal.dylib" 2>/dev/null || echo universal)"
    sync_file "$TEMP_DIR/libcurl_unity_universal.dylib" "$PLUGINS_DIR/macOS/libcurl_unity.dylib"
elif [[ -f "$MACOS_ARM64" ]]; then
    echo "  [warn] macos-x86_64 artifact 缺失（或无 lipo），回退同步 arm64 单架构 dylib。"
    echo "         注意: Plugins meta 声明 OSXUniversal，单架构产物在 Intel Mac 上会加载失败!"
    sync_file "$MACOS_ARM64" "$PLUGINS_DIR/macOS/libcurl_unity.dylib"
else
    echo "  [skip] macOS dylib artifacts not found"
fi

# Map artifact names to Plugin paths
sync_file "$TEMP_DIR/ios-arm64/libcurl_unity.a"        "$PLUGINS_DIR/iOS/libcurl_unity.a"
sync_file "$TEMP_DIR/android-arm64/libcurl_unity.so"   "$PLUGINS_DIR/Android/arm64-v8a/libcurl_unity.so"
sync_file "$TEMP_DIR/android-armv7/libcurl_unity.so"   "$PLUGINS_DIR/Android/armeabi-v7a/libcurl_unity.so"
sync_file "$TEMP_DIR/android-x86_64/libcurl_unity.so"  "$PLUGINS_DIR/Android/x86_64/libcurl_unity.so"
sync_file "$TEMP_DIR/windows-x64/libcurl_unity.dll"    "$PLUGINS_DIR/Windows/x86_64/libcurl_unity.dll"
sync_file "$TEMP_DIR/windows-x86/libcurl_unity.dll"    "$PLUGINS_DIR/Windows/x86/libcurl_unity.dll"

echo ""
echo "Done: $copied files synced from CI run #$RUN_ID"
