# VRC / NDMF 連携

DaerD は VRChat アバター向けの機能を備えていますが、**VRChat SDK が無くてもコンパイル・動作します**（該当する機能が非表示になるだけです）。汎用の AnimatorController エディタとしてそのまま使えます。

## パラメータストア

「このコントローラーのエクスプレッションパラメータを宣言しているもの」を、コントローラーに**明示的に割り当て**ます。対応するのは 2 種類です。

| 種類 | 用途 |
| --- | --- |
| **VRC Params** — `VRCExpressionParameters` アセット | アバター本体のワークフロー |
| **MA Params** — Modular Avatar の **MA Parameters** コンポーネント | NDMF / プレハブギミックのワークフロー |

::: warning 自動検出は明示的な操作のときだけ
**Detect** ボタンを押したときにのみ、しかも**完全一致した場合だけ**検出します。「シーンにあるアバターが 1 体だからそれ」といった推測はしません。ギミック用の Animator で誤爆しないためです。
:::

### ビット予算の表示

パラメータパネル上部に、ストアが使っている同期ビット数が表示されます（Bool = 1、Int / Float = 8）。

- `VRC Params: 96 / 256 bit` のように容量つきで表示されます。
- MA Parameters コンポーネントはアバター全体の合計に寄与するため、DaerD からは容量が見えません。使用量のみの表示になります。

### ストアの操作

| 操作 | 内容 |
| --- | --- |
| **Add All** | ストアにまだ無いコントローラーパラメータ（Trigger を除く）を、**同期なし・保存なし**の行として一括追加します。MA Parameters は先に宣言しないと何も使えないため、プレハブギミックの出発点になります |
| **Sync** | ストアをコントローラーのパラメータ一覧に合わせます（**差分プレビュー付き**） |
| **S** / **D** トグル | 各パラメータの Synced（同期＝ビットを消費）/ Saved（ワールドをまたいで保存）を切り替えます |
| **+** | その 1 行をストアへ追加します |

## VRChat 標準パラメータ

パラメータパネルの **Add** メニューに **VRChat** サブメニューがあります。

- **Add All Missing (N)** — まだ無い標準パラメータをまとめて追加します。
- カテゴリ別に 1 つずつ追加することもできます。
- **既にあるものはチェック付きの選択不可項目**として表示されるため、「このコントローラーはどの標準パラメータを持っているか」の確認にも使えます。

## Expressions Menu エディタ

コントローラーに割り当てた VRC Expressions Menu を、DaerD の中で編集できます（[ホーム画面](/guide/home) → **Open Menu Editor**）。

- パンくずでメニューツリーをたどれます。
- コントロールの編集ができます。
- 参照しているパラメータがコントローラーに無い場合などを警告します。

パラメータをリネームすると、メニュー側の参照も追従します（[カスケードリネーム](/features/rename)）。

## StateMachineBehaviour（VRC Behaviour）

インスペクターは主要な VRC Behaviour を**ネイティブのインスペクターと同じ見た目**で描画します。

- `VRCAvatarParameterDriver`
- `VRCAnimatorTrackingControl`
- `VRCAnimatorPlayAudio`
- `VRCAnimatorLocomotionControl`
- `VRCAnimatorLayerControl` / `VRCPlayableLayerControl`
- `VRCAnimatorTemporaryPoseSpace`

### コピー＆ペースト {#behaviour-copy-paste}

インスペクターで Behaviour のタイトルをクリックすると選択状態になり（複数選択可）、`Ctrl+C` / `Ctrl+V` で**選択した Behaviour だけ**をステート間でコピーできます。グラフの右クリックメニューからは **Behaviours → Copy From This State** / **Paste (Append)** / **Paste (Replace)** も使えます。

### 複数ステートの一括編集

ステートを複数選択すると、Behaviour は**種類 + インスタンス名**で束ねて表示されます。編集は、同じ Behaviour を持つすべてのステートへ反映されます。未所持のステートへの一括追加・全削除・全ペーストもできます。

## PhysBone パラメータの一括リネーム

PhysBone / Contact のパラメータは、接頭辞を共有する兄弟パラメータの一族（`_IsGrabbed`、`_Angle`、`_Stretch` …）を成します。1 つをリネームすると、DaerD は残りの兄弟も揃えてリネームするか尋ねます。

## 解析での VRC チェック

[コントローラー解析](/features/analysis) は、パラメータストアが割り当てられているとき次も検査します。

| 内容 | 深刻度 |
| --- | --- |
| 同期ビットが容量を超えている | Error |
| エクスプレッションパラメータに対応するコントローラーパラメータが無い（同期されているもののみ） | Info |
| エクスプレッションパラメータとコントローラーパラメータの**型が違う** | Info |

型違いはエラーではありません。VRChat はあらゆる組み合わせを変換します（parameter mismatching）。意図した変換なのかを確認できるよう Info として表示しています。

## 関連機能

- [巡回同期](/features/async-sync) — 同期ビットの節約
- [オブジェクトトグル](/features/object-toggle)
- [DBT ガジェット](/features/dbt-gadgets)
