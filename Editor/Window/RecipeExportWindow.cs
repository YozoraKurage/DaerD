using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Exports a controller (all layers, or a checked subset) to a recipe: C# that recreates
    /// the layers through the authoring API, plus a recipe .asset with every clip / mask
    /// reference pre-assigned (created automatically after the script compiles). Asset
    /// references live on the .asset — the code carries none.
    ///
    /// The code comes in two halves of one partial class. "&lt;Name&gt;.Generated.cs" is the
    /// exporter's and is rewritten every time; "&lt;Name&gt;.cs" is the author's and is written
    /// only when it doesn't exist. Reshaping exported code — by hand or by an AI — is the
    /// point of the API, and before the split the next export threw that work away.
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
        bool _createAsmdef = true;
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
                L.Tr("Generates a recipe: C# that rebuilds the checked layers through the DaerD authoring API. Clips and masks become fields on a recipe asset (assigned automatically), so the code carries no GUIDs and stays editable — by you or by an AI.\n\nTwo files, halves of one partial class: '<Name>.Generated.cs' is rewritten on every export, '<Name>.cs' is yours and is written only once. Reshape yours freely; a re-export lands beside it, and Compare on the recipe asset checks that both still declare the same controller."),
                MessageType.Info);

            _className = EditorGUILayout.TextField(L.Tr("Class Name"), _className);
            _namespace = EditorGUILayout.TextField(
                new GUIContent(L.Tr("Namespace (optional)")), _namespace);
            EditorGUILayout.BeginHorizontal();
            _folder = EditorGUILayout.TextField(L.Tr("Output Folder"), _folder);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string picked = EditorUtility.SaveFolderPanel(L.Tr("Output Folder"), _folder, string.Empty);
                if (!string.IsNullOrEmpty(picked))
                {
                    string normalized = RecipeExportQueue.NormalizeProjectFolder(picked);
                    if (normalized != null)
                        _folder = normalized;
                    else
                        Debug.LogWarning("DaerD: '" + picked + "' is outside this project — keeping the previous folder.");
                }
            }
            EditorGUILayout.EndHorizontal();

            string projectFolder = RecipeExportQueue.NormalizeProjectFolder(_folder);
            if (projectFolder == null)
                EditorGUILayout.HelpBox(
                    L.Tr("The output folder must be inside this project ('Assets/…')."),
                    MessageType.Error);
            else if (File.Exists(TargetCsPath(projectFolder)))
                EditorGUILayout.HelpBox(
                    L.Tr("'{0}' already exists — only the generated half beside it is rewritten, and the existing recipe asset is updated in place (no duplicates).",
                        TargetCsPath(projectFolder)),
                    MessageType.None);
            // Recipes reference the (editor-only) DaerD assembly, so outside an Editor folder
            // the player build would try — and fail — to compile them.
            else if (!("/" + projectFolder + "/").Contains("/Editor/"))
                EditorGUILayout.HelpBox(
                    L.Tr("The folder is not under an 'Editor' folder — an 'Editor' subfolder will be added so player builds don't try to compile the recipe."),
                    MessageType.None);

            _createAsset = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Create Recipe Asset"),
                    L.Tr("After the script compiles, create the .asset with the controller and all clip fields pre-assigned.")),
                _createAsset);
            _createAsmdef = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Assembly Definition (faster compiles)"),
                    L.Tr("Put the recipe folder in its own small assembly, so an export recompiles only the recipes instead of your whole editor assembly. Skipped when the folder already belongs to an asmdef or contains scripts DaerD didn't generate.")),
                _createAsmdef);

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
            using (new EditorGUI.DisabledScope(checkedCount == 0
                || string.IsNullOrEmpty(_className) || projectFolder == null))
                if (GUILayout.Button(L.Tr("Export"), GUILayout.Width(100)))
                {
                    DoExport(exclusive, projectFolder);
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();
        }

        string TargetCsPath(string projectFolder)
        {
            string folder = projectFolder;
            if (!("/" + folder + "/").Contains("/Editor/"))
                folder += "/Editor";
            return folder + "/" + RecipeScript.Identifier(_className ?? string.Empty, lowerFirst: false) + ".cs";
        }

        /// <summary>The exporter's half sits beside the hand half, one name apart.</summary>
        static string GeneratedPath(string folder, string className) =>
            folder + "/" + className + ".Generated.cs";

        /// <summary>Whether an existing "&lt;Name&gt;.cs" is a hand half (a partial that only
        /// carries Build) rather than a whole recipe from before the split. The marker line is
        /// the intended signal; "partial class" covers a hand half whose header was edited
        /// away, which is likely — that file is meant to be rewritten.</summary>
        static bool IsHandHalf(string path)
        {
            string text = File.ReadAllText(path);
            return text.StartsWith(RecipeExporter.HandHalfMarker)
                || text.Contains("partial class");
        }

        void DoExport(bool exclusive, string projectFolder)
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

            string folder = projectFolder;
            if (!("/" + folder + "/").Contains("/Editor/"))
                folder += "/Editor";
            // Through the AssetDatabase, not Directory.CreateDirectory: the pipeline must
            // know the folder or GenerateUniqueAssetPath/CreateAsset mangle their paths.
            if (!RecipeExportQueue.EnsureAssetFolder(folder))
            {
                EditorUtility.DisplayDialog(L.Tr("Export C# Recipe"),
                    L.Tr("Could not create the output folder '{0}'.", folder), "OK");
                return;
            }

            string csPath = folder + "/" + className + ".cs";
            string generatedPath = GeneratedPath(folder, className);

            // A recipe exported before the split carries the fields and the Build the
            // generated half now owns — leaving it as it is would be a duplicate definition,
            // so it becomes the hand half, with its old contents kept beside it.
            bool migrated = false;
            if (File.Exists(csPath) && !IsHandHalf(csPath))
            {
                if (!EditorUtility.DisplayDialog(L.Tr("Export C# Recipe"),
                        L.Tr("'{0}' is a single-file recipe from an earlier DaerD. Exports now write two halves of one partial class: '{1}', regenerated every time, and a hand half DaerD never overwrites.\n\nMigrating copies the current file to '{0}.bak' and replaces it with the hand half — carry anything you edited over from the backup.",
                            csPath, generatedPath),
                        L.Tr("Migrate"), L.Tr("Cancel")))
                    return;
                // Copied, not moved: the file is rewritten in place below so it keeps its
                // .meta — and with it the GUID every existing recipe .asset points its script
                // reference at.
                File.Copy(csPath, csPath + ".bak", true);
                migrated = true;
                Debug.Log("DaerD: '" + csPath + "' was a single-file recipe — its contents are"
                    + " backed up at '" + csPath + ".bak', and the file itself becomes your half.");
            }

            // Byte-identical re-export: skip the write entirely — no reimport, no compile,
            // no domain reload. The asset record below still refreshes the .asset fields.
            bool identical = File.Exists(generatedPath)
                && File.ReadAllText(generatedPath) == result.code;
            if (!identical)
                File.WriteAllText(generatedPath, result.code);

            // The half that is yours: written once, then left alone forever — that is the
            // whole point of the split.
            bool wroteHandHalf = migrated || !File.Exists(csPath);
            if (wroteHandHalf)
                File.WriteAllText(csPath, result.handHalf);

            if (_createAsmdef)
                EnsureRecipesAsmdef(folder);

            if (_createAsset)
            {
                string typeName = string.IsNullOrEmpty(_namespace)
                    ? className : _namespace.Trim() + "." + className;
                RecipeExportQueue.Enqueue(typeName, folder + "/" + className + ".asset",
                    _controller, exclusive, result.fields);
            }

            if (!identical)
                AssetDatabase.ImportAsset(generatedPath);
            if (wroteHandHalf)
                AssetDatabase.ImportAsset(csPath);
            Debug.Log("DaerD: recipe exported to '" + generatedPath + "'"
                + (identical ? " (code unchanged — no recompile)" : string.Empty)
                + (wroteHandHalf
                    ? " — your half is '" + csPath + "', and no export will overwrite it."
                    : " — '" + csPath + "' is yours and was left untouched; diff the generated"
                        + " half, carry the change over, then press Compare.")
                + (_createAsset ? " The recipe asset follows." : string.Empty));
            Close();
        }

        /// <summary>
        /// Gives the recipe folder its own tiny editor assembly, so exporting recompiles a
        /// handful of recipe files instead of the project's whole editor assembly. Only when
        /// it is safe: no asmdef already governs the folder, and every script in it is one
        /// DaerD generated (an asmdef changes which assembly neighbours compile into).
        /// </summary>
        static void EnsureRecipesAsmdef(string folder)
        {
            // Walk up to Assets: an existing asmdef anywhere above already governs us.
            for (string current = folder; !string.IsNullOrEmpty(current);
                current = current == "Assets" ? null : Path.GetDirectoryName(current)?.Replace('\\', '/'))
                if (Directory.Exists(current) && Directory.GetFiles(current, "*.asmdef").Length > 0)
                    return;

            foreach (var script in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                using (var reader = new StreamReader(script))
                {
                    // Both halves of an exported recipe count as DaerD's own.
                    string first = reader.ReadLine() ?? string.Empty;
                    if (first.Contains("<auto-generated> Exported from")
                        || first.StartsWith(RecipeExporter.HandHalfMarker))
                        continue;
                }
                Debug.Log("DaerD: '" + folder + "' contains scripts DaerD didn't generate — "
                    + "not adding an assembly definition (it would move them to another assembly).");
                return;
            }

            // Unique, deterministic assembly name per folder — asmdef names are global.
            uint hash = 2166136261;
            foreach (char c in folder)
                hash = (hash ^ c) * 16777619;
            string name = "DaerD.Recipes." + hash.ToString("x8");
            string path = folder + "/" + name + ".asmdef";
            File.WriteAllText(path,
                "{\n"
                + "    \"name\": \"" + name + "\",\n"
                + "    \"rootNamespace\": \"\",\n"
                + "    \"references\": [\"Yozolab.DaerD.Editor\"],\n"
                + "    \"includePlatforms\": [\"Editor\"],\n"
                + "    \"excludePlatforms\": [],\n"
                + "    \"allowUnsafeCode\": false,\n"
                + "    \"overrideReferences\": false,\n"
                + "    \"precompiledReferences\": [],\n"
                + "    \"autoReferenced\": false,\n"
                + "    \"defineConstraints\": [],\n"
                + "    \"versionDefines\": [],\n"
                + "    \"noEngineReferences\": false\n"
                + "}\n");
            AssetDatabase.ImportAsset(path);
            Debug.Log("DaerD: created '" + path + "' — future recipe exports recompile only this small assembly.");
        }
    }
}
