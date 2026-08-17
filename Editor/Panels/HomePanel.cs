using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

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
            DrawAsyncSyncs(controller);
            EditorGUILayout.Space(8);
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
            DrawAsyncSyncs(controller);
            EditorGUILayout.Space(8);
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
        /// The store and the menu are assigned by hand and never guessed from the scene, since
        /// DaerD is also used on gimmick controllers that belong to no avatar.</summary>
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

                // Announced as a parameter change so the parameters panel drops the store it has
                // cached and redraws its budget against the new one.
                PanelGui.ParameterStoreField(controller, Context.NotifyParametersChanged);

                var currentMenu = GraphFrameData.GetExpressionsMenu(controller);
                var pickedMenu = EditorGUILayout.ObjectField(
                    new GUIContent(L.Tr("Expressions Menu"),
                        L.Tr("The VRC Expressions Menu this controller belongs to, opened by the menu editor. Assigned explicitly — DaerD never guesses it from the scene.")),
                    currentMenu, typeof(ScriptableObject), false);
                if (pickedMenu != currentMenu)
                {
                    // The slot only accepts what the menu editor can actually read back.
                    if (pickedMenu == null || VrcMenuAccess.Is(pickedMenu))
                        GraphFrameData.SetExpressionsMenu(controller, pickedMenu);
                    else
                        EditorUtility.DisplayDialog(L.Tr("DaerD Menu"),
                            L.Tr("That asset is not a VRC Expressions Menu."), "OK");
                }

                // Inside the scope too, so its prefix lines up with the three slots above it.
                var wdTooltip = L.Tr("Bulk-set every state. Layers containing only Direct blend trees stay ON.");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent(L.Tr("Write Defaults"), wdTooltip));
                if (GUILayout.Button(new GUIContent(L.Tr("Set All ON"), wdTooltip)))
                    BulkSetWriteDefaults(controller, true);
                if (GUILayout.Button(new GUIContent(L.Tr("Set All OFF"), wdTooltip)))
                    BulkSetWriteDefaults(controller, false);
                EditorGUILayout.EndHorizontal();
            }

            EndCard();
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

            DrawLinkedStore(controller, status);
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
        /// The store the link implies, beside the store the controller actually uses.
        ///
        /// Read-only on purpose. The rows are edited in the Parameters panel, which is in the
        /// left column and visible from here — a second editing surface for the same rows is
        /// two implementations of the same list, and the panel's one already knows about
        /// effective names, the budget and the sync diff.
        /// </summary>
        void DrawLinkedStore(AnimatorController controller, PrefabLinkStatus status)
        {
            if (!status.IsHealthy) return;
            var linked = PrefabLinks.StoreOf(status);
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
            else if (linked == null)
            {
                EditorGUILayout.LabelField(
                    L.Tr("The linked prefab has no MA Parameters above its merge yet."),
                    EditorStyles.centeredGreyMiniLabel);
            }

            if (linked == null)
            {
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

        // ---- C# recipes --------------------------------------------------------

        /// <summary>
        /// The recipes that own layers in this controller, one row per asset rather than per
        /// layer — a recipe generates as many as it likes, and it is the asset that is the
        /// source of truth for all of them. Generate lives on the asset, so the useful action
        /// here is finding it.
        /// </summary>
        void DrawRecipes(AnimatorController controller)
        {
            // The record is keyed by layer; regrouped the other way round here, with a list of
            // its own to keep the rows in the order the layers were found in.
            var byRecipe = new Dictionary<UnityEngine.Object, List<AnimatorStateMachine>>();
            var recipes = new List<UnityEngine.Object>();
            foreach (var entry in GraphFrameData.GetCodeOwned(controller))
            {
                if (!byRecipe.TryGetValue(entry.Value, out var machines))
                {
                    byRecipe[entry.Value] = machines = new List<AnimatorStateMachine>();
                    recipes.Add(entry.Value);
                }
                machines.Add(entry.Key);
            }

            _recipesOpen = BeginFoldCard(L.Tr("C# Recipes"), recipes.Count, _recipesOpen);
            if (!_recipesOpen)
            {
                EndCard();
                return;
            }
            if (recipes.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No recipe-owned layers."),
                    EditorStyles.centeredGreyMiniLabel);

            foreach (var recipe in recipes)
            {
                var machines = byRecipe[recipe];
                var names = new List<string>();
                foreach (var machine in machines)
                    names.Add(LayerNameOf(controller, machine));
                string owned = string.Join(", ", names);

                EditorGUILayout.BeginHorizontal();
                DrawRowName(recipe.name, recipe.name + " — " + owned);
                DrawRowNote(owned);
                if (RowButton(L.Tr("Ping"), L.Tr("Highlight this object in the Project / graph")))
                    EditorGUIUtility.PingObject(recipe);
                // One layer is unambiguous; several would need a picker nobody asked for, and
                // the recipe asset is the better destination for those anyway.
                using (new EditorGUI.DisabledScope(machines.Count != 1))
                    if (RowButton(L.Tr("Select")))
                    {
                        SelectLayer(controller, machines[0]);
                        GUIUtility.ExitGUI();
                    }
                EditorGUILayout.EndHorizontal();
            }
            EndCard();
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
            DrawMenuTool(controller);
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

        /// <summary>
        /// The one tool that is still only a window. It edits a different asset — the menu
        /// tree, not this controller — through a breadcrumb, a control list and an inspector
        /// for the selected control, which wants a pane of its own rather than a card in a
        /// column beside four others.
        /// </summary>
        void DrawMenuTool(AnimatorController controller)
        {
            BeginCard(L.Tr("Expressions Menu"));
            EditorGUILayout.LabelField(
                L.Tr("The menu editor works on the avatar's menu tree, in a window of its own."),
                EditorStyles.miniLabel);
            if (GUILayout.Button(new GUIContent(L.Tr("Open Menu Editor"),
                    L.Tr("Edit the avatar's VRC Expressions Menu (auto-detected from the scene)."))))
            {
                VrcMenuWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
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
