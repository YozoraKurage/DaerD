# DaerD 機能拡張仕様書 — YGDR Animator Editor 機能アイデアの取り込み

対象バージョン: 0.9 以降(フェーズ分けは末尾)
ステータス: **実装済み(0.8.1 ブランチ)** — §1〜§10 を実装。既知の仕様差分:
§4 のマルチステート選択時のインスタンス名マッチング編集は未実装(Behaviour の
ステート間コピー & ペーストで代替)、§9.2 の条件クエリ選択・Redirect/Replicate 専用モードは
既存のトランジションコピー(コンテキスト付きペースト)と Seeded 生成で代替。
作成日: 2026-08-01

---

## 0. 背景と方針

[YGDR Animator Editor](https://github.com/YerGodDamnRight/YGDR-Animator-Editor)(GPLv3)の機能を調査し、
DaerD に取り込む価値のあるアイデアを仕様化する。

**ライセンス上の絶対条件**: YGDR は GPLv3 + 有償販売。**コード・シェーダー・アセットの流用は一切行わない**。
本仕様書は公開ドキュメントと挙動観察から書き起こした機能仕様であり、実装はすべて DaerD の
既存アーキテクチャ上でクリーンルームで行う。YGDR が参考にした MIT 系ツール
(hfcRed / Animation-Repathing、rrazgriz / RATS)も、参照する場合はアイデアレベルに留める。

**アーキテクチャ原則**(全機能共通):

- ロジックは `Editor/Model/`(純粋 C#、テスト可能)、UI は `Editor/Window/` / `Editor/Panels/` に分離する。
  既存の AapGadgets / AapGadgetWindow、ToggleBuilder / ToggleBuilderWindow の構成を踏襲。
- VRC SDK に**アセンブリ参照しない**。`VrcParameterDriver` と同様に型名マッチ +
  `SerializedObject` でアクセスし、SDK 不在時は機能を非表示にする。
- すべての操作は `UndoScope` で単一 Undo ステップにまとめる。ディスク上のアセット生成
  (.anim 等)のみ Undo 対象外とし、その旨を UI に明記する。
- すべての新規 UI 文字列は `L.Tr()` を通し、`DaerDLocale.cs` に日本語訳を追加する。
- 生成物(クリップ等)はコントローラーと同じディレクトリ、または明示されたサブアセットに保存する。
- 各 Model には `Tests/` に NUnit テストを追加する(インメモリ AnimatorController で検証)。

**対象外**(今回取り込まない):

- Constraint Converter(Animator 編集と無関係、需要薄)
- UI カスタマイズ系: グラフ背景変更、ノード色、パレット保存/共有、キーバインド再割当 UI
- Unity 標準ウィンドウのバグ修正群(DaerD はウィンドウ自体を置き換えるため該当せず)
- 多言語追加(EN/JA を維持。文字列は全て L.Tr 経由なので将来の追加は容易)

---

## 1. Network Sync ジェネレータ 【P1・最優先】

### 1.1 目的

VRChat では Parameter Driver やコンタクト等でローカルにのみ変化するパラメータで駆動される
レイヤーは、リモートプレイヤーの画面上で再生されない。定石パターンは
「同期パラメータ(Int 1 個または Bool×n bit)に現在ステートを書き込み、リモート側は
その値を条件にミラーステートへ遷移する」構造を手作業で組むことだが、ステート数に対して
二乗のオーダーで遷移が必要になり現実的でない。これをワンクリックで生成する。

### 1.2 エントリポイント

- レイヤーパネル各行の歯車ポップアップに「Network Sync…」ボタンを追加
- コントローラー概要(非選択時インスペクター)にもボタンを配置

### 1.3 ウィザード UI(`NetworkSyncWindow`)

| 項目 | 型 | 既定値 | 説明 |
|---|---|---|---|
| 対象レイヤー | Popup | 呼び出し元レイヤー | 同期化するレイヤー |
| 同期パラメータ名 | Text | `<レイヤー名>/Sync` | 生成する同期パラメータの名前 |
| エンコード | Popup | Int | `Int (1個・8bit)` / `Bool ×n (nbit)`。Bool 選択時は必要 bit 数 `ceil(log2(N))` と названия `<名前>/b0`〜`/bn` をプレビュー表示 |
| リモート遷移方式 | Popup | Any State | `Any State`(ミラー側に AnyState 遷移 N 本)/ `All-to-All`(ミラーステート間の総当たり遷移 N×(N-1) 本) |
| 遷移プロパティを引き継ぐ | Toggle | OFF | ON: 元レイヤーの遷移から duration 等を推定コピー。OFF: 即時遷移(exit time なし / duration 0) |
| リモートステートの接頭辞 | Text | `[Net] ` | ミラーステート名に付与 |
| Behaviour を除去 | Toggle | ON | ミラーステートから StateMachineBehaviour を除去(Driver の二重発火防止) |
| サブステートマシンに格納 | Toggle | ON | ミラー一式を `Network` サブステートマシンへ Pack(既存 `StatePacker` を利用) |
| 専用 Driver インスタンス | Toggle | ON | 同期値の書き込みを既存 Driver に追記せず、`Network` という名前の専用 VRCAvatarParameterDriver を各ステートに新設する |

### 1.4 生成内容

対象レイヤーのルートステートマシン直下のステート集合を `S[0..N-1]` とする
(サブステートマシン内は対象外。含まれる場合はバリデーションで警告し、続行可)。

1. `IsLocal`(Bool)パラメータがなければ追加。
2. 同期パラメータを追加:
   - Int モード: Int 1 個。N > 255 はエラー。
   - Bool モード: `ceil(log2(N))` 個の Bool。値は LSB-first で 2 進エンコード。
3. **ローカル側(既存ステート)**:
   - 各 `S[i]` に VRCAvatarParameterDriver(専用インスタンス設定時は名前 `Network`)を追加し、
     同期パラメータへ `i` を Set する(Bool モードでは各 bit を Set)。`localOnly = true`。
   - 既存ステートの全遷移(AnyState 遷移含む)に `IsLocal == true` 条件を**未付与の場合のみ**追加。
4. **リモート側(ミラーステート)**:
   - 各 `S[i]` の複製 `S'[i]`(接頭辞付き、motion / speed / WD / tag / cycleOffset を引き継ぎ)を
     元ノードの下方にオフセット配置。Behaviour 除去設定に従う。
   - Any State 方式: AnyState → `S'[i]` に「同期値 == i かつ IsLocal == false」+
     Can Transition To Self = OFF。
   - All-to-All 方式: すべての `S'[j]` → `S'[i]`(j ≠ i)に同条件の遷移。
5. **エントリ分岐**: Entry からの遷移を `IsLocal == true → 既定ステート` /
   `IsLocal == false → S'[既定ステートの index]` の 2 本に再構成する。
6. 格納設定 ON のとき、`S'` 一式と手順 5 のリモート側分岐先を `Network` サブステートマシンへ Pack。

### 1.5 バリデーション

- レイヤーが空、またはステート数 < 2 → エラー
- Int モードで N > 255、Bool モードで bit 数 > 8 → エラー(実用上の上限)
- 同期パラメータ名が既存かつ型不一致 → エラー。既存かつ型一致 → 再利用(警告表示)
- 対象レイヤーに既に接頭辞付きミラーステートが存在 → 「既に同期化済みの可能性」警告
- サブステートマシンを含むレイヤー → 「サブステートマシン内は同期対象外」警告

### 1.6 実装

- 新規: `Editor/Model/NetworkSyncBuilder.cs`(Request / Validate / Apply、AapGadgets 型式)
- 新規: `Editor/Window/NetworkSyncWindow.cs`
- 変更: `LayersPanel.cs`(歯車ポップアップ)、`InspectorPanel.cs`(概要ボタン)
- Driver への書き込みは `VrcParameterDriver` を拡張(エントリ追加 API を新設)。SDK 不在時は
  「VRChat SDK が見つかりません」ヘルプボックスを出し実行不可にする。
- テスト: Int/Bool エンコード、条件の網羅(全 i について値条件+IsLocal)、既存遷移への
  IsLocal 追加の冪等性、エントリ分岐、Pack 連携。

### 1.7 Analyzer 連携(任意・同フェーズ推奨)

- 新カテゴリ「Network Sync」: ミラー側と同期パラメータの整合(値条件の抜け・重複)を検査。

---

## 2. オブジェクトトグルの拡張 【P1】

現行の `ToggleBuilder`(0.8.1 で追加、`m_IsActive` のみ)を拡張する。

### 2.1 バインディング種別の追加

`ToggleBuilder.Target` を「パス + バインディング集合」に拡張する。

| 種別 | カーブ対象 | 出現条件 |
|---|---|---|
| Object | `GameObject.m_IsActive` | 常時(既定 ON) |
| Renderer | 実コンポーネント型の `m_Enabled`(MeshRenderer / SkinnedMeshRenderer 等、実型でバインド) | 対象に Renderer がある |
| Particle | `ParticleSystem.m_Enabled` | 同上 |
| Audio | `AudioSource.m_Enabled` | 同上 |
| Light | `Light.m_Enabled` | 同上 |
| PhysBone | `VRCPhysBone.m_Enabled`(型はリフレクション解決) | VRC SDK があり対象に付いている |
| BlendShape | `SkinnedMeshRenderer` の `blendShape.<名前>`、Off 値 / On 値(0–100)を個別指定 | 対象に blendshape 付き SMR がある |

- ウィザードの各行にコンポーネントチップ(トグルボタン)を表示。**シーン参照がある行のみ**
  チップを出し、手入力パス行は Object 固定とする(存在確認ができないため)。
- BlendShape チップは展開式: シェイプ行(名前 + Off/On float)、`+` で未追加シェイプの
  ドロップダウン、`−` で行削除。
- ON クリップには各バインディングの ON 値(enabled=1、blendshape=On 値)、OFF クリップには
  OFF 値を書く。行の「ON で表示」反転は全バインディングに適用。
- DBT モードでは 1D ツリー(0→OFF / 1→ON)がそのまま機能し、blendshape は Float パラメータの
  Radial メニュー(0〜1)で中間値まで滑らかに動く旨をヘルプボックスで案内する。

### 2.2 実装

- 変更: `ToggleBuilder.cs`(Target 構造の拡張、クリップ生成)、`ToggleBuilderWindow.cs`(チップ UI、
  行がシーン GameObject 参照を保持)
- テスト: 各バインディング種別のカーブ検証、blendshape Off/On 値、反転との組み合わせ。

---

## 3. VRC Expression Parameters 連携 【P1】

### 3.1 アセットアクセス層

- 新規: `Editor/Model/VrcExpressionParameters.cs`
  - `VRCExpressionParameters` アセットを型名マッチ + SerializedObject で読み書き
    (`parameters` 配列: name / valueType / saved / networkSynced / defaultValue)。
  - アセットの特定: ① シーン選択中の GameObject から `VRCAvatarDescriptor`(型名マッチ)を
    たどる ② 手動 ObjectField。両方をサポート。
  - ビット計算: Bool=1 / Int=8 / Float=8、networkSynced のみ加算。上限は SDK 定数を
    リフレクション取得し、失敗時は 256 にフォールバック。

### 3.2 パラメーターパネルへの表示

- 式パラメータアセットが解決できたとき、パネル上部に**パラメータ予算バー**
  (`使用 bit / 上限 bit`)を表示。
- 各行の右端に S(synced)/ D(saved)ミニトグルを表示(式パラメータ側に存在する場合のみ)。
  クリックでアセット側のフラグを切り替え(Undo 可)。
- コントローラーと式パラメータの**初期値は双方向リンク**: パネルで初期値を編集したら
  式パラメータ側 defaultValue も更新(逆方向はアセット監視まではせず、Sync 実行時に整合)。

### 3.3 同期(Sync)コマンド

- パラメーターパネル「Add」メニューに追加:
  - **Add to VRC Parameters** — 選択パラメータを式パラメータへ追加(既存時は無効表示)
  - **Sync VRC Parameters Asset…** — コントローラーのパラメータ一覧・順序に合わせて
    式パラメータを整列。**実行前に差分プレビューダイアログ**(追加される項目 / 削除される項目を
    チェックボックス付きで列挙、既定は全チェック)を表示し、ユーザーが項目単位で除外できる。
    ※ YGDR の「Mark for Sync」永続フラグは持たず、実行時チェックボックスで代替する。

### 3.4 リネーム / 型変換 / 削除のカスケード拡張

- `ParameterRenamer` / `ParameterConverter` / パラメータ削除に「式パラメータ・メニューも更新」
  オプションを追加(アセットが解決できたときのみ表示)。
- PhysBone / Contact の自動生成サフィックス(`_IsGrabbed`、`_Angle`、`_Stretch` 等)を検出し、
  同一プレフィックスの兄弟パラメータの一括リネームを提案するダイアログ。

### 3.5 Analyzer 新カテゴリ「VRC Parameters」

- メニューが存在しないパラメータを参照 → Warning
- 式パラメータにあるがコントローラーにない(またはその逆で synced のもの) → Info
- 型不一致(式パラメータ vs コントローラー) → Error
- 予算超過 → Error
- ※ アセットが解決できたときのみ検査を実行。

---

## 4. VRC Behaviour 対応の拡大 【P2】

現行の Tracking Control / Parameter Driver に加えて、インスペクターで以下を編集可能にする。
すべて型名マッチ + SerializedObject。SDK 不在時はセクションごと非表示。

| Behaviour | 主フィールド | 複数インスタンス |
|---|---|---|
| VRCAnimatorPlayAudio | ソースパス(AudioSource ドロップで解決)、クリップ群、再生順、音量/ピッチ範囲、ループ、enter/exit 動作、遅延 | 可 |
| VRCAnimatorLocomotionControl | 有効 / 無効の 2 択 | 単一 |
| VRCAnimatorLayerControl | Playable / レイヤー index / 目標ウェイト / ブレンド時間 | 可 |
| VRCPlayableLayerControl | Playable / 目標ウェイト / ブレンド時間 | 可 |
| VRCAnimatorTemporaryPoseSpace | Enter / Exit、遅延(秒 / 正規化) | 単一 |

- 「+ Add Behaviour」ドロップダウンをステートインスペクターに追加(単一型は既に付いている
  ステートが選択に含まれる場合リスト末尾でグレーアウト)。
- 複数インスタンス型は名前付き折りたたみ行 + ↑↓並べ替え + 個別削除 + Remove All。
  マルチステート選択時は**インスタンス名でマッチング**して共通編集する(YGDR 方式)。
- **Behaviour のコピー & ペースト**: ステートノード右クリックメニューに
  「Copy Behaviours」「Paste Behaviours(Replace / Append)」を追加。
  Driver 同士の Append は既存インスタンスへの行マージも選べる。
- 実装: `Editor/Model/VrcBehaviours.cs`(型名テーブル + 生成/複製/削除ヘルパー)、
  `InspectorPanel` の Behaviour セクションを共通描画基盤に整理。

---

## 5. VRC Expressions Menu エディタ 【P2】

- 新規: `Editor/Window/VrcMenuWindow.cs` + `Editor/Model/VrcMenuAccess.cs`(SerializedObject)。
  ClipsWindow と同様の独立ウィンドウ。コントローラー概要とパラメーターパネルから開く。
- 機能:
  - パンくずナビゲーション(Sub Menu の Open / 上位クリックで戻る)
  - コントロール一覧(並べ替え、追加 / 削除、`MAX_CONTROLS` でクランプ)
  - コントロールインスペクター: 名前 / アイコン / 型(Button・Toggle・SubMenu・
    TwoAxis・FourAxis・Radial)/ パラメータ(コントローラーのパラメータからドロップダウン +
    手入力フォールバック。**未定義・型不一致は警告アイコン**)/ 値 / SubMenu の Create ボタン /
    Puppet 系のサブパラメータと軸ラベル
  - オブジェクトトグル(§2)・Network Sync(§1)のウィザードから
    「メニューにトグルを追加」チェック(Toggle コントロールを自動生成)への導線
- Analyzer「VRC Parameters」(§3.5)がメニュー→パラメータ参照も検査する。

---

## 6. レイヤーのコピー / ペースト & レイヤーテンプレート 【P2】

### 6.1 レイヤークリップボード

- `Editor/Model/LayerClipboard.cs`: レイヤー 1 枚(設定 + ステートマシン全体 + フレーム/ノート)を
  インメモリで保持。`StateMachineCloner` + `FrameInheritance` を再利用。
- 歯車ポップアップに **Copy Layer / Paste Layer / Paste Settings Only** を追加。
- **コントローラー跨ぎ対応**: DaerD はタブで複数コントローラーを開けるため、ペースト先に
  存在しない参照パラメータを型ごと自動追加する(追加したパラメータ名を Undo 可能な
  ダイアログで報告)。
- Duplicate Layer は既存実装を Copy+Paste の合成に置き換えない(現状維持)。

### 6.2 レイヤーテンプレート

- テンプレート = 「レイヤー構造 + 使用パラメータ定義 + 生成クリップ」を格納した
  ScriptableObject アセット(`DaerDLayerTemplate`)。ユーザー指定フォルダに保存。
- 歯車ポップアップに「Save as Template…」。保存時に名前(`/` 区切りでサブメニュー化)。
- 「+ Add Layer」ボタンを、テンプレートが 1 つ以上あるときだけドロップダウン化
  (`New Layer` / テンプレート一覧 / `Delete Template/`)。
- インポート時に**パラメータリマップウィンドウ**(テンプレート内パラメータ → 既存 or 新規)を
  挟む。オブジェクトトグルのターゲットパスもここで書き換え可能にする。

---

## 7. Clip Remapper(リパスツール) 【P2】

- 新規: `Editor/Window/ClipRemapperWindow.cs` + `Editor/Model/ClipRepather.cs`。
- 機能:
  - アバター(Animator 付き GameObject)スロット → コントローラー参照クリップ全件をスキャンし、
    アバター階層に存在しないバインディングパスを列挙(壊れたパスセグメントはボタン化 →
    クリックで From 欄に自動入力)。
  - From / To パス欄(GameObject ドロップで階層パス自動入力)→ 一括書き換え。
    `AnimationUtility` でカーブを新バインディングへ移し替え、旧バインディングを削除。Undo 可。
  - 適用範囲: コントローラー全クリップ / Project で選択中のクリップのみ。
  - **Auto-Repath**(オプトイン): `ObjectChangeEvents` で Hierarchy のリネーム / 移動を監視し、
    「有効時点で正しかったバインディング」を追従更新。トグル状態はウィンドウ存続中のみ有効。
- ClipsWindow に「Remap…」導線を追加。
- Analyzer 新チェック: アバター選択時のみ「クリップのバインディング切れ」を Warning 表示。

---

## 8. ブレンドツリーテンプレート 【P2】

- ブレンドツリーグラフの右クリックに「Save as Template…」「Import Template/」を追加。
- テンプレート = サブツリーの深いコピーを格納した ScriptableObject(`DaerDBlendTreeTemplate`)。
  名前の `.` 区切りでサブメニュー化。
- インポート時にパラメータリマップウィンドウ(§6.2 と共通実装)を表示。
- 併せて「**Remap Parameter**」(選択ノード配下の blendParameter / directBlendParameter を
  一括で別 Float に付け替え)をブレンドツリーグラフの右クリックに追加。

---

## 9. パラメータ / トランジション QoL 群 【P3】

### 9.1 パラメータ

- **Remap to Parameter…** — 行の右クリック(または ? メニュー)から、全参照
  (遷移条件・ブレンドツリー・Behaviour・AAP クリップ、§3 有効時は式パラメータ/メニューも)を
  別パラメータへ付け替え。`ParameterRenamer` の走査系を共通化。
- **Delete and Clean** — 参照ごと安全に削除(条件行の除去 → パラメータ削除)。
- **行の複製 / コピー / ペースト** — 右クリックメニュー。コントローラー跨ぎ可。
  名前衝突時は ` 1`、` 2` を付与。
- **AAP バッジ** — クリップから書き込まれているパラメータに `AAP` ミニラベルを表示。
  クリックで書き込み元クリップ / ステート一覧(`ParameterUsageFinder` を拡張)。

### 9.2 トランジション

- **Redirect / Replicate** — 選択トランジションを維持したまま、
  「宛先を選び直して複製」「別ソース群へ複製」する 2 コマンド。グラフの右クリック
  メニューから実行 → 対象ステートをクリックで指定(ツールバーにモード表示、Esc 解除)。
- **Seeded バッチ生成** — `TransitionBatch`(チェーン / ファン / クロス積)に
  「クリップボードのトランジションを雛形にする」トグルを追加。条件・タイミングを引き継いで生成。
- **条件によるトランジション選択** — 検索フィールドに `param:Name` / `mode:If` / `value:1`
  形式のクエリを追加し、一致する遷移をレイヤー内で一括選択。
- **同一ペアの多重トランジション検出** — Analyzer に Info チェックを追加
  (既存の Duplicate Condition と統合表示)。

### 9.3 グラフ / ステート

- **Set Clip Loop Time** — ステート右クリック: 選択ステートの参照クリップの loopTime を一括 ON/OFF。
- **I / O / P ショートカット** — 選択ステートの入 / 出 / 双方向トランジションを選択。
- **Ctrl+A / Ctrl+Shift+A** — 全ノード / 全トランジション選択。
- **整列の拡張** — 既存の Align Selected に「水平 / 垂直の等間隔分布(Distribute)」を追加。
- **ステートインスペクターの In / Out ボタン** — マルチステート編集の各行に、そのステートの
  入 / 出トランジション選択ボタンを追加。

### 9.4 検討(任意)

- **カラータグ** — ステート / 遷移への色付きタグ(`AnimatorState.tag` を利用)。
  UI テーマ変更ではなく分類機能だが、優先度は低く、需要を見て判断する。

---

## 10. パラメーターストア抽象化と MA Parameters 対応 【P1・追加】

**背景**: DaerD はアバターの FX だけでなく、NDMF ギミック用の Animator にも使われる。
ギミックのコントローラーは編集時点ではどのアバターにも割り当てられていないため、
「シーンのアバターから自動解決する」設計は成立しない上、フォールバック
（シーンに 1 体ならそのアバターを採用）は**無関係なアバターのアセットを書き換える事故**につながる。

### 10.1 明示的な関連付け

- コントローラー ↔ パラメーターストアの関連付けは `GraphFrameData`（コントローラーの
  サイドカーサブアセット）に保存する（`parameterStore` / `expressionsMenu`）。
- パラメーターパネルに **Params スロット**（ObjectField）を常設。ここに割り当てたものだけが
  編集・検査対象。**シーンからの自動解決は全廃**。
- **Detect ボタン**（オプトイン・明示操作のみ）: 完全一致だけを検索する。
  ① Playable レイヤーにこのコントローラーを割り当てたアバターディスクリプタ、
  ② このコントローラーを参照する MA Merge Animator（同一オブジェクトまたは親の
  MA Parameters を採用）。「シーンに 1 体だけならそれ」のフォールバックは持たない。
- Expressions Menu も同様（メニューウィンドウのスロット + Detect、`GraphFrameData` 保存）。
- Analyzer の VRC Parameters 検査は保存された関連付けがあるときのみ実行。

### 10.2 ParameterStore 抽象

- `Editor/Model/ParameterStore.cs`: `Read / WriteAll / Add / Remove / Edit / Rename /
  UsedBits / Capacity / Analyze` を持つ抽象クラス。エントリ形状は共通
  （name / valueType / saved / synced / defaultValue / typed）。
- **VRC バックエンド**: 既存 `VrcExpressionParameters` アクセサへの委譲。順序付き WriteAll。
- **MA バックエンド**: `ModularAvatarParameters` を型名 + SerializedObject でアクセス
  （MA 参照なし）。マッピング: nameOrPrefix ↔ name、syncType(NotSynced=0/Int=1/Float=2/Bool=3)
  ↔ valueType + typed（NotSynced は typed=false で型検査対象外）、synced = syncType≠NotSynced
  かつ !localOnly、saved ↔ saved。isPrefix 行（PhysBone ファミリー）は保持しつつ編集対象外。
  MA は名前マッチで統合されるため WriteAll は**差分適用**（順序は持たない）。
  Capacity は -1（アバター全体の予算に合算されるため上限非表示）。
- パネルの予算バー / S/D トグル / +追加 / Sync / リネームカスケードはすべてストア経由。

### 10.3 パラメーターミスマッチングの尊重

Expression 側とコントローラー側の**型不一致は VRChat が全組み合わせで変換する公認テクニック**
（[Parameter Mismatching](https://vrc.school/docs/Other/Parameter-Mismatching)。
例: 同期 1bit の Bool でアニメーター側 Float を駆動）。DaerD はこれを壊さない:

- コントローラー側の型変換はストア側の型を**追従書き換えしない**（黙った書き換えは意図の破壊
  かつ同期ビット数の変動になる。ストア側の型はストア側で編集する）。
- Analyzer の型不一致は Error ではなく **Info**（意図的か確認を促す文言）。
- メニューエディタの型チェック（Puppet の Float 要求・Value フィールドの UI）は
  コントローラー型ではなく**ストア側の型を優先**して判定し、パラメータードロップダウンの
  型フィルターは行わない。

## 11. フェーズ計画

| フェーズ | 内容 |
|---|---|
| **0.9** | §1 Network Sync、§2 トグル拡張、§3 式パラメータ連携(+Analyzer 新カテゴリ) |
| **0.10** | §4 Behaviour 拡大、§5 メニューエディタ、§6 レイヤーコピー/テンプレート |
| **0.11** | §7 Clip Remapper、§8 ブレンドツリーテンプレート |
| **0.12** | §9 QoL 群(小粒なので前倒し実装可) |

各フェーズの完了条件: 対象 Model の NUnit テストが全緑 / 新規文字列の JA 訳完備 /
README 機能一覧の更新 / VRC SDK 不在プロジェクトでコンパイル・動作すること。
