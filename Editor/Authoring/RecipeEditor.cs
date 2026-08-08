using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Inspector for every <see cref="ControllerRecipe"/> subclass: the serialized asset
    /// fields (drag & drop clips here), plus Generate / Verify / Open in DaerD. Results of
    /// the last action stay visible until the next one.
    /// </summary>
    [CustomEditor(typeof(ControllerRecipe), true)]
    class RecipeEditor : Editor
    {
        List<string> _messages;
        bool _lastRunClean;
        string _cleanMessage;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var recipe = (ControllerRecipe)target;

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(recipe.targetController == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent(L.Tr("Generate"),
                        L.Tr("Apply this recipe to the target controller (undoable)."))))
                {
                    Run(recipe.Generate(), L.Tr("Clean — code and controller match."));
                }
                if (GUILayout.Button(new GUIContent(L.Tr("Verify"),
                        L.Tr("Compare what the code declares against the controller's current contents."))))
                {
                    Run(recipe.Verify(), L.Tr("Clean — code and controller match."));
                }
                if (GUILayout.Button(new GUIContent(L.Tr("Open in DaerD"))))
                {
                    DaerDWindow.Open(recipe.targetController);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }

            // Only exported recipes have a second half to hold the first one against.
            if (recipe.HasGeneratedHalf
                && GUILayout.Button(new GUIContent(L.Tr("Compare With Exported Half"),
                    L.Tr("Check that your Build declares the same controller as the exporter's BuildGenerated — the safety net for reshaping exported code by hand or with an AI."))))
            {
                Run(recipe.Compare(),
                    L.Tr("Clean — your half and the exported half declare the same thing."));
            }

            if (recipe.OwnedLayers.Count > 0)
                EditorGUILayout.LabelField(
                    L.Tr("Owns layers: {0}", string.Join(", ", recipe.OwnedLayers)),
                    EditorStyles.miniLabel);

            if (_messages == null) return;
            if (_lastRunClean)
            {
                EditorGUILayout.HelpBox(_cleanMessage, MessageType.Info);
                return;
            }
            EditorGUILayout.HelpBox(L.Tr("{0} finding(s):", _messages.Count), MessageType.Warning);
            foreach (var message in _messages)
                EditorGUILayout.LabelField("• " + message, EditorStyles.wordWrappedMiniLabel);
        }

        /// <summary>Shows one action's result until the next action replaces it. Each action
        /// brings its own "nothing to report" line — they mean different things.</summary>
        void Run(List<string> messages, string cleanMessage)
        {
            _messages = messages;
            _lastRunClean = messages.Count == 0;
            _cleanMessage = cleanMessage;
            GUIUtility.ExitGUI();
        }
    }

    /// <summary>Creates a recipe .asset from a selected recipe script — the manual path for
    /// hand-written recipes (exported ones arrive with their .asset already made).</summary>
    static class RecipeAssetMenu
    {
        const string MenuPath = "Assets/Create/DaerD/Recipe Asset From Script";

        [MenuItem(MenuPath, true)]
        static bool Validate() => SelectedRecipeType() != null;

        [MenuItem(MenuPath)]
        static void Create()
        {
            var type = SelectedRecipeType();
            if (type == null) return;
            var instance = ScriptableObject.CreateInstance(type);
            ProjectWindowUtil.CreateAsset(instance, type.Name + ".asset");
        }

        static System.Type SelectedRecipeType()
        {
            var script = Selection.activeObject as MonoScript;
            var type = script != null ? script.GetClass() : null;
            return type != null && !type.IsAbstract
                && typeof(ControllerRecipe).IsAssignableFrom(type) ? type : null;
        }
    }
}
