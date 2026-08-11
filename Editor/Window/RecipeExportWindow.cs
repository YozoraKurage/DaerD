using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Standalone recipe export wizard: a shell around <see cref="RecipeExportForm"/>, which
    /// the home screen embeds as well. Everything about what an export writes lives on the
    /// form; the window adds the title, the Cancel button and closing on a finished export.
    /// </summary>
    class RecipeExportWindow : EditorWindow
    {
        AnimatorController _controller;

        readonly RecipeExportForm _form = new RecipeExportForm();

        public static void Open(AnimatorController controller, string onlyLayer = null)
        {
            var window = CreateInstance<RecipeExportWindow>();
            window.titleContent = new GUIContent(L.Tr("Export C# Recipe"));
            window.minSize = new Vector2(420, 360);
            window._controller = controller;
            window._form.SetController(controller, onlyLayer);
            window._form.Exported = window.Close;
            window.ShowUtility();
        }

        void OnGUI()
        {
            // Utility windows outlive domain reloads but their fields don't; bail out.
            if (_controller == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Export C# Recipe"), EditorStyles.boldLabel);
            _form.DrawForm();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            _form.DrawExportButton();
            EditorGUILayout.EndHorizontal();
        }
    }
}
