using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- cleanup ----------------------------------------------------------

    /// <summary>Leftover / exposed sub-asset housekeeping, drawn at the bottom of the overview.</summary>
    class CleanupInspector
    {
        List<UnityEngine.Object> _leftovers;
        // In-use sub-assets that are visible in the Project window (see DrawExposedSubAssets).
        List<UnityEngine.Object> _exposed;

        /// <summary>Drops the scan results. They belong to the controller they were taken from.</summary>
        public void Clear()
        {
            _leftovers = null;
            _exposed = null;
        }

        public void DrawCleanupSection(AnimatorController controller)
        {
            bool isAsset = !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller));
            using (new EditorGUI.DisabledScope(!isAsset))
                if (GUILayout.Button(new GUIContent(L.Tr("Scan For Leftovers"),
                        L.Tr("Find sub-assets stored in the .controller file that nothing references any more.") + "\n"
                        + L.Tr("Blend trees, clips and states deleted from the graph can survive as invisible sub-assets; find them."))))
                {
                    _leftovers = ControllerCleanup.FindLeftoverSubAssets(controller);
                    _exposed = ControllerCleanup.FindExposedSubAssets(controller);
                }
            if (!isAsset)
                EditorGUILayout.LabelField(L.Tr("(unsaved controller — nothing to scan)"), EditorStyles.miniLabel);

            DrawExposedSubAssets(controller);
            if (_leftovers == null) return;

            // Entries deleted (or restored by Undo) since the scan linger as fake nulls.
            int live = 0;
            foreach (var asset in _leftovers)
                if (asset != null) live++;
            if (live == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("No leftover sub-assets found."), MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(L.Tr("{0} leftover sub-asset(s) in this .controller file.", live),
                MessageType.Warning);
            foreach (var asset in _leftovers)
            {
                if (asset == null) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(ControllerCleanup.Describe(asset), EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent(L.Tr("Ping"), L.Tr("Highlight this object in the Project / graph")),
                        EditorStyles.miniButton, GUILayout.Width(DaerDLayout.RowAction)))
                    EditorGUIUtility.PingObject(asset);
                if (GUILayout.Button(new GUIContent(L.Tr("Delete"),
                        L.Tr("Delete this leftover sub-asset from the .controller file")),
                        EditorStyles.miniButton, GUILayout.Width(DaerDLayout.RowAction)))
                {
                    // Undoable single delete — no dialog, matching the analyzer's one-click fixes.
                    ControllerCleanup.DeleteSubAssets(controller, new[] { asset });
                    _leftovers = ControllerCleanup.FindLeftoverSubAssets(controller);
                    EditorGUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();   // the leftover list was rebuilt under this layout pass
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button(L.Tr("Delete All")))
            {
                DeleteAllLeftovers(controller, live);
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// Sub-assets that are in use but visible in the Project window (behaviours whose hide
        /// flags an older paste cleared). Not garbage — deleting them would break the states
        /// that use them — so the offer here is to hide them, not to remove them.
        /// </summary>
        void DrawExposedSubAssets(AnimatorController controller)
        {
            if (_exposed == null) return;
            int live = 0;
            foreach (var asset in _exposed)
                if (asset != null) live++;
            if (live == 0) return;

            EditorGUILayout.HelpBox(
                L.Tr("{0} sub-asset(s) are showing up under this .controller in the Project window. They are in use — hiding them just restores how Unity normally stores them.", live),
                MessageType.Info);
            if (GUILayout.Button(new GUIContent(L.Tr("Hide In Project ({0})", live),
                    L.Tr("Restore the hidden flag on these sub-assets. Nothing is deleted and no reference changes."))))
            {
                ControllerCleanup.HideSubAssets(controller, _exposed);
                _exposed = ControllerCleanup.FindExposedSubAssets(controller);
                GUIUtility.ExitGUI();
            }
        }

        void DeleteAllLeftovers(AnimatorController controller, int count)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("Cleanup"),
                    L.Tr("Delete {0} leftover sub-asset(s) from '{1}'?\n\nNothing in this file references them. This can be undone.",
                        count, controller.name),
                    L.Tr("Delete"), L.Tr("Cancel")))
                return;
            ControllerCleanup.DeleteSubAssets(controller, _leftovers);
            _leftovers = ControllerCleanup.FindLeftoverSubAssets(controller);
        }
    }
}
