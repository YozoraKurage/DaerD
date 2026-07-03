# 設定 (Preferences)

DaerD の設定は Unity の **Edit → Preferences → Yozolab → daerD** から変更できます。設定はエディタ（`EditorPrefs`）に保存され、プロジェクトをまたいで共有されます。

## 新規トランジションの既定値

新しく作成するトランジションに適用する初期値です。ここで既定を整えておくと、毎回 Inspector で調整する手間を減らせます。

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Apply transition defaults | ON | 新規トランジションに以下の既定値を適用する |
| Has Exit Time | OFF | Exit Time を有効にするか |
| Exit Time | 0.75 | Exit Time の値 |
| Has Fixed Duration | ON | 遷移時間を秒（固定）で扱うか |
| Duration | 0.25 | 遷移時間 |
| Transition Offset | 0 | 遷移先の開始オフセット |
| Interruption Source | — | 割り込み元（None / Current / Next など） |
| Ordered Interruption | ON | 割り込みの順序評価 |
| Can Transition To Self | OFF | 自己遷移を許可するか |

## 新規ステートの既定値

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Apply state defaults | ON | 新規ステートに以下の既定値を適用する |
| Write Defaults | ON | 新規ステートの Write Defaults |
| Speed | 1 | 新規ステートの再生速度 |

## 表示

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Show transition conditions | ON | グラフ上のトランジションに条件を表示する |
| Show state badges | ON | ステートにバッジ（各種状態を示すアイコン）を表示する |

## 挙動

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Intercept double-click | OFF | `.controller` のダブルクリックを DaerD で開くように差し替える |

::: tip Intercept double-click
OFF のままでも、メニュー・右クリック・Inspector のコンテキストメニューから DaerD で開けます。標準の Animator ウィンドウを既定で使い続けたい場合は OFF のままにしておくとよいでしょう。詳しくは [クイックスタート](/guide/getting-started) を参照してください。
:::
