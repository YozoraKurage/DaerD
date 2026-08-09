# BlendTree 編集

BlendTree を持つステートは、グラフ上でダブルクリックすると **BlendTree 専用ビュー**に入ります。ツリー構造を視覚的に確認しながら編集できます。

## ネストした BlendTree

BlendTree は入れ子にできます。DaerD では、ネストした BlendTree にドリルダウンして編集でき、現在どの階層にいるかは上部のパンくず（ブレッドクラム）で確認できます。パンくずのクリックで上位の BlendTree やステートへ戻れます。

## ブレンドパラメータ

BlendTree のブレンドに使うパラメータ（1D の X、2D の X / Y、Direct の各パラメータ）は DaerD が把握しており、次の機能と連動します。

- [パラメータ型の自動変換](/features/parameter-conversion) — 型変更時に BlendTree のブレンドパラメータも考慮。
- [カスケードリネーム](/features/rename) — パラメータ名の変更を BlendTree にも反映。
- [コントローラー解析](/features/analysis) — BlendTree で使われているパラメータを「使用中」として認識。

## テンプレートとリマップ

BlendTree ビューの右クリックメニューから、サブツリーを再利用できます。

| 項目 | 内容 |
| --- | --- |
| **Save as Template** | サブツリーを、参照する Float パラメータごと 1 つの `.asset` として保存します（クリップは参照のまま） |
| **Import Template** | 保存したテンプレートを取り込みます。取り込み時に各パラメータを既存のものへ結線するか、新しい名前で作るかを選べます |
| **Remap Parameters** | このサブツリーが使っているパラメータを、まとめて別のパラメータへ差し替えます（`Keep` を選べばそのまま） |

::: tip サブメニューへの整理
テンプレートのアセット名に `.` を含めると、メニューが入れ子になります。
:::

## Direct BlendTree

Direct BlendTree（各子モーションを個別のパラメータで直接制御するタイプ）にも対応しています。

Direct BlendTree は、単なるブレンド手段以上のことができます。**Write Defaults ON のステートに置いた Direct BlendTree は毎フレーム評価される計算器**として機能し、DaerD はこれを [DBT ガジェット](/features/dbt-gadgets)として自動生成できます（加算・乗算・スムーズ・三角関数・LUT など）。

レイヤー内のステートがすべて Direct BlendTree で構成されている場合、そのレイヤーはレイヤー一覧に **DBT** バッジが付き、[解析](/features/analysis)の Write Defaults 一括設定でも特別扱いされます（OFF に揃えても ON のまま保たれます）。

## 関連機能

- [グラフ編集](/features/graph-editing)
- [DBT ガジェット](/features/dbt-gadgets)
- [オブジェクトトグル](/features/object-toggle)
- [コントローラー解析](/features/analysis)
