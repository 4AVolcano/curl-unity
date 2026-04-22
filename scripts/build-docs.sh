#!/usr/bin/env bash
#
# 本地构建 DocFX 文档站, 可选本地预览。
#
# 用法:
#   ./scripts/build-docs.sh            # 只构建, 产物在 docs/_site/
#   ./scripts/build-docs.sh --serve    # 构建 + 启动 http://localhost:8080 预览
#
# 依赖: dotnet SDK 8.0+。首次运行会自动安装 docfx global tool。
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DOCS_DIR="$PROJECT_ROOT/docs"
SITE_DIR="$DOCS_DIR/_site"

# 必须与 .github/workflows/docs.yml 里的 --version 保持一致。
# 升级 docfx 时两处一起改, 避免本地/CI 行为差异。
DOCFX_VERSION=2.78.3

SERVE=0
if [[ "${1:-}" == "--serve" ]]; then SERVE=1; fi

# ============================================================
# 安装 / 校验 docfx 版本 (与 CI 锁同一版本)
# ============================================================
if ! command -v docfx &>/dev/null; then
    echo "docfx 未安装, 正在安装 $DOCFX_VERSION ..."
    dotnet tool install -g docfx --version "$DOCFX_VERSION"
    # 提醒用户把 ~/.dotnet/tools 加进 PATH
    if ! command -v docfx &>/dev/null; then
        echo ""
        echo "docfx 安装完成, 但当前 shell PATH 还找不到它。"
        echo "请把以下目录加进 PATH 再重跑:"
        echo "  \$HOME/.dotnet/tools"
        echo ""
        echo "zsh:  echo 'export PATH=\$PATH:\$HOME/.dotnet/tools' >> ~/.zshrc && source ~/.zshrc"
        echo "bash: echo 'export PATH=\$PATH:\$HOME/.dotnet/tools' >> ~/.bashrc && source ~/.bashrc"
        exit 1
    fi
else
    # 已装, 检查版本是否匹配 CI
    installed=$(docfx --version 2>/dev/null | head -1 | awk '{print $1}')
    if [[ "$installed" != "$DOCFX_VERSION"* ]]; then
        echo "警告: 本地 docfx 版本 ($installed) 与 CI 锁定的 $DOCFX_VERSION 不一致, 可能出现行为差异。"
        echo "  同步到目标版本:  dotnet tool update -g docfx --version $DOCFX_VERSION"
    fi
fi

# ============================================================
# 构建
# ============================================================
cd "$DOCS_DIR"

# --warningsAsErrors 和 CI 保持一致,避免"本地通过 CI 挂"。
# 开发阶段如果想先放过 warning, 临时注释即可; 推送前务必保持和 CI 同样严格。
echo "==> 生成 API 元数据"
docfx metadata docfx.json --logLevel warning --warningsAsErrors

echo "==> 构建站点"
if [[ "$SERVE" == "1" ]]; then
    docfx build docfx.json --logLevel warning --warningsAsErrors --serve
else
    docfx build docfx.json --logLevel warning --warningsAsErrors
    echo ""
    echo "构建完成: $SITE_DIR"
    echo "本地预览: ./scripts/build-docs.sh --serve"
fi
