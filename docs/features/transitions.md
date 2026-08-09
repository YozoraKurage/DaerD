# トランジションのコピー＆ペースト

似たようなトランジションを何度も作るとき、標準の Animator では設定や条件を毎回手で入力する必要がありました。DaerD ではトランジションの**設定と条件をまとめてコピー＆ペースト**できます。

## コピーできる内容

トランジションのスナップショットには、次の情報が含まれます。

- 遷移の各種設定（Exit Time / Duration / Offset / Interruption など）
- 条件リスト（パラメータ・モード・しきい値）
- コピー元・コピー先のコンテキスト（どのノードから／どのノードへ向かう遷移だったか。ステート / サブステートマシン / Any State / Entry / Exit の区別）

コピー先のコンテキストまで覚えているため、単に設定を貼り付けるだけでなく、遷移そのものを別の場所へ再現するような使い方もしやすくなっています。

ステートを右クリックすると **Paste Transition → This State → Original Destinations** / **Original Sources → This State** が選べます。

## ドメインリロードをまたいでも保持

コピーした内容はセッション単位のクリップボードに保持され、**スクリプトの再コンパイルや Play モード切り替え（ドメインリロード）をまたいでも失われません**。作業の途中でコンパイルが走っても、コピーした内容をそのまま貼り付けられます。

## 条件だけの適用

既存のトランジションに対して、コピーしたスナップショットの設定を上書き適用することもできます。その際、遷移先はそのまま保ち、条件を含めるかどうかを選べます。「条件だけ揃えたい」「設定だけ揃えたい」といった調整がしやすくなっています。

## 複数トランジションの一括編集

トランジションを複数選択すると、インスペクターが一括編集モードになります。

- **Common Settings** — Mute / Solo / Has Exit Time / Exit Time / Fixed Duration / Duration / Offset / Interruption / Ordered Interruption / Can Transition To Self を、選択したすべてへ適用します。値が揃っていない項目は混在として表示されます。
- **Shared Conditions** — 選択したトランジションが共通して持つ条件を、まとめて編集できます。
- **Add The Same Condition To Every Selected Transition** — 同じ条件を選択全体へ追加します。
- **Paste Copied Transition Onto All N Selected** — コピー済みのトランジションを選択全体へ貼り付けます。

::: tip 選択のショートカット
ステートを選んで `I` / `O` / `P` を押すと、そのステートの**入って来る / 出て行く / 全接続**トランジションが選択されます。`Ctrl+Shift+A` でレイヤー内のすべてのトランジションを選択できます。
:::

## 雛形としての一括生成（Seeded）

コピーしたトランジションは、**新しく張るトランジションの雛形**としても使えます。複数のステートを選択して右クリック → **Connect States → Using the copied Transition as a template** から、設定と条件を引き継いだトランジションを一括生成できます（[グラフ編集](/features/graph-editing#一括生成-connect-states)）。

`Ctrl+Shift+V` は、コピーしたトランジションを新規トランジションとして貼り付けます。

## 関連機能

- [グラフ編集](/features/graph-editing) — トランジションの作成と表示。
- [ステートの複製](/features/graph-editing#ステートを複製する) — ステートに紐づくトランジションごと複製する。
- [パラメータ型の自動変換](/features/parameter-conversion) — 条件の一括書き換え。
