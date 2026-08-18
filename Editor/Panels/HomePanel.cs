using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;
using Yozolab.DaerD.Authoring;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The controller-wide screen, shown in the centre pane instead of the graph while Home is
    /// picked in the layer list. Everything here is about the controller rather than about any
    /// one layer — the assets it is associated with, the generated things saved with it
    /// (gadgets, sync setups, recipe-owned layers) and the tools that act on all of it — which
    /// is exactly what a layer's graph has no room for.
    ///
    /// The generated lists are the point: a gadget or a sync setup expands into a wall of trees,
    /// clips and states nobody can read back, so the record saved with the controller is the
    /// only description of it there is, and this is where those records are shown.
    ///
    /// Laid out as centred cards rather than as full-width rows: this pane is as wide as the
    /// window, and a row stretched across all of it puts a two-word label at one end and its
    /// buttons at the other with nothing in between. Wide enough, and the cards split into two
    /// columns — what the controller IS on the left, the generated things it carries on the
    /// right — so neither half has to be scrolled past to reach the other.
    /// </summary>
    class HomePanel : PanelBase
    {
        /// <summary>How wide the single column of cards is allowed to grow. A cap, not a size —
        /// a pane narrower than this gets the whole width instead.</summary>
        const float SingleColumnWidth = 560f;

        /// <summary>Cap for each half of the two-column layout.</summary>
        const float SplitColumnWidth = 460f;

        /// <summary>Pane width from which the cards split into two columns. Below it two columns
        /// would each be narrower than one card wants, and the rows inside them start wrapping
        /// their buttons off the edge — one column reads better than two cramped ones.</summary>
        const float TwoColumnMinWidth = 720f;

        /// <summary>Prefix width inside the cards. The default is sized for an inspector column
        /// and would eat half of a card's width.</summary>
        const float FieldLabelWidth = 110f;

        /// <summary>One width for every row action across the three lists, so the buttons line
        /// up down the column instead of stepping in and out with the label beside them.</summary>
        const float RowButtonWidth = 56f;

        readonly CleanupInspector _cleanup = new CleanupInspector();
        readonly ClipsForm _clips = new ClipsForm();
        readonly AnalyzerForm _analyzer = new AnalyzerForm();
        readonly RecipeExportForm _recipeExport = new RecipeExportForm();

        // The lists start expanded: seeing what the controller carries is the reason to open
        // this screen at all, so folding them away is the exception, not the default.
        bool _gadgetsOpen = true;
        bool _objectGadgetsOpen = true;
        bool _syncsOpen = true;
        bool _recipesOpen = true;
        // The tools start folded, for the opposite reason: each is a working surface of its
        // own, and several unfolded at once would bury everything else in the column.
        bool _clipsOpen;
        bool _analyzerOpen;
        bool _recipeExportOpen;
        bool _cleanupOpen;

        // Bumped whenever the controller changed in a way a tool's collected data could care
        // about. Each embedded tool remembers the revision it was filled at, so opening one
        // re-collects exactly once instead of walking the controller on every repaint.
        int _revision;
        int _clipsRevision = -1;
        int _analyzerRevision = -1;
        int _recipeExportRevision = -1;

        public HomePanel(DaerDContext context) : base(context, "Home")
        {
            context.ControllerChanged += OnControllerChanged;
            context.LayersChanged += OnStructureChanged;
            context.ParametersChanged += Refresh;
            context.GraphStructureChanged += OnStructureChanged;

            // The embedded clip index has no window to fall back on: a Jump is a request to go
            // and look at the state, so home gives way to the layer it lives in — even when
            // that layer is the one already selected underneath.
            _clips.JumpRequested = usage =>
            {
                if (Context.IsHomeSelected)
                    Context.SetLayer(usage.layerIndex);
                Context.NavigateTo(usage.layerIndex, usage.stateMachinePath, usage.state);
            };
            // A bulk replace rewrote motions; node labels carry their names.
            _clips.ControllerModified = () => Context.NotifyGraphStructureChanged();

            _analyzer.FocusRequested = FocusIssue;
            // A fix can delete a parameter or a transition — the same refresh the standalone
            // window asks every open DaerD window for, done straight on this one's context.
            _analyzer.ControllerModified = () =>
            {
                Context.ValidatePath();
                Context.NotifyParametersChanged();
                Context.NotifyGraphStructureChanged();
            };

            // Nothing to close here, so a finished export starts the form over — the defaults
            // it comes back with describe the controller as it is now.
            _recipeExport.Exported = () =>
            {
                _recipeExport.SetController(Context.Controller);
                _recipeExportRevision = _revision;
            };
        }

        /// <summary>An analyzer Ping, seen from inside the window that owns the graph: locate
        /// the issue and navigate there, leaving home on the way. False when the issue points at
        /// nothing the graph can show, and the form pings the Project window instead.</summary>
        bool FocusIssue(AnalyzerIssue issue)
        {
            var controller = Context.Controller;
            var location = ControllerLocator.LocateIssue(controller, issue);
            if (location == null) return false;
            if (Context.IsHomeSelected)
                Context.SetLayer(location.layerIndex);
            Context.NavigateTo(location.layerIndex, location.stateMachinePath, location.target);
            return true;
        }

        /// <summary>The leftover scan (and the object references captured in it) belongs to the
        /// outgoing controller — drop it on a tab switch.</summary>
        void OnControllerChanged()
        {
            _cleanup.Clear();
            OnStructureChanged();
        }

        void OnStructureChanged()
        {
            _revision++;
            Refresh();
        }

        protected override void DrawContent()
        {
            var controller = Context.Controller;

            // The pane's own width: IMGUI inside a UIElements host stretches to whatever it is
            // given, so the layout has to ask the element. It is NaN until the first layout pass
            // has run, which reads as "not wide enough" and settles on the next repaint.
            float width = contentRect.width;
            if (!float.IsNaN(width) && width >= TwoColumnMinWidth)
            {
                DrawTwoColumns(controller);
                return;
            }
            DrawOneColumn(controller);
        }

        /// <summary>What the controller is on the left, what it carries on the right. Splitting
        /// this way keeps the lists — the part that grows without bound — in one column, so the
        /// settings above them don't scroll away as gadgets pile up.</summary>
        void DrawTwoColumns(AnimatorController controller)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(SplitColumnWidth));
            DrawController(controller);
            EditorGUILayout.Space(8);
            DrawPrefabLink(controller);
            EditorGUILayout.Space(8);
            DrawTools(controller);
            EditorGUILayout.EndVertical();

            GUILayout.Space(8);

            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(SplitColumnWidth));
            DrawGadgets(controller);
            EditorGUILayout.Space(8);
            DrawObjectGadgets(controller);
            EditorGUILayout.Space(8);
            DrawAsyncSyncs(controller);
            DrawRecipes(controller);
            EditorGUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The narrow fallback: the same cards in one centred column, tools last —
        /// reading order rather than column balance decides here.</summary>
        void DrawOneColumn(AnimatorController controller)
        {
            // Flexible space on both sides centres the column; the cap is a MaxWidth so the
            // group still collapses with a narrow pane, which a fixed Width would not.
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginVertical(GUILayout.MaxWidth(SingleColumnWidth));

            DrawController(controller);
            EditorGUILayout.Space(8);
            DrawPrefabLink(controller);
            EditorGUILayout.Space(8);
            DrawGadgets(controller);
            EditorGUILayout.Space(8);
            DrawObjectGadgets(controller);
            EditorGUILayout.Space(8);
            DrawAsyncSyncs(controller);
            DrawRecipes(controller);
            EditorGUILayout.Space(8);
            DrawTools(controller);

            EditorGUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ---- cards -------------------------------------------------------------

        static GUIStyle s_cardTitleStyle;

        /// <summary>Foldout arrow with a card's heading weight behind it.</summary>
        static GUIStyle CardTitleStyle => s_cardTitleStyle ??= new GUIStyle(EditorStyles.foldout)
        {
            fontStyle = FontStyle.Bold,
        };

        /// <summary>Opens one section's card. Every section is a box with its name in bold at
        /// the top, so the column reads as a stack of things rather than as one long list.
        /// </summary>
        static void BeginCard(string title)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        /// <summary>
        /// A card whose body folds away, for the sections long enough to be worth hiding. The
        /// heading stays visible either way — with its count, which is the one thing about a
        /// list worth reading while it is closed.
        /// </summary>
        static bool BeginFoldCard(string title, bool open)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            return EditorGUILayout.Foldout(open, title, true, CardTitleStyle);
        }

        static bool BeginFoldCard(string title, int count, bool open) =>
            BeginFoldCard(title + " (" + count + ")", open);

        /// <summary>
        /// A tool's card: a folding heading with a Window button on its right. The tool is
        /// usable right here, but a report worth keeping beside the graph still wants a window
        /// of its own — and that route has to keep working anyway, since the menus and the
        /// layer settings popup open these tools that way.
        /// </summary>
        static bool BeginToolCard(string title, bool open, out bool windowRequested)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            bool now = EditorGUILayout.Foldout(open, title, true, CardTitleStyle);
            windowRequested = GUILayout.Button(new GUIContent(L.Tr("Window"),
                    L.Tr("Open this tool in a window of its own, so it stays visible while you edit the graph.")),
                EditorStyles.miniButton, GUILayout.Width(RowButtonWidth));
            EditorGUILayout.EndHorizontal();
            return now;
        }

        static void EndCard() => EditorGUILayout.EndVertical();

        // ---- controller --------------------------------------------------------

        /// <summary>Identity, plus the assets this controller is explicitly associated with.
        /// They are assigned by hand and never guessed from the scene, since DaerD is also used
        /// on gimmick controllers that belong to no avatar. The parameter store used to be a
        /// fourth slot here and is now in the Prefab Link card below, where the pin can offer an
        /// answer for it.</summary>
        void DrawController(AnimatorController controller)
        {
            BeginCard(L.Tr("Controller"));

            // One line rather than three labelled rows: the name and the two counts are read at
            // a glance, and spelling out what each number is costs three rows to say it.
            string identity = controller.name + "  —  " + L.Tr("{0} layers · {1} parameters",
                controller.layers.Length, controller.parameters.Length);
            EditorGUILayout.LabelField(new GUIContent(identity, identity));

            EditorGUILayout.Space(4);
            using (new PanelGui.LabelWidthScope(FieldLabelWidth))
            {
                var currentEmpty = GraphFrameData.GetEmptyClip(controller);
                var pickedEmpty = (AnimationClip)EditorGUILayout.ObjectField(
                    new GUIContent(L.Tr("Empty Clip"),
                        L.Tr("Stored with this controller. New states are created with it, and the analyzer's Fill fix assigns it to states with no motion.")),
                    currentEmpty, typeof(AnimationClip), false);
                if (pickedEmpty != currentEmpty)
                    GraphFrameData.SetEmptyClip(controller, pickedEmpty);

                // Inside the scope too, so its prefix lines up with the slot above it.
                var wdTooltip = L.Tr("Bulk-set every state. Layers containing only Direct blend trees stay ON.");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent(L.Tr("Write Defaults"), wdTooltip));
                if (GUILayout.Button(new GUIContent(L.Tr("Set All ON"), wdTooltip)))
                    BulkSetWriteDefaults(controller, true);
                if (GUILayout.Button(new GUIContent(L.Tr("Set All OFF"), wdTooltip)))
                    BulkSetWriteDefaults(controller, false);
                EditorGUILayout.EndHorizontal();
            }

            // Bottom-right and spelled with an ellipsis: destructive, rare, and never the
            // reason this card is open. Only shown when there is anything to discard.
            if (GraphFrameData.Find(controller) != null)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent(L.Tr("Discard DaerD Data…"),
                        L.Tr("Remove everything DaerD stores with this controller. The controller itself is untouched."))))
                    DiscardData(controller);
                EditorGUILayout.EndHorizontal();
            }

            EndCard();
        }

        static void DiscardData(AnimatorController controller)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("Discard DaerD Data"),
                    L.Tr("Remove everything DaerD stores with '{0}'?\n\nGone: graph frames and notes, gadget and sync records, the prefab and recipe links, the store and Empty clip assignments.\n\nKept: every layer, parameter, motion and generated clip — the controller plays exactly as before, DaerD just no longer manages it (no regeneration, no ownership marks).\n\nThe asset is saved immediately; this cannot be undone.",
                        controller.name),
                    L.Tr("Discard"), L.Tr("Cancel")))
                return;
            GraphFrameData.Discard(controller);
        }

        void BulkSetWriteDefaults(AnimatorController controller, bool value)
        {
            string message = value
                ? L.Tr("Set Write Defaults ON for every state in this controller?")
                : L.Tr("Set Write Defaults OFF for every state?\n\nLayers that contain only Direct blend trees are kept ON.");
            if (!EditorUtility.DisplayDialog(L.Tr("Write Defaults"), message,
                    value ? L.Tr("Set ON") : L.Tr("Set OFF"), L.Tr("Cancel")))
                return;
            ControllerAnalyzer.SetAllWriteDefaults(controller, value);
            // WD badges update immediately
            Context.NotifyGraphVisualsChanged(DaerDContext.GraphVisuals.AllStateNodes);
        }

        // ---- prefab link -------------------------------------------------------

        /// <summary>
        /// Which gimmick prefab this controller belongs to, and what follows from knowing it.
        ///
        /// Its own card rather than a fourth slot in the Controller card above, because the two
        /// answer different questions. The slots up there are assets the user hands DaerD; this
        /// is a claim about the project that DaerD then has to keep checking — it can go stale
        /// on its own, without anybody touching the controller, and a stale link needs a
        /// sentence rather than an empty field.
        ///
        /// Nothing here searches on its own (ADR 0028): the sweep runs on Scan and on nothing
        /// else, and drawing this card costs one reference resolution.
        /// </summary>
        void DrawPrefabLink(AnimatorController controller)
        {
            BeginCard(L.Tr("Prefab Link"));
            var status = PrefabLinks.Status(controller);

            using (new PanelGui.LabelWidthScope(FieldLabelWidth))
            {
                var picked = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent(L.Tr("Prefab"),
                        L.Tr("The gimmick prefab whose MA Merge Animator merges this controller. Drop one here, or press Scan to search the project. DaerD never picks one on its own.")),
                    status.prefab, typeof(GameObject), false);
                if (picked != status.prefab)
                {
                    DropPrefab(controller, picked);
                    GUIUtility.ExitGUI();   // the card was redrawn under this layout pass
                }
            }

            DrawLinkState(controller, status);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(L.Tr("Scan & Link"),
                    L.Tr("Search the project's prefabs for an MA Merge Animator that merges this controller. Only prefabs that already reference it are opened, and the answer is remembered until something in the project changes."))))
            {
                ScanAndLink(controller, rescan: false);
                GUIUtility.ExitGUI();
            }
            if (IsStale(status.state) && GUILayout.Button(new GUIContent(L.Tr("Rescan"),
                    L.Tr("Search the project again from scratch, ignoring the remembered answer."))))
            {
                ScanAndLink(controller, rescan: true);
                GUIUtility.ExitGUI();
            }
            using (new EditorGUI.DisabledScope(status.state == PrefabLinkState.None))
                if (GUILayout.Button(new GUIContent(L.Tr("Unlink"),
                        L.Tr("Forget which prefab this controller belongs to. Nothing inside the prefab is changed."))))
                {
                    Unlink(controller);
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            DrawStore(controller, status);
            EndCard();
        }

        /// <summary>The states a Rescan can plausibly answer: the link once resolved and no
        /// longer does, or points somewhere else. "None" is not stale — there is nothing to
        /// re-check — and Scan is the button for it.</summary>
        static bool IsStale(PrefabLinkState state) =>
            state == PrefabLinkState.PrefabMissing || state == PrefabLinkState.MergeMissing
            || state == PrefabLinkState.Diverged;

        /// <summary>
        /// The link in one line, or the sentence its state needs. Every broken state names what
        /// it is talking about, and none of them offers to repair itself: a link that points at
        /// another controller is somebody's edit, and which of the two is now the mistake is not
        /// a thing DaerD can know (the same rule the analyzer keeps).
        /// </summary>
        void DrawLinkState(AnimatorController controller, PrefabLinkStatus status)
        {
            switch (status.state)
            {
                case PrefabLinkState.Healthy:
                    string path = AssetDatabase.GetAssetPath(status.prefab);
                    EditorGUILayout.BeginHorizontal();
                    DrawRowName(status.prefab.name, path);
                    DrawRowNote(PrefabLinks.PathIn(status.prefab, status.mergeAnimator));
                    if (RowButton(L.Tr("Ping"), L.Tr("Highlight this object in the Project / graph")))
                        EditorGUIUtility.PingObject(status.prefab);
                    EditorGUILayout.EndHorizontal();
                    break;
                case PrefabLinkState.PrefabMissing:
                    // The prefab cannot be named: naming it would mean having saved its path,
                    // and a saved path is the thing this design does not do.
                    EditorGUILayout.HelpBox(
                        L.Tr("The linked prefab cannot be found — deleted, or on a branch that is not checked out. The link is kept exactly as it is; nothing is guessed in its place."),
                        MessageType.Warning);
                    break;
                case PrefabLinkState.MergeMissing:
                    EditorGUILayout.HelpBox(
                        L.Tr("The MA Merge Animator this controller was linked to is no longer inside '{0}'. The link is kept as it is — Rescan searches the project again.",
                            status.prefab.name),
                        MessageType.Warning);
                    break;
                case PrefabLinkState.Diverged:
                    EditorGUILayout.HelpBox(
                        L.Tr("The MA Merge Animator in '{0}' now merges {1}, not this controller. Nothing is re-pointed for you: fix it in the prefab, or link this controller to another one.",
                            status.prefab.name, Quoted(status.mergedController)),
                        MessageType.Warning);
                    break;
                case PrefabLinkState.Unverifiable:
                    EditorGUILayout.HelpBox(
                        L.Tr("Modular Avatar is not installed, so this link cannot be read. The saved link is untouched and comes back with it."),
                        MessageType.Info);
                    break;
                default:
                    EditorGUILayout.LabelField(
                        L.Tr("Not linked. Scan lists every prefab whose MA Merge Animator names this controller."),
                        EditorStyles.centeredGreyMiniLabel);
                    break;
            }
        }

        /// <summary>An object's name for a message, or a word for the empty slot — a Merge
        /// Animator with nothing in its animator field is a real thing to run into, and
        /// "merges ''" would read as a bug.</summary>
        static string Quoted(UnityEngine.Object target) =>
            target != null ? "'" + target.name + "'" : L.Tr("nothing at all");

        /// <summary>
        /// Which store this controller declares its parameters into — and, when the pin is
        /// healthy, what the linked prefab says the answer should be.
        ///
        /// <para>THE ONE PLACE A STORE IS ASSIGNED.</para>
        /// It used to be assignable from two (a slot in the Controller card, the same row again
        /// above the Parameters panel) while this card showed a third, read-only summary of the
        /// same association. Three surfaces for one field is how the same question came to be
        /// asked in three different vocabularies on one screen. It belongs HERE rather than in
        /// the Controller card because the pin is what answers it: for a gimmick, the store is
        /// the MA Parameters above the linked merge nearly every time, and that answer is one
        /// button away only while the two sit together.
        ///
        /// What is still not here is the ROWS. They are edited in the Parameters panel, which is
        /// in the left column and visible from here — a second editing surface for the same list
        /// is two implementations of it, and the panel's one already knows about effective names,
        /// the budget and the sync diff.
        /// </summary>
        void DrawStore(AnimatorController controller, PrefabLinkStatus status)
        {
            using (new PanelGui.LabelWidthScope(FieldLabelWidth))
                DrawStoreSlot(controller);

            var current = GraphFrameData.GetParameterStore(controller);
            var store = ParameterStore.TryWrap(current);
            if (store != null)
            {
                int capacity = store.Capacity();
                string summary = capacity >= 0
                    ? L.Tr("{0}: {1} — {2} of {3} synced bits",
                        store.Kind, store.Target.name, store.UsedBits(), capacity)
                    : L.Tr("{0}: {1} — {2} synced bits",
                        store.Kind, store.Target.name, store.UsedBits());
                EditorGUILayout.LabelField(new GUIContent(summary,
                    L.Tr("The rows themselves are edited in the Parameters panel on the left, which is showing this same store.")));
            }

            // Everything below is what the PIN knows about the store, so a controller with no
            // usable link gets the slot and nothing else — there is nothing to compare against.
            if (!status.IsHealthy) return;
            var linked = PrefabLinks.StoreOf(status);
            if (linked == null)
            {
                if (store == null)
                    EditorGUILayout.LabelField(
                        L.Tr("The linked prefab has no MA Parameters above its merge yet."),
                        EditorStyles.centeredGreyMiniLabel);
                // The one thing that is missing and that DaerD can supply, offered where the
                // absence is stated rather than in a menu somewhere else.
                if (GUILayout.Button(new GUIContent(L.Tr("Add MA Parameters"),
                        L.Tr("Add an MA Parameters component to the linked prefab's root, so this controller's parameters have somewhere to be declared. This writes the prefab file."))))
                {
                    AddParametersToPrefab(controller, status);
                    GUIUtility.ExitGUI();   // the prefab was reimported under this layout pass
                }
                return;
            }
            if (linked == current) return;
            if (GUILayout.Button(new GUIContent(L.Tr("Use The Prefab's MA Parameters"),
                    L.Tr("Point the parameter store slot at the MA Parameters that governs the linked merge. A button rather than something linking does for you, because the slot already holds an answer somebody gave."))))
            {
                GraphFrameData.SetParameterStore(controller, linked);
                Context.NotifyParametersChanged();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// The slot itself, plus the one search cheap enough to sit beside it.
        ///
        /// There used to be a second button here that swept every prefab in the project for a
        /// merge of this controller and took the MA Parameters above it. Scan, at the top of this
        /// card, now walks the same prefabs for the same reason and comes back with the prefab
        /// NAMED — and filling the store from a link somebody confirmed is the same answer with
        /// its provenance attached, which the silent one never had.
        ///
        /// Every change is announced as a parameter change: the parameters panel keeps a wrapped
        /// store of its own and would otherwise draw the old one's budget.
        /// </summary>
        void DrawStoreSlot(AnimatorController controller)
        {
            EditorGUILayout.BeginHorizontal();
            var current = GraphFrameData.GetParameterStore(controller);
            var picked = EditorGUILayout.ObjectField(
                new GUIContent(L.Tr("Params"),
                    L.Tr("The parameter store this controller belongs to: a VRC Expression Parameters asset, or a GameObject carrying an MA Parameters component. Assigned explicitly — DaerD never guesses it from the scene.")),
                current, typeof(UnityEngine.Object), true);
            if (picked != current)
            {
                var wrapped = ParameterStore.TryWrap(picked);
                if (picked != null && wrapped == null)
                    EditorUtility.DisplayDialog(L.Tr("Parameter Store"),
                        L.Tr("Assign a VRC Expression Parameters asset or an object with an MA Parameters component."), "OK");
                else
                {
                    // The wrapped component, not the whole GameObject, so the slot shows exactly
                    // what will be edited.
                    GraphFrameData.SetParameterStore(controller, wrapped != null ? wrapped.Target : null);
                    Context.NotifyParametersChanged();
                }
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Detect"),
                    L.Tr("Search the scene for an exact match: an avatar running this controller, or an MA Merge Animator referencing it. A gimmick that lives only as a prefab is in no scene at all — Scan above is the button for that one. Nothing is picked up automatically without one of them.")),
                    EditorStyles.miniButton, GUILayout.Width(52)))
            {
                var detected = ParameterStore.DetectFor(controller);
                if (detected == null)
                    EditorUtility.DisplayDialog(L.Tr("Parameter Store"),
                        L.Tr("No exact match in the scene — no avatar or MA Merge Animator references this controller. A gimmick that lives only as a prefab is found by Scan instead."), "OK");
                else
                {
                    GraphFrameData.SetParameterStore(controller, detected);
                    Context.NotifyParametersChanged();
                }
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---- prefab link actions -----------------------------------------------

        /// <summary>
        /// Scan, then link — or say why not. The three answers are told apart by
        /// <see cref="PrefabLinks.ScanFor"/> rather than by counting the list here, so the
        /// branch is one decision with a test on it instead of a shape the UI happens to have.
        /// </summary>
        void ScanAndLink(AnimatorController controller, bool rescan)
        {
            // Rescan exists for the case the memory is the problem: an answer remembered from
            // before the prefab was fixed. Ordinary Scan keeps it, since refilling means walking
            // the project again.
            if (rescan) PrefabLinks.ForgetCandidates();
            var scan = PrefabLinks.ScanFor(controller);
            switch (scan.choice)
            {
                case PrefabLinkChoice.One:
                    Confirm(controller, scan.plan);
                    break;
                case PrefabLinkChoice.Several:
                    ShowCandidateMenu(controller, scan.candidates);
                    break;
                default:
                    EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                        L.Tr("No prefab in this project has an MA Merge Animator that merges '{0}'.",
                            controller.name), "OK");
                    break;
            }
        }

        /// <summary>A prefab dropped on the slot: the same three answers asked of that one
        /// prefab, which costs no sweep at all — the user already said which.</summary>
        void DropPrefab(AnimatorController controller, GameObject prefab)
        {
            if (prefab == null)
            {
                Unlink(controller);
                return;
            }
            // A scene object cannot be the answer: a gimmick spends most of its life as a prefab
            // in no scene at all, and a link into a scene would die with the scene.
            if (!PrefabUtility.IsPartOfPrefabAsset(prefab))
            {
                EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                    L.Tr("'{0}' is not a prefab asset. Only a prefab in the project can be linked.",
                        prefab.name), "OK");
                return;
            }
            var scan = PrefabLinks.ScanIn(prefab, controller);
            switch (scan.choice)
            {
                case PrefabLinkChoice.One:
                    Confirm(controller, scan.plan);
                    break;
                case PrefabLinkChoice.Several:
                    ShowCandidateMenu(controller, scan.candidates);
                    break;
                default:
                    EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                        L.Tr("'{0}' has no MA Merge Animator that merges '{1}'.",
                            prefab.name, controller.name), "OK");
                    break;
            }
        }

        /// <summary>
        /// The picker for several candidates. Slashes are replaced in the label because
        /// GenericMenu reads one as "start a submenu", and both halves of what identifies a
        /// candidate — where the prefab is, where the merge is inside it — are paths.
        /// </summary>
        void ShowCandidateMenu(AnimatorController controller, List<PrefabLinkCandidate> candidates)
        {
            var menu = new GenericMenu();
            foreach (var candidate in candidates)
            {
                var captured = candidate;
                string label = AssetDatabase.GetAssetPath(candidate.prefab) + "  :  "
                    + PrefabLinks.PathIn(candidate.prefab, candidate.mergeAnimator);
                menu.AddItem(new GUIContent(label.Replace("/", " › ")), false,
                    () => Confirm(controller, PrefabLinks.PlanFor(controller, captured)));
            }
            menu.ShowAsContext();
        }

        /// <summary>
        /// The confirmation, written out of the plan rather than out of the UI's own idea of
        /// what is about to happen — including the store slot, which is the half a person would
        /// not otherwise expect a button called "link" to touch.
        /// </summary>
        void Confirm(AnimatorController controller, PrefabLinkPlan plan)
        {
            if (plan == null || plan.candidate == null) return;
            string message = L.Tr("Link this controller to '{0}'?\n\nPrefab: {1}\nMerge Animator: {2}",
                plan.candidate.prefab.name, AssetDatabase.GetAssetPath(plan.candidate.prefab),
                PrefabLinks.PathIn(plan.candidate.prefab, plan.candidate.mergeAnimator));
            if (plan.FillsStore)
                message += "\n\n" + L.Tr("The parameter store slot is empty, so it is set to the MA Parameters on '{0}' at the same time.",
                    plan.store.name);
            else if (plan.StoreDiffers)
                message += "\n\n" + L.Tr("The parameter store slot already holds '{0}' and is left alone. A button below adopts the prefab's own instead.",
                    plan.currentStore.name);

            if (!EditorUtility.DisplayDialog(L.Tr("Prefab Link"), message, L.Tr("Link"), L.Tr("Cancel")))
                return;
            PrefabLinks.Apply(controller, plan);
            Context.NotifyPrefabLinkChanged();
            if (plan.FillsStore) Context.NotifyParametersChanged();
        }

        /// <summary>
        /// The first thing DaerD writes into somebody's prefab, and the shape every later one
        /// follows: refuse first, then say what is about to be added, then write once.
        ///
        /// The refusal comes before the question rather than after it, because a dialog that
        /// asks and then fails is a dialog that taught the user nothing. Saving an asset cannot
        /// be undone, so the sentence about what is being added IS the undo — there is no second
        /// chance to read it.
        /// </summary>
        void AddParametersToPrefab(AnimatorController controller, PrefabLinkStatus status)
        {
            var prefab = status.prefab;
            switch (PrefabWriter.Check(prefab))
            {
                case PrefabWriteRefusal.ImmutablePackage:
                    EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                        L.Tr("'{0}' lives in a package the package manager owns, so DaerD will not write to it. Copy the prefab into this project's Assets folder first.",
                            AssetDatabase.GetAssetPath(prefab)), "OK");
                    return;
                case PrefabWriteRefusal.OpenWithUnsavedEdits:
                    EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                        L.Tr("'{0}' is open in prefab mode with unsaved changes. Save or discard them first — writing the file underneath an open stage would lose one of the two sets of edits.",
                            prefab.name), "OK");
                    return;
                case PrefabWriteRefusal.NotAPrefabAsset:
                    EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                        L.Tr("The linked prefab is not a prefab asset any more, so there is nothing to write to."), "OK");
                    return;
            }

            if (!EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                    L.Tr("Add an MA Parameters component to the root of '{0}'?\n\nThat is the only change: one component on the root, nothing removed, nothing moved. It goes on the root because Modular Avatar looks for it on the merge's own object and upwards, so every Merge Animator in this prefab — including ones added later — can see it there.\n\nThe prefab file is saved immediately, and saving an asset cannot be undone.",
                        prefab.name),
                    L.Tr("Add"), L.Tr("Cancel")))
                return;

            var added = PrefabWriter.AddParameters(prefab);
            if (added == null)
            {
                EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                    L.Tr("'{0}' was not changed — Modular Avatar is not installed, or the prefab could not be opened.",
                        prefab.name), "OK");
                return;
            }
            // The slot is filled only when it is empty, the same rule linking follows: a store
            // somebody chose is an answer, not a gap.
            if (GraphFrameData.GetParameterStore(controller) == null)
                GraphFrameData.SetParameterStore(controller, added);
            Context.NotifyParametersChanged();
        }

        void Unlink(AnimatorController controller)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("Prefab Link"),
                    L.Tr("Forget which prefab this controller belongs to?\n\nNothing inside the prefab is changed, and the parameter store slot is left as it is."),
                    L.Tr("Unlink"), L.Tr("Cancel")))
                return;
            GraphFrameData.ClearPrefabLink(controller);
            Context.NotifyPrefabLinkChanged();
        }

        // ---- DBT gadgets -------------------------------------------------------

        /// <summary>The gadgets saved with this controller, each with the operation it computes
        /// and the layer whose root Direct tree it hangs off. Editing re-opens the wizard on that
        /// gadget, so the inputs it was made from are the ones on screen.</summary>
        void DrawGadgets(AnimatorController controller)
        {
            var gadgets = GraphFrameData.GetGadgets(controller);
            _gadgetsOpen = BeginFoldCard(L.Tr("DBT Gadgets"), gadgets.Count, _gadgetsOpen);
            if (!_gadgetsOpen)
            {
                EndCard();
                return;
            }
            if (gadgets.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No gadgets yet."), EditorStyles.centeredGreyMiniLabel);

            foreach (var config in gadgets)
            {
                string kind = KindLabel(config);
                string layer = LayerNameOf(controller, config.layer);
                EditorGUILayout.BeginHorizontal();
                DrawRowName(config.output, config.output + " (" + kind + ") — " + layer);
                DrawRowNote(kind);
                DrawRowNote(layer);
                if (RowButton(L.Tr("Edit")))
                {
                    AapGadgetWindow.Open(controller, config, OnGadgetApplied);
                    GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
                }
                if (RowButton(L.Tr("Select")))
                {
                    SelectLayer(controller, config.layer);
                    GUIUtility.ExitGUI();
                }
                if (RowButton(L.Tr("Delete")))
                {
                    DeleteGadget(controller, config);
                    GUIUtility.ExitGUI();   // the gadget list was rebuilt under this layout pass
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button(new GUIContent(L.Tr("+ Add Gadget"),
                    L.Tr("Add a Direct blend tree gadget that computes a float operation every frame."))))
            {
                AapGadgetWindow.Open(controller, OnGadgetApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            EndCard();
        }

        /// <summary>The operation a saved gadget computes, as the wizard's popup names it.
        /// <see cref="AapGadgets.KindLabels"/> is indexed by the enum the config stores as an
        /// int.</summary>
        static string KindLabel(GraphFrameData.AapGadgetConfig config) =>
            config.kind >= 0 && config.kind < AapGadgets.KindLabels.Length
                ? AapGadgets.KindLabels[config.kind] : "?";

        /// <summary>A gadget was created, regenerated or deleted: parameters, a blend tree and
        /// possibly a whole layer changed with it.</summary>
        void OnGadgetApplied() => Context.NotifyLayerStructureChanged();

        void DeleteGadget(AnimatorController controller, GraphFrameData.AapGadgetConfig config)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("DBT Gadget"),
                    L.Tr("Delete this gadget? Its trees, clips and parameters are removed."),
                    L.Tr("Delete"), L.Tr("Cancel")))
                return;
            AapGadgets.RemoveGadget(controller, config);
            // No build follows this one, so the sub-assets it freed are flushed here.
            DbtBuilder.CommitSubAssets(controller);
            OnGadgetApplied();
        }

        // ---- object gadgets ----------------------------------------------------

        /// <summary>
        /// The gadgets whose subject is an object in the linked prefab, listed apart from the
        /// DBT gadgets above. Two lists rather than one because they are two families: these are
        /// regenerated against a prefab and are meaningless without a healthy pin, while a DBT
        /// gadget is arithmetic over parameters and cares about no prefab at all. Sharing a card
        /// would mean one heading that is true of half its rows.
        ///
        /// With the pin unusable the card says so and offers nothing. There is no half of this
        /// that works without it — every path is relative to the merge — and buttons that refuse
        /// one by one on being pressed teach less than one sentence that says why.
        /// </summary>
        void DrawObjectGadgets(AnimatorController controller)
        {
            var gadgets = GraphFrameData.GetObjectGadgets(controller);
            _objectGadgetsOpen = BeginFoldCard(L.Tr("Object Gadgets"), gadgets.Count,
                _objectGadgetsOpen);
            if (!_objectGadgetsOpen)
            {
                EndCard();
                return;
            }

            string refusal = ObjectGadgets.LinkRefusal(controller);
            if (refusal != null)
            {
                EditorGUILayout.HelpBox(refusal, MessageType.Info);
                EndCard();
                return;
            }
            if (gadgets.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No object gadgets yet."),
                    EditorStyles.centeredGreyMiniLabel);

            foreach (var config in gadgets)
            {
                string kind = ObjectGadgets.KindLabel(config);
                string mode = ObjectGadgets.ModeLabel(config);
                string targets = L.Tr("{0} object(s)", config.targets.Count);
                EditorGUILayout.BeginHorizontal();
                DrawRowName(config.name,
                    config.name + " (" + kind + ", " + mode + ") — " + targets);
                DrawRowNote(kind);
                DrawRowNote(mode);
                DrawRowNote(targets);
                if (RowButton(L.Tr("Edit")))
                {
                    ObjectGadgetWindow.Open(controller, config, OnGadgetApplied);
                    GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
                }
                if (RowButton(L.Tr("Delete")))
                {
                    DeleteObjectGadget(controller, config);
                    GUIUtility.ExitGUI();   // the list was rebuilt under this layout pass
                }
                EditorGUILayout.EndHorizontal();
            }

            // Named for the family, not for the one kind that exists — the card's heading
            // already says which family, exactly as the DBT gadgets card above it does. The
            // family is meant to grow into "pick objects in the prefab and do something to
            // them", so a button called Add Toggle would have to be renamed on the day a second
            // kind lands, and until then it says that toggling is all this list can ever hold.
            // The editor it opens is still the toggle one; the tooltip is where that is said.
            if (GUILayout.Button(new GUIContent(L.Tr("+ Add Gadget"),
                    L.Tr("Add a gadget whose subject is an object inside the linked prefab. One kind so far: a toggle, which switches those objects on and off from a parameter."))))
            {
                ObjectGadgetWindow.Open(controller, OnGadgetApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            EndCard();
        }

        /// <summary>Deleting names what goes with it. Not politeness: an asset save cannot be
        /// undone once it reaches a prefab, and even here the parameter is the piece somebody
        /// else's layer may be reading — so the sentence lists exactly what the record holds.
        /// </summary>
        void DeleteObjectGadget(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("Object Gadget"),
                    L.Tr("Delete the object gadget '{0}'?\n\nThis removes {1}. Nothing inside the prefab is changed.",
                        config.name, ObjectGadgetLoss(controller, config)),
                    L.Tr("Delete"), L.Tr("Cancel")))
                return;
            ObjectGadgets.Remove(controller, config);
            // No build follows this one, so the sub-assets it freed are flushed here.
            DbtBuilder.CommitSubAssets(controller);
            OnGadgetApplied();
        }

        /// <summary>What deleting one object gadget takes with it, read off the record rather
        /// than described from memory — the dialog and the sweep have to be the same list.
        /// Internal so a test can hold the two side by side: a dialog that under-states what is
        /// about to go is worse than no dialog.</summary>
        internal static string ObjectGadgetLoss(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            var lost = new List<string>();
            string layer = LayerNameOf(controller, config.layer);
            lost.Add(config.mode == (int)ToggleBuilder.Mode.Layer
                ? L.Tr("the layer '{0}'", layer)
                : L.Tr("its blend tree in the layer '{0}'", layer));

            int clips = 0, supplied = 0;
            foreach (var output in new[] { config.onClip, config.offClip })
            {
                if (output == null || output.clip == null) continue;
                if (output.userProvided) supplied++;
                else clips++;
            }
            if (clips > 0) lost.Add(L.Tr("{0} generated clip(s)", clips));
            // Named apart from the generated ones because what happens to them is different:
            // the file stays and only this gadget's rows leave it. Somebody about to press
            // Delete on a gadget pointed at their own clip is owed that distinction.
            if (supplied > 0) lost.Add(L.Tr("this gadget's rows in {0} clip(s) you supplied", supplied));
            if (config.createdParameter)
                lost.Add(L.Tr("the parameter '{0}'", config.parameter));
            return string.Join(", ", lost);
        }

        // ---- async sync --------------------------------------------------------

        /// <summary>The sync setups saved with this controller. Selecting one opens its layer,
        /// where the settings panel takes over the centre pane — that is where a setup is
        /// edited, so there is no second copy of that form here.</summary>
        void DrawAsyncSyncs(AnimatorController controller)
        {
            var configs = GraphFrameData.GetAsyncSyncs(controller);
            _syncsOpen = BeginFoldCard(L.Tr("Async Sync"), configs.Count, _syncsOpen);
            if (!_syncsOpen)
            {
                EndCard();
                return;
            }
            if (configs.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No async sync setups yet."),
                    EditorStyles.centeredGreyMiniLabel);

            foreach (var config in configs)
            {
                string shape = L.Tr("{0} target(s), {1}s step",
                    config.targets.Count, config.stepSeconds.ToString("0.###"));
                EditorGUILayout.BeginHorizontal();
                DrawRowName(config.baseName, config.baseName + " — " + shape);
                DrawRowNote(shape);
                if (RowButton(L.Tr("Select")))
                {
                    int index = AsyncSyncBuilder.LayerIndexOf(controller, config);
                    if (index >= 0) Context.SetLayer(index);
                    GUIUtility.ExitGUI();
                }
                if (RowButton(L.Tr("Delete")))
                {
                    DeleteAsyncSync(controller, config);
                    GUIUtility.ExitGUI();   // the setup list was rebuilt under this layout pass
                }
                EditorGUILayout.EndHorizontal();
            }

            if (GUILayout.Button(new GUIContent(L.Tr("+ New Async Sync"),
                    L.Tr("Time-multiplex several parameters over a few synced ones (index + value channels) — parameter compression."))))
            {
                AsyncSyncWindow.Open(controller, layerIndex => Context.NotifyLayerStructureChanged(layerIndex));
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            EndCard();
        }

        /// <summary>
        /// Deleting names what goes with it, for the reason the object gadget's dialog does and
        /// then some: a setup spreads over as many as four kinds of layer and a namespace full
        /// of parameters, and until now the only way to be rid of one was to delete its main
        /// layer by hand — which left the Ready, Stale and group layers standing as orphans
        /// nothing pointed at any more.
        ///
        /// The sentence also says what STAYS, because those are the two pieces somebody about
        /// to press Delete would most reasonably fear for: the Empty clip (shared, possibly
        /// theirs) and the parameter store's declaration rows.
        /// </summary>
        void DeleteAsyncSync(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("Async Sync"),
                    L.Tr("Delete the async sync setup '{0}'?\n\nThis removes {1}. The multiplexed parameters, the Empty clip and the parameter store rows are left alone.",
                        config.baseName, AsyncSyncLoss(controller, config)),
                    L.Tr("Delete"), L.Tr("Cancel")))
                return;
            AsyncSyncBuilder.Remove(controller, config);
            Context.NotifyLayerStructureChanged();
        }

        /// <summary>What deleting one async sync setup takes with it, read off the record and
        /// off the same enumerators the removal uses — the dialog and the sweep have to be the
        /// same list. Internal so a test can hold the two side by side, exactly as
        /// <see cref="ObjectGadgetLoss"/> is.</summary>
        internal static string AsyncSyncLoss(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config)
        {
            var lost = new List<string>();
            // The layers by the names they answer to NOW: a setup's layers can be renamed, and
            // the row somebody is about to delete has to be findable in the layer list.
            var layers = new List<string>();
            foreach (var machine in AsyncSyncBuilder.OwnedLayers(config))
                layers.Add(LayerNameOf(controller, machine));
            // The singular goes through the object gadget's own phrase, so the one thing the
            // two delete dialogs have in common is said the same way in both.
            if (layers.Count == 1) lost.Add(L.Tr("the layer '{0}'", layers[0]));
            else if (layers.Count > 1)
                lost.Add(L.Tr("the layers {0}", "'" + string.Join("', '", layers) + "'"));

            int parameters = AsyncSyncBuilder.OwnedParameters(controller, config).Count;
            if (parameters > 0) lost.Add(L.Tr("{0} generated parameter(s)", parameters));

            int requests = 0;
            foreach (var request in GraphFrameData.GetSyncRequests(controller))
                if (request.baseName == config.baseName) requests++;
            if (requests > 0) lost.Add(L.Tr("{0} sync request(s) on states", requests));
            return string.Join(", ", lost);
        }

        // ---- C# recipes --------------------------------------------------------

        // The last Generate or Verify run's result, kept until the next action replaces it —
        // the rule the recipe inspector keeps, because a result that vanishes on the next
        // repaint has said nothing at all. Held per recipe so it cannot appear under a row it
        // did not come from.
        UnityEngine.Object _recipeResultFor;
        List<string> _recipeMessages;
        string _recipeCleanMessage;

        /// <summary>
        /// The C# recipes this controller is linked to: where its source is, whether the code
        /// that WOULD run is the code on disk, and the two actions that belong to a recipe.
        ///
        /// One row per recipe ASSET rather than per layer — a recipe generates as many layers as
        /// it likes and the asset is the source of truth for all of them. The rows come from the
        /// link, which is the controller's own record of where its recipes are, with any recipe
        /// that owns layers here without being linked appended after them: that is a recipe from
        /// before links existed, and it joins the first list the next time it generates.
        ///
        /// Unlike the gadget cards above, this one hides itself when there is nothing to list.
        /// Those say "none yet" because adding one is something you do from that card; a recipe
        /// is made by the export tool further down the screen, so an empty card here would be a
        /// heading with nothing to offer.
        /// </summary>
        void DrawRecipes(AnimatorController controller)
        {
            // The code-owned record is keyed by layer; regrouped the other way round here, with
            // a list of its own to keep the rows in the order the layers were found in.
            var byRecipe = new Dictionary<UnityEngine.Object, List<AnimatorStateMachine>>();
            var owners = new List<UnityEngine.Object>();
            foreach (var entry in GraphFrameData.GetCodeOwned(controller))
            {
                if (!byRecipe.TryGetValue(entry.Value, out var machines))
                {
                    byRecipe[entry.Value] = machines = new List<AnimatorStateMachine>();
                    owners.Add(entry.Value);
                }
                machines.Add(entry.Key);
            }

            var rows = new List<UnityEngine.Object>(GraphFrameData.LinkedRecipes(controller));
            foreach (var owner in owners)
                if (!rows.Contains(owner)) rows.Add(owner);
            if (rows.Count == 0) return;

            EditorGUILayout.Space(8);
            _recipesOpen = BeginFoldCard(L.Tr("C# Recipes"), rows.Count, _recipesOpen);
            if (!_recipesOpen)
            {
                EndCard();
                return;
            }

            foreach (var row in rows)
            {
                var recipe = row as ControllerRecipe;
                var names = new List<string>();
                if (byRecipe.TryGetValue(row, out var machines))
                    foreach (var machine in machines)
                        names.Add(LayerNameOf(controller, machine));
                // A linked recipe that has never generated owns nothing, which is a state to say
                // rather than an empty gap — it is what a freshly exported recipe looks like.
                string owned = names.Count > 0
                    ? string.Join(", ", names) : L.Tr("owns no layers yet");

                string state = RecipeState(controller, recipe, out string reason);

                EditorGUILayout.BeginHorizontal();
                DrawRowName(row.name, row.name + " — " + owned);
                DrawRowNote(owned);
                DrawRowNote(state, reason ?? L.Tr("The compiled code matches this recipe's .cs. Whether the CONTROLLER matches the recipe is the question Verify answers."));
                // Disabled rather than refusing on the press: the reason is on the row already,
                // and a button that explains itself only after being clicked teaches later than
                // it needs to.
                using (new EditorGUI.DisabledScope(reason != null))
                {
                    if (RowButton(L.Tr("Generate"),
                        L.Tr("Apply this recipe to the target controller (undoable).")))
                    {
                        ShowRecipeResult(row, recipe.Generate(),
                            L.Tr("Clean — code and controller match."));
                        Context.NotifyLayerStructureChanged();
                        GUIUtility.ExitGUI();   // layers changed under this layout pass
                    }
                    if (RowButton(L.Tr("Verify"),
                        L.Tr("Compare what the code declares against the controller's current contents.")))
                    {
                        ShowRecipeResult(row, recipe.Verify(),
                            L.Tr("Clean — code and controller match."));
                        GUIUtility.ExitGUI();
                    }
                }
                if (RowButton(L.Tr("Open"),
                    L.Tr("Select this recipe asset and highlight it in the Project window.")))
                {
                    Selection.activeObject = row;
                    EditorGUIUtility.PingObject(row);
                }
                EditorGUILayout.EndHorizontal();

                if (reason != null)
                    EditorGUILayout.LabelField(reason, EditorStyles.wordWrappedMiniLabel);
                if (_recipeResultFor == row) DrawRecipeResult();
            }
            EndCard();
        }

        /// <summary>
        /// A recipe's state as the short word for its row, with the sentence that explains it
        /// when there is one — a null <paramref name="reason"/> means the row can be run.
        ///
        /// The order is the card's own. Whether the recipe points at THIS controller comes first,
        /// because one with another target would write into a different asset entirely and no
        /// amount of freshness makes that safe. Everything under it is
        /// <see cref="RecipeFreshness"/>'s verdict, reused rather than re-derived: the button and
        /// the method behind it have to agree on what runnable means, or the button offers a run
        /// that the method then refuses.
        /// </summary>
        static string RecipeState(AnimatorController controller, ControllerRecipe recipe,
            out string reason)
        {
            if (recipe == null)
            {
                reason = L.Tr("This asset is not a recipe DaerD can run.");
                return L.Tr("Unusable");
            }
            if (recipe.targetController != controller)
            {
                reason = L.Tr("This recipe generates into {0}, not this controller — running it from here would write somewhere else. Open it and check its target controller.",
                    Quoted(recipe.targetController));
                return L.Tr("Wrong target");
            }
            var staleness = RecipeFreshness.Check(recipe);
            reason = RecipeFreshness.Reason(staleness);
            switch (staleness)
            {
                case RecipeFreshness.Staleness.CompileFailed: return L.Tr("Compile error");
                case RecipeFreshness.Staleness.Compiling: return L.Tr("Compiling");
                case RecipeFreshness.Staleness.SourceNewer: return L.Tr(".cs is newer");
                default: return L.Tr("Up to date");
            }
        }

        void ShowRecipeResult(UnityEngine.Object recipe, List<string> messages, string clean)
        {
            _recipeResultFor = recipe;
            _recipeMessages = messages;
            _recipeCleanMessage = clean;
        }

        /// <summary>The last run's findings, said the same way the recipe inspector says them —
        /// one screen showing the same list in two vocabularies is how a reader ends up unsure
        /// whether they are looking at the same answer.</summary>
        void DrawRecipeResult()
        {
            if (_recipeMessages == null) return;
            if (_recipeMessages.Count == 0)
            {
                EditorGUILayout.HelpBox(_recipeCleanMessage, MessageType.Info);
                return;
            }
            EditorGUILayout.HelpBox(L.Tr("{0} finding(s):", _recipeMessages.Count),
                MessageType.Warning);
            foreach (var message in _recipeMessages)
                EditorGUILayout.LabelField("• " + message, EditorStyles.wordWrappedMiniLabel);
        }

        // ---- tools -------------------------------------------------------------

        /// <summary>One folding card per tool, so a tool can be worked in without leaving the
        /// screen — and every one of them keeps the window it used to open, one button away.
        /// They start folded: each is a working surface of its own, and several unfolded at once
        /// would bury everything else in the column.</summary>
        void DrawTools(AnimatorController controller)
        {
            DrawAnalyzerTool(controller);
            EditorGUILayout.Space(8);
            DrawClipsTool(controller);
            EditorGUILayout.Space(8);
            DrawRecipeExportTool(controller);
            EditorGUILayout.Space(8);
            DrawCleanupTool(controller);
        }

        /// <summary>The analysis report, inline. Bound the same way the clip index is: a full
        /// audit walks the whole controller, so it runs when something changed, not per repaint.
        /// </summary>
        void DrawAnalyzerTool(AnimatorController controller)
        {
            _analyzerOpen = BeginToolCard(L.Tr("Analyzer"), _analyzerOpen, out bool window);
            if (window)
            {
                AnalyzerWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (_analyzerOpen)
            {
                if (_analyzer.Controller != controller || _analyzerRevision != _revision)
                {
                    _analyzer.SetController(controller);
                    _analyzerRevision = _revision;
                }
                _analyzer.DrawHeader(withControllerSlot: false);
                _analyzer.DrawReport();
            }
            EndCard();
        }

        /// <summary>The clip index, inline. The form is bound lazily and only re-collected when
        /// something could have changed it — walking every clip in the controller is not a
        /// per-repaint price.</summary>
        void DrawClipsTool(AnimatorController controller)
        {
            _clipsOpen = BeginToolCard(L.Tr("Clips"), _clipsOpen, out bool window);
            if (window)
            {
                ClipsWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (_clipsOpen)
            {
                if (_clips.Controller != controller || _clipsRevision != _revision)
                {
                    _clips.SetController(controller);
                    _clipsRevision = _revision;
                }
                _clips.DrawHeader(withControllerSlot: false);
                _clips.DrawList();
            }
            EndCard();
        }

        /// <summary>
        /// The recipe exporter, inline. Only its layer list is re-read when the controller
        /// changes — the class name, folder and toggles are typed by the user and must survive
        /// an edit made elsewhere while the card is open.
        /// </summary>
        void DrawRecipeExportTool(AnimatorController controller)
        {
            _recipeExportOpen = BeginToolCard(L.Tr("Recipe Export"), _recipeExportOpen, out bool window);
            if (window)
            {
                RecipeExportWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (_recipeExportOpen)
            {
                if (_recipeExport.Controller != controller)
                {
                    _recipeExport.SetController(controller);
                    _recipeExportRevision = _revision;
                }
                else if (_recipeExportRevision != _revision)
                {
                    _recipeExport.RefreshLayers();
                    _recipeExportRevision = _revision;
                }
                _recipeExport.DrawForm();
                EditorGUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                _recipeExport.DrawExportButton();
                EditorGUILayout.EndHorizontal();
            }
            EndCard();
        }

        /// <summary>Leftover sub-asset housekeeping. Folded away because it has nothing to say
        /// until a scan has been run, and no window of its own to offer.</summary>
        void DrawCleanupTool(AnimatorController controller)
        {
            _cleanupOpen = BeginFoldCard(L.Tr("Cleanup"), _cleanupOpen);
            if (_cleanupOpen)
                _cleanup.DrawCleanupSection(controller);
            EndCard();
        }

        // ---- shared row pieces -------------------------------------------------

        /// <summary>A list row's subject. It takes the slack in the row, which is what pushes
        /// the buttons to the right edge; the full text is the tooltip, because a narrow column
        /// clips the label and there would be no other way to read it.</summary>
        static void DrawRowName(string name, string full) =>
            EditorGUILayout.LabelField(new GUIContent(name, full), GUILayout.ExpandWidth(true));

        /// <summary>A grey aside beside the row's subject — what the gadget computes, where it
        /// lives, how big the setup is. Same weight as the layer list's badges, and sized to its
        /// text so a long one is not cut in half.</summary>
        static void DrawRowNote(string note) =>
            GUILayout.Label(note, EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandWidth(false));

        /// <summary>The same aside with something to say on hover — for the notes that are a
        /// verdict rather than a description, where the short word is only half the answer.
        /// </summary>
        static void DrawRowNote(string note, string tooltip) =>
            GUILayout.Label(new GUIContent(note, tooltip), EditorStyles.centeredGreyMiniLabel,
                GUILayout.ExpandWidth(false));

        /// <summary>A row action, at the one width they all share.</summary>
        static bool RowButton(string label) =>
            GUILayout.Button(label, EditorStyles.miniButton, GUILayout.Width(RowButtonWidth));

        static bool RowButton(string label, string tooltip) =>
            GUILayout.Button(new GUIContent(label, tooltip), EditorStyles.miniButton,
                GUILayout.Width(RowButtonWidth));

        /// <summary>The name of the layer a saved record points at. Records identify their
        /// layer by its root state machine so that renames and reorders don't break them, which
        /// is why the name has to be looked up at all.</summary>
        static string LayerNameOf(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (machine == null) return "?";
            foreach (var layer in controller.layers)
                if (layer.stateMachine == machine)
                    return layer.name;
            return machine.name;
        }

        /// <summary>Leaves home for the layer a record lives in. A record whose layer is gone
        /// stays where it is — there is nowhere to go.</summary>
        void SelectLayer(AnimatorController controller, AnimatorStateMachine machine)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == machine)
                {
                    Context.SetLayer(i);
                    return;
                }
        }
    }
}
