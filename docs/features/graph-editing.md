# グラフ編集

DaerD のグラフビューは、Unity の GraphView をベースにした編集画面です。ステート・トランジション・サブステートマシン・BlendTree を扱いやすく編集できます。

## ノードの種類

グラフには次のようなノードが表示されます。

- **ステート** — 通常のアニメーションステート。
- **サブステートマシン** — 入れ子になったステートマシン。ダブルクリックで中に入れます。
- **特殊ノード** — Entry / Exit / Any State などの特別なノード。
- **BlendTree ステート** — BlendTree を持つステート。ダブルクリックで [BlendTree ビュー](/features/blendtree) に入ります。
- **フレーム / メモ** — グループ枠と付箋（[フレームとメモ](/features/frames)）。

## ステートを作る

グラフの空白部分を右クリック、またはノード検索メニューから目的のノードを選んで追加します。新規ステートには [設定](/guide/settings) の既定値（Write Defaults・Speed）と、コントローラーの [Empty クリップ](/features/clips#empty-clip)が適用されます。

## トランジションをつなぐ

ステートのポートからドラッグして接続先のノードへつなぐと、トランジションが作成されます。新規トランジションには [設定](/guide/settings) の既定値（Exit Time・Duration・Interruption など）が適用されます。

グラフ上には各トランジションの条件を表示でき（[設定](/guide/settings) の *Condition Labels On Edges*）、遷移の内容をひと目で把握できます。

### 一括生成（Connect States）

複数のステートを選択して右クリック → **Connect States** から、まとめてトランジションを張れます。

| 項目 | 内容 |
| --- | --- |
| **Chain in click order** | 選択した順に数珠つなぎ |
| **'X' → the other N selected** | 1 つのステートから他のすべてへ |
| **The other N selected → 'X'** | 他のすべてから 1 つのステートへ |
| **Step 1 / Step 2（マーク方式）** | 選択を「送り元」としてマークし、別の選択を「送り先」にして総当たりで接続 |

さらに **Using the copied Transition as a template**（Seeded）を選ぶと、[コピー済みのトランジション](/features/transitions)を雛形として設定・条件ごと一括生成します。

## ショートカット

| キー | 動作 |
| --- | --- |
| `Ctrl+C` / `Ctrl+V` | 選択のコピー / 貼り付け（ステート・フレーム・メモ） |
| `Ctrl+Shift+V` | コピーしたトランジションを新規トランジションとして貼り付け |
| `Ctrl+D` | 選択ステートをその場に複製 |
| `Ctrl+A` | すべてのノードを選択 |
| `Ctrl+Shift+A` | すべてのトランジションを選択 |
| `I` / `O` / `P` | 選択ステートの **入って来る / 出て行く / 全接続**トランジションを選択 |
| `F2` | 選択ステートの名前を変更（フレームの改題・メモの編集も同じキー） |
| `Ctrl+F2` | 選択ステートの **AnimationClip 名**を変更（[カスケードリネーム](/features/rename)） |
| `Shift + スクロール` | 表示中のレイヤーを上下に切り替え（[Home](/guide/home) とも連続） |

## ステートを複製する

ステートを選択して `Ctrl+D` で、その場に複製できます。複製ではステートの設定に加え、そのステートに関わるトランジションもコピーされます。名前は元のステートと衝突しないよう自動で調整されます。

レイヤーやコントローラーをまたぐコピーについては [レイヤー操作](/features/layers#state-copy-paste) を参照してください。

## ステート検索

ツールバーの検索欄から、**ステート名・モーション名**で全レイヤーを横断検索できます。サブステートマシンや BlendTree ステートも対象で、結果を選ぶとそのレイヤーと階層が開いて対象がフレームされます。

## 整列とレイアウト

右クリックメニューの **Layout** から。

- **Align horizontal / vertical** — 選択ノードを同じ行 / 列に揃える
- **Distribute horizontal / vertical** — 等間隔に整列
- **Frame All** — グラフ全体を画面に収める

## サブステートマシンへの出し入れ

右クリックメニューの **Sub-State Machine** から。

- **Pack Selected States** — 選択したステートをサブステートマシンにまとめる
- **Unpack Into Parent** — サブステートマシンの中身を親へ展開する

## 階層をたどる

サブステートマシンや BlendTree に入ると、上部のパンくず（ブレッドクラム）に現在の階層が表示されます。パンくずのクリックで上の階層へ戻れます。

## バッジ表示

ステートには状態を示すバッジを表示できます（[設定](/guide/settings) の *State Badges*）。**WD** は Write Defaults が ON、**B** は StateMachineBehaviour を持つことを示します。

## そのほかのメニュー

- **Set as Default State** — 選択ステートをデフォルトステートにする
- **Clip Loop Time → On / Off** — 選択ステートのクリップの Loop Time を一括切り替え
- **Behaviours → Copy / Paste (Append) / Paste (Replace)** — [Behaviour のコピー＆ペースト](/features/vrchat#behaviour-copy-paste)
- **Disconnect All** — そのステートの接続をすべて外す

## 複数コントローラーのタブ表示

複数のコントローラーを開くとタブとして並び、クリックで切り替えられます。タブごとに最後に開いていたレイヤー（および [Home](/guide/home) を選んでいたかどうか）が記憶され、ドメインリロードをまたいでも保持されます。詳しくは [クイックスタート](/guide/getting-started) を参照してください。
