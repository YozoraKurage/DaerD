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
                    _messages = recipe.Generate();
                    _lastRunClean = _messages.Count == 0;
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button(new GUIContent(L.Tr("Verify"),
                        L.Tr("Compare what the code declares against the controller's current contents."))))
                {
                    _messages = recipe.Verify();
                    _lastRunClean = _messages.Count == 0;
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button(new GUIContent(L.Tr("Open in DaerD"))))
                {
                    DaerDWindow.Open(recipe.targetController);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }

            if (recipe.OwnedLayers.Count > 0)
                EditorGUILayout.LabelField(
                    L.Tr("Owns layers: {0}", string.Join(", ", recipe.OwnedLayers)),
                    EditorStyles.miniLabel);

            if (_messages == null) return;
            if (_lastRunClean)
            {
                EditorGUILayout.HelpBox(L.Tr("Clean — code and controller match."), MessageType.Info);
                return;
            }
            EditorGUILayout.HelpBox(L.Tr("{0} finding(s):", _messages.Count), MessageType.Warning);
            foreach (var message in _messages)
                EditorGUILayout.LabelField("• " + message, EditorStyles.wordWrappedMiniLabel);
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
