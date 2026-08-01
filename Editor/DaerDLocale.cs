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

        // Tr runs many times per IMGUI repaint; resolve the language once instead of hitting
        // EditorPrefs on every call. Invalidated by the setter, reset naturally on domain reload.
        static bool? s_isJapanese;

        /// <summary>Fired when the user changes the language preference; UI rebuilds itself.</summary>
        public static event Action LanguageChanged;

        public static DaerDLanguage Language
        {
            get => (DaerDLanguage)EditorPrefs.GetInt(PrefKey, (int)DaerDLanguage.Auto);
            set
            {
                if (value == Language) return;
                EditorPrefs.SetInt(PrefKey, (int)value);
                s_isJapanese = null;
                LanguageChanged?.Invoke();
            }
        }

        public static bool IsJapanese
        {
            get
            {
                if (!s_isJapanese.HasValue)
                {
                    var language = Language;
                    s_isJapanese = language == DaerDLanguage.Japanese ||
                        (language == DaerDLanguage.Auto && Application.systemLanguage == SystemLanguage.Japanese);
                }
                return s_isJapanese.Value;
            }
        }

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
            ["Direct Blend Tree"] = "Direct ブレンドツリー",

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
            ["State '{0}' has Write Defaults OFF and no motion; animated properties freeze at their last value while it plays."] =
                "ステート '{0}' は Write Defaults が OFF なのにモーションが未設定です。再生中、アニメーション対象のプロパティが直前の値のまま固まります。",
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
            ["State '{0}' plays a Direct blend tree but has Write Defaults OFF."] =
                "ステート '{0}' は Direct ブレンドツリーを再生しますが、Write Defaults が OFF です。",
            ["Direct blend tree '{0}' has a child with no weight parameter; that child never plays."] =
                "Direct ブレンドツリー '{0}' にウェイトパラメーター未設定の子があります。その子は再生されません。",
            ["Direct blend tree '{0}' weights a child with missing parameter '{1}'."] =
                "Direct ブレンドツリー '{0}' の子が、存在しないパラメーター '{1}' をウェイトに使用しています。",
            ["Weight parameter '{1}' of Direct blend tree '{0}' is not a Float."] =
                "Direct ブレンドツリー '{0}' のウェイトパラメーター '{1}' が Float ではありません。",

            // ---- analyzer fixes --------------------------------------------
            ["Delete"] = "削除",
            ["Fix"] = "修正",
            ["Fill"] = "穴埋め",
            ["Delete this unused parameter"] = "この未使用パラメーターを削除します",
            ["Delete this transition"] = "この遷移を削除します",
            ["Remove the duplicate conditions"] = "重複している条件を取り除きます",
            ["Remove the missing behaviour entries"] = "参照が壊れた Behaviour エントリを取り除きます",
            ["Turn Write Defaults ON for this state"] = "このステートの Write Defaults を ON にします",
            ["Assign this controller's Empty clip"] =
                "このコントローラーの Empty クリップを割り当てます",
            ["Fill the empty child slots with this controller's Empty clip"] =
                "モーション未設定の子スロットにこのコントローラーの Empty クリップを割り当てます",

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
            ["DaerD Analyzer"] = "DaerD 解析",
            ["Assign an Animator Controller to analyze."] =
                "解析する Animator Controller を指定してください。",
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

            // ---- clip index / cleanup --------------------------------------
            ["Empty Clip"] = "Empty クリップ",
            ["Stored with this controller. New states are created with it, and the analyzer's Fill fix assigns it to states with no motion."] =
                "このコントローラーと一緒に保存されます。新規ステート作成時に自動で設定され、解析の「穴埋め」修正でモーション未設定のステートやブレンドツリーの空スロットにも割り当てられます。",
            ["List Clips"] = "クリップ一覧",
            ["DaerD Clips"] = "DaerD クリップ一覧",
            ["Assign an Animator Controller to list its animation clips."] =
                "クリップを一覧表示する Animator Controller を指定してください。",
            ["Refresh"] = "更新",
            ["List every AnimationClip this controller references and the states that use it."] =
                "このコントローラーが参照しているすべての AnimationClip と、それを使っているステートを一覧表示します。",
            ["No clips are referenced by this controller."] =
                "このコントローラーが参照しているクリップはありません。",
            ["{0} clip(s) referenced."] = "参照クリップ {0} 件。",
            ["(embedded)"] = "（内包アセット）",
            ["Jump"] = "移動",
            ["Open the layer and select the state that uses this clip"] =
                "このクリップを使っているステートを開いて選択します",
            ["Replace With"] = "差し替え先",
            ["Swap every use of this clip in this controller for the picked clip (undoable)"] =
                "このコントローラー内でこのクリップを使っている箇所を、指定したクリップにすべて差し替えます（Undo 可）",
            ["Cleanup"] = "クリーンアップ",
            ["Find sub-assets stored in the .controller file that nothing references any more."] =
                "もうどこからも参照されていないのに .controller ファイル内に残っているサブアセット（ゴミ）を検出します。",
            ["Scan For Leftovers"] = "ゴミを検索",
            ["Blend trees, clips and states deleted from the graph can survive as invisible sub-assets; find them."] =
                "グラフから削除したブレンドツリー・クリップ・ステートは、見えないサブアセットとしてファイル内に残ることがあります。それらを検索します。",
            ["(unsaved controller — nothing to scan)"] =
                "（未保存のコントローラーのため、スキャンできません）",
            ["No leftover sub-assets found."] = "未使用のサブアセットは見つかりませんでした。",
            ["{0} leftover sub-asset(s) in this .controller file."] =
                "この .controller ファイルに未使用のサブアセットが {0} 件残っています。",
            ["Delete All"] = "すべて削除",
            ["Delete this leftover sub-asset from the .controller file"] =
                "この未使用サブアセットを .controller ファイルから削除します",
            ["Delete {0} leftover sub-asset(s) from '{1}'?\n\nNothing in this file references them. This can be undone."] =
                "'{1}' から未使用のサブアセット {0} 件を削除しますか？\n\nこのファイル内のどこからも参照されていません。この操作は Undo で取り消せます。",

            // ---- AAP smoothing / DBT -----------------------------------------
            ["This controller has no Float parameters to smooth."] =
                "このコントローラーにはスムーズ化できる Float パラメーターがありません。",
            ["Output Parameter"] = "出力パラメーター",
            ["Smoothing Parameter"] = "スムージング量パラメーター",
            ["Default Smoothing"] = "スムージング量の初期値",
            ["0 = follow instantly; closer to 1 = smoother and slower. Stored as the smoothing parameter's default value."] =
                "0 で即追従、1 に近いほど滑らかで遅くなります。スムージング量パラメーターの初期値として保存されます。",
            ["Range Min"] = "最小値",
            ["Range Max"] = "最大値",
            ["Target Layer"] = "追加先レイヤー",
            ["Create new layer"] = "新規レイヤーを作成",
            ["New Layer Name"] = "新規レイヤー名",
            ["Create"] = "作成",
            ["No controller."] = "コントローラーがありません。",
            ["The source must be an existing Float parameter."] =
                "元パラメーターには既存の Float パラメーターを指定してください。",
            ["The output parameter needs a name different from the source."] =
                "出力パラメーターには元パラメーターと異なる名前が必要です。",
            ["A parameter named '{0}' already exists."] =
                "パラメーター '{0}' は既に存在します。",
            ["The smoothing parameter needs its own name."] =
                "スムージング量パラメーターには他と重複しない名前が必要です。",
            ["Parameter '{0}' exists but is not a Float."] =
                "パラメーター '{0}' は存在しますが Float ではありません。",
            ["Range Min must be smaller than Range Max."] =
                "最小値は最大値より小さくしてください。",
            ["The target layer no longer exists."] =
                "追加先レイヤーが存在しません。",
            ["The target layer must be empty or contain only Direct blend tree states."] =
                "追加先レイヤーは空か、Direct ブレンドツリーのステートのみで構成されている必要があります。",
            ["The new layer needs a name."] = "新規レイヤーの名前を入力してください。",
            ["Every state in this layer is a Direct blend tree"] =
                "このレイヤーのステートはすべて Direct ブレンドツリーです",
            ["DBT Gadget"] = "DBT ガジェット",
            ["Adds a Direct blend tree gadget that computes the picked operation every frame. The generated clips and trees are stored as sub-assets of this controller."] =
                "選択した演算を毎フレーム計算する Direct ブレンドツリーの仕掛けを追加します。生成されるクリップとツリーはこのコントローラーのサブアセットとして保存されます。",
            ["Operation"] = "演算",
            ["Input A"] = "入力 A",
            ["Input B"] = "入力 B",
            ["Output Min"] = "出力の最小値",
            ["Output Max"] = "出力の最大値",
            ["Input Min"] = "入力の最小値",
            ["Input Max"] = "入力の最大値",
            ["Threshold"] = "しきい値",
            ["The second input must be an existing Float parameter."] =
                "入力 B には既存の Float パラメーターを指定してください。",
            ["The output parameter needs a name different from the inputs."] =
                "出力パラメーターには入力と異なる名前が必要です。",
            ["Input Min must be smaller than Input Max."] =
                "入力の最小値は最大値より小さくしてください。",
            ["output = lerp(input, output, smoothing) — exponential smoothing recalculated every frame."] =
                "output = lerp(入力, 出力, スムージング量)。毎フレーム再計算される指数スムージングです。",
            ["output = A + B. Positive values only (Direct weights clamp at 0); use Add (Ranged) for signed inputs."] =
                "output = A + B。正の値専用です（Direct のウェイトは 0 未満にならないため）。負の値を扱う場合は Add (Ranged) を使用してください。",
            ["output = A + B over the given range; works with negative values."] =
                "指定した範囲で output = A + B を計算します。負の値にも対応します。",
            ["output = A - B. Positive values only; use Sub (Ranged) for signed inputs."] =
                "output = A - B。正の値専用です。負の値を扱う場合は Sub (Ranged) を使用してください。",
            ["output = A - B over the given range; use a symmetric range (Min = -Max)."] =
                "指定した範囲で output = A - B を計算します。範囲は対称（最小値 = -最大値）にしてください。",
            ["output = A × B via nested Direct trees. Positive values only."] =
                "Direct ツリーのネストで output = A × B を計算します。正の値専用です。",
            ["output = A AND B, for 0/1 inputs."] =
                "output = A AND B（0/1 の入力用）。",
            ["output = A OR B, for 0/1 inputs."] =
                "output = A OR B（0/1 の入力用）。",
            ["output = 1 - input, for 0/1 inputs."] =
                "output = 1 - 入力（0/1 の入力用）。",
            ["output = 1 when the input is at or above the threshold, else 0."] =
                "入力がしきい値以上のとき output = 1、未満のとき 0 になります。",
            ["Linearly remaps the input range to the output range (reversed output ranges invert the slope)."] =
                "入力範囲を出力範囲へ線形にリマップします（出力範囲を逆順にすると傾きが反転します）。",

            // ---- object toggle wizard --------------------------------------
            ["Object Toggle"] = "オブジェクトトグル",
            ["Object Toggle…"] = "オブジェクトトグル…",
            ["Generate ON/OFF clips for picked GameObjects and the layer or Direct blend tree machinery that plays them."] =
                "選択した GameObject の ON/OFF クリップと、それを再生するレイヤーまたは Direct ブレンドツリーの仕掛けを生成します。",
            ["Creates ON/OFF clips that toggle the listed GameObjects and wires them to a parameter. The clips are saved next to the controller asset."] =
                "リストした GameObject を切り替える ON/OFF クリップを作成し、パラメーターに配線します。クリップはコントローラーアセットと同じフォルダに保存されます。",
            ["Toggle Name"] = "トグル名",
            ["Wiring"] = "配線方式",
            ["Adds a layer with OFF/ON states and instant transitions driven by a Bool parameter."] =
                "OFF/ON の 2 ステートと Bool パラメーターによる即時遷移を持つレイヤーを追加します。",
            ["Adds a 1D tree (0 = OFF, 1 = ON) driven by a Float parameter to a Direct blend tree layer — many toggles can share one layer."] =
                "Float パラメーターで駆動する 1D ツリー（0 = OFF、1 = ON）を Direct ブレンドツリーレイヤーに追加します。複数のトグルで 1 レイヤーを共有できます。",
            ["Parameter"] = "パラメーター",
            ["Default ON"] = "初期状態 ON",
            ["Stored as the parameter's default value; the layer also starts on the ON state."] =
                "パラメーターの初期値として保存されます。レイヤー方式では ON ステートから開始します。",
            ["Uses the existing '{0}' parameter."] =
                "既存のパラメーター '{0}' を使用します。",
            ["Parameter '{0}' exists but is a {1} — pick another name or wiring."] =
                "パラメーター '{0}' は {1} 型として既に存在します。別の名前か配線方式を選んでください。",
            ["Target Objects"] = "対象オブジェクト",
            ["Path Root"] = "パスのルート",
            ["The GameObject holding the Animator; dropped objects get their path relative to it."] =
                "Animator を持つ GameObject です。ドロップしたオブジェクトのパスはここからの相対で計算されます。",
            ["Add Object"] = "オブジェクト追加",
            ["Drop a scene GameObject to add it as a target."] =
                "シーン上の GameObject をドロップすると対象に追加されます。",
            ["Add Selection"] = "選択中を追加",
            ["Add every GameObject selected in the Hierarchy."] =
                "Hierarchy で選択中のすべての GameObject を追加します。",
            ["Active"] = "ON で表示",
            ["Checked: the object is active while the toggle is ON. Unchecked inverts it."] =
                "チェック時はトグル ON でオブジェクトが有効になります。外すと反転します。",
            ["No targets yet. Drop GameObjects above or type hierarchy paths (relative to the Animator root)."] =
                "対象がまだありません。上の欄に GameObject をドロップするか、階層パス（Animator ルートからの相対）を入力してください。",
            ["'{0}' is not a child of the path root '{1}'."] =
                "'{0}' はパスのルート '{1}' の子ではありません。",
            ["The path root itself can't be toggled — animations can't re-enable the object that hosts the Animator."] =
                "パスのルート自体はトグルできません。Animator を持つオブジェクトを無効にすると、アニメーションで再有効化できなくなります。",
            ["The toggle needs a name."] = "トグルの名前を入力してください。",
            ["The parameter needs a name."] = "パラメーターの名前を入力してください。",
            ["Parameter '{0}' exists but is not a Bool."] =
                "パラメーター '{0}' は存在しますが Bool ではありません。",
            ["Add at least one target object."] =
                "対象オブジェクトを 1 つ以上追加してください。",
            ["Every target needs a hierarchy path."] =
                "すべての対象に階層パスが必要です。",
            ["Target path '{0}' is listed more than once."] =
                "対象パス '{0}' が重複しています。",

            // ---- network sync ----------------------------------------------
            ["Network Sync"] = "ネットワーク同期",
            ["Network Sync…"] = "ネットワーク同期…",
            ["Generate the local-driver + remote-mirror structure that syncs this layer to other VRChat players."] =
                "このレイヤーを他の VRChat プレイヤーへ同期させる、ローカル側 Driver + リモート側ミラーの構造を生成します。",
            ["Makes a layer driven by local-only parameters visible to remote players: each state writes its index into a synced parameter via a Parameter Driver, and generated remote states mirror the layer for everyone else. IsLocal separates the two halves."] =
                "ローカル専用パラメーターで動くレイヤーをリモートプレイヤーにも見えるようにします。各ステートが Parameter Driver で自分の番号を同期パラメーターに書き込み、生成されるリモートステート群がその値を条件にレイヤーをミラー再生します。ローカル側とリモート側は IsLocal で分離されます。",
            ["Encoding"] = "エンコード",
            ["{0} bit(s): {1} … {2}"] = "{0} bit: {1} … {2}",
            ["Sync Parameter"] = "同期パラメーター",
            ["Remote Wiring"] = "リモート遷移方式",
            ["Any State: N transitions from the Any State node. All-to-All: N×(N-1) transitions between the mirror states."] =
                "Any State: AnyState ノードから N 本の遷移。All-to-All: ミラーステート間の総当たり N×(N-1) 本の遷移。",
            ["Preserve Transition Timing"] = "遷移タイミングを引き継ぐ",
            ["Copy blend duration and interruption settings from each state's first outgoing transition (exit time stays off). Off generates instant transitions."] =
                "各ステートの最初の遷移からブレンド時間や割り込み設定をコピーします（Exit Time は常に OFF）。OFF の場合は即時遷移を生成します。",
            ["Remote State Prefix"] = "リモートステート接頭辞",
            ["Strip Behaviours On Mirrors"] = "ミラーの Behaviour を除去",
            ["Remote copies drop their StateMachineBehaviours so drivers and audio don't fire twice."] =
                "リモート側の複製から StateMachineBehaviour を除去し、Driver や Audio の二重発火を防ぎます。",
            ["Pack Into Sub-State Machine"] = "サブステートマシンに格納",
            ["Group the generated remote states into a 'Network' sub-state machine to keep the layer readable."] =
                "生成されるリモートステート群を 'Network' サブステートマシンにまとめ、レイヤーを見やすく保ちます。",
            ["Own Driver Instance"] = "専用 Driver インスタンス",
            ["Write the sync values through a dedicated Parameter Driver named 'Network' instead of appending rows to a driver already on the state."] =
                "同期値の書き込みを、ステートに既にある Driver へ追記せず 'Network' という名前の専用 Parameter Driver で行います。",
            ["The target layer needs at least two states to sync."] =
                "同期するにはレイヤーに 2 つ以上のステートが必要です。",
            ["The sync parameter needs a name."] =
                "同期パラメーターの名前を入力してください。",
            ["The remote state prefix must not be empty."] =
                "リモートステートの接頭辞を入力してください。",
            ["Int encoding supports up to 255 states."] =
                "Int エンコードは最大 255 ステートまで対応します。",
            ["Bool encoding supports up to 8 bits (256 states)."] =
                "Bool エンコードは最大 8 bit（256 ステート）まで対応します。",
            ["Parameter '{0}' exists but is not an Int."] =
                "パラメーター '{0}' は存在しますが Int ではありません。",
            ["VRChat SDK not found — the Parameter Driver behaviour is required."] =
                "VRChat SDK が見つかりません。Parameter Driver ビヘイビアが必要です。",
            ["Some states already carry the remote prefix — this layer may already be synced."] =
                "リモート接頭辞の付いたステートが既にあります。このレイヤーは同期済みかもしれません。",
            ["Sub-state machines are not mirrored; only root-level states are synced."] =
                "サブステートマシンはミラーされません。同期対象はルート直下のステートのみです。",
            ["The existing parameter '{0}' will be reused."] =
                "既存のパラメーター '{0}' を再利用します。",

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
