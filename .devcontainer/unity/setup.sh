#!/usr/bin/env bash
# テスト用 Unity プロジェクトを用意する。postCreateCommand から呼ばれるほか、
# 手で何度実行しても同じ状態になる（冪等）。
#
# プロジェクト本体はボリューム側 ($DAERD_UNITY_PROJECT) に置き、このリポジトリは
# ローカルパッケージ (file:/workspace) として参照させる。リポジトリ側には
# Library/ も Assets/ も作らない。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

editor_version() {
  if [[ -r "${UNITY_PATH:-/opt/unity}/version" ]]; then
    cat "${UNITY_PATH:-/opt/unity}/version"
  else
    echo "${UNITY_VERSION:-2022.3.22f1}"
  fi
}

scaffold_project() {
  local version
  version="$(editor_version)"

  mkdir -p "$UNITY_PROJECT/Assets" "$UNITY_PROJECT/Packages" \
           "$UNITY_PROJECT/ProjectSettings" "$UNITY_LOG_DIR"

  # manifest はテンプレートから毎回同期する。生成物なので手編集は想定しない。
  cp "$SCRIPT_DIR/manifest.json" "$UNITY_PROJECT/Packages/manifest.json"

  # これが無いと Unity が「別バージョンで作られたプロジェクト」とみなす。
  printf 'm_EditorVersion: %s\n' "$version" > "$UNITY_PROJECT/ProjectSettings/ProjectVersion.txt"

  info "テストプロジェクト: $UNITY_PROJECT (Unity $version)"
}

check_drop_dir() {
  # temp/ (チルダ無し) はローカルパッケージの一部として Unity にインポートされて
  # しまう。ユーザー提供の生成 cs が入っていると丸ごとコンパイル対象になり、
  # VRC SDK 等が無い状態でコンパイルエラー → テストが一切走らなくなる。
  if [[ -d "$PACKAGE_ROOT/temp" ]]; then
    warn "$PACKAGE_ROOT/temp が存在する。Unity のインポート対象に入ってしまうので"
    warn "temp~ にリネームすること:  mv /workspace/temp /workspace/temp~"
  fi
}

warmup() {
  local log="$UNITY_LOG_DIR/setup.log"
  info "パッケージ解決と初回インポートを実行中（数分かかる。ログ: $log）"
  if "$UNITY_EDITOR" -nographics -projectPath "$UNITY_PROJECT" -logFile "$log" -quit; then
    info "初回インポート完了。以降 .devcontainer/unity/run-tests.sh でテストを走らせられる"
  else
    warn "初回インポートが非ゼロで終了した。ログの末尾:"
    tail -40 "$log" >&2
    return 1
  fi
}

# xdotool と xwd。GUI モードの常駐で出るダイアログ（Safe Mode の問い）を押すのと、
# 画面の記録に使う。Dockerfile で入れてあるが、再ビルド前の古いイメージでは無いので、
# root 無しで deb を展開して $HOME/.local/opt/x11tools に置く（test-daemon.sh が拾う）。
install_x11tools() {
  local x="$HOME/.local/opt/x11tools"
  if command -v xdotool >/dev/null 2>&1 || [[ -x "$x/usr/bin/xdotool" ]]; then
    return 0
  fi
  info "xdotool がイメージに無いので、deb を展開して $x に置く（再ビルドすれば不要）"
  local a="$HOME/.local/opt/apt"
  mkdir -p "$a/lists/partial" "$a/cache/archives/partial" "$x"
  local opts=(-o "Dir::State::Lists=$a/lists" -o "Dir::Cache=$a/cache"
              -o "Dir::State::status=/var/lib/dpkg/status" -o "Debug::NoLocking=1")
  if (cd "$a" && apt-get "${opts[@]}" update >/dev/null 2>&1 \
      && apt-get "${opts[@]}" download xdotool libxdo3 x11-apps >/dev/null 2>&1 \
      && for d in *.deb; do dpkg -x "$d" "$x"; done); then
    info "置いた: $x/usr/bin/xdotool"
  else
    warn "xdotool の展開に失敗した（ネットワーク？）。GUI 常駐は動くが、Safe Mode の問いは押せない"
  fi
}

check_editor() {
  # ベースイメージの Unity は root がインストールしたもの。node から実行・読み取り
  # できるかをここで一度だけ確かめておく（駄目なら症状がテスト実行時の不可解な
  # エラーとして出るので、先に潰す）。
  [[ -x "$UNITY_BIN" ]] || die "Unity 本体が実行できない: $UNITY_BIN"
  [[ -r "${UNITY_PATH:-/opt/unity}/Editor/Data/Managed/UnityEngine.dll" ]] \
    || warn "Unity の Data ディレクトリが読めないかもしれない（権限を確認すること）"
}

main() {
  check_editor
  restore_license
  scaffold_project
  check_drop_dir
  install_x11tools

  if ! have_license; then
    license_hint
    info "ライセンス有効化後に .devcontainer/unity/setup.sh をもう一度実行すること"
    return 0
  fi

  warmup
}

main "$@"
