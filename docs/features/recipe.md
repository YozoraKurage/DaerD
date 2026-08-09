# C# Recipe（AnimatorController ⇄ C#）

**C# Recipe** は、AnimatorController を「それを組み立てる編集可能な C# コード」に変換し、そのコードから元のコントローラーを再生成する機能です。

グラフで手作業を繰り返すには多すぎる作業（同じ形のレイヤーを 20 個、パラメータだけ違うステート群）や、**AI にコントローラーの編集を任せたい**とき、そして巡回同期のような設定をコードで精密に管理したいときに使います。

## エクスポートする

コントローラー全体、または選択したレイヤーだけを C# にできます。

- [ホーム画面](/guide/home) の **Recipe Export** ツールカード
- レイヤー設定の **Export Layer To C#**（そのレイヤー 1 枚を変換）

| 設定 | 内容 |
| --- | --- |
| **Class Name** | 生成されるクラス名 |
| **Namespace** | 名前空間（任意） |
| **Output Folder** | 出力先。プロジェクト内である必要があります。`Editor` フォルダ配下でない場合は `Editor` サブフォルダが自動で追加されます（プレイヤービルドでコンパイルされないように） |
| **Create Recipe Asset** | コンパイル後に、コントローラーとクリップ参照が代入済みの `.asset` を作ります |
| **Assembly Definition** | Recipe フォルダを専用の小さなアセンブリに分け、エクスポートのたびにエディタアセンブリ全体を再コンパイルしないようにします |
| **Layers To Export** | 全レイヤーを選ぶと**排他的（exclusive）** Recipe に、一部だけなら**そのレイヤーだけを名前で置き換える** Recipe になります |

## 2 つのファイルに分かれる理由

エクスポートは **1 つの partial class を 2 ファイル**に分けて出力します。

| ファイル | 役割 |
| --- | --- |
| `<Name>.Generated.cs` | **機械側**。エクスポートのたびに丸ごと書き直されます。`BuildGenerated()` を持ちますが、実行はされません |
| `<Name>.cs` | **あなた（や AI）側**。`Build()` を持ち、**DaerD は二度と上書きしません**。実際に実行されるのはこちらです |

これは「エクスポート → コードを整形 → Generate → コントローラーを編集 → 再エクスポート」というループで、整形した内容が消えないための分割です。再エクスポートは Generated 側にだけ落ちるので、**コントローラー側の変更が Generated の git diff にきれいに出ます**。

::: tip エクスポート側と比較
Recipe アセットの **Compare With Exported Half** は、`Build()` と `BuildGenerated()` が**宣言する内容（IR）**を突き合わせます。読み方ではなく**出来上がる物**の比較なので、ループやヘルパーで書き直しても、AI に整形させても通ります。整形が意味を変えていないことの安全網です。
:::

## Recipe アセット

エクスポートで作られる Recipe は `ControllerRecipe` を継承した ScriptableObject です。

- **Generate** — `targetController` へ適用します（Undo 可）。非排他の Recipe は宣言したレイヤーを**同名で置き換え**、それ以外には触れません。排他的な Recipe はコントローラー全体（パラメータとレイヤー一覧）を所有します。
- **Verify** — コードと実物の乖離をレポートします。
- **Open in DaerD** — 対象コントローラーを DaerD で開きます。

AnimationClip や AvatarMask の参照は Recipe アセットの `[SerializeField]` フィールドへ自動代入され、インスペクターでドラッグ＆ドロップで差し替えられます。**コードに GUID は一切入りません。**

Recipe が所有するレイヤーは、レイヤー一覧に **C#** バッジが付きます。

## API

生成コードの冒頭には API チートシートが付きます。方言は [AnimatorAsCode](https://github.com/hai-vr/av3-animator-as-code) V1 準拠で、パラメータは型付きハンドルです。

```csharp
protected override void Build(ControllerBuilder c)
{
    var toggle = c.BoolParameter("Outfit");
    var layer  = c.Layer("Outfit");

    var off = layer.NewState("Off").WithAnimation(offClip).Default();
    var on  = layer.NewState("On").WithAnimation(onClip);

    off.TransitionsTo(on).When(toggle.IsTrue());
    on.TransitionsTo(off).When(toggle.IsFalse());
}
```

主なもの:

| 分類 | API |
| --- | --- |
| パラメータ | `FloatParameter` / `IntParameter` / `BoolParameter` / `TriggerParameter` |
| レイヤー | `Layer` / `SyncedLayer` / `WithWeight` / `Additive` / `WithAvatarMask` / `WithIkPass` |
| ステート | `NewState` / `NewSubStateMachine` / `At` / `Default` / `WithAnimation` / `WithSpeed` / `WithMotionTime` / `WithWriteDefaultsSetTo` |
| 遷移 | `TransitionsTo` / `AnyTransitionsTo` / `EntryTransitionsTo` / `Exits` / `When` / `And` / `AfterAnimationFinishes` / `WithTransitionDurationSeconds` |
| 条件 | `IsTrue()` / `IsFalse()` / `IsGreaterThan(x)` / `IsLessThan(x)` / `IsEqualTo(x)` |
| Parameter Driver | `Drives` / `DrivingIncreases` / `DrivingCopies` / `DrivingRemaps` / `DrivingLocally` |
| BlendTree | `NewBlendTree` |

### エスケープハッチ

```csharp
c.Raw(controller => { /* DaerD / Unity の全 API */ });
```

### ウィザードの機能をコードから

- `c.AsyncSync()` — [巡回同期](/features/async-sync)をフル設定。ウィザードには無い**明示スケジュール**（`Schedule("Hue","Outfit","Hue","Tail")`）も指定できます。
- `c.Gadgets("DBT")` — [DBT ガジェット](/features/dbt-gadgets)を C# で並べて 1 レイヤーに生成。引数はパラメータハンドルでも文字列でも渡せます。

どちらも Generate のたびに自分が作ったレイヤー・補助レイヤー・出力パラメータの名前空間を掃除してから作り直すため、**何度 Generate しても積み上がりません**。

エクスポートしたコードのうち、ツリーがそのまま展開されて読みづらいガジェットレイヤーだけをガジェット呼び出しに書き換える、という使い方ができます。

## 正しさの担保

エクスポータは C# を手で書き起こしているのではありません。**実際にビルダー API を実行しながら、その呼び出し列を記録する**方式です。出力されたコードは、元のコントローラーを再構築できることが確認済みの呼び出し列そのものになります。

## 関連機能

- [DBT ガジェット](/features/dbt-gadgets)
- [巡回同期](/features/async-sync)
- [レイヤー操作](/features/layers)
