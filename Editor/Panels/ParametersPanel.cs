using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Edit;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD
{
    /// <summary>Parameter CRUD, with auto type conversion and cascade rename.</summary>
    class ParametersPanel : PanelBase
    {
        readonly ListReorder _reorder = new ListReorder();
        string _search = string.Empty;

        // Parameter store (VRC expression parameters asset or MA Parameters component) the
        // user explicitly associated with this controller (persisted in GraphFrameData).
        // Never auto-resolved — DaerD is also used on NDMF gimmick controllers that belong
        // to no avatar. The entry map is rebuilt every DrawContent — stores are small and
        // edits elsewhere must show up immediately.
        ParameterStore _store;
        bool _storeLoaded;
        Dictionary<string, VrcExpressionParameters.Entry> _exprEntries;
        // Rebuilt beside the entry map and for the same reason: an MA row renamed in MA's own
        // inspector has to show its new built name here without anything of DaerD's being
        // touched first. Working it out is a walk up the object's ancestors — a handful of
        // components, not a scan of the project — so it belongs with the read above rather than
        // behind an invalidation of its own.
        Dictionary<string, string> _builtNames;

        // Two whole-controller scans the rows need. Both walk far more than the parameter list
        // — the AAP set reads every clip, the unused set every transition condition, blend
        // tree, state parameter and driver entry — and DrawContent runs for every repaint the
        // pointer causes. So they are computed once and dropped when something that could
        // change them happens.
        HashSet<string> _aapParams;
        HashSet<string> _unusedParams;
        AnimatorController _scanCacheController;

        /// <summary>Session clipboard for one parameter definition; survives controller
        /// switches so parameters can be copied across open tabs.</summary>
        static AnimatorControllerParameter s_parameterClipboard;

        /// <summary>Show runtime values while the editor plays. Session-static like the
        /// analyzer's severity filter — a display preference, not worth an EditorPref. On by
        /// default: during play mode the defaults are the less useful of the two.</summary>
        static bool s_live = true;

        static readonly GUIContent FindContent = new GUIContent("?",
            "Find where this parameter is used (click to list every usage)");

        public ParametersPanel(DaerDContext context)
            : base(context, "Parameters")
        {
            context.ControllerChanged += Refresh;
            context.ParametersChanged += Refresh;
            context.ControllerChanged += InvalidateStore;
            // The store slot is also editable from the home screen, which announces the change
            // as a parameter change — the cached wrapper here would otherwise stay stale.
            context.ParametersChanged += InvalidateStore;
            context.GraphStructureChanged += DropScans;
            context.ParametersChanged += DropScans;
            // A blend tree edit moves which parameter drives it, which is exactly what the
            // unused set is counting.
            context.BlendTreeChanged += DropScans;
        }

        void DropScans()
        {
            _aapParams = null;
            _unusedParams = null;
        }

        void InvalidateStore()
        {
            _storeLoaded = false;
            _store = null;
        }

        /// <summary>
        /// Toolbar row: Add plus the filter box. Lives outside the scroll view so a long
        /// parameter list can't push it off screen. Add is pinned to the LEFT so a narrow
        /// panel clips the search field, not the button.
        /// </summary>
        protected override void DrawPinnedHeader()
        {
            if (Context?.Controller == null) return;
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.Tr("Add"), EditorStyles.toolbarButton, GUILayout.Width(40)))
                ShowAddMenu();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();
            DrawLiveBar();
        }

        /// <summary>
        /// The play-mode row: which Animator is being read, and the switch back to editing the
        /// controller's defaults. Only drawn while the editor plays, so the panel looks exactly
        /// as it always has the rest of the time.
        /// </summary>
        void DrawLiveBar()
        {
            if (!EditorApplication.isPlaying) return;
            var live = Context.Live;

            EditorGUILayout.BeginHorizontal();
            s_live = GUILayout.Toggle(s_live, new GUIContent(L.Tr("Live"),
                    L.Tr("Show what the running Animator holds instead of the controller's defaults.")),
                EditorStyles.miniButton, GUILayout.Width(50));
            var shown = live.Pinned != null ? live.Pinned : live.Current;
            var picked = (Animator)EditorGUILayout.ObjectField(shown, typeof(Animator), true,
                GUILayout.MinWidth(0));
            if (picked != shown) live.Pinned = picked;
            EditorGUILayout.EndHorizontal();

            if (!s_live) return;
            if (live.IsLive)
                EditorGUILayout.LabelField(
                    L.Tr("Runtime values. Editing one writes to the Animator, not to the controller asset."),
                    EditorStyles.centeredGreyMiniLabel);
            else if (live.Ambiguous)
                EditorGUILayout.HelpBox(
                    L.Tr("Several Animators in the scene run this controller. Pick the one to read above."),
                    MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    L.Tr("No Animator in the scene is running this controller."), MessageType.Info);
        }

        protected override void DrawContent()
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;

            var unused = UnusedParams(controller);

            if (!_storeLoaded)
            {
                _store = ParameterStore.Of(controller);
                _storeLoaded = true;
            }
            _exprEntries = null;
            _builtNames = null;
            if (_store != null)
            {
                _exprEntries = new Dictionary<string, VrcExpressionParameters.Entry>();
                foreach (var entry in _store.Read())
                    _exprEntries[entry.name] = entry;
                _builtNames = _store.EffectiveNames();
            }

            DrawVrcBudget();
            EditorGUILayout.Space(2);

            // Maps each drawn (search-filtered) row back to its index in the full array.
            var visibleReal = new List<int>();
            _reorder.Begin();
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (!string.IsNullOrEmpty(_search) &&
                    p.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var rowRect = EditorGUILayout.BeginHorizontal();
                _reorder.DrawHandle();
                visibleReal.Add(i);

                var prevColor = GUI.color;
                if (unused.Contains(p.name)) GUI.color = DaerDColors.Warning;
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.DelayedTextField(p.name, GUILayout.MinWidth(90));
                if (EditorGUI.EndChangeCheck() && newName != p.name && !string.IsNullOrEmpty(newName))
                {
                    if (!ParameterRenamer.Rename(controller, p.name, newName))
                        EditorUtility.DisplayDialog(L.Tr("Rename Failed"),
                            L.Tr("A parameter named '{0}' already exists.", newName), L.Tr("OK"));
                    else
                    {
                        _store?.Rename(p.name, newName);
                        // A menu whose controls name this parameter would be left pointing at a
                        // name nothing answers to. Nothing assigns that association any more
                        // (see GraphFrameData.expressionsMenu) — this reaches controllers that
                        // were given a menu while the slot existed, and follows a rename through
                        // for them rather than breaking their menu on the way out.
                        var rootMenu = GraphFrameData.GetExpressionsMenu(controller);
                        if (VrcMenuAccess.Is(rootMenu))
                            VrcMenuAccess.RenameParameterReferences(rootMenu, p.name, newName);
                        OfferSiblingRename(controller, p.name, newName);
                    }
                    Context.NotifyParametersChanged();
                    Context.NotifyGraphStructureChanged();
                    GUIUtility.ExitGUI();
                }
                GUI.color = prevColor;

                EditorGUI.BeginChangeCheck();
                var newType = (AnimatorControllerParameterType)EditorGUILayout.EnumPopup(p.type, GUILayout.Width(66));
                if (EditorGUI.EndChangeCheck() && newType != p.type)
                {
                    HandleTypeChange(p.name, newType);
                    GUIUtility.ExitGUI();
                }

                // The clip scan is a guess about what will happen; while something is running,
                // the animation system's own answer is available and outranks it.
                bool driven = s_live && Context.Live.IsCurveDriven(p.name);
                if (driven || AapParams(controller).Contains(p.name))
                    GUILayout.Label(new GUIContent("AAP", driven
                        ? L.Tr("An animation clip is driving this right now")
                        : L.Tr("Driven by an animation clip (Animator-Animated Parameter)")),
                        EditorStyles.centeredGreyMiniLabel, GUILayout.Width(30));

                DrawValue(controller, parameters, i);
                DrawVrcFlags(p);

                // Find-uses: lists every transition condition / blend-tree blend slot / state
                // parameter override that mentions this parameter, plus row actions
                // (duplicate / copy / remap / delete, PhysBone family completion).
                if (GUILayout.Button(FindContent, EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                {
                    ShowUsagesMenu(p.name, i);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();

                // Right-click anywhere on the row is the same menu as "?" — the glyph is easy
                // to miss and a context click is what the hand tries first.
                if (Event.current.type == EventType.ContextClick &&
                    rowRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    ShowUsagesMenu(p.name, i);
                    GUIUtility.ExitGUI();
                }
                _reorder.Row(rowRect);
            }
            _reorder.End((from, to) => MoveParameter(visibleReal[from], visibleReal[to]));

            if (parameters.Length == 0)
                EditorGUILayout.LabelField(L.Tr("No parameters."), EditorStyles.centeredGreyMiniLabel);
        }

        /// <summary>The value column: what the Animator holds while something is running it,
        /// and the controller's own default the rest of the time.</summary>
        void DrawValue(AnimatorController controller, AnimatorControllerParameter[] parameters, int index)
        {
            var p = parameters[index];
            if (s_live && Context.Live.Has(p.name, p.type)) DrawLiveValue(p);
            else DrawDefaultValue(controller, parameters, index);
        }

        void DrawLiveValue(AnimatorControllerParameter p)
        {
            var live = Context.Live;
            if (p.type == AnimatorControllerParameterType.Trigger)
            {
                // A trigger is consumed by the transition that reads it, so there is no steady
                // value to show — only the act of setting it.
                if (GUILayout.Button(new GUIContent(L.Tr("Fire"),
                        L.Tr("Set this trigger on the running Animator")),
                        EditorStyles.miniButton, GUILayout.Width(56)))
                    live.FireTrigger(p.name);
                return;
            }

            // A curve-driven parameter is rewritten by the animation system every frame; a
            // value typed here would be gone before the next repaint.
            bool driven = live.IsCurveDriven(p.name);
            float f = 0f;
            int n = 0;
            bool b = false;
            using (new EditorGUI.DisabledScope(driven))
            {
                EditorGUI.BeginChangeCheck();
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        f = EditorGUILayout.FloatField(live.GetFloat(p.name), GUILayout.Width(56));
                        break;
                    case AnimatorControllerParameterType.Int:
                        n = EditorGUILayout.IntField(live.GetInt(p.name), GUILayout.Width(56));
                        break;
                    default:
                        b = EditorGUILayout.Toggle(live.GetBool(p.name), GUILayout.Width(56));
                        break;
                }
                if (!EditorGUI.EndChangeCheck() || driven) return;
            }
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: live.SetFloat(p.name, f); break;
                case AnimatorControllerParameterType.Int: live.SetInt(p.name, n); break;
                default: live.SetBool(p.name, b); break;
            }
        }

        void DrawDefaultValue(AnimatorController controller, AnimatorControllerParameter[] parameters, int index)
        {
            var p = parameters[index];
            EditorGUI.BeginChangeCheck();
            float f = p.defaultFloat;
            int n = p.defaultInt;
            bool b = p.defaultBool;
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float:
                    f = EditorGUILayout.FloatField(p.defaultFloat, GUILayout.Width(56));
                    break;
                case AnimatorControllerParameterType.Int:
                    n = EditorGUILayout.IntField(p.defaultInt, GUILayout.Width(56));
                    break;
                default:
                    b = EditorGUILayout.Toggle(p.defaultBool, GUILayout.Width(56));
                    break;
            }
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(controller, "Edit Parameter Default");
                p.defaultFloat = f;
                p.defaultInt = n;
                p.defaultBool = b;
                controller.parameters = parameters;
                EditorUtility.SetDirty(controller);

                // Defaults stay linked with the avatar's expression parameters asset.
                if (_store != null && _exprEntries != null && _exprEntries.ContainsKey(p.name))
                {
                    float linked = p.type == AnimatorControllerParameterType.Float ? f
                        : p.type == AnimatorControllerParameterType.Int ? n
                        : b ? 1f : 0f;
                    _store.Edit(p.name, e => e.defaultValue = linked);
                }
            }
        }

        /// <summary>
        /// What this panel shows ABOUT the store rather than about one row: the synced-bit
        /// budget and the two bulk actions on the declaration list.
        ///
        /// The slot that assigns the store used to be this row too. It is on the home screen now,
        /// in the Prefab Link card, where the pin can answer it — so what is left here is the
        /// half that is about the list on screen, and a controller with no store gets a sentence
        /// saying where the slot went instead of a second copy of it.
        /// </summary>
        void DrawVrcBudget()
        {
            if (_store == null)
            {
                EditorGUILayout.LabelField(
                    L.Tr("No parameter store. Assign one in the Prefab Link card on the Home screen."),
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }
            int used = _store.UsedBits();
            int capacity = _store.Capacity();

            EditorGUILayout.BeginHorizontal();
            var prev = GUI.color;
            if (capacity >= 0 && used > capacity) GUI.color = DaerDColors.Warning;
            string label = capacity >= 0
                ? L.Tr("{0}: {1} / {2} bit", _store.Kind, used, capacity)
                : L.Tr("{0}: {1} bit", _store.Kind, used);
            EditorGUILayout.LabelField(
                new GUIContent(label,
                    L.Tr("Synced bits used by this store (Bool = 1, Int / Float = 8). MA components contribute to the avatar's total, so no capacity is shown.")),
                EditorStyles.miniLabel);
            GUI.color = prev;
            GUILayout.FlexibleSpace();
            // Both labels say what the button DOES rather than what it is about. "Add All" and
            // "Sync" named the subject twice over and the action not at all: one of them adds
            // rows that are deliberately NOT synced, and the other syncs nothing — it opens a
            // list to look at.
            if (GUILayout.Button(new GUIContent(L.Tr("Declare Missing"),
                    L.Tr("Declare every controller parameter the store doesn't list yet (Triggers aside): the rows go in neither synced nor saved. That is the opposite default from the per-row '+', which adds one parameter somebody deliberately picked and usually wants on the wire. An MA Parameters component has to declare a parameter before anything can use it, so this is the starting point for a prefab gimmick.")),
                    EditorStyles.miniButton, GUILayout.Width(100)))
            {
                AddMissingToStore();
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Sync List"),
                    L.Tr("Open the list that holds the store's rows against this controller's parameters, row by row. Nothing is added or removed until it is applied there.")),
                    EditorStyles.miniButton, GUILayout.Width(64)))
            {
                VrcParamSyncWindow.Open(Context.Controller, _store, Refresh);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// The bulk form of the per-row "+", for the MA Parameters workflow: a gimmick prefab
        /// has to declare every parameter it uses, and most of them are local. Rows are added
        /// unsynced and unsaved — the opposite default of the single-row "+", which adds one
        /// parameter the user deliberately picked and usually wants on the wire.
        /// </summary>
        void AddMissingToStore()
        {
            if (_store == null) return;
            var missing = ParameterStore.MissingEntries(Context.Controller, _store);
            if (missing.Count == 0)
            {
                EditorUtility.DisplayDialog(L.Tr("Parameter Store"),
                    L.Tr("Every controller parameter is already in the store."), "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog(L.Tr("Parameter Store"),
                    L.Tr("Add {0} parameter(s) to the store, unsynced and unsaved?", missing.Count),
                    L.Tr("Add"), L.Tr("Cancel")))
                return;
            using (new UndoScope("Add Parameters To Store"))
                foreach (var entry in missing)
                    _store.Add(entry);
            Refresh();
        }

        /// <summary>Per-row S (synced) / D (saved) toggles and the declared type for parameters
        /// present in the expression asset, and a "+" to add the ones that aren't.</summary>
        void DrawVrcFlags(AnimatorControllerParameter parameter)
        {
            if (_store == null || _exprEntries == null) return;
            DrawBuiltName(parameter.name);
            if (_exprEntries.TryGetValue(parameter.name, out var entry))
            {
                bool synced = GUILayout.Toggle(entry.synced,
                    new GUIContent("S", L.Tr("Network synced (costs bits)")),
                    EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton));
                bool saved = GUILayout.Toggle(entry.saved,
                    new GUIContent("D", L.Tr("Saved between worlds")),
                    EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton));
                if (synced != entry.synced || saved != entry.saved)
                    _store.Edit(parameter.name, e =>
                    {
                        e.synced = synced;
                        e.saved = saved;
                    });
                DrawStoreType(parameter, entry);
                return;
            }

            var mapped = VrcExpressionParameters.MapType(parameter.type);
            using (new EditorGUI.DisabledScope(mapped == null))
                if (GUILayout.Button(new GUIContent("+", L.Tr("Add to the VRC expression parameters asset")),
                        EditorStyles.miniButton, GUILayout.Width(DaerDLayout.RowAction)))
                    _store.Add(new VrcExpressionParameters.Entry
                    {
                        name = parameter.name,
                        valueType = mapped.Value,
                        defaultValue = parameter.type == AnimatorControllerParameterType.Float
                            ? parameter.defaultFloat
                            : parameter.type == AnimatorControllerParameterType.Int
                                ? parameter.defaultInt
                                : parameter.defaultBool ? 1f : 0f,
                    });
        }

        /// <summary>In VrcExpressionParameters.ValueType order — the popup writes its index
        /// straight back as the type, so the two lists are the same list.</summary>
        static readonly string[] StoreTypeLabels = { "Int", "Float", "Bool" };

        /// <summary>
        /// What the store declares this parameter AS, which is a different question from what
        /// the animator holds and is answered here because it is the only place both answers are
        /// on screen at once.
        ///
        /// It is worth a control rather than a trip to the asset: the type decides the synced
        /// bits the row costs (Bool = 1, Int / Float = 8) and what a menu control does with it,
        /// so it is the one field somebody trimming an over-budget avatar edits over and over.
        /// The bit meter above is read from the store, so it follows on the same repaint.
        ///
        /// A row that disagrees with the animator gets a mark and no more. VRChat converts
        /// between every combination on the way in, and a Bool row driving a Float parameter is a
        /// documented way to spend one bit instead of eight — so the mark says what is happening
        /// in the same voice the analyzer uses, and does not tell anyone off.
        ///
        /// An MA "NotSynced" row gets no popup at all. It declares no type, and the control would
        /// have to invent one to show a value — which would charge the avatar bits for a
        /// parameter nobody asked to sync.
        /// </summary>
        void DrawStoreType(AnimatorControllerParameter parameter,
            VrcExpressionParameters.Entry entry)
        {
            if (!entry.typed) return;
            int chosen = EditorGUILayout.Popup((int)entry.valueType, StoreTypeLabels,
                EditorStyles.miniButton, GUILayout.Width(DaerDLayout.RowAction + 12f));
            if (chosen != (int)entry.valueType)
                _store.SetValueType(parameter.name, (VrcExpressionParameters.ValueType)chosen);
            if (!VrcExpressionParameters.Mismatched(entry, parameter.type)) return;
            GUILayout.Label(new GUIContent("≠",
                    L.Tr("This row is declared {0} while the controller's parameter is {1}. VRChat converts between them (parameter mismatching) — a Bool row driving a Float costs one synced bit instead of eight, so this is a saving as often as it is a slip.",
                        entry.valueType, parameter.type)),
                EditorStyles.miniLabel, GUILayout.Width(12f));
        }

        /// <summary>
        /// What the avatar's build will call this row, shown only where that is not what the
        /// row is called — which is an MA Parameters component declaring the parameter
        /// internal, and nothing else today.
        ///
        /// Worth a column of its own because the difference is invisible and consequential: the
        /// name on the left is the one every condition, driver and clip in this controller uses
        /// and is right to use, and the name on the right is the only one that means anything
        /// to the wire, to a menu control, or to anybody debugging the built avatar. Somebody
        /// looking for "Hat" in a build log will never find it, and this is where they learn
        /// what to look for instead.
        ///
        /// Sized to its text rather than given a column, because the ordinary row has nothing
        /// here at all and a permanent blank column would cost every row width it needs.
        /// </summary>
        void DrawBuiltName(string name)
        {
            if (_builtNames == null || !_builtNames.TryGetValue(name, out var built)) return;
            var content = new GUIContent("→ " + built,
                L.Tr("The name the built avatar gives this parameter. This row is declared internal, so it is renamed on the way in — this, not the name on the left, is what travels and what a build log will call it."));
            float width = Mathf.Min(EditorStyles.miniLabel.CalcSize(content).x, 160f);
            GUILayout.Label(content, EditorStyles.miniLabel, GUILayout.Width(width));
        }

        /// <summary>PhysBone / Contact parameter families share a prefix — offer to carry a
        /// prefix rename over to the sibling parameters.</summary>
        void OfferSiblingRename(AnimatorController controller, string oldName, string newName)
        {
            var siblings = PhysBoneSiblings.Siblings(controller, oldName);
            if (siblings.Count == 0) return;
            var renames = new List<(string from, string to)>();
            foreach (var sibling in siblings)
            {
                var renamed = PhysBoneSiblings.RenamedSibling(sibling, oldName, newName);
                if (renamed != null && DbtBuilder.FindParameter(controller, renamed) == null)
                    renames.Add((sibling, renamed));
            }
            if (renames.Count == 0) return;
            if (!EditorUtility.DisplayDialog(L.Tr("Rename Parameter Family"),
                    L.Tr("{0} sibling parameter(s) share this PhysBone/Contact prefix. Rename them to match?", renames.Count),
                    L.Tr("Rename All"), L.Tr("Only This One")))
                return;
            using (new UndoScope("Rename Parameter Family"))
                foreach (var (from, to) in renames)
                {
                    ParameterRenamer.Rename(controller, from, to);
                    _store?.Rename(from, to);
                }
        }

        void ShowUsagesMenu(string parameterName, int index)
        {
            var controller = Context.Controller;
            var usages = ParameterUsageFinder.Find(controller, parameterName);
            var menu = new GenericMenu();
            if (usages.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent(L.Tr("'{0}' is not used anywhere", parameterName)));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(L.Tr("{0} usage(s) of '{1}'", usages.Count, parameterName)));
                menu.AddSeparator(string.Empty);
                foreach (var u in usages)
                {
                    var captured = u;
                    // GenericMenu uses '/' as a sub-menu separator — escape to keep the full path
                    // readable on one menu line.
                    var label = new GUIContent(captured.label.Replace('/', '∕'));
                    menu.AddItem(label, false, () =>
                        Context.NavigateTo(captured.layerIndex, captured.stateMachinePath, captured.selection));
                }
            }

            // PhysBone family completion. Any row can seed it: a member name contributes its
            // prefix, any other name IS the prefix — so "make one parameter, right-click,
            // complete the family" needs no prefix prompt. Present members show checked so the
            // submenu also answers "which of the five does this controller have?"; the types
            // are fixed by what the PhysBone system writes, hence shown, not chosen.
            menu.AddSeparator(string.Empty);
            var familyPrefix = PhysBoneSiblings.PrefixOf(parameterName) ?? parameterName;
            var missingFamily = PhysBoneSiblings.MissingFamily(controller, parameterName);
            foreach (var (suffix, type) in PhysBoneSiblings.Family)
            {
                var fullName = familyPrefix + suffix;
                var shownName = fullName.Replace('/', '∕');
                if (missingFamily.Contains((fullName, type)))
                {
                    var captured = (fullName, type);
                    menu.AddItem(new GUIContent("PhysBone/" + L.Tr("Add {0}  ({1})", shownName, type)),
                        false, () => AddPhysBoneFamily(index,
                            new List<(string, AnimatorControllerParameterType)> { captured }));
                }
                else
                    menu.AddItem(new GUIContent("PhysBone/" + shownName + "  (" + type + ")"),
                        true, null);   // already on the controller — shown checked, non-clickable
            }
            if (missingFamily.Count > 1)
                menu.AddItem(new GUIContent("PhysBone/" + L.Tr("Add All Missing ({0})", missingFamily.Count)),
                    false, () => AddPhysBoneFamily(index, missingFamily));

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent(L.Tr("Duplicate")), false, () => DuplicateParameter(index));
            menu.AddItem(new GUIContent(L.Tr("Copy")), false, () => CopyParameter(index));
            if (s_parameterClipboard != null)
                menu.AddItem(new GUIContent(L.Tr("Paste After ('{0}')", s_parameterClipboard.name)),
                    false, () => PasteParameterAfter(index));
            else
                menu.AddDisabledItem(new GUIContent(L.Tr("Paste After")));

            // Redirect every reference to another parameter (both stay in the list).
            bool anyTarget = false;
            foreach (var other in controller.parameters)
            {
                if (other.name == parameterName) continue;
                anyTarget = true;
                var captured = other.name;
                menu.AddItem(new GUIContent(L.Tr("Remap References To") + "/" + captured.Replace('/', '∕')),
                    false, () =>
                    {
                        ParameterRenamer.RedirectReferences(Context.Controller, parameterName, captured);
                        Context.NotifyParametersChanged();
                        Context.NotifyGraphStructureChanged();
                    });
            }
            if (!anyTarget)
                menu.AddDisabledItem(new GUIContent(L.Tr("Remap References To")));

            // Plain delete moved here from the per-row "✕": a destructive control on every row
            // was one misclick from losing a parameter, and the menu is where the rest of the
            // row's actions already live. No confirm, same as the button it replaces — the
            // references it may orphan are exactly what "Delete and Clean" below is for.
            menu.AddItem(new GUIContent(L.Tr("Delete")), false, () => RemoveParameter(index));
            menu.AddItem(new GUIContent(L.Tr("Delete and Clean")), false, () =>
            {
                if (!EditorUtility.DisplayDialog(L.Tr("Delete and Clean"),
                        L.Tr("Delete '{0}' and remove every condition and driver entry that references it?", parameterName),
                        L.Tr("Delete"), L.Tr("Cancel")))
                    return;
                ParameterRenamer.DeleteAndClean(Context.Controller, parameterName);
                Context.NotifyParametersChanged();
                Context.NotifyGraphStructureChanged();
            });
            menu.ShowAsContext();
        }

        /// <summary>Add PhysBone family members right after the seed row, in family order, as
        /// one undo step. Deliberately touches neither the store nor the defaults: these are
        /// written by each client's own PhysBone system, so declaring them in expression
        /// parameters would only spend synced bits on values the wire never carries.</summary>
        void AddPhysBoneFamily(int index,
            List<(string name, AnimatorControllerParameterType type)> members)
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Add PhysBone Parameters");
            int insertAt = index + 1;
            foreach (var (name, type) in members)
            {
                if (DbtBuilder.FindParameter(controller, name) != null) continue;
                controller.AddParameter(name, type);
                MoveParameter(controller.parameters.Length - 1,
                    Mathf.Min(insertAt++, controller.parameters.Length - 1));
            }
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        /// <summary>Parameters written by clips (AAP); cached per controller and dropped on
        /// structure edits.</summary>
        HashSet<string> AapParams(AnimatorController controller)
        {
            if (_aapParams != null && _scanCacheController == controller) return _aapParams;
            _aapParams = new HashSet<string>();
            _scanCacheController = controller;
            foreach (var entry in ControllerCleanup.CollectClipUsages(controller))
            {
                if (entry.clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(entry.clip))
                    if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path))
                        _aapParams.Add(binding.propertyName);
            }
            return _aapParams;
        }

        /// <summary>Parameters nothing in the controller reads. The scan behind this walks the
        /// whole graph, so it is held the same way the AAP set beside it is.</summary>
        HashSet<string> UnusedParams(AnimatorController controller)
        {
            if (_unusedParams != null && _scanCacheController == controller) return _unusedParams;
            _scanCacheController = controller;
            return _unusedParams = new HashSet<string>(ControllerAnalyzer.FindUnusedParameters(controller));
        }

        void DuplicateParameter(int index)
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;
            if (index < 0 || index >= parameters.Length) return;
            var source = parameters[index];
            Undo.RegisterCompleteObjectUndo(controller, "Duplicate Parameter");
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = MakeUniqueName(controller, source.name),
                type = source.type,
                defaultFloat = source.defaultFloat,
                defaultInt = source.defaultInt,
                defaultBool = source.defaultBool,
            });
            MoveParameter(controller.parameters.Length - 1, index + 1);
            Context.NotifyParametersChanged();
        }

        void CopyParameter(int index)
        {
            var parameters = Context.Controller.parameters;
            if (index < 0 || index >= parameters.Length) return;
            var source = parameters[index];
            s_parameterClipboard = new AnimatorControllerParameter
            {
                name = source.name,
                type = source.type,
                defaultFloat = source.defaultFloat,
                defaultInt = source.defaultInt,
                defaultBool = source.defaultBool,
            };
        }

        void PasteParameterAfter(int index)
        {
            var controller = Context.Controller;
            if (s_parameterClipboard == null) return;
            Undo.RegisterCompleteObjectUndo(controller, "Paste Parameter");
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = MakeUniqueName(controller, s_parameterClipboard.name),
                type = s_parameterClipboard.type,
                defaultFloat = s_parameterClipboard.defaultFloat,
                defaultInt = s_parameterClipboard.defaultInt,
                defaultBool = s_parameterClipboard.defaultBool,
            });
            MoveParameter(controller.parameters.Length - 1,
                Mathf.Min(index + 1, controller.parameters.Length - 1));
            Context.NotifyParametersChanged();
        }

        void ShowAddMenu()
        {
            var controller = Context.Controller;
            var existing = new HashSet<string>();
            foreach (var p in controller.parameters) existing.Add(p.name);

            var menu = new GenericMenu();
            foreach (AnimatorControllerParameterType type in new[]
            {
                AnimatorControllerParameterType.Float,
                AnimatorControllerParameterType.Int,
                AnimatorControllerParameterType.Bool,
                AnimatorControllerParameterType.Trigger,
            })
            {
                var captured = type;
                menu.AddItem(new GUIContent(type.ToString()), false, () => AddParameter(captured));
            }

            // The generators that add parameters of their own — DBT gadgets, object gadgets and
            // async sync — are reached from the home screen, which lists what a controller
            // already has as well as offering to add more. Adding one is not the part that needs
            // an entry point: finding the four you built last month is, and this menu could
            // never show that.

            // VRChat built-in parameters. Already-present ones show as a checked, disabled entry so
            // the menu doubles as a quick "which standard parameters does this controller have?".
            menu.AddSeparator(string.Empty);
            int missing = 0;
            foreach (var def in VrcParameters.All)
                if (!existing.Contains(def.name)) missing++;

            if (missing > 0)
                menu.AddItem(new GUIContent(L.Tr("VRChat/Add All Missing ({0})", missing)), false, AddAllVrcParameters);
            else
                menu.AddDisabledItem(new GUIContent(L.Tr("VRChat/Add All Missing")));
            var syncLabel = new GUIContent(L.Tr("VRChat/Sync Expression Parameters Asset"));
            if (_store != null)
            {
                var store = _store;
                menu.AddItem(syncLabel, false, () => VrcParamSyncWindow.Open(controller, store, Refresh));
            }
            else
                menu.AddDisabledItem(syncLabel);
            menu.AddSeparator("VRChat/");

            foreach (var def in VrcParameters.All)
            {
                var captured = def;
                var label = new GUIContent("VRChat/" + def.category + "/" + def.name + "  (" + def.type + ")");
                if (existing.Contains(def.name))
                    menu.AddItem(label, true, null);   // already added — shown checked, non-clickable
                else
                    menu.AddItem(label, false, () => AddVrcParameter(captured));
            }

            menu.ShowAsContext();
        }

        void AddVrcParameter(VrcParameters.Definition def)
        {
            var controller = Context.Controller;
            foreach (var p in controller.parameters)
                if (p.name == def.name) return;   // never duplicate a built-in name
            Undo.RegisterCompleteObjectUndo(controller, "Add VRChat Parameter");
            controller.AddParameter(def.name, VrcParameters.UnityType(def.type));
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void AddAllVrcParameters()
        {
            var controller = Context.Controller;
            var existing = new HashSet<string>();
            foreach (var p in controller.parameters) existing.Add(p.name);

            Undo.RegisterCompleteObjectUndo(controller, "Add VRChat Parameters");
            int added = 0;
            foreach (var def in VrcParameters.All)
                if (existing.Add(def.name))
                {
                    controller.AddParameter(def.name, VrcParameters.UnityType(def.type));
                    added++;
                }
            if (added > 0)
            {
                EditorUtility.SetDirty(controller);
                Context.NotifyParametersChanged();
            }
        }

        void AddParameter(AnimatorControllerParameterType type)
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Add Parameter");
            controller.AddParameter(MakeUniqueName(controller, "New " + type), type);
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void RemoveParameter(int index)
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Remove Parameter");
            controller.RemoveParameter(index);
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void MoveParameter(int from, int to)
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;
            if (from < 0 || from >= parameters.Length || to < 0 || to >= parameters.Length || from == to)
                return;
            Undo.RegisterCompleteObjectUndo(controller, "Reorder Parameters");
            var moved = parameters[from];
            if (from < to)
                Array.Copy(parameters, from + 1, parameters, from, to - from);
            else
                Array.Copy(parameters, to, parameters, to + 1, from - to);
            parameters[to] = moved;
            controller.parameters = parameters;
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        // NOTE: a controller-side type conversion deliberately does NOT touch the store's
        // valueType. Differing types are a supported VRChat technique ("parameter
        // mismatching" — VRChat converts between all combinations, e.g. a 1-bit synced Bool
        // driving an animator Float), and silently rewriting the store would both destroy
        // that intent and change the synced bit cost. Edit the store type from its own row.
        void HandleTypeChange(string parameterName, AnimatorControllerParameterType newType)
        {
            var plan = ParameterConverter.ComputeConversion(Context.Controller, parameterName, newType);
            if (plan.conditionChanges.Count == 0 && plan.warnings.Count == 0)
            {
                ParameterConverter.Apply(plan);
                Context.NotifyParametersChanged();
                Context.NotifyGraphStructureChanged();
            }
            else
            {
                ParameterConversionPreviewWindow.Open(plan, () =>
                {
                    Context.NotifyParametersChanged();
                    Context.NotifyGraphStructureChanged();
                });
            }
        }

        static string MakeUniqueName(AnimatorController controller, string baseName)
        {
            bool Taken(string n)
            {
                foreach (var p in controller.parameters)
                    if (p.name == n) return true;
                return false;
            }
            if (!Taken(baseName)) return baseName;
            int i = 1;
            while (Taken(baseName + " " + i)) i++;
            return baseName + " " + i;
        }
    }
}
