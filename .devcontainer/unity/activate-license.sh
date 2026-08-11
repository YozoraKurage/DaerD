#!/usr/bin/env bash
# Unity Personal ライセンスの手動アクティベーション。
#
#   activate-license.sh --login               Unity ID でログインして有効化する
#   activate-license.sh --create-alf          .alf を生成する
#   activate-license.sh --install <file.ulf>  .ulf を取り込む
#   activate-license.sh --return              シートを返却する
#   activate-license.sh --status              現在の状態を表示する
#
# batchmode でもライセンスは必須。--login が通ればそれが一番早い。2 要素認証を
# 使っている場合は通らないので、.alf → ブラウザ → .ulf の手動経路を使う。

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

readonly DROP_DIR="$PACKAGE_ROOT/temp~"

usage() {
  sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

cmd_status() {
  restore_license
  if have_license; then
    local current
    current="$(license_file)"
    info "ライセンス有効: $current"
    # .ulf は XML。有効期限だけ拾って出す。
    local expiry
    expiry="$(grep -o 'Expires Value="[^"]*"' "$current" | head -1 | cut -d'"' -f2 || true)"
    [[ -n "$expiry" ]] && info "有効期限: $expiry"
    [[ -s "$LICENSE_STORE_FILE" ]] \
      && info "再ビルド用の控え: $LICENSE_STORE_FILE" \
      || warn "控えが $LICENSE_STORE_FILE に無い。再ビルドすると消える"
  else
    warn "ライセンス未設定"
    license_hint
    return 1
  fi
}

cmd_login() {
  # 資格情報は環境変数からのみ受け取る。引数で渡すと ps とシェル履歴に残る。
  [[ -n "${UNITY_EMAIL:-}"    ]] || die "UNITY_EMAIL が未設定"
  [[ -n "${UNITY_PASSWORD:-}" ]] || die "UNITY_PASSWORD が未設定"

  local log="${TMPDIR:-/tmp}/unity-login.log"
  local args=(-nographics -logFile "$log" -quit
              -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD")
  # シリアルを渡さなければ Personal として有効化される。
  [[ -n "${UNITY_SERIAL:-}" ]] && args+=(-serial "$UNITY_SERIAL")

  info "Unity ID でアクティベート中…"
  "$UNITY_EDITOR" "${args[@]}" || true

  if ! have_license; then
    # ログインに失敗した理由（2FA 等）はログの該当行だけ見せる。パスワードは
    # ログに出ないが、念のため全文は流さない。
    grep -iE 'licen[cs]|activat|token|2fa|two.factor|invalid|denied' "$log" \
      | tail -15 >&2 || true
    die "アクティベートできなかった（ログ: $log）。2 要素認証を使っているなら --create-alf の経路へ"
  fi

  store_license
  info "ライセンスを有効化した: $(license_file)"
  info "コンテナを捨てる前に activate-license.sh --return でシートを返すこと"
}

cmd_return() {
  have_license || die "返却するライセンスが無い"
  [[ -n "${UNITY_EMAIL:-}" && -n "${UNITY_PASSWORD:-}" ]] \
    || die "返却にも UNITY_EMAIL / UNITY_PASSWORD が要る"

  local log="${TMPDIR:-/tmp}/unity-return.log"
  info "シートを返却中…"
  "$UNITY_EDITOR" -nographics -logFile "$log" -quit \
    -username "$UNITY_EMAIL" -password "$UNITY_PASSWORD" -returnlicense || true

  rm -f "$LICENSE_SYSTEM_FILE" "$LICENSE_USER_FILE" "$LICENSE_STORE_FILE"
  info "返却した（ログ: $log）"
}

cmd_create_alf() {
  mkdir -p "$DROP_DIR"
  local log="$DROP_DIR/create-alf.log"

  info "アクティベーションファイル (.alf) を生成中…"
  # ライセンスが無い状態で走らせるので Unity は非ゼロで終わる。成否はファイルの
  # 有無で判定する。
  ( cd "$DROP_DIR" && "$UNITY_EDITOR" -nographics -logFile "$log" -createManualActivationFile -quit ) || true

  local alf
  alf="$(find "$DROP_DIR" -maxdepth 1 -name 'Unity_v*.alf' -newer "$log" -print -quit 2>/dev/null || true)"
  [[ -z "$alf" ]] && alf="$(find "$DROP_DIR" -maxdepth 1 -name 'Unity_v*.alf' -print -quit)"
  [[ -z "$alf" ]] && { tail -30 "$log" >&2; die ".alf を生成できなかった（ログ: $log）"; }

  cat <<EOS

$(info ".alf を生成した: $alf")

次の手順:
  1. ホスト側の temp~/ フォルダを開き、$(basename "$alf") を取り出す
  2. https://license.unity3d.com/manual にアップロード
     → ライセンス種別で "Unity Personal Edition" を選ぶ
  3. 落ちてきた Unity_v2022.x.ulf を temp~/ に置く
  4. .devcontainer/unity/activate-license.sh --install temp~/Unity_v2022.x.ulf

EOS
}

cmd_install() {
  local ulf="$1"
  [[ -f "$ulf" ]] || die "$ulf が見つからない"
  grep -q '<License' "$ulf" || die "$ulf は .ulf ライセンスファイルではなさそう"

  local log="${TMPDIR:-/tmp}/unity-license-install.log"
  info "ライセンスを取り込み中…"
  "$UNITY_EDITOR" -nographics -logFile "$log" -manualLicenseFile "$(realpath "$ulf")" -quit || true

  # -manualLicenseFile が所定の場所に置いてくれないバージョンもあるので、
  # 置かれていなければ自分でコピーする。
  if ! have_license; then
    mkdir -p "$(dirname "$LICENSE_SYSTEM_FILE")"
    cp "$ulf" "$LICENSE_SYSTEM_FILE"
  fi
  have_license || { tail -30 "$log" >&2; die "ライセンスの取り込みに失敗した（ログ: $log）"; }

  # 再ビルドで消えないようボリュームにも控えを取る。machine-id はイメージで
  # 固定されているので、同じ .ulf がそのまま通る。
  store_license

  info "ライセンスを有効化した"
  info "続けて .devcontainer/unity/setup.sh を実行するとテストプロジェクトが用意される"
}

case "${1:-}" in
  --login)      cmd_login ;;
  --return)     cmd_return ;;
  --create-alf) cmd_create_alf ;;
  --install)    [[ $# -ge 2 ]] || usage 1; cmd_install "$2" ;;
  --status|"")  cmd_status ;;
  -h|--help)    usage ;;
  *)            usage 1 ;;
esac
