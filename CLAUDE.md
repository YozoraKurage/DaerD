# DaerD — リポジトリルール

## ユーザー固有データを git 履歴に絶対に残さない

ユーザーから提供されるデータ（アバター名・キャラクター名・実プロジェクトのパス・
.controller や生成 cs の実物・エラーログに含まれるパス断片など）は、**いかなる形でも
git の履歴に入れてはならない**。コミットされるファイルだけでなく、コミットメッセージの
本文・テストコード・コメント・ドキュメントの例も対象。

- ユーザー提供の実ファイルは `temp~/` に置く（.gitignore 済み）。temp~/ 以外に
  コピーしない。末尾のチルダは Unity にインポートさせないための慣習で、
  これが無いと生成 cs がテストプロジェクトのコンパイル対象に入ってしまう。
- バグ報告のパスやログを再現テスト・コミットメッセージに書くときは、必ず汎用名に
  置換する（例: `Assets/Chara/FX`、`Avatar_FX.controller`）。構造だけを保ち、
  固有名詞は残さない。
- **コミット前に必ず確認する**: ステージ内容とメッセージに対して固有名詞
  （アバター名・ユーザー名・実パス）を grep する。会話中に登場した固有名詞は
  すべて検索対象。
- 漏れて入れてしまった場合: 未 push なら履歴を書き換えて除去し、バックアップ ref
  （refs/original）も削除する。push 済みなら直ちにユーザーに報告して指示を仰ぐ。

## テストの走らせ方

この DevContainer には Unity 2022.3.22f1 が入っている（ベースは game-ci の
Editor イメージ）。EditMode テストはコンテナ内で完結する。

```
.devcontainer/unity/run-tests.sh                      # 全件
.devcontainer/unity/run-tests.sh --filter '*Frame*'   # 絞り込み
```

- 出力はサマリと失敗内容だけ。Unity の生ログは
  `$DAERD_UNITY_PROJECT/Logs/tests.log`、結果 XML は同ディレクトリの
  `test-results.xml`。ログを丸ごと読み込まないこと（数万行ある）。
- 終了コード: 0 = 全件成功 / 1 = テスト失敗 / 3 = コンパイルエラー等で結果が
  出なかった / 4 = ライセンス未設定。
- テストプロジェクトは `/home/node/unity-testproject`（名前付きボリューム）。
  このリポジトリを `file:/workspace` のローカルパッケージとして参照している
  ので、リポジトリ側には Library/ も Assets/ も生成されない。
- 初回だけ Unity Personal ライセンスの有効化が要る:
  `.devcontainer/unity/activate-license.sh --status` で状態を確認できる。
  未設定なら手順が表示されるが、ブラウザ操作を含むのでユーザーに依頼すること。

## VRChat SDK の有無で挙動が変わる

テストプロジェクトには vrc-get で VRChat SDK を入れてある（`add-vpm.sh --list`
で確認できる）。SDK 側にも 7 件テストがあるので、全件実行の件数は DaerD 分より
多く出る。

```
.devcontainer/unity/add-vpm.sh com.vrchat.avatars    # 入れる
.devcontainer/unity/add-vpm.sh --remove com.vrchat.avatars
```

**ビヘイビアやドライバに触る変更は、SDK 有りと無しの両方で走らせること。**
製品コードは型を名前で探すので、SDK があるとテスト側スタブではなく SDK の型が
実際に付く。型でキャストするテストはこの差で「SDK 有りでだけ落ちる」ようになる
（読むときは `VrcParameterDriver.ReadSpec` のような型非依存のアクセサを使う）。

パッケージを足した直後の 1 回目は、Unity のコンパイルが終わらないうちにテストが
始まって大量の NullReference になることがある。`run-tests.sh` はこれを検出して
自動でやり直すので、そのまま任せてよい。
