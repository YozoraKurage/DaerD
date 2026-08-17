using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The recipe export form, shared between the standalone window
    /// (<see cref="RecipeExportWindow"/>) and the home screen's inline card: class name,
    /// namespace, output folder, the two toggles and the layer picker, plus the export itself.
    /// Same split as <see cref="AsyncSyncForm"/> — the host owns the window chrome (and its
    /// Cancel button), the form owns every input the export reads.
    ///
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
    class RecipeExportForm
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

        /// <summary>Raised after an export actually ran. The window closes on it; an embedded
        /// host starts the form over.</summary>
        public Action Exported;

        public AnimatorController Controller => _controller;

        /// <summary>(Re)binds the form and fills in the defaults the export starts from.
        /// <paramref name="onlyLayer"/> ticks that one layer alone — the layer settings popup's
        /// "Export Layer To C#" route.</summary>
        public void SetController(AnimatorController controller, string onlyLayer = null)
        {
            _controller = controller;
            _layerNames.Clear();
            _checked.Clear();
            if (controller == null) return;

            _className = RecipeScript.Identifier(controller.name, lowerFirst: false) + "Recipe";
            _namespace = EditorPrefs.GetString(NamespacePref, string.Empty);
            string controllerPath = AssetDatabase.GetAssetPath(controller);
            _folder = string.IsNullOrEmpty(controllerPath)
                ? "Assets" : Path.GetDirectoryName(controllerPath).Replace('\\', '/');
            foreach (var layer in controller.layers)
            {
                _layerNames.Add(layer.name);
                _checked.Add(onlyLayer == null || layer.name == onlyLayer);
            }
        }

        /// <summary>
        /// Re-reads the controller's layers, keeping the ticks of the ones that are still there
        /// (matched by name — that is what the export writes anyway) and ticking newcomers.
        /// The window snapshots the list at Open and is closed long before it changes; an
        /// embedded card stays open across layer edits and has to catch up.
        /// </summary>
        public void RefreshLayers()
        {
            if (_controller == null) return;
            var previous = new Dictionary<string, bool>();
            for (int i = 0; i < _layerNames.Count; i++)
                previous[_layerNames[i]] = _checked[i];

            _layerNames.Clear();
            _checked.Clear();
            foreach (var layer in _controller.layers)
            {
                _layerNames.Add(layer.name);
                _checked.Add(!previous.TryGetValue(layer.name, out bool was) || was);
            }
        }

        int CheckedCount
        {
            get
            {
                int count = 0;
                foreach (var isChecked in _checked)
                    if (isChecked) count++;
                return count;
            }
        }

        /// <summary>All layers ticked: the recipe owns the whole controller.</summary>
        bool Exclusive => CheckedCount == _layerNames.Count;

        /// <summary>The output folder as an "Assets/…" path, or null when it is outside the
        /// project — which is what makes the export impossible.</summary>
        string ProjectFolder => RecipeExportQueue.NormalizeProjectFolder(_folder);

        public void DrawForm()
        {
            EditorGUILayout.HelpBox(
                L.Tr("Generates a recipe: C# that rebuilds the checked layers through the DaerD authoring API. Clips and masks become fields on a recipe asset (assigned automatically), so the code carries no GUIDs and stays editable — by you or by an AI.\n\nTwo files, halves of one partial class: '<Name>.Generated.cs' is rewritten on every export, '<Name>.cs' is yours and is written only once. Reshape yours freely; a re-export lands beside it, and Compare on the recipe asset checks that both still declare the same controller."),
                MessageType.Info);

            _className = EditorGUILayout.TextField(L.Tr("Class Name"), _className);
            _namespace = EditorGUILayout.TextField(
                new GUIContent(L.Tr("Namespace (optional)")), _namespace);
            EditorGUILayout.BeginHorizontal();
            _folder = EditorGUILayout.TextField(L.Tr("Output Folder"), _folder);
            if (GUILayout.Button("…", GUILayout.Width(DaerDLayout.GlyphButton)))
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

            string projectFolder = ProjectFolder;
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
            EditorGUILayout.LabelField(
                L.Tr("Layers To Export") + " (" + CheckedCount + "/" + _layerNames.Count + ")",
                EditorStyles.boldLabel);
            // Bounded on purpose: the tick list is the one part that grows with the controller,
            // and it scrolls inside the form rather than pushing the buttons off the host.
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90));
            for (int i = 0; i < _layerNames.Count; i++)
                _checked[i] = EditorGUILayout.ToggleLeft(_layerNames[i], _checked[i]);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField(Exclusive
                    ? L.Tr("All layers: the recipe will own the whole controller (exclusive).")
                    : L.Tr("Subset: the recipe will replace only these layers, by name."),
                EditorStyles.miniLabel);
        }

        /// <summary>The Export button alone, so a host can put its own buttons on the same row.
        /// </summary>
        public void DrawExportButton()
        {
            string projectFolder = ProjectFolder;
            using (new EditorGUI.DisabledScope(CheckedCount == 0
                || string.IsNullOrEmpty(_className) || projectFolder == null))
                if (GUILayout.Button(L.Tr("Export"), GUILayout.Width(DaerDLayout.DialogButton)))
                {
                    DoExport(Exclusive, projectFolder);
                    GUIUtility.ExitGUI();
                }
        }

        /// <summary>Where the author's half will land — the same path the export computes,
        /// asked ahead of time so the form can say the file is already there.</summary>
        string TargetCsPath(string projectFolder) =>
            RecipeExport.ResolveProjectFolder(projectFolder) + "/"
            + RecipeScript.Identifier(_className ?? string.Empty, lowerFirst: false) + ".cs";

        /// <summary>
        /// The form's part of an export: save the namespace, ask the one question that needs a
        /// human, and hand the rest to <see cref="RecipeExport"/> — which is also what a
        /// scripted or batch-mode export calls, so the two cannot drift apart.
        /// </summary>
        void DoExport(bool exclusive, string projectFolder)
        {
            EditorPrefs.SetString(NamespacePref, _namespace ?? string.Empty);

            var options = new RecipeExport.Options
            {
                className = _className,
                namespaceName = _namespace,
                createAsset = _createAsset,
                createAsmdef = _createAsmdef,
            };
            if (!exclusive)
            {
                var subset = new List<string>();
                for (int i = 0; i < _layerNames.Count; i++)
                    if (_checked[i]) subset.Add(_layerNames[i]);
                options.layerNames = subset;
            }

            // A recipe exported before the split carries the fields and the Build the
            // generated half now owns — leaving it as it is would be a duplicate definition,
            // so it becomes the hand half, with its old contents kept beside it. Replacing a
            // file the author owns is the one decision the export won't make on its own.
            string csPath = TargetCsPath(projectFolder);
            if (RecipeExport.IsSingleFileRecipe(csPath))
            {
                string generatedPath = csPath.Substring(0, csPath.Length - 3) + ".Generated.cs";
                if (!EditorUtility.DisplayDialog(L.Tr("Export C# Recipe"),
                        L.Tr("'{0}' is a single-file recipe from an earlier DaerD. Exports now write two halves of one partial class: '{1}', regenerated every time, and a hand half DaerD never overwrites.\n\nMigrating copies the current file to '{0}.bak' and replaces it with the hand half — carry anything you edited over from the backup.",
                            csPath, generatedPath),
                        L.Tr("Migrate"), L.Tr("Cancel")))
                    return;
                options.migrateSingleFile = true;
            }

            try
            {
                RecipeExport.ToProject(_controller, projectFolder, options);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(L.Tr("Export C# Recipe"), e.Message, "OK");
                return;
            }
            Exported?.Invoke();
        }
    }
}
