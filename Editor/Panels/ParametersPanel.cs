using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Parameter CRUD, with auto type conversion, cascade rename and find-usages.</summary>
    class ParametersPanel : PanelBase
    {
        readonly Action<string> _highlight;
        string _search = string.Empty;
        string _highlightedParameter;

        public ParametersPanel(DaerDContext context, Action<string> highlightCallback)
            : base(context, "Parameters")
        {
            _highlight = highlightCallback;
            context.ControllerChanged += Refresh;
            context.ParametersChanged += Refresh;
        }

        protected override void DrawContent()
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;

            EditorGUILayout.BeginHorizontal();
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
            if (GUILayout.Button("Add ▾", EditorStyles.toolbarDropDown, GUILayout.Width(54)))
                ShowAddMenu();
            EditorGUILayout.EndHorizontal();

            var unused = new HashSet<string>(ControllerAnalyzer.FindUnusedParameters(controller));
            if (unused.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(unused.Count + " unused parameter(s)", EditorStyles.miniLabel);
                if (GUILayout.Button("Remove Unused", EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    RemoveUnused(unused);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (!string.IsNullOrEmpty(_search) &&
                    p.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                EditorGUILayout.BeginHorizontal();

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

                bool highlighted = _highlightedParameter == p.name;
                var prevBg = GUI.backgroundColor;
                if (highlighted) GUI.backgroundColor = new Color(0.96f, 0.84f, 0.22f);
                if (GUILayout.Button("Find", EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    _highlightedParameter = highlighted ? null : p.name;
                    _highlight?.Invoke(_highlightedParameter);
                }
                GUI.backgroundColor = prevBg;

                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(20)))
                    { MoveParameter(i, i - 1); GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(i == parameters.Length - 1))
                    if (GUILayout.Button("▼", EditorStyles.miniButtonMid, GUILayout.Width(20)))
                    { MoveParameter(i, i + 1); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("✕", EditorStyles.miniButtonRight, GUILayout.Width(20)))
                { RemoveParameter(i); GUIUtility.ExitGUI(); }

                EditorGUILayout.EndHorizontal();
            }

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

        void ShowAddMenu()
        {
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
                menu.AddItem(new GUIContent("Add " + type), false, () => AddParameter(captured));
            }
            menu.ShowAsContext();
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

        void RemoveUnused(HashSet<string> unused)
        {
            var controller = Context.Controller;
            Undo.RegisterCompleteObjectUndo(controller, "Remove Unused Parameters");
            for (int i = controller.parameters.Length - 1; i >= 0; i--)
                if (unused.Contains(controller.parameters[i].name))
                    controller.RemoveParameter(i);
            EditorUtility.SetDirty(controller);
            Context.NotifyParametersChanged();
        }

        void MoveParameter(int from, int to)
        {
            var controller = Context.Controller;
            var parameters = controller.parameters;
            if (to < 0 || to >= parameters.Length) return;
            Undo.RegisterCompleteObjectUndo(controller, "Reorder Parameters");
            var moved = parameters[from];
            parameters[from] = parameters[to];
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
