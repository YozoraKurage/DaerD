#!/usr/bin/env bash
# DaerD 常駐 Unity: テストと Editor 操作を、起動費を払わずに依頼できるようにする。
#
#   test-daemon.sh start [--batch]   受け口をインストールして常駐 Unity を起動
#   test-daemon.sh stop              行儀よく終了（応答が無ければ kill）
#   test-daemon.sh status            生死・モード・ハートビートの鮮度
#   test-daemon.sh restart [--batch]
#
# 既定は GUI モード（xvfb の仮想画面上で、-batchmode を付けずに起動する）。
# batchmode と -nographics が禁じていた Play モード・EditorWindow・描画が使える。
# --batch は GL が動かない環境向けの従来起動。
#
# コンパイルエラーがある状態で開くと（実測 2026-09-15）、GUI は「Enter Safe Mode?」の
# ダイアログで主スレッドが止まり（受け口も鼓動も動かない）、batchmode は
# "Scripts have compiler errors." で自ら終了する。GUI では xdotool があれば
# ダイアログの Ignore を押して続行し、受け口がエラー本文を返す（unity-do.sh compile /
# run-tests.sh）。xdotool が無ければ殺してエラー本文を出し、終了コード 3 で止まる。
# xdotool は Dockerfile で入れる。再ビルド前のコンテナでは setup.sh が root なしで
# deb を展開して $HOME/.local/opt/x11tools に置く。
#
# 起動中は run-tests.sh / exec-method.sh / unity-do.sh が自動でここへルーティングする
# （どちらのモードでも同じ受け口が動く）。常駐している間、同じプロジェクトを別の Unity
# で開くことはできない。パッケージの出し入れ（SDK 剥がし等）をしたら restart するのが安全。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
readonly PID_FILE="$DAEMON_DIR/daemon.pid"
readonly RECEIVER_SRC="$SCRIPT_DIR/daemon/DaerDTestDaemon.cs"
readonly RECEIVER_DST="$UNITY_PROJECT/Assets/DaerDTestDaemon/Editor/DaerDTestDaemon.cs"
readonly DAEMON_LOG="$UNITY_LOG_DIR/daemon.log"

MODE="${DAERD_DAEMON_MODE:-gui}"   # gui | batch

parse_mode() {
  for a in "$@"; do
    case "$a" in
      --batch) MODE=batch ;;
      --gui)   MODE=gui ;;
      *) die "不明な引数: $a" ;;
    esac
  done
}

