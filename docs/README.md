# DaerD ドキュメントサイト

[DaerD](https://github.com/YozoraKurage/DaerD) のドキュメントサイトです。[VitePress](https://vitepress.dev/) で構築し、Cloudflare Workers（静的アセット）で配信します。

このディレクトリは **`docs` ブランチ専用**です。`main` ブランチ（Unity パッケージ本体）には含めません。

## ローカル開発

```bash
cd docs
npm install
npm run dev      # 開発サーバー（http://localhost:5173）
npm run build    # 本番ビルド → .vitepress/dist
npm run preview  # ビルド結果をローカルで確認
```

## Cloudflare Workers へのデプロイ

Cloudflare の **Workers Builds**（Git 連携）を使うと、`docs` ブランチへの push を検知して自動でビルド＆デプロイされます。

### 初回セットアップ（ダッシュボードで一度だけ）

1. [Cloudflare ダッシュボード](https://dash.cloudflare.com/) → **Workers & Pages** → **Create** → **Workers** → **Connect to Git** を開きます。
2. GitHub の `YozoraKurage/DaerD` リポジトリを選択します。
3. ビルド設定を次のように指定します。

   | 項目 | 値 |
   | --- | --- |
   | Production branch | `docs` |
   | Root directory | `docs` |
   | Build command | `npm ci && npm run build` |
   | Deploy command | `npx wrangler deploy` |

4. 保存すると初回ビルドが走り、`https://daerd-docs.<サブドメイン>.workers.dev` で公開されます。

以降は `docs` ブランチへ push するたびに自動でデプロイされます。

### 手動デプロイ（任意）

ローカルからデプロイする場合は、Cloudflare へログインして次を実行します。

```bash
cd docs
npm run build
npx wrangler deploy
```

## 構成

| パス | 内容 |
| --- | --- |
| `.vitepress/config.mts` | サイト設定（ナビ・サイドバー・検索など） |
| `index.md` | トップページ |
| `guide/` | ガイド（導入・使い方） |
| `features/` | 機能ドキュメント |
| `wrangler.jsonc` | Cloudflare Workers（静的アセット）設定 |
