# DBT ガジェット (AAP)

**DBT ガジェット**は、Float パラメータに対する演算を **Direct BlendTree のなかで毎フレーム実行する仕掛け**を自動生成する機能です。Write Defaults ON のステート 1 つとネストした BlendTree だけで構成され、実行には DaerD もスクリプトも必要ありません（生成後は素の AnimatorController として動作します）。

計算結果は **AAP**（Animator-Animated Parameter — アニメーションクリップで書き換えられる Animator パラメータ）として出力され、以降は普通のパラメータと同じように条件や BlendTree から参照できます。

## 生成する

次のいずれかから開けます。

- [ホーム画面](/guide/home) の **DBT Gadgets** カード → **+ Add Gadget**
- パラメータパネルの **Add** メニュー → **DBT Gadget (AAP)**

ウィザードで演算の種類・入力パラメータ・出力パラメータ名・追加先レイヤーを指定して **Create** します。生成されたクリップと BlendTree は **`.controller` のサブアセット**として保存されるため、アセットが散らかりません。

::: tip 出力パラメータ名の既定値
入力の名前から自動で決まります（`A` と `B` を入力にした場合、Add なら `A+B`、Smooth なら `A/Smoothed`、Remap なら `A/Remapped` など）。そのまま使っても、書き換えても構いません。
:::

## 演算の一覧

| 種類 | 内容 |
| --- | --- |
| **Smooth** | `output = lerp(input, output, smoothing)` — 指数スムーズ。毎フレーム再計算されます |
| **Smooth (Linear)** | 毎フレーム Step Size ぶんだけ入力へ近づく一定速度の追従。Step Size を Frame Time ガジェットで駆動するとフレームレート非依存になります |
| **Add** | `A + B`。正の値のみ（Direct のウェイトは 0 でクランプされます） |
| **Add (Ranged)** | 指定した範囲での `A + B`。負の値も扱えます |
| **Sub** | `A - B`。正の値のみ |
| **Sub (Ranged)** | 指定した範囲での `A - B`。範囲は対称（Min = -Max）にします |
| **Multiply** | `A × B`。ネストした Direct ツリーで実現。正の値のみ |
| **Divide** | `A / B`。正の入力のみ。B の逆数を経由するため結果は **3 フレーム遅れ**ます |
| **Reciprocal** | `1 / input`。正の入力のみで、1 以上は厳密・1 未満はルックアップテーブル（240 で頭打ち）。結果は **2 フレーム遅れ**ます |
| **And** / **Or** | 0/1 入力に対する論理積・論理和 |
| **Not** | `1 - input`（0/1 入力） |
| **Float As Bool** | しきい値以上なら 1、それ未満は 0 |
| **Remap** | 入力範囲を出力範囲へ線形にリマップ。出力範囲を逆順にすると傾きが反転します |
| **Frame Time** | 前フレームからの経過秒。入力はありません |
| **Separate Digits** | 0..1 の入力を小数第 3 位まで分解し、`/Tenths`（0〜0.9）・`/Hundredths`（0〜0.09）・`/Thousandths`（0〜0.009）へ出力。出力名はこの 3 つのベース名になります |
| **Sine** / **Cosine** / **Tangent** | `sin(2π×input)` など。入力 0..1 が 1 周に対応します。Tangent は極付近を ±100 で抑えています |
| **LUT (Curve)** | 任意の AnimationCurve を 1D BlendTree の区分線形ルックアップテーブルにベイク。カーブの時間軸が入力、値が出力です（サンプル数 2〜128） |
| **Atan2** | `atan2(Y, X)` を**周**単位（0..1、+X から反時計回り）で返します。結果をそのまま Sine / Cosine ガジェットへ渡せます |
| **Buffer (Delay)** | 入力をちょうど N フレーム（1〜8）遅らせたコピー |

### Buffer が要る理由

BlendTree の計算は 1 段につき 1 フレーム遅れます。そのため、同じパラメータを参照していても**段数の異なる分岐は別のフレームの値を見ている**ことになります。浅いほうの分岐に Buffer を挟むと、2 つの分岐のタイミングを揃えられます。

### Atan2 の注意点

- 原点付近（ベクトルの長さが 0 に近い領域）では結果が 0 へ潰れます。必要なら大きさでゲートしてください。
- 0 と 1 の継ぎ目は +X 方向の狭い帯にあります。
- Directions（円周のサンプル数、8〜64）が精度を決めます。隣り合う方向の間で約 1/N 周ぶんの誤差になり、1 方向につき 1 クリップを消費します。

## 補助レイヤー

ほとんどのガジェットは BlendTree の中だけで完結します。**Frame Time だけ**は BlendTree では読めない値（実時間）を扱うため、コントローラーの末尾に専用の補助レイヤーを 1 つ追加します。このレイヤーは BlendTree レイヤーより後ろに置かれている必要があります。

::: warning Frame Time は 1 コントローラーに 1 つ
Frame Time が動かす時計は共有の仕掛けです。複数追加しないでください。
:::

## 保存・再生成・削除

生成したガジェットは**コントローラーに記録として保存**されます。ツリーやクリップは読み返せない量になるため、この記録が実質的な唯一の説明になります。

- [ホーム画面](/guide/home) の **DBT Gadgets** カードに一覧表示されます。
- **Edit** でウィザードを設定入りで開き直し、**Regenerate** でその場に作り直します（古いツリーは掃除されてから再生成されるため、増殖しません）。
- **Delete** はそのガジェットのツリー・クリップ・パラメータをまとめて削除します。

Direct BlendTree だけで構成されたレイヤーは、レイヤー一覧に **DBT** バッジが付きます。

## 解析との連携

[コントローラー解析](/features/analysis) は DBT の健全性も検査します。

- Direct BlendTree を再生するステートの **Write Defaults が OFF** になっている（ワンクリック修正あり）
- Direct BlendTree の子に**ウェイトパラメータが未設定**（その子は再生されません）
- ウェイトパラメータが**存在しない**、または **Float ではない**

## C# から並べる

[C# Recipe](/features/recipe) の `c.Gadgets("DBT")` を使うと、ガジェットをコードで並べて 1 レイヤーに生成できます。

```csharp
c.Gadgets("DBT")
    .Multiply("Speed", "Scale", "Speed*Scale")
    .Remap("Speed*Scale", "Out/Speed", 0f, 1f, -1f, 1f)
    .Smooth("Out/Speed", "Out/Speed/Smoothed", "Out/Speed/Smoothing", 0.9f);
```

Generate のたびにそのレイヤー・補助レイヤー・出力パラメータの名前空間が掃除されてから作り直されるため、何度実行しても積み上がりません。

## 関連機能

- [BlendTree 編集](/features/blendtree)
- [オブジェクトトグル](/features/object-toggle)
- [コントローラー解析](/features/analysis)
- [C# Recipe](/features/recipe)
