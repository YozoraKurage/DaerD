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

        static readonly GUIContent FindContent = new GUIContent("?",
            "Find where this parameter is used (click to list every usage)");

        public ParametersPanel(DaerDContext context)
            : base(context, "Parameters")
        {
            context.ControllerChanged += Refresh;
            context.ParametersChanged += Refresh;
        }

        protected override void DrawContent()
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;

            var unused = new HashSet<string>(ControllerAnalyzer.FindUnusedParameters(controller));

            // Add is pinned to the LEFT so a narrow panel clips the search field, not the
            // button.
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add", EditorStyles.toolbarButton, GUILayout.Width(40)))
                ShowAddMenu();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

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

                DrawDefaultValue(controller, parameters, i);

                // Find-uses: lists every transition condition / blend-tree blend slot / state
                // parameter override that mentions this parameter, with click-to-navigate.
                if (GUILayout.Button(FindContent, EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    ShowUsagesMenu(p.name);
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

        void ShowUsagesMenu(string parameterName)
        {
            var usages = ParameterUsageFinder.Find(Context.Controller, parameterName);
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
            menu.ShowAsContext();
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
                Context.NotifyParametersChanged();
                Context.NotifyGraphStructureChanged();
            }
            else
            {
                ParameterConversionPreviewWindow.Open(plan, () =>
                {
                    Context.NotifyParametersChanged();
                    Context.NotifyGraphStructureChanged();
                });
            }
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
