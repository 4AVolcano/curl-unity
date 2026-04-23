#!/usr/bin/env bash
#
# 跑单元 + 集成测试,收集代码覆盖率并生成 HTML 报告。
#
# 使用: ./scripts/coverage.sh [--open]
#   --open    生成后自动打开 HTML 报告 (macOS: open; Linux: xdg-open; Windows: start)
#
# 输出:
#   build/coverage/raw/         各项目原始 cobertura XML
#   build/coverage/report/      ReportGenerator 合并后的 HTML + 汇总
#   build/coverage/report/index.html  入口
#
# 前置: 已在项目根 `dotnet tool restore`,把本地 ReportGenerator 装好 (CI/新克隆时跑一次)。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

OUT_RAW="build/coverage/raw"
OUT_REPORT="build/coverage/report"
SETTINGS="coverlet.runsettings"

# 确保本地工具可用 (无网环境下 restore 失败会在这里早停)
dotnet tool restore >/dev/null

# 清空上次结果 (只清 coverage 目录, 不动 build/ 其它产物)
rm -rf "$OUT_RAW" "$OUT_REPORT"
mkdir -p "$OUT_RAW"

echo "==> Running unit tests with coverage"
dotnet test tests/CurlUnity.UnitTests/CurlUnity.UnitTests.csproj \
    --nologo \
    --collect:"XPlat Code Coverage" \
    --settings "$SETTINGS" \
    --results-directory "$OUT_RAW/unit" \
    >/dev/null

echo "==> Running integration tests with coverage"
dotnet test tests/CurlUnity.IntegrationTests/CurlUnity.IntegrationTests.csproj \
    --nologo \
    --collect:"XPlat Code Coverage" \
    --settings "$SETTINGS" \
    --results-directory "$OUT_RAW/integration" \
    >/dev/null

echo "==> Normalising assembly names + merging coverage + generating HTML report"
# 两个 test project 都把 Runtime source linked 编进自己的 DLL, cobertura 里的
# <package name="CurlUnity.UnitTests"> / "CurlUnity.IntegrationTests" 实际是同一份
# 源码。ReportGenerator 默认按 (assembly, class) 去重, 不同 assembly 会视为
# 不同 class, 导致 total coverable 翻倍、合并后数字被稀释。
# 解决: 把两份 XML 的 package name 统一成 CurlUnity.Runtime, ReportGenerator 就能
# 按 class name union 两份数据 (同一 class 在两边的 hit 合并, 未测的为 missed)。
#
# 用 sed + 临时文件做原地替换, 跨平台一致 (macOS BSD sed 的 -i 要求 '', GNU sed 不要,
# 避开这个差异的最简单方式就是写临时文件再 mv)。不使用 python3, 避免引入额外依赖。
for xml in $(find "$OUT_RAW" -name coverage.cobertura.xml); do
    sed \
        -e 's|<package name="CurlUnity\.UnitTests"|<package name="CurlUnity.Runtime"|g' \
        -e 's|<package name="CurlUnity\.IntegrationTests"|<package name="CurlUnity.Runtime"|g' \
        "$xml" > "$xml.tmp" && mv "$xml.tmp" "$xml"
done

dotnet reportgenerator \
    -reports:"$OUT_RAW/**/coverage.cobertura.xml" \
    -targetdir:"$OUT_REPORT" \
    -reporttypes:"Html;TextSummary;MarkdownSummaryGithub" \
    -title:"curl-unity coverage" \
    -verbosity:Warning

echo
echo "==> Summary"
cat "$OUT_REPORT/Summary.txt"

echo
echo "==> HTML report: $OUT_REPORT/index.html"

if [[ "${1:-}" == "--open" ]]; then
    case "$OSTYPE" in
        darwin*) open "$OUT_REPORT/index.html" ;;
        linux*)  xdg-open "$OUT_REPORT/index.html" 2>/dev/null || true ;;
        msys*|cygwin*) start "$OUT_REPORT/index.html" ;;
    esac
fi
