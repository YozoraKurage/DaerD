using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Exports a controller (all layers, or a checked subset) to a recipe: a .cs file whose
    /// Build method recreates the layers through the authoring API, plus a recipe .asset with
    /// every clip / mask reference pre-assigned (created automatically after the script
    /// compiles). Asset references live on the .asset — the code carries none.
    /// </summary>
    class RecipeExportWindow : EditorWindow
    {
        AnimatorController _controller;
        readonly List<string> _layerNames = new List<string>();
        readonly List<bool> _checked = new List<bool>();
        string _className;
        string _namespace;
        string _folder;
        bool _createAsset = true;
        Vector2 _scroll;

        const string NamespacePref = "Yozolab.DaerD.RecipeNamespace";

        public static void Open(AnimatorController controller, string onlyLayer = null)
        {
            var window = CreateInstance<RecipeExportWindow>();
            window.titleContent = new GUIContent(L.Tr("Export C# Recipe"));
            window.minSize = new Vector2(420, 360);
            window._controller = controller;
            window._className = RecipeScript.Identifier(controller.name, lowerFirst: false) + "Recipe";
            window._namespace = EditorPrefs.GetString(NamespacePref, string.Empty);
            string controllerPath = AssetDatabase.GetAssetPath(controller);
            window._folder = string.IsNullOrEmpty(controllerPath)
                ? "Assets" : Path.GetDirectoryName(controllerPath).Replace('\\', '/');
            foreach (var layer in controller.layers)
            {
                window._layerNames.Add(layer.name);
                window._checked.Add(onlyLayer == null || layer.name == onlyLayer);
            }
            window.ShowUtility();
        }

        void OnGUI()
        {
            if (_controller == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Export C# Recipe"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Generates a recipe: C# that rebuilds the checked layers through the DaerD authoring API. Clips and masks become fields on a recipe asset (assigned automatically), so the code carries no GUIDs and stays editable — by you or by an AI."),
                MessageType.Info);

            _className = EditorGUILayout.TextField(L.Tr("Class Name"), _className);
            _namespace = EditorGUILayout.TextField(
                new GUIContent(L.Tr("Namespace (optional)")), _namespace);
            EditorGUILayout.BeginHorizontal();
            _folder = EditorGUILayout.TextField(L.Tr("Output Folder"), _folder);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string picked = EditorUtility.SaveFolderPanel(L.Tr("Output Folder"), _folder, string.Empty);
                if (!string.IsNullOrEmpty(picked) && picked.Contains("Assets"))
                    _folder = "Assets" + picked.Substring(picked.IndexOf("Assets") + "Assets".Length);
            }
            EditorGUILayout.EndHorizontal();

            // Recipes reference the (editor-only) DaerD assembly, so outside an Editor folder
            // the player build would try — and fail — to compile them.
            if (!("/" + _folder + "/").Contains("/Editor/"))
                EditorGUILayout.HelpBox(
                    L.Tr("The folder is not under an 'Editor' folder — an 'Editor' subfolder will be added so player builds don't try to compile the recipe."),
                    MessageType.None);

            _createAsset = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Create Recipe Asset"),
                    L.Tr("After the script compiles, create the .asset with the controller and all clip fields pre-assigned.")),
                _createAsset);

            EditorGUILayout.Space(4);
            int checkedCount = 0;
            foreach (var isChecked in _checked)
                if (isChecked) checkedCount++;
            bool exclusive = checkedCount == _layerNames.Count;
            EditorGUILayout.LabelField(
                L.Tr("Layers To Export") + " (" + checkedCount + "/" + _layerNames.Count + ")",
                EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90));
            for (int i = 0; i < _layerNames.Count; i++)
                _checked[i] = EditorGUILayout.ToggleLeft(_layerNames[i], _checked[i]);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField(exclusive
                    ? L.Tr("All layers: the recipe will own the whole controller (exclusive).")
                    : L.Tr("Subset: the recipe will replace only these layers, by name."),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(100)))
                Close();
            using (new EditorGUI.DisabledScope(checkedCount == 0 || string.IsNullOrEmpty(_className)))
                if (GUILayout.Button(L.Tr("Export"), GUILayout.Width(100)))
                {
                    DoExport(exclusive);
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();
        }

        void DoExport(bool exclusive)
        {
            EditorPrefs.SetString(NamespacePref, _namespace ?? string.Empty);
            string className = RecipeScript.Identifier(_className, lowerFirst: false);

            List<string> subset = null;
            if (!exclusive)
            {
                subset = new List<string>();
                for (int i = 0; i < _layerNames.Count; i++)
                    if (_checked[i]) subset.Add(_layerNames[i]);
            }

            var result = RecipeExporter.Export(_controller, subset, className,
                string.IsNullOrEmpty(_namespace) ? null : _namespace.Trim());
            foreach (var warning in result.warnings)
                Debug.LogWarning("DaerD: " + warning);

            string folder = _folder.TrimEnd('/');
            if (!("/" + folder + "/").Contains("/Editor/"))
                folder += "/Editor";
            Directory.CreateDirectory(folder);

            string csPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + className + ".cs");
            File.WriteAllText(csPath, result.code);

            if (_createAsset)
            {
                string typeName = string.IsNullOrEmpty(_namespace)
                    ? className : _namespace.Trim() + "." + className;
                RecipeExportQueue.Enqueue(typeName, folder + "/" + className + ".asset",
                    _controller, exclusive, result.fields);
            }

            AssetDatabase.ImportAsset(csPath);
            Debug.Log("DaerD: recipe exported to '" + csPath + "'"
                + (_createAsset ? " — the recipe asset follows once the script compiles." : "."));
            Close();
        }
    }
}
