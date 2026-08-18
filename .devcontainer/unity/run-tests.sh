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

# デーモン（test-daemon.sh start で常駐させた Unity）が生きていれば、起動費を払わずに
# そちらへ依頼する。死んでいれば黙って従来のコールド実行へ落ちる。
readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
# 死活は PID だけで見る。ハートビートは使わない — 同期的な Refresh や長いテスト
# フレームの間は update が止まって鼓動も止まるので、鮮度で判定すると「忙しい」を
# 「死んだ」と誤読してコールドに落ち、常駐とロック衝突する(実測済み)。
daemon_alive() {
  [[ -f "$DAEMON_DIR/daemon.pid" ]] \
    && kill -0 "$(cat "$DAEMON_DIR/daemon.pid")" 2>/dev/null
}
if daemon_alive; then
  info "デーモンへ依頼 (PID $(cat "$DAEMON_DIR/daemon.pid"))"
  rm -f "$DAEMON_DIR/done" "$DAEMON_DIR/result.xml"
  printf '{"filter":"%s","category":"%s"}' "$FILTER" "$CATEGORY" \
    > "$DAEMON_DIR/request.json"
  for _ in $(seq 1 900); do
    sleep 1
    [[ -f "$DAEMON_DIR/done" ]] && break
    daemon_alive || break
  done
  if [[ ! -f "$DAEMON_DIR/done" ]] && daemon_alive; then
    # 生きているのに 15 分応答が無い。勝手に殺してコールドへ落ちると常駐と
    # ロック衝突するので、ここでは状況を言って止まるだけにする。
    warn "デーモンは生きているが 15 分応答が無い。test-daemon.sh restart を検討 (ログ: $UNITY_LOG_DIR/daemon.log)"
    exit 1
  fi
  if [[ -f "$DAEMON_DIR/done" ]]; then
    code=$(head -1 "$DAEMON_DIR/done")
    if [[ "$code" == 3 ]]; then
      warn "デーモン側でコンパイルエラー: $(sed -n 2p "$DAEMON_DIR/done")"
      grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' \
        "$UNITY_LOG_DIR/daemon.log" 2>/dev/null | sort -u | head -50
      exit 3
    fi
    echo ""
    node "$SCRIPT_DIR/summarize-results.js" "$DAEMON_DIR/result.xml" || true
    exit "$code"
  fi
  warn "デーモンのプロセスが死んでいた。後始末してコールドで続行する"
  rm -f "$DAEMON_DIR/daemon.pid" "$DAEMON_DIR/running.json" "$DAEMON_DIR/request.json"
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

run_unity() {
  set +e
  "$UNITY_EDITOR" "${args[@]}"
  unity_status=$?
  set -e
}

info "実行中… (ログ: $LOG)"
run_unity

# パッケージを足した直後などは、Unity がまだコンパイルを終えていないうちにテストが
# 始まることがある。そうなると AddStateMachineBehaviour が黙って null を返し、
# 何十件もの無関係な NullReference になって出てくる。原因はログのこの一行だけ。
# 一度通せば Library が温まって解消するので、黙って一回やり直す。
if grep -q "Please fix compile errors" "$LOG" 2>/dev/null; then
  warn "コンパイルが終わらないうちにテストが走った。インポートを通してからやり直す"
  "$UNITY_EDITOR" -nographics -projectPath "$UNITY_PROJECT" \
    -logFile "$UNITY_LOG_DIR/import.log" -quit || true
  rm -f "$RESULTS"
  run_unity
fi

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