pid_alive() {
  [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null
}

beat_age() {
  # ハートビートの秒齢。無ければ大きな値。
  local f="$DAEMON_DIR/alive"
  [[ -f "$f" ]] || { echo 99999; return; }
  echo $(( $(date +%s) - $(stat -c %Y "$f") ))
}

# 起動中の Unity 本体の PID。ラッパー（unity-editor / xvfb-run）ではなく本体を記録する:
# ラッパーを kill しても Unity は残るし、本体が終われば xvfb-run は Xvfb を片付けて
# 自分で終わる。
unity_pid() {
  pgrep -f -- "^${UNITY_BIN} .*-projectPath ${UNITY_PROJECT}( |$)" | head -1 || true
}

install_receiver() {
  mkdir -p "$(dirname "$RECEIVER_DST")" "$DAEMON_DIR"
  cp "$RECEIVER_SRC" "$RECEIVER_DST"
  touch "$DAEMON_DIR/enabled"
}

launch() {
  if [[ "$MODE" == batch ]]; then
    nohup "$UNITY_EDITOR" -batchmode -nographics \
      -projectPath "$UNITY_PROJECT" -logFile "$DAEMON_LOG" \
      >/dev/null 2>&1 &
  else
    # unity-editor ラッパーは必ず -batchmode を足すので、本体を直接 xvfb に載せる。
    nohup xvfb-run -a -s "-screen 0 ${DAERD_XVFB_SCREEN:-1920x1080x24}" \
      "$UNITY_BIN" -projectPath "$UNITY_PROJECT" -logFile "$DAEMON_LOG" \
      >/dev/null 2>&1 &
  fi
  local wrapper=$! pid=""
  for _ in $(seq 1 30); do
    sleep 1
    pid="$(unity_pid)"
    [[ -n "$pid" ]] && break
    kill -0 "$wrapper" 2>/dev/null || break
  done
  echo "${pid:-$wrapper}" > "$PID_FILE"
}

# xdotool / xwd の在りか。イメージに無ければ setup.sh が展開した場所を使う。
x11_env() {
  if ! command -v xdotool >/dev/null 2>&1; then
    local x="$HOME/.local/opt/x11tools"
    [[ -x "$x/usr/bin/xdotool" ]] || return 1
    export PATH="$x/usr/bin:$PATH" LD_LIBRARY_PATH="$x/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  fi
  # Unity 本体の環境から、xvfb-run が選んだ画面と認証ファイルを借りる
  local pid; pid="$(cat "$PID_FILE" 2>/dev/null)" || return 1
  [[ -r "/proc/$pid/environ" ]] || return 1
  eval "$(tr '\0' '\n' < "/proc/$pid/environ" | grep -E '^(DISPLAY|XAUTHORITY)=' | sed 's/^/export /')"
  [[ -n "${DISPLAY:-}" ]]
}

# 画面を PNG に落とす（正体不明のダイアログの手がかり用）
screenshot() {
  command -v xwd >/dev/null 2>&1 || return 1
  xwd -root -silent 2>/dev/null | python3 "$SCRIPT_DIR/xwd2png.py" /dev/stdin "$1" >/dev/null 2>&1
}

# 「Enter Safe Mode?」なら Ignore を押す。ボタンは下段に [Ignore][Enter Safe Mode][Quit] の
# 3 等分で並ぶので、左 1/6 の位置を押す（実測 651x183）。押せたら 0。
press_safe_mode_ignore() {
  local w
  w="$(xdotool search --name '^Enter Safe Mode\?$' 2>/dev/null | head -1)"
  [[ -n "$w" ]] || return 1
  local WIDTH HEIGHT X Y SCREEN WINDOW
  eval "$(xdotool getwindowgeometry --shell "$w")"
  xdotool mousemove --window "$w" $(( WIDTH / 6 )) $(( HEIGHT - 18 )) click 1
  sleep 1
  [[ -z "$(xdotool search --name '^Enter Safe Mode\?$' 2>/dev/null)" ]]
}

log_idle() {
  [[ -f "$DAEMON_LOG" ]] || { echo 0; return; }
  echo $(( $(date +%s) - $(stat -c %Y "$DAEMON_LOG") ))
}

# 鼓動が来るまで待つ。0=準備完了 / 1=Unity が死んだ / 2=ダイアログで止まった(GUI のみ)
wait_ready() {
  for _ in $(seq 1 150); do
    sleep 2
    [[ $(beat_age) -lt 10 ]] && return 0
    pid_alive || return 1
    # 取り込みやコンパイルの間はログが流れ続ける。鼓動が無いままログまで止まるのは、
    # ダイアログが主スレッドを掴んでいるとき(Safe Mode の問いで実測 162 秒無音)。
    if [[ "$MODE" == gui && $(log_idle) -ge 30 ]]; then return 2; fi
  done
  return 1
}

start() {
  if pid_alive; then
    info "既に起動している (PID $(cat "$PID_FILE"))"
    return 0
  fi
  restore_license
  have_license || { license_hint; exit 4; }
  install_receiver
  rm -f "$DAEMON_DIR/alive" "$DAEMON_DIR/request.json" "$DAEMON_DIR/running.json" \
        "$DAEMON_DIR/done" "$DAEMON_DIR/quit"
  mkdir -p "$UNITY_LOG_DIR"
  # 前回のログを残す。止まったデーモンは再起動で直すので、上書きすると原因の手がかりが
  # 再起動と一緒に消える(2026-09-11 に実際に消えた)。状態遷移は TestDaemon/trace.log にも残る。
  if [[ -f "$DAEMON_LOG" ]]; then mv -f "$DAEMON_LOG" "$UNITY_LOG_DIR/daemon.prev.log"; fi
  info "常駐 Unity を起動中… (${MODE} モード、ログ: $DAEMON_LOG)"
  launch
  local rc=0
  wait_ready || rc=$?
  if [[ $rc -eq 0 ]]; then
    info "デーモン準備完了 (PID $(cat "$PID_FILE"))"
    return 0
  fi
  if [[ $rc -eq 2 ]]; then
    warn "鼓動が無いままログが止まった — ダイアログが主スレッドを掴んでいるとみなす"
    if x11_env; then
      if press_safe_mode_ignore; then
        info "「Enter Safe Mode?」の Ignore を押した。コンパイルエラーのまま続行する（unity-do.sh compile で本文が読める）"
        rc=0
        wait_ready || rc=$?
        if [[ $rc -eq 0 ]]; then
          info "デーモン準備完了 (PID $(cat "$PID_FILE"))"
          return 0
        fi
      else
        screenshot "$UNITY_LOG_DIR/dialog.png" && warn "正体不明のダイアログ。画面: $UNITY_LOG_DIR/dialog.png"
      fi
    fi
    kill -9 "$(cat "$PID_FILE")" 2>/dev/null || true
    sleep 2
  fi
  rm -f "$PID_FILE"
  local errors
  errors="$(grep -o '[^ ]*\.cs([0-9]*,[0-9]*): error CS[0-9]*: .*' "$DAEMON_LOG" 2>/dev/null | sort -u | head -30 || true)"
  if [[ -n "$errors" ]]; then
    warn "コンパイルエラーがあるので常駐を起動できない（GUI は Safe Mode の問いで止まり、batch は終了する）。直してから start:"
    printf '%s\n' "$errors" >&2
    exit 3
  fi
  if [[ $rc -eq 2 ]]; then warn "ダイアログで止まったまま。$DAEMON_LOG の末尾と $UNITY_LOG_DIR/dialog.png を確認"
  elif pid_alive; then warn "5 分待ってもハートビートが来ない。$DAEMON_LOG を確認"
  else warn "Unity が起動中に死んだ。$DAEMON_LOG を確認"; fi
  exit 1
}

stop() {
  if ! pid_alive; then
    info "起動していない"
    rm -f "$PID_FILE"
    return 0
  fi
  touch "$DAEMON_DIR/quit"
  # テスト実行中は quit が読まれるまで時間がかかる(同期フレーム中は update が
  # 止まる)。忙しいだけの常駐を kill しないよう、猶予は長めに取る。
  for _ in $(seq 1 60); do
    sleep 2
    pid_alive || { info "停止した"; rm -f "$PID_FILE" "$DAEMON_DIR/quit"; return 0; }
  done
  warn "2 分待っても応答が無いので kill する"
  kill -9 "$(cat "$PID_FILE")" 2>/dev/null || true
  rm -f "$PID_FILE" "$DAEMON_DIR/quit"
}

status() {
  if pid_alive; then
    local pid mode="GUI"
    pid="$(cat "$PID_FILE")"
    ps -o args= -p "$pid" 2>/dev/null | grep -q -- '-batchmode' && mode="batch"
    info "起動中 (PID $pid、${mode} モード、ハートビート $(beat_age) 秒前)"
  else
    info "停止中"
  fi
}

cmd="${1:-status}"; shift || true
case "$cmd" in
  start)   parse_mode "$@"; start ;;
  stop)    stop ;;
  restart) parse_mode "$@"; stop; start ;;
  status)  status ;;
  -h|--help) sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
  *) die "不明なコマンド: $cmd" ;;
esac
