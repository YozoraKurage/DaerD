# オブジェクトトグル

**オブジェクトトグル**は、GameObject の ON/OFF を切り替えるアニメーションクリップと、その配線をまとめて自動生成するウィザードです。

シーンのオブジェクトをドロップするだけで Animator ルートからの相対パスが計算され、ON/OFF 2 本のクリップと、それを切り替えるレイヤーまたは BlendTree が作られます。

## 開く

- [ホーム画面](/guide/home) → パラメータパネルの **Add** メニュー → **Object Toggle**

## 配線の 2 方式

| 方式 | 内容 |
| --- | --- |
| **Layer** | 新規レイヤーに OFF / ON の 2 ステートと即時遷移を作り、**Bool** パラメータで切り替えます |
| **Direct BlendTree** | `0 = OFF` / `1 = ON` の 1D ツリーを Direct BlendTree レイヤーに追加し、**Float** パラメータで駆動します。多数のトグルを 1 レイヤーに集約できます |

パラメータは新規作成でも既存の流用でも構いません（型が合わない既存パラメータは警告されます）。**Default ON** を指定すると、パラメータの初期値が ON になり、レイヤーも ON ステートから始まります。

## 対象の指定

- **Path Root** — Animator を持つ GameObject。ドロップされたオブジェクトはここからの相対パスになります。
- **Add Object** — シーンの GameObject をドロップして追加します。
- **Add Selection** — Hierarchy で選択中の GameObject をまとめて追加します。
- 直接ハイエラルキーパスを打ち込むこともできます。

各対象の **Active** チェックは「トグル ON のときにオブジェクトが有効か」を意味します。外すと反転（トグル ON で非表示）になります。

::: warning ルート自身はトグルできません
Animator を持っているオブジェクトを自分のアニメーションで無効化することはできないため、Path Root 自身は対象にできません。対象は Path Root の子である必要があります。
:::

## GameObject 以外も切り替える

`GameObject.m_IsActive` に加えて、対象ごとに次を一緒にアニメーションできます。

| 種類 | 内容 |
| --- | --- |
| **Object** | `GameObject.m_IsActive` |
| **Renderer** / **ParticleSystem** / **AudioSource** / **Light** | 各コンポーネントの `m_Enabled` |
| **VRCPhysBone** | PhysBone の `m_Enabled`（VRChat SDK がある場合） |
| **BlendShapes** | ブレンドシェイプのウェイト。シェイプごとに OFF 時 / ON 時の値を指定します |

## 生成されるもの

クリップは `.controller` と**同じフォルダに `.anim` アセットとして保存**されます。コントローラーのクリーンアップで消えず、普通に編集できるようにするためです。

::: tip DBT ガジェットとの使い分け
Direct BlendTree 方式のトグルは、[DBT ガジェット](/features/dbt-gadgets)と同じ Write Defaults ON の Direct BlendTree レイヤーに同居できます。トグルが多いアバターほど、レイヤー数を抑えられます。
:::

## 関連機能

- [DBT ガジェット](/features/dbt-gadgets)
- [VRC / NDMF 連携](/features/vrchat)
- [クリップとリパス](/features/clips)
