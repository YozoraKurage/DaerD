# 設定 (Preferences)

DaerD の設定は Unity の **Edit → Preferences → Yozolab → daerD** から変更できます。設定はエディタ（`EditorPrefs`）に保存され、プロジェクトをまたいで共有されます。

いちばん下の **Reset To Defaults** ですべて既定値に戻せます。

## 表示言語

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Language | Auto (System Language) | DaerD のウィンドウと解析結果の表示言語。`Auto` / `English` / `日本語` |

翻訳は gettext の `.po` 形式で `Editor/Localization/<言語コード>.po` に格納されています（Poedit などで編集できます）。未翻訳の項目は英語のまま表示され、`.po` を保存すると**開いているウィンドウに即座に反映**されます。

## 新規トランジションの既定値 (New Transition Defaults)

新しく作成するトランジションに適用する初期値です。ここで既定を整えておくと、毎回 Inspector で調整する手間を減らせます。

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Apply To New Transitions | ON | 新規トランジションに以下の既定値を適用する |
| Has Exit Time | OFF | Exit Time を有効にするか |
| Exit Time | 0.75 | Exit Time の値 |
| Fixed Duration | ON | 遷移時間を秒（固定）で扱うか |
| Duration | 0.25 | 遷移時間 |
| Offset | 0 | 遷移先の開始オフセット |
| Interruption | None | 割り込み元（None / Current / Next など） |
| Ordered Interruption | ON | 割り込みの順序評価 |
| Can Transition To Self | OFF | 自己遷移を許可するか |

## 新規ステートの既定値 (New State Defaults)

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Apply To New States | ON | 新規ステートに以下の既定値を適用する |
| Write Defaults | ON | 新規ステートの Write Defaults |
| Speed | 1 | 新規ステートの再生速度 |

::: tip Empty クリップ
新規ステートのモーションは設定ではなく**コントローラーごと**に決まります。[ホーム画面](/guide/home) で Empty クリップを指定しておくと、新規ステートに自動で設定されます（[詳細](/features/clips#empty-clip)）。
:::

## グラフ表示 (Graph Display)

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Condition Labels On Edges | ON | トランジションに条件の 1 行要約を表示する（次回のグラフ再構築から反映） |
| State Badges (WD / B) | ON | Write Defaults が ON のステートと、StateMachineBehaviour を持つステートに印を付ける |

## 挙動 (Behavior)

| 設定 | 既定値 | 説明 |
| --- | --- | --- |
| Intercept .controller Double-Click | OFF | `.controller` のダブルクリックを DaerD で開くように差し替える |

::: tip Intercept .controller Double-Click
OFF のままでも、メニュー・右クリック・Inspector のコンテキストメニューから DaerD で開けます。標準の Animator ウィンドウを既定で使い続けたい場合は OFF のままにしておくとよいでしょう。詳しくは [クイックスタート](/guide/getting-started) を参照してください。
:::
