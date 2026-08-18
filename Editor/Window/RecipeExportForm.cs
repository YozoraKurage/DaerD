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

        /// <summary>The recipes this controller is linked to, in the order the popup lists them.
        /// Empty is the old world: no popup, and everything below behaves as it always did.
        /// </summary>
        readonly List<ControllerRecipe> _links = new List<ControllerRecipe>();

        /// <summary>Index into <see cref="_links"/>, or <c>_links.Count</c> for "new recipe" —
        /// the last entry of the popup, so the two are one control rather than a mode toggle
        /// beside a list.</summary>
        int _target;

        /// <summary>Where the selected link's code lives, read off its script when the selection
        /// changes rather than per repaint. Empty class name means the script could not be
        /// named, which is the one thing that makes an update impossible.</summary>
        string _linkClassName;
        string _linkNamespace;
        string _linkFolder;

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
            _links.Clear();
            _target = 0;
            if (controller == null) return;

            // Opening this form is the moment somebody is about to decide where a recipe goes, so
            // it is also the moment to find out that one already exists. Recipes exported before
            // the link existed are taken up here.
            RecipeLinks.Adopt(controller);

            // The defaults for a NEW recipe, filled in whether or not a link is selected: the
            // popup can be switched to "new" at any point and these are what it comes back to.
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

            foreach (var linked in GraphFrameData.LinkedRecipes(controller))
                if (linked is ControllerRecipe recipe)
                    _links.Add(recipe);
            // The ticks are already in, so the decision has the one piece of evidence it uses.
            int picked = RecipeLinks.PickDefault(_links, CheckedLayers());
            _target = picked < 0 ? _links.Count : picked;
            SyncLink();
        }

        /// <summary>The ticked layer names, which is what a default target is chosen by.</summary>
        List<string> CheckedLayers()
        {
            var names = new List<string>();
            for (int i = 0; i < _layerNames.Count; i++)
                if (_checked[i]) names.Add(_layerNames[i]);
            return names;
        }

        /// <summary>Updating an existing recipe rather than writing a new one.</summary>
        bool Updating => _target >= 0 && _target < _links.Count;

        /// <summary>Reads the selected link's code location. Cached rather than asked per
        /// repaint: it resolves a MonoScript and an asset path, and the answer only changes when
        /// the selection does.</summary>
        void SyncLink()
        {
            _linkClassName = null;
            _linkNamespace = null;
            _linkFolder = null;
            if (!Updating) return;
            var recipe = _links[_target];
            _linkNamespace = recipe.GetType().Namespace ?? string.Empty;
            if (RecipeLinks.ScriptLocation(recipe, out string folder, out string className))
            {
                _linkFolder = folder;
                _linkClassName = className;
            }
        }

        /// <summary>What the export will actually use — the link's own identity while one is
        /// selected, so an update lands on the pair that already exists instead of beside it.
        /// </summary>
        string EffectiveClassName => Updating ? _linkClassName : _className;
        string EffectiveNamespace => Updating ? _linkNamespace : _namespace;
        string EffectiveFolder => Updating ? _linkFolder : _folder;

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
        string ProjectFolder => RecipeExportQueue.NormalizeProjectFolder(EffectiveFolder);

        /// <summary>
        /// Which recipe this export lands in: one of the ones this controller is already linked
        /// to, or a new one.
        ///
        /// Hidden entirely when nothing is linked — a popup whose only entry is "new" is a
        /// control that says the obvious. Where there IS a link the default is to update it,
        /// because writing a second recipe beside an existing one is the accident the link exists
        /// to prevent.
        /// </summary>
        void DrawTargetPopup()
        {
            if (_links.Count == 0) return;

            var options = new GUIContent[_links.Count + 1];
            for (int i = 0; i < _links.Count; i++)
            {
                // The class name, because that is what the pair of files is called and what a
                // duplicate would collide on; the path, because two recipes can share a class
                // name and that is precisely the mess being untangled.
                string path = AssetDatabase.GetAssetPath(_links[i]);
                options[i] = new GUIContent(_links[i].GetType().Name + "  —  " + path, path);
            }
            options[_links.Count] = new GUIContent(L.Tr("New recipe…"));

            int picked = EditorGUILayout.Popup(new GUIContent(L.Tr("Export To"),
                    L.Tr("Update one of the recipes this controller is already linked to, or write a new one. An update rewrites that recipe's generated half in place and never touches your half.")),
                _target, options);
            if (picked != _target)
            {
                _target = picked;
                SyncLink();
            }
        }

        public void DrawForm()
        {
            EditorGUILayout.HelpBox(
                L.Tr("Generates a recipe: C# that rebuilds the checked layers through the DaerD authoring API. Clips and masks become fields on a recipe asset (assigned automatically), so the code carries no GUIDs and stays editable — by you or by an AI.\n\nTwo files, halves of one partial class: '<Name>.Generated.cs' is rewritten on every export, '<Name>.cs' is yours and is written only once. Reshape yours freely; a re-export lands beside it, and Compare on the recipe asset checks that both still declare the same controller."),
                MessageType.Info);

            DrawTargetPopup();

            bool updating = Updating;
            using (new EditorGUI.DisabledScope(updating))
            {
                string typedClass = EditorGUILayout.TextField(
                    L.Tr("Class Name"), EffectiveClassName);
                if (!updating) _className = typedClass;
                string typedNamespace = EditorGUILayout.TextField(
                    new GUIContent(L.Tr("Namespace (optional)")), EffectiveNamespace);
                if (!updating) _namespace = typedNamespace;
                EditorGUILayout.BeginHorizontal();
                string typedFolder = EditorGUILayout.TextField(
                    L.Tr("Output Folder"), EffectiveFolder);
                if (!updating) _folder = typedFolder;
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
            }

            if (updating && string.IsNullOrEmpty(_linkClassName))
            {
                // No script means no pair of files to rewrite, and inventing a folder for one
                // would put a second copy exactly where this whole mechanism exists to stop it.
                EditorGUILayout.HelpBox(
                    L.Tr("DaerD cannot find the script behind this recipe asset, so there is no pair of files to update. Choose 'New recipe…' to write a fresh one."),
                    MessageType.Error);
                DrawLayerPicker();
                return;
            }
            if (updating)
                EditorGUILayout.HelpBox(
                    L.Tr("Updating an existing recipe: its class name and folder are shown as they are and cannot be changed here — that pair of files IS the recipe. Only the generated half is rewritten; your half is left alone. To write one under a different name, choose 'New recipe…'."),
                    MessageType.None);

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

            DrawLayerPicker();
        }

        /// <summary>The tick list, which every path through the form shows — including the one
        /// that has already refused to export, because which layers are ticked is what the
        /// popup's next selection will be judged against.</summary>
        void DrawLayerPicker()
        {
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
                || string.IsNullOrEmpty(EffectiveClassName) || projectFolder == null))
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
            + RecipeScript.Identifier(EffectiveClassName ?? string.Empty, lowerFirst: false) + ".cs";

        /// <summary>
        /// The form's part of an export: save the namespace, ask the one question that needs a
        /// human, and hand the rest to <see cref="RecipeExport"/> — which is also what a
        /// scripted or batch-mode export calls, so the two cannot drift apart.
        /// </summary>
        void DoExport(bool exclusive, string projectFolder)
        {
            // Only a new recipe teaches the remembered namespace. An update carries the one its
            // class already has, and writing that back would make one recipe's namespace the
            // default for every controller afterwards.
            if (!Updating) EditorPrefs.SetString(NamespacePref, _namespace ?? string.Empty);

            var options = new RecipeExport.Options
            {
                className = EffectiveClassName,
                namespaceName = EffectiveNamespace,
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
