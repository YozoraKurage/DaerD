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

## コミットメッセージは設計判断の一次資料

このリポジトリでは、設計判断の記録は doc コメントではなくコミットメッセージが持つ。
コードコメントは「今どうであるか」を、コミットメッセージは「なぜそうなったか」を
書く場所と使い分ける。後から `git log` を読む人間と AI が唯一の読者だと思って書く。

- 件名は conventional prefix（feat / fix / refactor / chore）+ 変更が使う人に
  もたらす意味を一文で。本文が主役なので件名で説明し切ろうとしない。ただし
  機能領域が特定できる語（analyzer、sync、graph 等）を件名か本文冒頭に一つは
  入れる — 後から履歴を領域で絞れることが一次資料の条件。
- 本文には「何をしたか」ではなく**なぜこの形か**を書く: 動機になった問題、
  選んだ機構、その帰結。差分を読めば分かることは繰り返さない。
- **採らなかった案と捨てた理由を書く。** 特に既存の方式を置き換える・巻き戻す
  コミットは、前の方式を採用したときの論拠に答える形で、捨てた理由と
  **失った能力**を必ず書く。「Replaces X」だけで理由の無いコミットを作らない。
- **トレードオフと保証の射程を書く**: 何を犠牲にしたか、この変更が保証
  しないこと、意図的に対応しなかったケースとその理由。
- 本文中の数値の主張（「N スロットまで検証」「約 1/7 が該当」等）は、テストや
  計測の実際の範囲と一致させる。テスト側の doc と食い違う数字を書かない。
- 互換性への影響を書く: 保存済みデータ・生成物・レシピ API・既存コントローラが
  どう扱われるか。「既存には影響なし」も明示する価値のある主張。

## 設計来歴は distillery で引く（pull 型）

「なぜこの設計にしたか」「過去に何を決めたか」を問われたら、推測や差分からの再構成で
答えず、先に `.distillery/decisions/index.md` と該当 ADR を読む。コード側からは
`git blame` → コミットの `Session:` トレーラー → `.distillery/sessions/` で逆引きできる。
頼まれていないのに過去ログをコンテキストへ大量に持ち込まない（必要になってから引く）。

- `.distillery/` はローカル専用（remote 無し・pre-push 拒否）。中身、特に sessions/ の
  生ログを外部へ送らない・コピーしない。共有できるのは人間が承認した ADR のみ。
- ADR の作成と状態変更（waive / deprecate 等）は人間の承認を得てから確定する。
  手順は distillery スキルに従う。

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

テストプロジェクトには GestureManager・Av3Emulator・NDMF・Modular Avatar も埋め込み
パッケージで入れてある（`Packages/` 直下の `vrchat.blackstartx.gesture-manager` /
`lyuma.av3emulator` / `nadena.dev.ndmf` / `nadena.dev.modular-avatar`）。DD DynamicAnalyze
が前 2 つを Rec で、NDMF を BuildCapture で型参照する（asmdef の versionDefines `DAERD_GM` /
`DAERD_AV3E` / `DAERD_NDMF` / `DAERD_VRC`）ためで、**4 つとも VRChat SDK に依存している**
（NDMF は SDK 無しでも動く設計だが、Unity.Collections などの依存を SDK 経由で解決している
ので、この構成では SDK と一緒に落ちる）。**SDK を抜くときは 4 つとも `Packages/` の外へ
先に退避する** — 順序を逆にするとツール側がコンパイルエラーになり、テストが 1 件も
走らない（終了コード 3）。MA は NDMF に依存するので単独で抜くこともできる。

```
mkdir -p /home/node/unity-tools-aside
mv /home/node/unity-testproject/Packages/vrchat.blackstartx.gesture-manager \
   /home/node/unity-testproject/Packages/lyuma.av3emulator \
   /home/node/unity-testproject/Packages/nadena.dev.modular-avatar \
   /home/node/unity-testproject/Packages/nadena.dev.ndmf /home/node/unity-tools-aside/
.devcontainer/unity/add-vpm.sh --remove com.vrchat.avatars
# 戻すときは逆順（SDK を入れてから mv で戻す）
```

ツール不在では `#if DAERD_*` の中が丸ごと消え、asmdef の参照は未解決のまま無害に残る。
「ツールが無くても DaerD がコンパイルできる」ことは、この構成での全件実行が保証している。

**ビヘイビアやドライバに触る変更は、SDK 有りと無しの両方で走らせること。**
製品コードは型を名前で探すので、SDK があるとテスト側スタブではなく SDK の型が
実際に付く。型でキャストするテストはこの差で「SDK 有りでだけ落ちる」ようになる
（読むときは `VrcParameterDriver.ReadSpec` のような型非依存のアクセサを使う）。

パッケージを足した直後の 1 回目は、Unity のコンパイルが終わらないうちにテストが
始まって大量の NullReference になることがある。`run-tests.sh` はこれを検出して
自動でやり直すので、そのまま任せてよい。
