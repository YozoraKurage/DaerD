using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Parameter CRUD, with auto type conversion and cascade rename.</summary>
    class ParametersPanel : PanelBase
    {
        readonly ListReorder _reorder = new ListReorder();
        string _search = string.Empty;

        // VRC expression parameters asset (resolved from the scene avatar). Lazy: the scene
        // scan runs once per controller, not per repaint. The entry map is rebuilt every
        // DrawContent — the asset is small and edits elsewhere must show up immediately.
        UnityEngine.Object _exprAsset;
        bool _exprResolved;
        Dictionary<string, VrcExpressionParameters.Entry> _exprEntries;

        // Parameters written by AAP clips (one-key Animator-binding curves). Scanning every
        // clip is too costly per repaint, so the set is cached and dropped on structure edits.
        HashSet<string> _aapParams;
        AnimatorController _aapCacheController;

        /// <summary>Session clipboard for one parameter definition; survives controller
        /// switches so parameters can be copied across open tabs.</summary>
        static AnimatorControllerParameter s_parameterClipboard;

        static readonly GUIContent FindContent = new GUIContent("?",
            "Find where this parameter is used (click to list every usage)");

        public ParametersPanel(DaerDContext context)
            : base(context, "Parameters")
        {
            context.ControllerChanged += Refresh;
            context.ParametersChanged += Refresh;
            context.ControllerChanged += InvalidateExpressionAsset;
            context.GraphStructureChanged += () => _aapParams = null;
            context.ParametersChanged += () => _aapParams = null;
        }

        void InvalidateExpressionAsset()
        {
            _exprResolved = false;
            _exprAsset = null;
        }

        protected override void DrawContent()
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;

            var unused = new HashSet<string>(ControllerAnalyzer.FindUnusedParameters(controller));

            if (!_exprResolved)
            {
                _exprAsset = VrcExpressionParameters.FindAssetFor(controller);
                _exprResolved = true;
            }
            _exprEntries = null;
            if (_exprAsset != null)
            {
                _exprEntries = new Dictionary<string, VrcExpressionParameters.Entry>();
                foreach (var entry in VrcExpressionParameters.Read(_exprAsset))
                    _exprEntries[entry.name] = entry;
            }

            // Add is pinned to the LEFT so a narrow panel clips the search field, not the
            // button.
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add", EditorStyles.toolbarButton, GUILayout.Width(40)))
                ShowAddMenu();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            DrawVrcBudget();
            EditorGUILayout.Space(2);

            // Maps each drawn (search-filtered) row back to its index in the full array.
            var visibleReal = new List<int>();
            _reorder.Begin();
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (!string.IsNullOrEmpty(_search) &&
                    p.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var rowRect = EditorGUILayout.BeginHorizontal();
                _reorder.DrawHandle();
                visibleReal.Add(i);

                var prevColor = GUI.color;
                if (unused.Contains(p.name)) GUI.color = new Color(1f, 0.6f, 0.6f);
                EditorGUI.BeginChangeCheck();
                string newName = EditorGUILayout.DelayedTextField(p.name, GUILayout.MinWidth(90));
                if (EditorGUI.EndChangeCheck() && newName != p.name && !string.IsNullOrEmpty(newName))
                {
                    if (!ParameterRenamer.Rename(controller, p.name, newName))
                        EditorUtility.DisplayDialog("Rename Failed",
                            "A parameter named '" + newName + "' already exists.", "OK");
                    else
                    {
                        if (_exprAsset != null)
                            VrcExpressionParameters.Rename(_exprAsset, p.name, newName);
                        var rootMenu = VrcMenuAccess.FindMenuFor(controller);
                        if (rootMenu != null)
                            VrcMenuAccess.RenameParameterReferences(rootMenu, p.name, newName);
                        OfferSiblingRename(controller, p.name, newName);
                    }
                    Context.NotifyParametersChanged();
                    Context.NotifyGraphStructureChanged();
                    GUIUtility.ExitGUI();
                }
                GUI.color = prevColor;

                EditorGUI.BeginChangeCheck();
                var newType = (AnimatorControllerParameterType)EditorGUILayout.EnumPopup(p.type, GUILayout.Width(66));
                if (EditorGUI.EndChangeCheck() && newType != p.type)
                {
                    HandleTypeChange(p.name, newType);
                    GUIUtility.ExitGUI();
                }

                if (AapParams(controller).Contains(p.name))
                    GUILayout.Label(new GUIContent("AAP",
                        L.Tr("Driven by an animation clip (Animator-Animated Parameter)")),
                        EditorStyles.centeredGreyMiniLabel, GUILayout.Width(30));

                DrawDefaultValue(controller, parameters, i);
                DrawVrcFlags(p);

                // Find-uses: lists every transition condition / blend-tree blend slot / state
                // parameter override that mentions this parameter, plus row actions
                // (duplicate / copy / remap / delete-and-clean).
                if (GUILayout.Button(FindContent, EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    ShowUsagesMenu(p.name, i);
                    GUIUtility.ExitGUI();
                }

                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                { RemoveParameter(i); GUIUtility.ExitGUI(); }

                EditorGUILayout.EndHorizontal();
                _reorder.Row(rowRect);
            }
            _reorder.End((from, to) => MoveParameter(visibleReal[from], visibleReal[to]));

            if (parameters.Length == 0)
                EditorGUILayout.LabelField("No parameters.", EditorStyles.centeredGreyMiniLabel);
        }

        void DrawDefaultValue(AnimatorController controller, AnimatorControllerParameter[] parameters, int index)
        {
            var p = parameters[index];
            EditorGUI.BeginChangeCheck();
            float f = p.defaultFloat;
            int n = p.defaultInt;
            bool b = p.defaultBool;
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float:
                    f = EditorGUILayout.FloatField(p.defaultFloat, GUILayout.Width(56));
                    break;
                case AnimatorControllerParameterType.Int:
                    n = EditorGUILayout.IntField(p.defaultInt, GUILayout.Width(56));
                    break;
                default:
                    b = EditorGUILayout.Toggle(p.defaultBool, GUILayout.Width(56));
                    break;
            }
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(controller, "Edit Parameter Default");
                p.defaultFloat = f;
                p.defaultInt = n;
                p.defaultBool = b;
                controller.parameters = parameters;
                EditorUtility.SetDirty(controller);

                // Defaults stay linked with the avatar's expression parameters asset.
                if (_exprAsset != null && _exprEntries != null && _exprEntries.ContainsKey(p.name))
                {
                    float linked = p.type == AnimatorControllerParameterType.Float ? f
                        : p.type == AnimatorControllerParameterType.Int ? n
                        : b ? 1f : 0f;
                    VrcExpressionParameters.Edit(_exprAsset, p.name, e => e.defaultValue = linked);
                }
            }
        }

        /// <summary>Budget line for the resolved VRC expression parameters asset: used /
        /// available synced bits, plus the Sync and re-resolve actions.</summary>
        void DrawVrcBudget()
        {
            if (_exprAsset == null) return;
            int used = VrcExpressionParameters.UsedBits(_exprAsset);
            int capacity = VrcExpressionParameters.Capacity(_exprAsset);

            EditorGUILayout.BeginHorizontal();
            var prev = GUI.color;
            if (used > capacity) GUI.color = new Color(1f, 0.5f, 0.5f);
            EditorGUILayout.LabelField(
                new GUIContent(L.Tr("VRC Parameters: {0} / {1} bit", used, capacity),
                    L.Tr("Synced bits used by the avatar's expression parameters asset (Bool = 1, Int / Float = 8).")),
                EditorStyles.miniLabel);
            GUI.color = prev;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(L.Tr("Sync…"),
                    L.Tr("Align the VRC expression parameters asset to this controller's parameter list and order (with a diff preview).")),
                    EditorStyles.miniButton, GUILayout.Width(52)))
            {
                VrcParamSyncWindow.Open(Context.Controller, _exprAsset, Refresh);
                GUIUtility.ExitGUI();
            }
            if (GUILayout.Button(new GUIContent("↻", L.Tr("Re-resolve the expression parameters asset from the scene.")),
                    EditorStyles.miniButton, GUILayout.Width(22)))
                InvalidateExpressionAsset();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Per-row S (synced) / D (saved) toggles for parameters present in the
        /// expression asset, and a "+" to add the ones that aren't.</summary>
        void DrawVrcFlags(AnimatorControllerParameter parameter)
        {
            if (_exprAsset == null || _exprEntries == null) return;
            if (_exprEntries.TryGetValue(parameter.name, out var entry))
            {
                bool synced = GUILayout.Toggle(entry.synced,
                    new GUIContent("S", L.Tr("Network synced (costs bits)")),
                    EditorStyles.miniButton, GUILayout.Width(22));
                bool saved = GUILayout.Toggle(entry.saved,
                    new GUIContent("D", L.Tr("Saved between worlds")),
                    EditorStyles.miniButton, GUILayout.Width(22));
                if (synced != entry.synced || saved != entry.saved)
                    VrcExpressionParameters.Edit(_exprAsset, parameter.name, e =>
                    {
                        e.synced = synced;
                        e.saved = saved;
                    });
                return;
            }

            var mapped = VrcExpressionParameters.MapType(parameter.type);
            using (new EditorGUI.DisabledScope(mapped == null))
                if (GUILayout.Button(new GUIContent("+", L.Tr("Add to the VRC expression parameters asset")),
                        EditorStyles.miniButton, GUILayout.Width(46)))
                    VrcExpressionParameters.Add(_exprAsset, new VrcExpressionParameters.Entry
                    {
                        name = parameter.name,
                        valueType = mapped.Value,
                        defaultValue = parameter.type == AnimatorControllerParameterType.Float
                            ? parameter.defaultFloat
                            : parameter.type == AnimatorControllerParameterType.Int
                                ? parameter.defaultInt
                                : parameter.defaultBool ? 1f : 0f,
                    });
        }

        /// <summary>PhysBone / Contact parameter families share a prefix — offer to carry a
        /// prefix rename over to the sibling parameters.</summary>
        void OfferSiblingRename(AnimatorController controller, string oldName, string newName)
        {
            var siblings = PhysBoneSiblings.Siblings(controller, oldName);
            if (siblings.Count == 0) return;
            var renames = new List<(string from, string to)>();
            foreach (var sibling in siblings)
            {
                var renamed = PhysBoneSiblings.RenamedSibling(sibling, oldName, newName);
                if (renamed != null && DbtBuilder.FindParameter(controller, renamed) == null)
                    renames.Add((sibling, renamed));
            }
            if (renames.Count == 0) return;
            if (!EditorUtility.DisplayDialog(L.Tr("Rename Parameter Family"),
                    L.Tr("{0} sibling parameter(s) share this PhysBone/Contact prefix. Rename them to match?", renames.Count),
                    L.Tr("Rename All"), L.Tr("Only This One")))
                return;
            using (new UndoScope("Rename Parameter Family"))
                foreach (var (from, to) in renames)
                {
                    ParameterRenamer.Rename(controller, from, to);
                    if (_exprAsset != null)
                        VrcExpressionParameters.Rename(_exprAsset, from, to);
                }
        }

        /// <summary>A DBT gadget added parameters, possibly a layer and a blend tree — let
        /// every panel and the graph pick that up.</summary>
        void OnDbtGadgetApplied()
        {
            Context.NotifyParametersChanged();
            Context.NotifyLayersChanged();
            Context.NotifyGraphStructureChanged();
        }

        void ShowUsagesMenu(string parameterName, int index)
        {
            var controller = Context.Controller;
            var usages = ParameterUsageFinder.Find(controller, parameterName);
            var menu = new GenericMenu();
            if (usages.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("'" + parameterName + "' is not used anywhere"));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent(usages.Count + " usage(s) of '" + parameterName + "'"));
                menu.AddSeparator(string.Empty);
                foreach (var u in usages)
                {
                    var captured = u;
                    // GenericMenu uses '/' as a sub-menu separator — escape to keep the full path
                    // readable on one menu line.
                    var label = new GUIContent(captured.label.Replace('/', '∕'));
                    menu.AddItem(label, false, () =>
                        Context.NavigateTo(captured.layerIndex, captured.stateMachinePath, captured.selection));
                }
            }

            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateParameter(index));
            menu.AddItem(new GUIContent("Copy"), false, () => CopyParameter(index));
            if (s_parameterClipboard != null)
                menu.AddItem(new GUIContent("Paste After ('" + s_parameterClipboard.name + "')"),
                    false, () => PasteParameterAfter(index));
            else
                menu.AddDisabledItem(new GUIContent("Paste After"));

            // Redirect every reference to another parameter (both stay in the list).
            bool anyTarget = false;
            foreach (var other in controller.parameters)
            {
                if (other.name == parameterName) continue;
                anyTarget = true;
                var captured = other.name;
                menu.AddItem(new GUIContent("Remap References To/" + captured.Replace('/', '∕')),
                    false, () =>
                    {
                        ParameterRenamer.RedirectReferences(Context.Controller, parameterName, captured);
                        Context.NotifyParametersChanged();
                        Context.NotifyGraphStructureChanged();
                    });
            }
            if (!anyTarget)
                menu.AddDisabledItem(new GUIContent("Remap References To"));

            menu.AddItem(new GUIContent("Delete and Clean"), false, () =>
            {
                if (!EditorUtility.DisplayDialog(L.Tr("Delete and Clean"),
                        L.Tr("Delete '{0}' and remove every condition and driver entry that references it?", parameterName),
                        L.Tr("Delete"), L.Tr("Cancel")))
                    return;
                ParameterRenamer.DeleteAndClean(Context.Controller, parameterName);
                Context.NotifyParametersChanged();
                Context.NotifyGraphStructureChanged();
            });
            menu.ShowAsContext();
        }

        /// <summary>Parameters written by clips (AAP); cached per controller and dropped on
        /// structure edits.</summary>
        HashSet<string> AapParams(AnimatorController controller)
        {
            if (_aapParams != null && _aapCacheController == controller) return _aapParams;
            _aapParams = new HashSet<string>();
            _aapCacheController = controller;
            foreach (var entry in ControllerCleanup.CollectClipUsages(controller))
            {
                if (entry.clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(entry.clip))
                    if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path))
                        _aapParams.Add(binding.propertyName);
            }
            return _aapParams;
        }

        void DuplicateParameter(int index)
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;
            if (index < 0 || index >= parameters.Length) return;
            var source = parameters[index];
            Undo.RegisterCompleteObjectUndo(controller, "Duplicate Parameter");
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = MakeUniqueName(controller, source.name),
                type = source.type,
                defaultFloat = source.defaultFloat,
                defaultInt = source.defaultInt,
                defaultBool = source.defaultBool,
            });
            MoveParameter(controller.parameters.Length - 1, index + 1);
            Context.NotifyParametersChanged();
        }

        void CopyParameter(int index)
        {
            var parameters = Context.Controller.parameters;
            if (index < 0 || index >= parameters.Length) return;
            var source = parameters[index];
            s_parameterClipboard = new AnimatorControllerParameter
            {
                name = source.name,
                type = source.type,
                defaultFloat = source.defaultFloat,
                defaultInt = source.defaultInt,
                defaultBool = source.defaultBool,
            };
        }

        void PasteParameterAfter(int index)
        {
            var controller = Context.Controller;
            if (s_parameterClipboard == null) return;
            Undo.RegisterCompleteObjectUndo(controller, "Paste Parameter");
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = MakeUniqueName(controller, s_parameterClipboard.name),
                type = s_parameterClipboard.type,
                defaultFloat = s_parameterClipboard.defaultFloat,
                defaultInt = s_parameterClipboard.defaultInt,
                defaultBool = s_parameterClipboard.defaultBool,
            });
            MoveParameter(controller.parameters.Length - 1,
                Mathf.Min(index + 1, controller.parameters.Length - 1));
            Context.NotifyParametersChanged();
        }

        void ShowAddMenu()
        {
            var controller = Context.Controller;
            var existing = new HashSet<string>();
            foreach (var p in controller.parameters) existing.Add(p.name);

            var menu = new GenericMenu();
            foreach (AnimatorControllerParameterType type in new[]
            {
                AnimatorControllerParameterType.Float,
                AnimatorControllerParameterType.Int,
                AnimatorControllerParameterType.Bool,
                AnimatorControllerParameterType.Trigger,
            })
            {
                var captured = type;
                menu.AddItem(new GUIContent(type.ToString()), false, () => AddParameter(captured));
            }

            // Computed parameters: a DBT gadget adds its output (and helper) parameters and
            // the Direct-blend-tree machinery that drives them.
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("DBT Gadget (AAP)..."), false, () =>
                AapGadgetWindow.Open(Context.Controller, OnDbtGadgetApplied));
            menu.AddItem(new GUIContent("Object Toggle..."), false, () =>
                ToggleBuilderWindow.Open(Context.Controller, _ => OnDbtGadgetApplied()));

            // VRChat built-in parameters. Already-present ones show as a checked, disabled entry so
            // the menu doubles as a quick "which standard parameters does this controller have?".
            menu.AddSeparator(string.Empty);
            int missing = 0;
            foreach (var def in VrcParameters.All)
                if (!existing.Contains(def.name)) missing++;

            if (missing > 0)
                menu.AddItem(new GUIContent("VRChat/Add All Missing (" + missing + ")"), false, AddAllVrcParameters);
            else
                menu.AddDisabledItem(new GUIContent("VRChat/Add All Missing"));
            var syncLabel = new GUIContent("VRChat/Sync Expression Parameters Asset...");
            if (_exprAsset != null)
            {
                var asset = _exprAsset;
                menu.AddItem(syncLabel, false, () => VrcParamSyncWindow.Open(controller, asset, Refresh));
            }
            else
                menu.AddDisabledItem(syncLabel);
            menu.AddSeparator("VRChat/");

            foreach (var def in VrcParameters.All)
            {
                var captured = def;
                var label = new GUIContent("VRChat/" + def.category + "/" + def.name + "  (" + def.type + ")");
                if (existing.Contains(def.name))
                    menu.AddItem(label, true, null);   // already added — shown checked, non-clickable
                else
                    menu.AddItem(label, false, () => AddVrcParameter(captured));
            }

            menu.ShowAsContext();
        }

        static AnimatorControllerParameterType ToUnityType(VrcParameters.ParamType type)
        {
            switch (type)
            {
                case VrcParameters.ParamType.Int: return AnimatorControllerParameterType.Int;
                case VrcParameters.ParamType.Bool: return AnimatorControllerParameterType.Bool;
                default: return AnimatorControllerParameterType.Float;
            }
        }

        void AddVrcParameter(VrcParameters.Definition def)
        {
            var controller = Context.Controller;
            foreach (var p in controller.parameters)
                if (p.name == def.name) return;   // never duplicate a built-in name
            Undo.RegisterCompleteObjectUndo(controller, "Add VRChat Parameter");
            controller.AddParameter(def.name, ToUnityType(def.type));
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void AddAllVrcParameters()
        {
            var controller = Context.Controller;
            var existing = new HashSet<string>();
            foreach (var p in controller.parameters) existing.Add(p.name);

            Undo.RegisterCompleteObjectUndo(controller, "Add VRChat Parameters");
            int added = 0;
            foreach (var def in VrcParameters.All)
                if (existing.Add(def.name))
                {
                    controller.AddParameter(def.name, ToUnityType(def.type));
                    added++;
                }
            if (added > 0)
            {
                EditorUtility.SetDirty(controller);
                Context.NotifyParametersChanged();
            }
        }

        void AddParameter(AnimatorControllerParameterType type)
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Add Parameter");
            controller.AddParameter(MakeUniqueName(controller, "New " + type), type);
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void RemoveParameter(int index)
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Remove Parameter");
            controller.RemoveParameter(index);
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void MoveParameter(int from, int to)
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;
            if (from < 0 || from >= parameters.Length || to < 0 || to >= parameters.Length || from == to)
                return;
            Undo.RegisterCompleteObjectUndo(controller, "Reorder Parameters");
            var moved = parameters[from];
            if (from < to)
                Array.Copy(parameters, from + 1, parameters, from, to - from);
            else
                Array.Copy(parameters, to, parameters, to + 1, from - to);
            parameters[to] = moved;
            controller.parameters = parameters;
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void HandleTypeChange(string parameterName, AnimatorControllerParameterType newType)
        {
            var plan = ParameterConverter.ComputeConversion(Context.Controller, parameterName, newType);
            if (plan.conditionChanges.Count == 0 && plan.warnings.Count == 0)
            {
                ParameterConverter.Apply(plan);
                SyncExpressionType(parameterName, newType);
                Context.NotifyParametersChanged();
                Context.NotifyGraphStructureChanged();
            }
            else
            {
                ParameterConversionPreviewWindow.Open(plan, () =>
                {
                    SyncExpressionType(parameterName, newType);
                    Context.NotifyParametersChanged();
                    Context.NotifyGraphStructureChanged();
                });
            }
        }

        /// <summary>Keeps the expression asset's valueType in step with a controller-side
        /// type conversion (Trigger has no expression equivalent and leaves it untouched).</summary>
        void SyncExpressionType(string parameterName, AnimatorControllerParameterType newType)
        {
            var mapped = VrcExpressionParameters.MapType(newType);
            if (_exprAsset != null && mapped != null)
                VrcExpressionParameters.Edit(_exprAsset, parameterName, e => e.valueType = mapped.Value);
        }

        static string MakeUniqueName(AnimatorController controller, string baseName)
        {
            bool Taken(string n)
            {
                foreach (var p in controller.parameters)
                    if (p.name == n) return true;
                return false;
            }
            if (!Taken(baseName)) return baseName;
            int i = 1;
            while (Taken(baseName + " " + i)) i++;
            return baseName + " " + i;
        }
    }
}
