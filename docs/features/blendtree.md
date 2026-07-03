# BlendTree 編集

BlendTree を持つステートは、グラフ上でダブルクリックすると **BlendTree 専用ビュー**に入ります。ツリー構造を視覚的に確認しながら編集できます。

## ネストした BlendTree

BlendTree は入れ子にできます。DaerD では、ネストした BlendTree にドリルダウンして編集でき、現在どの階層にいるかは上部のパンくず（ブレッドクラム）で確認できます。パンくずのクリックで上位の BlendTree やステートへ戻れます。

## ブレンドパラメータ

BlendTree のブレンドに使うパラメータ（1D の X、2D の X / Y、Direct の各パラメータ）は DaerD が把握しており、次の機能と連動します。

- [パラメータ型の自動変換](/features/parameter-conversion) — 型変更時に BlendTree のブレンドパラメータも考慮。
- [カスケードリネーム](/features/rename) — パラメータ名の変更を BlendTree にも反映。
- [コントローラー解析](/features/analysis) — BlendTree で使われているパラメータを「使用中」として認識。

## Direct BlendTree

Direct BlendTree（各子モーションを個別のパラメータで直接制御するタイプ）にも対応しています。解析では、レイヤー内のステートがすべて Direct BlendTree のみで構成されているかどうかも判定に利用されます（[コントローラー解析](/features/analysis)）。

## 関連機能

- [グラフ編集](/features/graph-editing)
- [コントローラー解析](/features/analysis)
