---
layout: home

hero:
  name: DaerD
  text: AnimatorController を、もっと直感的に。
  tagline: Unity の AnimatorController エディタを GraphView ベースで置き換えるエディタ拡張。パラメータ型の自動変換やカスケードリネームといった安全なリファクタリングから、Direct BlendTree ガジェット・パラメータ圧縮・C# への相互変換まで。
  actions:
    - theme: brand
      text: はじめる
      link: /guide/
    - theme: alt
      text: インストール
      link: /guide/installation
    - theme: alt
      text: 機能一覧
      link: /features/
    - theme: alt
      text: GitHub
      link: https://github.com/YozoraKurage/DaerD

features:
  - icon: 🕸️
    title: GraphView ベースの編集
    details: ステート・トランジション・サブステートマシン・BlendTree を、応答性の高いグラフ上で編集。複数のコントローラーをタブで同時に開けます。
    link: /features/graph-editing
    linkText: 詳しく見る
  - icon: 🏠
    title: ホーム画面
    details: コントローラー全体の設定と、生成した仕掛けの記録、解析やクリップ一覧といったツールを 1 画面に。レイヤーのグラフには置き場所がないものが集まります。
    link: /guide/home
    linkText: 詳しく見る
  - icon: 🔀
    title: 壊さないリファクタリング
    details: パラメータの型を変えれば参照する条件を、名前を変えれば条件・BlendTree・Parameter Driver・パラメータストアを自動で追従。差分プレビュー付き。
    link: /features/parameter-conversion
    linkText: 詳しく見る
  - icon: 🔍
    title: コントローラー解析
    details: 未使用パラメータ、不正な条件、到達不能ステート、出られないステート群、Write Defaults の混在などを検出し、多くはワンクリックで修正。
    link: /features/analysis
    linkText: 詳しく見る
  - icon: 🧮
    title: DBT ガジェット
    details: Direct BlendTree で毎フレーム演算する仕掛けを自動生成。加算・乗算・スムーズ・逆数・三角関数・LUT・atan2・バッファまで、WD ON の 1 ステートで。
    link: /features/dbt-gadgets
    linkText: 詳しく見る
  - icon: 📡
    title: 巡回同期（パラメータ圧縮）
    details: 複数パラメータを「インデックス + 値チャンネル」へ時分割多重し、同期ビットを節約。順序・レート・コストをプレビューしながら設計できます。
    link: /features/async-sync
    linkText: 詳しく見る
  - icon: 📄
    title: C# Recipe
    details: コントローラーを編集可能な C# へ変換し、コードから再生成。整形した半分は上書きされないため、AI に編集を任せても往復できます。
    link: /features/recipe
    linkText: 詳しく見る
  - icon: 🐾
    title: VRC / NDMF 連携
    details: VRC Expression Parameters と MA Parameters の双方に対応。ビット予算・Expressions Menu 編集・VRC Behaviour の編集。SDK が無くても動作します。
    link: /features/vrchat
    linkText: 詳しく見る
  - icon: 🗂️
    title: フレームとメモ
    details: ノードを囲むフレーム（グループ枠）と付箋メモでグラフを整理。レイヤーやコントローラーをまたいでコピーできます。
    link: /features/frames
    linkText: 詳しく見る
---

## 使う前に

::: warning 個人ツールです / このドキュメントは AI が書いています
DaerD は作成者 **yozorakurage** が自分の作業効率化のために作ったもので、**互換性の保証も動作保証もありません**。コードもドキュメントも、**ほぼ全てが AI によって作られています**。

このドキュメントは AI（Claude）が更新・保守していますが、更新されるのは管理者がそう指示したときだけです。**管理者はサボりがちなので、内容が最新でない可能性があります。**

その代わり **MIT License** なので、改変も再配布も商用利用も自由です。**あなたの責任でお使いください。**

→ [注意事項をすべて読む](/notice)
:::
