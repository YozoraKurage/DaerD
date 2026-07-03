import { defineConfig } from 'vitepress'

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
      { text: 'v0.7.2', link: 'https://github.com/YozoraKurage/DaerD/releases' }
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
            { text: '設定 (Preferences)', link: '/guide/settings' },
            { text: 'よくある質問', link: '/guide/faq' }
          ]
        }
      ],
      '/features/': [
        {
          text: '機能一覧',
          items: [
            { text: '概要', link: '/features/' },
            { text: 'グラフ編集', link: '/features/graph-editing' },
            { text: 'パラメータ型の自動変換', link: '/features/parameter-conversion' },
            { text: 'トランジションのコピー＆ペースト', link: '/features/transitions' },
            { text: 'カスケードリネーム', link: '/features/rename' },
            { text: 'フレームとメモ', link: '/features/frames' },
            { text: 'BlendTree 編集', link: '/features/blendtree' },
            { text: 'コントローラー解析', link: '/features/analysis' }
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
      message: 'MIT License のもとで公開されています。',
      copyright: 'Copyright © 2026 Yozolab'
    }
  }
})
