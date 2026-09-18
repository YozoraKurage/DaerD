#!/usr/bin/env bash
# 常駐 Unity (test-daemon.sh) に、テスト以外の操作を依頼する。
#
#   unity-do.sh run  <file.cs>            C# の断片を Editor 内で実行して結果を表示
#   unity-do.sh run  -e 'return 1 + 1;'   断片を直接渡す
#   unity-do.sh run  -                    標準入力から
#   unity-do.sh console [error|warning|log ...] [--limit N] [--full] [--clear]
#                                         コンソールの内容（既定: 全種別・末尾 50 件・1 行目だけ）
#   unity-do.sh compile [--force]         再コンパイルしてエラー本文を表示（--force は全アセンブリ）
#
# 断片はメソッド本体として扱う（`return x;` で値を返す。先頭の using 行はそのまま
# 使える）。System / System.Linq / UnityEngine / UnityEditor 等は取り込み済み。
# Unity 同梱の Roslyn で外部コンパイルし、DLL を常駐エディタへ読み込んで実行するので、
# ドメインリロードは起きない（1 回 2〜3 秒）。
# 終了コード: 0=成功 / 1=実行時エラー / 3=コンパイルエラー / 5=デーモンが止まっていた。
# デーモン専用でコールドへのフォールバックは無い — 先に test-daemon.sh start。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
daemon_alive() {
  [[ -f "$DAEMON_DIR/daemon.pid" ]] \
    && kill -0 "$(cat "$DAEMON_DIR/daemon.pid")" 2>/dev/null
}

usage() { sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

cmd="${1:-}"; shift || true
op=""; arg=""
case "$cmd" in
  run)
    src="${1:-}"
    snippet="$DAEMON_DIR/snippet-in.cs"
    mkdir -p "$DAEMON_DIR"
    case "$src" in
      -e) [[ $# -ge 2 ]] || die "-e の後に断片が要る"; printf '%s\n' "$2" > "$snippet" ;;
      -)  cat > "$snippet" ;;
      "") die "断片のファイル、-e '...'、- のどれかが要る" ;;
      *)  [[ -f "$src" ]] || die "ファイルが無い: $src"; cp "$src" "$snippet" ;;
    esac
    op=snippet; arg="$snippet" ;;
  console)
    op=console
    while [[ $# -gt 0 ]]; do
      case "$1" in
        error|warning|log|full|clear) arg="$arg $1" ;;
        --full) arg="$arg full" ;;
        --clear) arg="$arg clear" ;;
        --limit) arg="$arg limit=${2:?--limit に数が要る}"; shift ;;
        *) die "不明な引数: $1" ;;
      esac
      shift
    done
    arg="${arg# }" ;;
  compile)
    op=compile
    [[ "${1:-}" == "--force" ]] && arg="force" ;;
  -h|--help|"") usage; exit 0 ;;
  *) die "不明なコマンド: $cmd" ;;
esac

daemon_alive || die "デーモンが起動していない。先に test-daemon.sh start"
case "$arg" in *'"'* | *'\'* ) die 'arg に " と \ は使えない' ;; esac

rm -f "$DAEMON_DIR/done" "$DAEMON_DIR/exec-result.txt"
printf '{"op":"%s","arg":"%s"}' "$op" "$arg" > "$DAEMON_DIR/request.json"

# 通常は数秒。/workspace のソースが変わった直後は再コンパイルを挟むので長めに待つ。
for _ in $(seq 1 300); do
  sleep 1
  [[ -f "$DAEMON_DIR/done" ]] && break
  daemon_alive || die "デーモンが死んだ。$UNITY_LOG_DIR/daemon.log を確認"
done
[[ -f "$DAEMON_DIR/done" ]] || die "5 分応答が無い。test-daemon.sh restart を検討"

code=$(head -1 "$DAEMON_DIR/done")
cat "$DAEMON_DIR/exec-result.txt" 2>/dev/null
[[ "$code" == 5 ]] && warn "デーモンが止まっていた: $(sed -n 2p "$DAEMON_DIR/done")"
exit "$code"
