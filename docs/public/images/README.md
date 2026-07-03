# 画像ディレクトリ

ドキュメントで使うスクリーンショットや図をここに置きます。

`public/` 配下のファイルはサイトのルートに配置されるため、Markdown からは **`/images/...`** で参照します（`public/` や `docs/` は付けません）。

## 使い方

`docs/public/images/interface-overview.png` を置いた場合:

```md
![DaerD の画面構成](/images/interface-overview.png)
```

キャプション付きにしたいときは HTML の `<figure>` が使えます:

```md
<figure>
  <img src="/images/interface-overview.png" alt="DaerD の画面構成">
  <figcaption>DaerD ウィンドウの全体構成</figcaption>
</figure>
```

## 命名の目安

- 小文字・ハイフン区切り（例: `graph-editing-context-menu.png`）
- ページ／機能ごとに接頭辞を揃えると探しやすい（例: `interface-*`, `blendtree-*`）
- 画面キャプチャは PNG、写真やグラデーションが多いものは JPG/WebP を推奨
