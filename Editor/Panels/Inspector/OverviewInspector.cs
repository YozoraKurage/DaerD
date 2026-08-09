using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- overview ---------------------------------------------------------

    /// <summary>What the inspector shows when nothing is selected: who this controller is, and
    /// the way to the home screen. Everything the overview used to carry — the associations,
    /// the bulk Write Defaults, the tool buttons and the cleanup section — lives there now,
    /// where it sits beside the controller's gadgets, sync setups and recipes instead of being
    /// wedged into a column sized for one state's inspector.</summary>
    class OverviewInspector
    {
        readonly DaerDContext _context;

        public OverviewInspector(DaerDContext context)
        {
            _context = context;
        }

        public void DrawOverview()
        {
            var controller = _context.Controller;
            EditorGUILayout.LabelField(L.Tr("Controller"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.Tr("Name"), controller.name);
            EditorGUILayout.LabelField(L.Tr("Layers"), controller.layers.Length.ToString());
            EditorGUILayout.LabelField(L.Tr("Parameters"), controller.parameters.Length.ToString());

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                L.Tr("Gadgets, sync setups and tools for this controller live in the Home screen."),
                MessageType.None);
            // Harmless while home is already showing — SelectHome is a no-op there.
            if (GUILayout.Button(L.Tr("Open Home")))
                _context.SelectHome();
        }
    }
}
