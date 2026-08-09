# コントローラー解析

DaerD はコントローラー全体を監査し、見つかった問題を一覧で提示します。多くの問題は**ワンクリックで修正**できます。

## 開く

- [ホーム画面](/guide/home) の **Analyzer** ツールカード
- ツールバーの **Analyze** ボタン
- Unity メニューの **YozoLab → DaerD Analyzer**（独立ウィンドウ）

ホーム画面のカード右上の **Window** ボタン、または上記のメニューから独立ウィンドウとして開けば、**グラフを編集しながら結果を見続けられます**。

結果は Error / Warning / Info でフィルタでき、**Copy** でレポート全文をクリップボードへコピーできます。**Ping** を押すと、その問題の場所（レイヤー・ステート・パラメータ）へ移動します。

## 検出される項目

| カテゴリ | 深刻度 | 内容 | 修正 |
| --- | --- | --- | --- |
| **Unused Parameter** | Info | どこからも参照されていないパラメータ | Delete |
| **Invalid Condition** | Error | 存在しないパラメータを参照する条件、または型に対して無効なモードの条件 | — |
| **Duplicate Condition** | Info | 同じトランジションに重複した条件 | Fix |
| **Dead Transition** | Warning | 実質的に成立し得ない（デッド）トランジション | Delete |
| **Unreachable State** | Warning | 入ってくる遷移がなく、デフォルトステートでもないステート | — |
| **Duplicate Name** | Warning | 同一ステートマシン内で重複しているステート名 | — |
| **WriteDefaults** | Warning | 1 つのレイヤー内で Write Defaults の ON / OFF が混在している | — |
| **Missing Motion** | Warning / **Error** | モーション未設定のステート、または空の BlendTree スロット。**Write Defaults OFF のステートは Error** | Fill |
| **Empty Layer** | Info | ステートを 1 つも持たないレイヤー |  — |
| **Layer Weight** | Info | 既定ウェイトが 0 のレイヤー（Base レイヤーを除く） | — |
| **Missing Behaviour** | Error | スクリプトが失われた（null の）StateMachineBehaviour | Fix |
| **Direct Blend Tree** | Error / Warning | [DBT ガジェット](/features/dbt-gadgets)の健全性 | Fix |
| **Terminal States** | Info | 一度入ると外へ抜けられないステートの集合 | — |
| **VRC Parameters** | Error / Info | [パラメータストア](/features/vrchat)との不整合 | — |
| **Clip Bindings** | Warning | ヒエラルキーに存在しないパスを指すクリップのバインディング | — |

### 補足

**Missing Motion が Error になる条件** — Write Defaults OFF のステートにモーションが無いと、そのステートは何もサンプルせず何も書き戻しません。結果としてアニメーションされている全プロパティが**直前の値で固まります**。見た目の穴ではなく実際の不具合なので Error 扱いです。

**Terminal States** — グラフを強連結成分に分解し、「入れるが二度と出られない」まとまりを検出します（レイヤーのデフォルトステートを含むまとまりは、そのレイヤーの主ループなので除外されます）。意図しない行き止まりの発見に役立ちます。

**Direct Blend Tree** — Direct BlendTree を再生するステートの Write Defaults が OFF（Error、修正可）、子にウェイトパラメータが未設定（Warning）、ウェイトパラメータが存在しない（Error）／Float でない（Warning）を検査します。

**Layer Weight** — ランタイムでウェイトを上げる設計は珍しくないため、Warning ではなく Info です。Base レイヤーはランタイムで常にウェイト 1 になるので対象外です。

**Empty Layer** — 同期レイヤー（Synced Layer）は元レイヤーのステートを映すだけで自前のステートマシンを持たないため、対象外です。

## Fill 修正と Empty クリップ

**Fill** による修正は、コントローラーに [Empty クリップ](/features/clips#empty-clip)が設定されているときだけ提供されます。設定しておくと、モーション未設定のステートや空の BlendTree スロットを一括で埋められます。

## パラメータの使用箇所

未使用パラメータの判定では、次の参照をすべて「使用中」として数えます。

- トランジションの条件
- BlendTree のブレンドパラメータ（X / Y / Direct）
- ステートの Speed / Time / CycleOffset / Mirror パラメータ

このため、BlendTree やステートの各種パラメータで使っているだけのパラメータが、誤って「未使用」と判定されることはありません。

## 一括修正

[ホーム画面](/guide/home) の Controller カードからは、コントローラー全体に対する **Write Defaults の一括設定**を行えます。

- すべてのステートの Write Defaults をまとめて ON / OFF に揃えます。
- **OFF に揃える場合でも、Direct BlendTree だけで構成されたレイヤーは ON のまま保たれます**（Direct BlendTree は WD ON が前提のためです）。

## 関連機能

- [クリーンアップ](/features/cleanup) — 参照されなくなったサブアセットの削除
- [クリップとリパス](/features/clips) — 壊れたバインディングの修正
- [パラメータ型の自動変換](/features/parameter-conversion)
- [VRC / NDMF 連携](/features/vrchat)
