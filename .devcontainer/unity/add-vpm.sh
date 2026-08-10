#!/usr/bin/env bash
# VPM パッケージをテストプロジェクトへ入れる（vrc-get 経由）。
#
#   add-vpm.sh com.vrchat.avatars          最新を入れる
#   add-vpm.sh com.vrchat.avatars@3.10.4   版を指定する
#   add-vpm.sh --remove com.vrchat.avatars 抜く
#   add-vpm.sh --list                      いま入っているものを見る
#
# DaerD は VRChat SDK 無しでも動くことをスタブで担保しているが、実際の利用者の
# プロジェクトには SDK がある。両方で通ることを確かめたいときにこれを使う。
# SDK があると製品コードは型を名前で探して SDK 側の型を掴むので、テストの書き方
# 次第で SDK 有りでだけ落ちる、という差が出る（実際に出た）。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

readonly VRC_GET_VERSION=1.9.2
readonly VRC_GET="$HOME/.local/bin/vrc-get"

ensure_vrc_get() {
  [[ -x "$VRC_GET" ]] && return 0
  # イメージに焼いてあれば PATH 上にいる。無ければ取ってくる（再ビルド直後など）。
  if command -v vrc-get >/dev/null 2>&1; then return 0; fi
  info "vrc-get を取得中…"
  mkdir -p "$(dirname "$VRC_GET")"
  curl -sSL --fail --max-time 180 -o "$VRC_GET" \
    "https://github.com/vrc-get/vrc-get/releases/download/v${VRC_GET_VERSION}/x86_64-unknown-linux-musl-vrc-get" \
    || die "vrc-get を取得できなかった"
  chmod +x "$VRC_GET"
}

vrc() { "$(command -v vrc-get || echo "$VRC_GET")" "$@"; }

# パッケージを足したあとは必ず一度インポートを通しておく。これをやらずにテストを
# 走らせると、Unity がまだコンパイル中のまま AddStateMachineBehaviour が拒否され、
# 実際には無関係な大量の NullReference として現れる。
warm_up() {
  local log="$UNITY_LOG_DIR/import.log"
  mkdir -p "$UNITY_LOG_DIR"
  info "インポートを流している（数分かかる。ログ: $log）"
  "$UNITY_EDITOR" -nographics -projectPath "$UNITY_PROJECT" -logFile "$log" -quit \
    || { tail -40 "$log" >&2; die "インポートが失敗した（ログ: $log）"; }
  info "完了。テストを走らせられる"
}

main() {
  restore_license
  have_license || { license_hint; exit 4; }
  [[ -f "$UNITY_PROJECT/Packages/manifest.json" ]] || "$SCRIPT_DIR/setup.sh"
  ensure_vrc_get

  case "${1:-}" in
    --list)
      ( cd "$UNITY_PROJECT" && vrc info project ) ;;
    --remove)
      [[ $# -ge 2 ]] || die "--remove にはパッケージ名が要る"
      ( cd "$UNITY_PROJECT" && vrc remove "$2" -y )
      warm_up ;;
    -h|--help|"")
      sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' ;;
    *)
      ( cd "$UNITY_PROJECT" && vrc install "$@" -y )
      warm_up ;;
  esac
}

main "$@"
