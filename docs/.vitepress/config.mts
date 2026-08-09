import { defineConfig } from 'vitepress'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

// ルート（main 由来）の package.json からバージョンを取得する。
// リリース Action が version をバンプし、それが docs ブランチへマージされると
// ここに自動で反映される（ナビのバージョン表記が main に追従する）。
const pkg = JSON.parse(
  readFileSync(fileURLToPath(new URL('../../package.json', import.meta.url)), 'utf-8')
)

// DaerD ドキュメントサイト設定（日本語）
export default defineConfig({
  lang: 'ja-JP',
  title: 'DaerD',
  description: 'Unity の AnimatorController を GraphView で置き換えるエディタ拡張 DaerD のドキュメント',
  lastUpdated: true,
  cleanUrls: true,
  head: [
    ['meta', { name: 'theme-color', content: '#3c8772' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'DaerD ドキュメント' }],
    ['meta', {
      property: 'og:description',
      content: 'Unity の AnimatorController を GraphView で置き換えるエディタ拡張 DaerD のドキュメント'
    }]
  ],

  themeConfig: {
    nav: [
      { text: 'ガイド', link: '/guide/', activeMatch: '/guide/' },
      { text: '機能', link: '/features/', activeMatch: '/features/' },
      { text: '注意事項', link: '/notice', activeMatch: '/notice' },
      { text: `v${pkg.version}`, link: 'https://github.com/YozoraKurage/DaerD/releases' }
    ],

    sidebar: {
      '/guide/': [
        {
          text: 'はじめに',
          items: [
            { text: 'DaerD とは', link: '/guide/' },
            { text: 'インストール', link: '/guide/installation' },
            { text: 'クイックスタート', link: '/guide/getting-started' }
          ]
        },
        {
          text: '使い方',
          items: [
            { text: '画面構成', link: '/guide/interface' },
            { text: 'ホーム画面', link: '/guide/home' },
            { text: '設定 (Preferences)', link: '/guide/settings' },
            { text: 'よくある質問', link: '/guide/faq' }
          ]
        }
      ],
      '/features/': [
        {
          text: '機能一覧',
          items: [{ text: '概要', link: '/features/' }]
        },
        {
          text: '編集',
          items: [
            { text: 'グラフ編集', link: '/features/graph-editing' },
            { text: 'BlendTree 編集', link: '/features/blendtree' },
            { text: 'レイヤー操作', link: '/features/layers' },
            { text: 'フレームとメモ', link: '/features/frames' }
          ]
        },
        {
          text: 'リファクタリング',
          items: [
            { text: 'パラメータ型の自動変換', link: '/features/parameter-conversion' },
            { text: 'カスケードリネーム', link: '/features/rename' },
            { text: 'トランジションのコピー＆ペースト', link: '/features/transitions' },
            { text: 'クリップとリパス', link: '/features/clips' }
          ]
        },
        {
          text: '検証と整理',
          items: [
            { text: 'コントローラー解析', link: '/features/analysis' },
            { text: 'クリーンアップ', link: '/features/cleanup' }
          ]
        },
        {
          text: '生成ツール',
          items: [
            { text: 'DBT ガジェット', link: '/features/dbt-gadgets' },
            { text: 'オブジェクトトグル', link: '/features/object-toggle' },
            { text: '巡回同期', link: '/features/async-sync' },
            { text: 'C# Recipe', link: '/features/recipe' }
          ]
        },
        {
          text: 'VRChat',
          items: [
            { text: 'VRC / NDMF 連携', link: '/features/vrchat' }
          ]
        }
      ]
    },

    outline: { label: '目次', level: [2, 3] },
    docFooter: { prev: '前へ', next: '次へ' },
    darkModeSwitchLabel: '外観',
    lightModeSwitchTitle: 'ライトモードに切り替え',
    darkModeSwitchTitle: 'ダークモードに切り替え',
    sidebarMenuLabel: 'メニュー',
    returnToTopLabel: 'トップに戻る',
    lastUpdated: {
      text: '最終更新',
      formatOptions: { dateStyle: 'medium' }
    },

    search: {
      provider: 'local',
      options: {
        translations: {
          button: { buttonText: '検索', buttonAriaLabel: '検索' },
          modal: {
            noResultsText: '一致する結果が見つかりませんでした',
            resetButtonTitle: '検索をリセット',
            footer: {
              selectText: '選択',
              navigateText: '移動',
              closeText: '閉じる'
            }
          }
        }
      }
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/YozoraKurage/DaerD' }
    ],

    editLink: {
      pattern: 'https://github.com/YozoraKurage/DaerD/edit/docs/docs/:path',
      text: 'このページを編集'
    },

    footer: {
      message:
        'MIT License のもとで公開されています。動作・互換性の保証はありません — '
        + '<a href="/notice">注意事項</a>。'
        + 'このドキュメントは AI によって更新・保守されており、最新でない場合があります。',
      copyright: 'Copyright © 2026 Yozolab'
    }
  }
})
