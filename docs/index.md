---
layout: home

hero:
  name: DaerD
  text: AnimatorController を、もっと直感的に。
  tagline: Unity の AnimatorController エディタを GraphView ベースで置き換えるエディタ拡張。パラメータ型の自動変換、トランジションのコピー＆ペースト、カスケードリネーム、コントローラー解析などを搭載。
  actions:
    - theme: brand
      text: はじめる
      link: /guide/
    - theme: alt
      text: インストール
      link: /guide/installation
    - theme: alt
      text: GitHub
      link: https://github.com/YozoraKurage/DaerD

features:
  - icon: 🕸️
    title: GraphView ベースの編集
    details: ステート・トランジション・サブステートマシン・BlendTree を、応答性の高いグラフ上で編集。複数のコントローラーをタブで同時に開けます。
    link: /features/graph-editing
    linkText: 詳しく見る
  - icon: 🔀
    title: パラメータ型の自動変換
    details: パラメータの型を変更すると、それを参照するすべての条件を自動で書き換え。適用前にプレビューで差分を確認できます。
    link: /features/parameter-conversion
    linkText: 詳しく見る
  - icon: 📋
    title: トランジションのコピー＆ペースト
    details: トランジションの設定・条件をまるごとコピー＆ペースト。ドメインリロードをまたいでも内容が保持されます。
    link: /features/transitions
    linkText: 詳しく見る
  - icon: ✏️
    title: カスケードリネーム
    details: パラメータ名を変更すると参照するすべての条件を追従。ステートの AnimationClip 名の変更は .anim アセットにも反映されます。
    link: /features/rename
    linkText: 詳しく見る
  - icon: 🗂️
    title: フレームとメモ
    details: ノードを囲むフレーム（グループ枠）と付箋メモでグラフを整理。フレームごとの複製にも対応します。
    link: /features/frames
    linkText: 詳しく見る
  - icon: 🔍
    title: コントローラー解析
    details: 未使用パラメータ、不正な条件、到達不能ステート、Write Defaults の混在などを検出し、一括修正を提供します。
    link: /features/analysis
    linkText: 詳しく見る
---
