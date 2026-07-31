# DaerD (DD,ディーディー)

For animator controllers

GraphView ベースの AnimatorController エディタ。パラメーターの自動型変換、
トランジションのコピー＆ペースト、カスケードリネーム、コントローラー解析などを備えます。

## 主な機能

- ステートマシン / ブレンドツリーのグラフ編集（タブで複数コントローラーを開ける）
- Shift + スクロール — 表示中のレイヤーを上下に切り替え
- ステート検索 — ツールバーの検索欄からステート名・モーション名で全レイヤーを横断検索してジャンプ
- 解析 (Analyze) — 未使用パラメーター、壊れた条件、発火しない遷移、到達不能ステート、
  モーション未設定、Write Defaults 混在などを検出。多くの問題はワンクリックで修正可能
- クリーンアップ — 参照している AnimationClip を使用ステートつきで一覧表示（ステートへジャンプ・一括差し替え可能）。
  .controller 内に残った未参照のサブアセット（ゴミ）を検出してワンクリックで削除
- Empty クリップ — コントローラーごとにプレースホルダー用クリップを指定すると、新規ステートに自動で設定され、
  解析の「穴埋め」修正でモーション未設定のステートや空のブレンドツリースロットを一括補完
- 日本語 / English 表示切り替え（Preferences > Yozolab > daerD、既定はシステム言語に追従）

# Vibe Coding

## ToS

If I quit, MIT license will be removed from relevant products.

... Though this might change depending on how I feel :)
