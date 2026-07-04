using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    enum DaerDLanguage
    {
        Auto = 0,
        English = 1,
        Japanese = 2,
    }

    /// <summary>
    /// Minimal string table for the editor UI. Keys are the English strings themselves, so
    /// call sites read naturally and an untranslated string just falls through to English.
    /// </summary>
    static class L
    {
        const string PrefKey = "Yozolab.DaerD.Language";

        /// <summary>Fired when the user changes the language preference; UI rebuilds itself.</summary>
        public static event Action LanguageChanged;

        public static DaerDLanguage Language
        {
            get => (DaerDLanguage)EditorPrefs.GetInt(PrefKey, (int)DaerDLanguage.Auto);
            set
            {
                if (value == Language) return;
                EditorPrefs.SetInt(PrefKey, (int)value);
                LanguageChanged?.Invoke();
            }
        }

        public static bool IsJapanese =>
            Language == DaerDLanguage.Japanese ||
            (Language == DaerDLanguage.Auto && Application.systemLanguage == SystemLanguage.Japanese);

        public static string Tr(string english) =>
            IsJapanese && Ja.TryGetValue(english, out var ja) ? ja : english;

        public static string Tr(string english, params object[] args) =>
            string.Format(Tr(english), args);

        static readonly Dictionary<string, string> Ja = new Dictionary<string, string>
        {
            // ---- toolbar ---------------------------------------------------
            ["Select Sync"] = "選択同期",
            ["Sync the Animation window's clip to the selected State's AnimationClip"] =
                "選択したステートの AnimationClip を Animation ウィンドウに同期します",
            ["Preview"] = "プレビュー",
            ["Auto-toggle the Animation window's Preview on clip change. " +
             "Implies Select Sync. Requires a scene GameObject with an Animator " +
             "running this controller to be selected — Unity's preview can't run " +
             "without a target."] =
                "クリップ切り替え時に Animation ウィンドウの Preview を自動で入れ直します。" +
                "選択同期も同時に ON になります。このコントローラーを実行する Animator を持つ" +
                "シーン上の GameObject を選択している必要があります。",
            ["Frame All"] = "全体表示",
            ["Analyze"] = "解析",
            ["Settings"] = "設定",
            ["Search states (name or motion)"] = "ステート検索（名前 / モーション名）",
            ["No matches."] = "一致するものがありません。",
            ["Close tab"] = "タブを閉じる",

            // ---- analyzer categories ---------------------------------------
            ["Unused Parameter"] = "未使用パラメーター",
            ["Invalid Condition"] = "無効な条件",
            ["Dead Transition"] = "発火しない遷移",
            ["Unreachable State"] = "到達不能ステート",
            ["Duplicate Name"] = "名前の重複",
            ["Terminal States"] = "行き止まりステート",
            ["WriteDefaults"] = "Write Defaults 混在",
            ["Missing Motion"] = "モーション未設定",
            ["Empty Layer"] = "空のレイヤー",
            ["Layer Weight"] = "レイヤーウェイト",
            ["Missing Behaviour"] = "Behaviour 参照切れ",
            ["Duplicate Condition"] = "条件の重複",

            // ---- analyzer messages -----------------------------------------
            ["Parameter '{0}' is never referenced."] =
                "パラメーター '{0}' はどこからも参照されていません。",
            ["Condition references missing parameter '{0}'."] =
                "条件が存在しないパラメーター '{0}' を参照しています。",
            ["Mode '{0}' is invalid for {1} parameter '{2}'."] =
                "モード '{0}' は {1} 型パラメーター '{2}' に対して無効です。",
            ["Transition {0} has no conditions and no exit time; it can never fire."] =
                "遷移 {0} には条件も Exit Time もないため、発火することがありません。",
            ["State '{0}' has no incoming transition and is not a default state."] =
                "ステート '{0}' には入ってくる遷移がなく、デフォルトステートでもありません。",
            ["State name '{0}' is used more than once in '{1}'."] =
                "ステート名 '{0}' が '{1}' 内で複数回使われています。",
            ["Layer '{0}': once entered, '{1}' can never be left (no outgoing transition or exit)."] =
                "レイヤー '{0}': 一度 '{1}' に入ると抜け出せません（外向きの遷移も Exit もありません）。",
            ["Layer '{0}' mixes Write Defaults ON and OFF across its states."] =
                "レイヤー '{0}' のステートで Write Defaults の ON と OFF が混在しています。",
            ["State '{0}' has no motion assigned."] =
                "ステート '{0}' にモーションが設定されていません。",
            ["Blend tree '{0}' in state '{1}' has a child slot with no motion."] =
                "ステート '{1}' のブレンドツリー '{0}' にモーション未設定の子スロットがあります。",
            ["Layer '{0}' contains no states."] =
                "レイヤー '{0}' にステートがありません。",
            ["Layer '{0}' has default weight 0; it has no effect until its weight is raised at runtime."] =
                "レイヤー '{0}' のデフォルトウェイトが 0 のため、実行時にウェイトを上げるまで効果がありません。",
            ["State '{0}' has a missing (null) behaviour script."] =
                "ステート '{0}' に参照が壊れた（null の）Behaviour があります。",
            ["State machine '{0}' has a missing (null) behaviour script."] =
                "ステートマシン '{0}' に参照が壊れた（null の）Behaviour があります。",
            ["Transition {0} has duplicate conditions."] =
                "遷移 {0} に同じ内容の条件が重複しています。",

            // ---- analyzer fixes --------------------------------------------
            ["Delete"] = "削除",
            ["Fix"] = "修正",
            ["Delete this unused parameter"] = "この未使用パラメーターを削除します",
            ["Delete this transition"] = "この遷移を削除します",
            ["Remove the duplicate conditions"] = "重複している条件を取り除きます",
            ["Remove the missing behaviour entries"] = "参照が壊れた Behaviour エントリを取り除きます",

            // ---- overview / analysis UI ------------------------------------
            ["Controller"] = "コントローラー",
            ["Name"] = "名前",
            ["Layers"] = "レイヤー数",
            ["Parameters"] = "パラメーター数",
            ["Write Defaults"] = "Write Defaults",
            ["Bulk-set every state. Layers containing only Direct blend trees stay ON."] =
                "全ステートを一括設定します。Direct ブレンドツリーのみのレイヤーは ON のまま維持されます。",
            ["Set All ON"] = "すべて ON",
            ["Set All OFF"] = "すべて OFF",
            ["Set Write Defaults ON for every state in this controller?"] =
                "このコントローラーの全ステートの Write Defaults を ON にしますか？",
            ["Set Write Defaults OFF for every state?\n\nLayers that contain only Direct blend trees are kept ON."] =
                "全ステートの Write Defaults を OFF にしますか？\n\nDirect ブレンドツリーのみのレイヤーは ON のまま維持されます。",
            ["Set ON"] = "ON にする",
            ["Set OFF"] = "OFF にする",
            ["Cancel"] = "キャンセル",
            ["Analyze Controller"] = "コントローラーを解析",
            ["Audit this controller for unused parameters, broken conditions, unreachable states and more."] =
                "未使用パラメーター・壊れた条件・到達不能ステートなどをまとめて診断します。",
            ["No issues found."] = "問題は見つかりませんでした。",
            ["{0} error(s)"] = "エラー {0} 件",
            ["{0} warning(s)"] = "警告 {0} 件",
            ["{0} info"] = "情報 {0} 件",
            ["All {0} issue(s) are hidden by the filter above."] =
                "{0} 件すべてが上のフィルターで非表示になっています。",
            ["Ping"] = "表示",
            ["Copy"] = "コピー",
            ["Copy the full report to the clipboard"] = "レポート全文をクリップボードにコピーします",
            ["Highlight this object in the Project / graph"] =
                "対象のオブジェクトを Project / グラフ上でハイライトします",

            // ---- settings ---------------------------------------------------
            ["Language"] = "言語 (Language)",
            ["Auto (System Language)"] = "自動（システム言語）",
            ["Display language for daerD windows and analysis results."] =
                "daerD のウィンドウと解析結果の表示言語です。",
            ["New Transition Defaults"] = "新規トランジションのデフォルト値",
            ["Apply To New Transitions"] = "新規トランジションに適用",
            ["New State Defaults"] = "新規ステートのデフォルト値",
            ["Apply To New States"] = "新規ステートに適用",
            ["Graph Display"] = "グラフ表示",
            ["Condition Labels On Edges"] = "エッジ上の条件ラベル",
            ["Show a one-line condition summary on transition edges. Takes effect on the next graph rebuild."] =
                "遷移エッジに条件の要約を 1 行で表示します。次のグラフ再構築時に反映されます。",
            ["State Badges (WD / B)"] = "ステートバッジ (WD / B)",
            ["Mark states with Write Defaults ON and states carrying StateMachineBehaviours."] =
                "Write Defaults が ON のステートと StateMachineBehaviour を持つステートに印を付けます。",
            ["Behavior"] = "動作",
            ["Intercept .controller Double-Click"] = ".controller のダブルクリックを引き継ぐ",
            ["When on, double-clicking an Animator Controller opens this editor instead of Unity's window."] =
                "ON にすると、Animator Controller のダブルクリックで Unity 標準の代わりにこのエディタが開きます。",
            ["Reset To Defaults"] = "デフォルトに戻す",
        };
    }
}
