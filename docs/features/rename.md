# カスケードリネーム

名前の変更は、標準の Animator では「参照している側」を壊しがちな操作です。DaerD では、変更を関連箇所へ**波及（カスケード）**させることで、リネームを安全に行えます。

## パラメータ名のリネーム

パラメータパネルで名前を書き換えると、DaerD はそのパラメータを参照している箇所を追従して更新します。対象には次が含まれます。

- トランジションの条件
- BlendTree のブレンドパラメータ（Direct BlendTree の子ウェイト、同期レイヤーのオーバーライドツリーを含む）
- ステートの Speed / Time / CycleOffset / Mirror パラメータ
- **VRC Parameter Driver のエントリ**
- [パラメータストア](/features/vrchat)（VRC Expression Parameters / MA Parameters）の該当行
- VRC Expressions Menu からの参照

Unity の標準エディタはこれらを追従しません。リネーム後に「条件が古い名前を参照したまま壊れる」ことを防げます。

## PhysBone / Contact の一族リネーム

VRC PhysBone や Contact は、1 つの接頭辞を共有する自動生成パラメータの一族を作ります（`Tail_IsGrabbed`、`Tail_Angle`、`Tail_Stretch`、`Tail_Squish`、`Tail_IsPosed`）。

このうち 1 つをリネームすると、DaerD は同じ接頭辞を持つ兄弟を検出して「**まとめてリネームしますか？**」と尋ねます。**Rename All** で一族すべての接頭辞が揃います。

## AnimationClip 名のリネーム

ステートが参照する AnimationClip の名前を変更すると（インスペクター、または `Ctrl+F2`）、DaerD は状況に応じて実体まで更新します。

| クリップの実体 | 動作 |
| --- | --- |
| 独立した `.anim` アセット | ディスク上のアセット名もリネームします |
| 他アセットに埋め込まれたサブアセット | アセット内のオブジェクト名を更新します |
| メモリ上にしか無いクリップ | オブジェクト名を更新します |
| モデル（FBX 等）からインポートされたクリップ | **読み取り専用**のため変更できません。インポーター側で変更してください |

ステート名とクリップ名を揃えて管理したい場合に役立ちます。

## パラメータの削除

パラメータパネルからは、**Delete and Clean** で「そのパラメータを削除し、参照していた条件と Parameter Driver のエントリもすべて取り除く」削除ができます。壊れた条件を残さずに整理できます。

## 関連機能

- [パラメータ型の自動変換](/features/parameter-conversion) — 名前ではなく型を変えたときの追従。
- [コントローラー解析](/features/analysis) — 重複名や未使用パラメータの検出。
- [VRC / NDMF 連携](/features/vrchat) — パラメータストアとの同期。
