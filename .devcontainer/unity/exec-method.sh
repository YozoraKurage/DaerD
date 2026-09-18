#!/usr/bin/env bash
# 常駐 Unity (test-daemon.sh) に static メソッドの実行を依頼する。
#
#   exec-method.sh 'Yozolab.DaerD.Analyze.ClipDigestEntry.Run' /path/to/args.txt
#
# 対象は static string Method(string)（可視性は不問 — リフレクション経由）。
# 第 2 引数は文字列としてそのまま渡す。慣習としてファイルパスを渡し、中身の形式は
# メソッド側の取り決めにする（クリップ解析なら「1 行 1 アセットパス」）。
# 結果テキストは標準出力へ。終了コード: 0=成功 / 1=実行時エラー / 3=コンパイルエラー。
# デーモン専用でコールドへのフォールバックは無い — 先に test-daemon.sh start。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

TARGET="${1:?第 1 引数に 'Full.Type.Name.Method' が要る}"
ARG="${2:-}"

readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
daemon_alive() {
  [[ -f "$DAEMON_DIR/daemon.pid" ]] \
    && kill -0 "$(cat "$DAEMON_DIR/daemon.pid")" 2>/dev/null
}
daemon_alive || die "デーモンが起動していない。先に test-daemon.sh start"

# request.json は素朴な printf で組むので、エスケープが要る文字は最初から拒否する。
case "$TARGET$ARG" in
  *'"'* | *'\'* ) die 'target/arg に " と \ は使えない' ;;
esac

# 相対パスの引数は、デーモン側のカレントディレクトリに依存しないよう絶対化する。
[[ -n "$ARG" && -f "$ARG" ]] && ARG="$(realpath "$ARG")"

rm -f "$DAEMON_DIR/done" "$DAEMON_DIR/exec-result.txt"
printf '{"exec":"%s","execArg":"%s"}' "$TARGET" "$ARG" > "$DAEMON_DIR/request.json"

# 通常は数秒。/workspace のソースが変わった直後は再コンパイルを挟むので長めに待つ。
for _ in $(seq 1 300); do
  sleep 1
  [[ -f "$DAEMON_DIR/done" ]] && break
  daemon_alive || die "デーモンが死んだ。$UNITY_LOG_DIR/daemon.log を確認"
done
[[ -f "$DAEMON_DIR/done" ]] || die "5 分応答が無い。test-daemon.sh restart を検討"

code=$(head -1 "$DAEMON_DIR/done")
if [[ "$code" == 3 ]]; then
  warn "デーモン側でコンパイルエラー: $(sed -n 2p "$DAEMON_DIR/done")"
  grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' \
    "$UNITY_LOG_DIR/daemon.log" 2>/dev/null | sort -u | head -50
  exit 3
fi
cat "$DAEMON_DIR/exec-result.txt" 2>/dev/null
exit "$code"
