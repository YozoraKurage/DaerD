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

        // The three lists start expanded: seeing what the controller carries is the reason to
        // open this screen at all, so folding them away is the exception, not the default.
        bool _gadgetsOpen = true;
        bool _syncsOpen = true;
        bool _recipesOpen = true;

        public HomePanel(DaerDContext context) : base(context, "Home")
        {
            context.ControllerChanged += OnControllerChanged;
            context.LayersChanged += Refresh;
            context.ParametersChanged += Refresh;
            context.GraphStructureChanged += Refresh;
        }

        /// <summary>The leftover scan (and the object references captured in it) belongs to the
        /// outgoing controller — drop it on a tab switch.</summary>
        void OnControllerChanged()
        {
            _cleanup.Clear();
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

        /// <summary>The reports and editors that act on the whole controller. Two to a row: they
        /// are short labels, and a stack of full-width buttons reads as the most important thing
        /// in the column when it is the least. Each opens in its own window, so the explanations
        /// live in tooltips rather than in labels here.</summary>
        void DrawTools(AnimatorController controller)
        {
            BeginCard(L.Tr("Tools"));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(L.Tr("Analyze Controller"),
                    L.Tr("Audit this controller for unused parameters, broken conditions, unreachable states and more."))))
            {
                AnalyzerWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("List Clips"),
                    L.Tr("List every AnimationClip this controller references and the states that use it."))))
            {
                ClipsWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(L.Tr("Export C# Recipe"),
                    L.Tr("Convert this controller (or chosen layers) into editable C# that rebuilds it — clips stay assignable by drag & drop on the recipe asset."))))
            {
                RecipeExportWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Expressions Menu"),
                    L.Tr("Edit the avatar's VRC Expressions Menu (auto-detected from the scene)."))))
            {
                VrcMenuWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
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
