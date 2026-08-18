# DaerD 構成の地図

実装エージェント向けの読み先。**まずこれを読み、探索 grep は最後の手段にする**
（grep が要ったならこの地図の不備 — 直すこと）。ルールの本体は CLAUDE.md、
設計判断の来歴は `git log` と `.distillery/decisions/`。

## 依存の向き

```
Panels / Window / Graph / DynamicAnalyze   （UI・可視化 — 何にでも依存してよい）
        ↓
Authoring   （C# Recipe API とエクスポート — Model に依存）
        ↓
Model の 6 棟（相互は Engine/Analyze/Edit → IR/Persist/Bridge の向きが基本）
        ↓
Styles / Utils / Localization（どこからでも使う道具）
```

- **public は Recipe API 面だけ**。`Editor/AssemblyInfo.cs` の列挙が正で、
  `PublicSurface_IsExactlyTheRecipeApi` テストが守っている。新規メンバーは internal。
- **Persist は Authoring に依存しない**。直列化型が外部型を持つときは
  `UnityEngine.Object`（例: `parameterStore`、`PrefabLink.mergeAnimator`）。
- 直列化型（Persist と GraphFrameData 内の record 群）は**追加進化のみ**・
  名前空間据え置き。

## Model の 6 棟

| 棟 | 役割 | 主な型 |
|---|---|---|
| **Engine** | 機構の生成（作る側） | DbtBuilder, ToggleBuilder, ObjectGadgets, AapGadgets, AsyncSync*（Builder/Applier/Schedule/Split/Cost/Naming）, NetworkSyncBuilder, SyncRequestBuilder |
| **Analyze** | 読み取り専用の解析 | ControllerAnalyzer, ControllerReachability, LayerOwners（レイヤー所有の逆引き）, ParameterUsageFinder, AapWriteScan, ControllerLocator, StateSearch |
| **IR** | コントローラーの中間表現 | ControllerIR（Parse）, ControllerIRDiff（Compare）, ControllerIRBuilder（Rebuild — レシピ Generate の実体） |
| **Persist** | 直列化される型 | GraphFrameData（DD の全保存状態: objectGadgets, codeOwned, PrefabLink, asyncSync…）, DaerDLayerTemplate, DaerDBlendTreeTemplate |
| **Bridge** | 外界への型非依存アクセサ | VrcParameters / VrcMenuAccess / VrcBehaviours / VrcParameterDriver（SDK 型を名前で）, ParameterStore（MA）, PrefabLinks / PrefabWriter（Check→純 Judge の前例）, LiveAnimator / AnimatorPlayback, SavedByVersion（PackageInfo→純 Format、GraphFrameData.savedByVersion のスタンプ元） |
| **Edit** | ユーザー操作の構造編集 | 各種 Clipboard, ParameterRenamer / Converter, ClipRenamer / Repather, StateDuplicator / Packer, GraphLayout |

## Authoring（C# Recipe）

- 宣言 API: ControllerBuilder → Layer/Machine/State/Transition/Tree*Builder、
  Param ハンドル（Float/Int/Bool/Trigger）、GadgetRecipeBuilder（AAP）、
  ObjectRecipeBuilder（オブジェクトガジェット）、AsyncSyncRecipeBuilder。
- 実行: ControllerRecipe（.asset 基底、Generate/Verify/Compare）、
  RecipeDriver（宣言→既存機構の再生）、RecipeFreshness（鮮度判定）、
  RecipeLinks（controller→recipe リンク）。
- エクスポート: RecipeExporter（コード生成）→ RecipeExport（ToSource/ToProject）
  → RecipeExportQueue（コンパイル後の .asset 作成/更新）。RecipeScript が
  識別子/整形、RecipeFoldPlanner が畳み込み。CLI は RecipeExportCli（public 据え置き）。

## UI

- **DaerDWindow** が本体（タブ = コントローラー、TabStrip）。状態のハブは
  DaerDContext / DaerDContextExtensions。
- **Panels/** はウィンドウ内 IMGUI: LayersPanel（レイヤー一覧 + バッジ）,
  HomePanel（ハブ画面: プレハブリンク/Recipe カード）, ParametersPanel,
  AsyncSyncPanel, BlendTree*, InspectorPanel + Inspector/*。
- **Window/** は独立ウィンドウとフォーム: RecipeExportForm/Window,
  ObjectGadgetWindow, AapGadgetWindow, AsyncSyncForm/Window, Analyzer*,
  Clips*, VrcParamSyncWindow ほか。
- **Graph/** は GraphView ベースのアニメーターグラフ（ノード/エッジ/クリップボード）。
- 文言は必ず `L.Tr(...)`（Localization/PoCatalog、.po カタログ）。色とアイコンは
  Styles/DaerDColors・DaerDIcons。

## DynamicAnalyze

走らせて答える解析: Simulation/SimSession（自前ドライバ）, PlayRecorder/
BuildCapture（実機記録; GestureManager/Av3Emulator/NDMF は versionDefines
`DAERD_GM`/`DAERD_AV3E`/`DAERD_NDMF`/`DAERD_VRC` で条件参照）, WaveformView（表示）。

## テスト

`/workspace/Tests`（90 ファイル、`Yozolab.DaerD.Tests.*`）。対象と同名 +Tests が原則
（例: ObjectGadgets → ObjectGadgetsTests）。実行はテストデーモン経由
（CLAUDE.md の「テストの走らせ方」）。コミットゲートは変更対応フィクスチャの
部分選択、全件はシリーズ末尾に 1 回。
