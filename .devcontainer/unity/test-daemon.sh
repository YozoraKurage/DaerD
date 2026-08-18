#!/usr/bin/env bash
# DaerD テストデーモン: 常駐 batchmode Unity にテストを依頼できるようにする。
#
#   test-daemon.sh start      受け口をインストールして常駐 Unity を起動
#   test-daemon.sh stop       行儀よく終了（応答が無ければ kill）
#   test-daemon.sh status     生死とハートビートの鮮度
#   test-daemon.sh restart
#
# 起動中は run-tests.sh が自動でデーモンへルーティングする（起動費 ~2 分 → 数十秒）。
# 注意: デーモンが生きている間、同じプロジェクトを別の Unity で開くことはできない。
# パッケージの出し入れ（SDK 剥がし等）をしたら restart するのが安全。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

readonly DAEMON_DIR="$UNITY_PROJECT/TestDaemon"
readonly PID_FILE="$DAEMON_DIR/daemon.pid"
readonly RECEIVER_SRC="$SCRIPT_DIR/daemon/DaerDTestDaemon.cs"
readonly RECEIVER_DST="$UNITY_PROJECT/Assets/DaerDTestDaemon/Editor/DaerDTestDaemon.cs"
readonly DAEMON_LOG="$UNITY_LOG_DIR/daemon.log"

pid_alive() {
  [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null
}

beat_age() {
  # ハートビートの秒齢。無ければ大きな値。
  local f="$DAEMON_DIR/alive"
  [[ -f "$f" ]] || { echo 99999; return; }
  echo $(( $(date +%s) - $(stat -c %Y "$f") ))
}

install_receiver() {
  mkdir -p "$(dirname "$RECEIVER_DST")" "$DAEMON_DIR"
  cp "$RECEIVER_SRC" "$RECEIVER_DST"
  touch "$DAEMON_DIR/enabled"
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
  info "常駐 Unity を起動中… (ログ: $DAEMON_LOG)"
  nohup "$UNITY_EDITOR" -batchmode -nographics \
    -projectPath "$UNITY_PROJECT" -logFile "$DAEMON_LOG" \
    >/dev/null 2>&1 &
  echo $! > "$PID_FILE"
  for _ in $(seq 1 150); do
    sleep 2
    if [[ $(beat_age) -lt 10 ]]; then
      info "デーモン準備完了 (PID $(cat "$PID_FILE"))"
      return 0
    fi
    pid_alive || { warn "Unity が起動中に死んだ。$DAEMON_LOG を確認"; exit 1; }
  done
  warn "5 分待ってもハートビートが来ない。$DAEMON_LOG を確認"; exit 1
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
    info "起動中 (PID $(cat "$PID_FILE")、ハートビート $(beat_age) 秒前)"
  else
    info "停止中"
  fi
}

case "${1:-status}" in
  start)   start ;;
  stop)    stop ;;
  restart) stop; start ;;
  status)  status ;;
  -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
  *) die "不明なコマンド: $1" ;;
esac
