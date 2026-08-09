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
    /// </summary>
    class HomePanel : PanelBase
    {
        readonly CleanupInspector _cleanup = new CleanupInspector();

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
            DrawController(controller);
            PanelGui.HorizontalLine();
            DrawGadgets(controller);
            PanelGui.HorizontalLine();
            DrawAsyncSyncs(controller);
            PanelGui.HorizontalLine();
            DrawRecipes(controller);
            PanelGui.HorizontalLine();
            DrawTools(controller);
        }

        // ---- controller --------------------------------------------------------

        /// <summary>Identity, plus the assets this controller is explicitly associated with.
        /// The store and the menu are assigned by hand and never guessed from the scene, since
        /// DaerD is also used on gimmick controllers that belong to no avatar.</summary>
        void DrawController(AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("Controller"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.Tr("Name"), controller.name);
            EditorGUILayout.LabelField(L.Tr("Layers"), controller.layers.Length.ToString());
            EditorGUILayout.LabelField(L.Tr("Parameters"), controller.parameters.Length.ToString());

            EditorGUILayout.Space(6);
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

            EditorGUILayout.Space(4);
            var wdTooltip = L.Tr("Bulk-set every state. Layers containing only Direct blend trees stay ON.");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(L.Tr("Write Defaults"), wdTooltip));
            if (GUILayout.Button(new GUIContent(L.Tr("Set All ON"), wdTooltip)))
                BulkSetWriteDefaults(controller, true);
            if (GUILayout.Button(new GUIContent(L.Tr("Set All OFF"), wdTooltip)))
                BulkSetWriteDefaults(controller, false);
            EditorGUILayout.EndHorizontal();
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

        /// <summary>The gadgets saved with this controller, each with the layer whose root
        /// Direct tree it hangs off. Editing re-opens the wizard on that gadget, so the inputs
        /// it was made from are the ones on screen.</summary>
        void DrawGadgets(AnimatorController controller)
        {
            var gadgets = GraphFrameData.GetGadgets(controller);
            EditorGUILayout.LabelField(L.Tr("DBT Gadgets") + " (" + gadgets.Count + ")",
                EditorStyles.boldLabel);
            if (gadgets.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No gadgets yet."), EditorStyles.centeredGreyMiniLabel);

            foreach (var config in gadgets)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(AapGadgetWindow.GadgetLabel(config) + "  —  "
                    + LayerNameOf(controller, config.layer));
                if (GUILayout.Button(L.Tr("Edit"), EditorStyles.miniButton, GUILayout.Width(46)))
                {
                    AapGadgetWindow.Open(controller, config, OnGadgetApplied);
                    GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
                }
                if (GUILayout.Button(L.Tr("Select"), EditorStyles.miniButton, GUILayout.Width(56)))
                {
                    SelectLayer(controller, config.layer);
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button(L.Tr("Delete"), EditorStyles.miniButton, GUILayout.Width(56)))
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
        }

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
            EditorGUILayout.LabelField(L.Tr("Async Sync") + " (" + configs.Count + ")",
                EditorStyles.boldLabel);
            if (configs.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No async sync setups yet."),
                    EditorStyles.centeredGreyMiniLabel);

            foreach (var config in configs)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(config.baseName + "  —  "
                    + L.Tr("{0} target(s), {1}s step",
                        config.targets.Count, config.stepSeconds.ToString("0.###")));
                if (GUILayout.Button(L.Tr("Select"), EditorStyles.miniButton, GUILayout.Width(56)))
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

            EditorGUILayout.LabelField(L.Tr("C# Recipes") + " (" + recipes.Count + ")",
                EditorStyles.boldLabel);
            if (recipes.Count == 0)
                EditorGUILayout.LabelField(L.Tr("No recipe-owned layers."),
                    EditorStyles.centeredGreyMiniLabel);

            foreach (var recipe in recipes)
            {
                var machines = byRecipe[recipe];
                var names = new List<string>();
                foreach (var machine in machines)
                    names.Add(LayerNameOf(controller, machine));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(recipe.name + "  —  " + string.Join(", ", names));
                if (GUILayout.Button(new GUIContent(L.Tr("Ping"),
                        L.Tr("Highlight this object in the Project / graph")),
                        EditorStyles.miniButton, GUILayout.Width(46)))
                    EditorGUIUtility.PingObject(recipe);
                // One layer is unambiguous; several would need a picker nobody asked for, and
                // the recipe asset is the better destination for those anyway.
                using (new EditorGUI.DisabledScope(machines.Count != 1))
                    if (GUILayout.Button(L.Tr("Select"), EditorStyles.miniButton, GUILayout.Width(56)))
                    {
                        SelectLayer(controller, machines[0]);
                        GUIUtility.ExitGUI();
                    }
                EditorGUILayout.EndHorizontal();
            }
        }

        // ---- tools -------------------------------------------------------------

        /// <summary>The reports and editors that act on the whole controller. Each opens in its
        /// own window, so the explanations live in tooltips rather than in labels here.</summary>
        void DrawTools(AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("Tools"), EditorStyles.boldLabel);
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
            _cleanup.DrawCleanupSection(controller);
        }

        // ---- shared row pieces -------------------------------------------------

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
