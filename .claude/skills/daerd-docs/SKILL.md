---
name: daerd-docs
description: DaerD のドキュメントサイト（docs ブランチの docs/ 配下、VitePress）を実装に合わせて更新する。ユーザーが「ドキュメントを更新して」「docs を最新にして」「main を docs に反映して」と言ったとき、リリース後にサイトを追従させるとき、新機能のページを追加するときに使う。
---

# DaerD ドキュメントサイトの更新

`docs` ブランチの `docs/` 配下にある VitePress サイトを、`Editor/` の実装に合わせて更新する。

## 大前提

- **`main` ブランチには絶対に触らない。** 作業は `docs` ブランチのみ。
- **`docs/` 配下（と必要ならこのスキル自身）以外を変更しない。** 特にルートの
  `README.md` は main 由来なので、docs ブランチで編集すると次の sync マージで衝突する。
  README の記述が古いと気づいたら、直さずユーザーに報告する。
- **README を情報源にしない。** README は実装より遅れていることがある（実例:
  「逆数・除算・三角関数は補助レイヤーを追加する」は 0.10.0 時点で既に誤り）。
  正は常に `Editor/` のコード。
- 表記は日本語。既存ページの語調（です・ます、簡潔な見出し、`:::` コールアウト、表）に合わせる。

## 手順

### 1. main を docs に反映する

```bash
git fetch origin
git merge-base --is-ancestor origin/main HEAD && echo "already in" || git merge origin/main
```

既に取り込み済みなら何もしない（`chore: sync docs with main` が積まれている履歴）。

### 2. 差分を把握する

```bash
git log --oneline <前回リリースタグ>..origin/main
git diff --stat <前回リリースタグ> origin/main -- Editor Tests
```

`Editor/` の新規ファイルが、たいてい新機能そのもの（例: `Panels/HomePanel.cs` =
ホーム画面、`Window/*Form.cs` = ウィンドウのタブ内埋め込み）。

### 3. 実装から事実を拾う

ドキュメントに書く事実は**必ずコードから取る**。特に効く読み方:

| 知りたいこと | 見る場所 |
|---|---|
| ユーザーに見える文言・ラベル・ツールチップ | `grep -n "L.Tr(" Editor/**/*.cs` — 説明文がそのまま入っている |
| 設定項目と既定値 | `Editor/DaerDSettings.cs`（`DaerDSettingsProvider.DrawGui` が実際のラベル） |
| 解析の検出項目 | `Editor/Model/AnalyzerIssue.cs` の `IssueKind` + `ControllerAnalyzer.cs` の severity / message |
| DBT ガジェットの種類と意味 | `Editor/Model/AapGadgets.cs` の `Kind` / `KindLabels`、`Window/AapGadgetWindow.cs` の説明文 |
| 巡回同期の仕様 | `Editor/Model/AsyncSyncBuilder.cs` のクラスコメント（制約まで書いてある） |
| C# Recipe の API | `Editor/Authoring/*.cs` の `public` メソッドシグネチャ |
| ショートカット | `Editor/Graph/AnimatorGraphView.cs` の `OnKeyDown` |
| 右クリックメニュー | `Editor/Graph/GraphContextMenu.cs` |
| レイヤーのバッジ | `Editor/Panels/LayersPanel.cs` |

コード中の `<summary>` コメントは設計意図まで書かれていることが多く、
「なぜそうなっているか」の説明はそこから起こせる。

**コード例を書くときは引数の順序と型をシグネチャで確認する**（`Remap(input, output,
inMin, inMax, outMin, outMax)` のような順序を憶測で書かない）。

### 4. 書く

サイト構成:

| パス | 内容 |
|---|---|
| `docs/index.md` | トップ（`layout: home`。frontmatter の後ろに本文を書くと features の下に出る） |
| `docs/notice.md` | 注意事項（下記「必ず残す注意書き」） |
| `docs/guide/` | 導入・画面構成・ホーム画面・設定・FAQ |
| `docs/features/` | 機能ごとの詳細 |
| `docs/.vitepress/config.mts` | ナビ・サイドバー・フッター |

ページを追加したら **`config.mts` のサイドバーにも追加する**（グループは
編集 / リファクタリング / 検証と整理 / 生成ツール / VRChat）。`docs/features/index.md`
の一覧と一覧表も更新する。

### 5. 必ず残す注意書き

ユーザーの明示的な要望。**削らない・薄めない。**

- ドキュメントは **AI（Claude）が更新・保守**している。
- 更新は管理者の指示があったときだけで、**管理者はサボりがちなので最新でない可能性がある**。
- DaerD は作成者 **yozorakurage の作業効率化**のための個人ツール。
- **互換性の保証なし・動作保証なし**。
- **ほぼ全てが AI によって作られている**（コードもドキュメントも）。
- その代わり **MIT License** なので自由に使ってよい。ただし**利用者の責任**で。

現在の掲載場所（増やすのはよいが、減らさない）:

- `docs/notice.md` — 全文
- `docs/index.md` — トップページ下部の warning
- `docs/guide/index.md` — 冒頭の warning と末尾のライセンス info
- `docs/guide/faq.md` — 冒頭 2 問
- `docs/.vitepress/config.mts` — `themeConfig.footer.message`
- ナビの「注意事項」項目

### 6. ビルドで検証する

```bash
cd docs && npm ci && npm run build
```

VitePress は**デッドリンクでビルドを落とす**ので、ページ間リンクはこれで検証できる。

**アンカーリンクは検証されない。** そして VitePress のスラグ生成は日本語の半濁点
（`プ` `ペ` など）を分解するため、`#empty-クリップ` のような日本語アンカーは
**リンクは通るのに実際は飛ばない**。日本語見出しへリンクするときは、見出し側に
明示 ID を付ける:

```markdown
## Empty クリップ {#empty-clip}
```

そのうえで、ビルド後の `docs/.vitepress/dist` の `id="..."` と、HTML 内の
`href="/...#..."` を突き合わせて検証する（未一致が 0 件になるまで直す）。

### 7. コミット前チェック

- `git status --short` が `docs/` 配下（＋ `.claude/skills/`）だけであること。
- ルート `README.md`・`Editor/`・`Tests/`・`package.json` に差分が無いこと。
- **ユーザー固有データ（アバター名・実プロジェクトのパス・ユーザー名）が入っていないこと**
  — リポジトリルールの `CLAUDE.md` を参照。例に使うのは `A` / `B` / `Speed` / `Outfit`
  のような汎用名だけ。
- `node_modules/` と `.vitepress/dist/` は `docs/.gitignore` 済み。

コミットは**ユーザーが求めたときだけ**行う。

## デプロイ

`docs` ブランチへ push すると Cloudflare Workers Builds が自動でビルド・デプロイする
（Production branch: `docs` / Root directory: `docs`）。詳細は `docs/README.md`。

## よくある落とし穴

- **サイトの遅れは想像以上**。「0.10.0 の差分だけ」と思って開くと、実際は数バージョン分
  抜けていることがある。着手前に README の機能リストとサイトのページ一覧を突き合わせ、
  抜けの規模をユーザーに報告してから範囲を決める。
- ナビ右上のバージョンはルート `package.json` から読んでいる（main に追従する）。
  ここを手で書き換えない。
- `docs/README.md` はサイトには出ない（開発者向けの手順書）。構成を変えたらここも直す。
