using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- overview ---------------------------------------------------------

    /// <summary>The controller's home screen: what the inspector shows when nothing is selected.</summary>
    class OverviewInspector
    {
        readonly DaerDContext _context;
        readonly CleanupInspector _cleanup;

        public OverviewInspector(DaerDContext context, CleanupInspector cleanup)
        {
            _context = context;
            _cleanup = cleanup;
        }

        // Deliberately sparse: identity rows, the two per-controller settings and three
        // action buttons. Explanations live in tooltips (and confirm dialogs), not in
        // always-visible labels — the reports themselves open in their own windows.
        public void DrawOverview()
        {
            var controller = _context.Controller;
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

            var wdTooltip = L.Tr("Bulk-set every state. Layers containing only Direct blend trees stay ON.");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(L.Tr("Write Defaults"), wdTooltip));
            if (GUILayout.Button(new GUIContent(L.Tr("Set All ON"), wdTooltip)))
                BulkSetWriteDefaults(controller, true);
            if (GUILayout.Button(new GUIContent(L.Tr("Set All OFF"), wdTooltip)))
                BulkSetWriteDefaults(controller, false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
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
            if (GUILayout.Button(new GUIContent(L.Tr("Object Toggle"),
                    L.Tr("Generate ON/OFF clips for picked GameObjects and the layer or Direct blend tree machinery that plays them."))))
            {
                ToggleBuilderWindow.Open(controller, OnToggleApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Async Sync"),
                    L.Tr("Time-multiplex several parameters over a few synced ones (index + value channels) — parameter compression."))))
            {
                AsyncSyncWindow.Open(controller, OnToggleApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Expressions Menu"),
                    L.Tr("Edit the avatar's VRC Expressions Menu (auto-detected from the scene)."))))
            {
                VrcMenuWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Export C# Recipe"),
                    L.Tr("Convert this controller (or chosen layers) into editable C# that rebuilds it — clips stay assignable by drag & drop on the recipe asset."))))
            {
                RecipeExportWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            _cleanup.DrawCleanupSection(controller);
        }

        /// <summary>The toggle wizard added a parameter, clips and possibly a layer — let
        /// every panel and the graph pick that up, and show the layer it landed in.</summary>
        void OnToggleApplied(int layerIndex) => _context.NotifyLayerStructureChanged(layerIndex);

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
            _context.NotifyGraphVisualsChanged(DaerDContext.GraphVisuals.AllStateNodes);
        }
    }
}
