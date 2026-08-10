#!/usr/bin/env bash
# DaerD の EditMode テストを batchmode で実行する。
#
#   run-tests.sh                                   全件
#   run-tests.sh --filter 'Yozolab.DaerD.Tests.AapGadgetsTests'
#   run-tests.sh --filter '*Frame*'                部分一致（NUnit のフィルタ構文）
#   run-tests.sh --category Slow
#   run-tests.sh --log                             失敗時に Unity ログの末尾も出す
#
# 標準出力にはサマリと失敗内容だけを出す。Unity の生ログ（数万行）は
# $DAERD_UNITY_PROJECT/Logs/tests.log に残る。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

FILTER=""
CATEGORY=""
SHOW_LOG=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --filter)   FILTER="${2:?--filter に値が要る}"; shift 2 ;;
    --category) CATEGORY="${2:?--category に値が要る}"; shift 2 ;;
    --log)      SHOW_LOG=1; shift ;;
    -h|--help)  sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)          die "不明な引数: $1" ;;
  esac
done

restore_license
have_license || { license_hint; exit 4; }

if [[ ! -f "$UNITY_PROJECT/Packages/manifest.json" ]]; then
  info "テストプロジェクトが未作成。setup.sh を先に実行する"
  "$SCRIPT_DIR/setup.sh"
fi

mkdir -p "$UNITY_LOG_DIR"
readonly LOG="$UNITY_LOG_DIR/tests.log"
readonly RESULTS="$UNITY_LOG_DIR/test-results.xml"
rm -f "$RESULTS"

args=(
  -nographics
  -projectPath "$UNITY_PROJECT"
  -logFile "$LOG"
  -runTests
  -testPlatform EditMode
  -testResults "$RESULTS"
)
[[ -n "$FILTER"   ]] && args+=(-testFilter "$FILTER")
[[ -n "$CATEGORY" ]] && args+=(-testCategory "$CATEGORY")

info "実行中… (ログ: $LOG)"
set +e
"$UNITY_EDITOR" "${args[@]}"
unity_status=$?
set -e

# コンパイルエラーだと結果 XML すら出ない。その場合はログから CS エラーだけ拾う。
if [[ ! -f "$RESULTS" ]]; then
  warn "結果 XML が出力されなかった (Unity 終了コード: $unity_status)"
  if grep -q 'error CS' "$LOG" 2>/dev/null; then
    echo ""
    echo "コンパイルエラー:"
    grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' "$LOG" | sort -u | head -50
  else
    tail -40 "$LOG" >&2
  fi
  exit 3
fi

echo ""
set +e
node "$SCRIPT_DIR/summarize-results.js" "$RESULTS"
summary_status=$?
set -e

if [[ $summary_status -ne 0 || $unity_status -ne 0 ]]; then
  echo ""
  info "生ログ: $LOG   結果 XML: $RESULTS"
  [[ $SHOW_LOG -eq 1 ]] && { echo ""; tail -60 "$LOG"; }
  exit 1
fi

exit 0
