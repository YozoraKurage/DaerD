# インストール

DaerD は Unity 2022.3 以降に対応した VPM / UPM パッケージ（`net.yozolab.daerd`）です。用途に合わせて次のいずれかの方法で導入できます。

## 方法 1: VCC / ALCOM（VPM リスティング）

VRChat 向けプロジェクトなど、VPM を利用している場合はリスティングリポジトリ経由での導入が最も簡単です。アップデートも管理できます。

DaerD は VPM リスティング **[vpm.yozolab.net](https://vpm.yozolab.net)** で配信されています。

### ワンクリックで追加（VCC）

次のボタンから VCC にリスティングを追加できます。

<a class="vp-button" href="vcc://vpm/addRepo?url=https://vpm.yozolab.net/index.json">VCC にリスティングを追加</a>

うまく開かない場合は、下の「手動で追加」の手順で URL を貼り付けてください。

### 手動で追加

1. VCC（VRChat Creator Companion）または ALCOM を開きます。
2. **Settings → Packages → Add Repository** を開き、次の URL を入力して追加します。

   ```
   https://vpm.yozolab.net/index.json
   ```

3. プロジェクトの **Manage Project** 画面で `DaerD` を選び、**Add** します。

::: tip リスティングサイト
リスティングの一覧やパッケージのバージョンは [vpm.yozolab.net](https://vpm.yozolab.net) で確認できます。
:::

## 方法 2: UPM（Git URL）

VPM を使わない通常の Unity プロジェクトでは、Package Manager から Git URL で追加できます。

1. Unity メニューの **Window → Package Manager** を開きます。
2. 左上の **＋ → Add package from git URL...** を選択します。
3. 次の URL を入力して **Add** します。

```
https://github.com/YozoraKurage/DaerD.git
```

特定のバージョンに固定したい場合は、末尾にタグを付けます。

```
https://github.com/YozoraKurage/DaerD.git#0.10.0
```

## 方法 3: unitypackage / zip

Package Manager を使わず手動で導入する場合は、[GitHub Releases](https://github.com/YozoraKurage/DaerD/releases) から `.unitypackage` または `.zip` をダウンロードできます。

- **`.unitypackage`** — ダウンロードして Unity にドラッグ＆ドロップ、または **Assets → Import Package → Custom Package...** からインポートします。
- **`.zip`** — 展開して `Packages/net.yozolab.daerd/` に配置すると、埋め込みパッケージとして認識されます。

## 動作要件

| 項目 | 要件 |
| --- | --- |
| Unity | 2022.3 以降 |
| プラットフォーム | エディタ拡張のため実行プラットフォームは問いません |

## 導入の確認

インポート後、Unity メニューに **YozoLab → DaerD** が表示されていればインストール成功です。次は [クイックスタート](/guide/getting-started) に進みましょう。
