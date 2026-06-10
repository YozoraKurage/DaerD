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

            EditorGUILayout.BeginHorizontal();
            // MinWidth lets the search field shrink so the Add button stays pinned to the right.
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(24));
            if (GUILayout.Button("Add ▾", EditorStyles.toolbarDropDown, GUILayout.Width(54)))
                ShowAddMenu();
            EditorGUILayout.EndHorizontal();

            // Null when the VRChat SDK isn't installed or no loaded avatar uses this controller.
            var vrcInfo = VrcExpressions.GetInfo(controller);
            DrawVrcSummary(vrcInfo);

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
                DrawVrcStatus(controller, p, vrcInfo);

                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22)))
                { RemoveParameter(i); GUIUtility.ExitGUI(); }

                EditorGUILayout.EndHorizontal();
                _reorder.Row(rowRect);
            }
            _reorder.End((from, to) => MoveParameter(visibleReal[from], visibleReal[to]));

            if (parameters.Length == 0)
                EditorGUILayout.LabelField("No parameters.", EditorStyles.centeredGreyMiniLabel);
        }

        // ---- VRC Expression Parameters (no-ops without the VRChat SDK) --------

        /// <summary>One line with the avatar's used/total sync bits and a ping to the asset.</summary>
        void DrawVrcSummary(VrcExpressionsInfo info)
        {
            if (info == null) return;
            EditorGUILayout.BeginHorizontal();
            var prevColor = GUI.color;
            if (info.UsedBits > info.MaxBits) GUI.color = new Color(1f, 0.55f, 0.55f);
            EditorGUILayout.LabelField(
                "VRC sync: " + info.UsedBits + "/" + info.MaxBits + " bits — " + info.AvatarName,
                EditorStyles.miniLabel);
            GUI.color = prevColor;
            if (info.ParametersAsset != null &&
                GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40)))
                EditorGUIUtility.PingObject(info.ParametersAsset);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Per-row chip: S (synced, with bit cost), L (local only), ! (type mismatch), or a +
        /// button that adds the parameter to the avatar's Expression Parameters as synced.
        /// </summary>
        void DrawVrcStatus(AnimatorController controller, AnimatorControllerParameter p, VrcExpressionsInfo info)
        {
            if (info == null) return;
            if (!info.Status.TryGetValue(p.name, out var status))
                status = VrcParamStatus.NotInExpressions;

            var prevColor = GUI.color;
            switch (status)
            {
                case VrcParamStatus.Synced:
                    info.BitCost.TryGetValue(p.name, out int bits);
                    GUI.color = new Color(0.55f, 0.85f, 0.60f);
                    GUILayout.Label(new GUIContent("S",
                        "Synced expression parameter (" + bits + " bit" + (bits == 1 ? "" : "s") + ")"),
                        EditorStyles.miniLabel, GUILayout.Width(12));
                    break;
                case VrcParamStatus.Local:
                    GUI.color = new Color(0.65f, 0.75f, 0.95f);
                    GUILayout.Label(new GUIContent("L", "Expression parameter, not network-synced"),
                        EditorStyles.miniLabel, GUILayout.Width(12));
                    break;
                case VrcParamStatus.TypeMismatch:
                    GUI.color = new Color(1f, 0.55f, 0.55f);
                    GUILayout.Label(new GUIContent("!",
                        "An expression parameter with this name exists, but its value type doesn't match"),
                        EditorStyles.miniLabel, GUILayout.Width(12));
                    break;
                default:
                    if (GUILayout.Button(new GUIContent("+",
                            "Add to the avatar's VRC Expression Parameters (synced)"),
                        EditorStyles.miniButton, GUILayout.Width(18)))
                    {
                        if (VrcExpressions.AddToExpressions(controller, p))
                            Refresh();
                    }
                    break;
            }
            GUI.color = prevColor;
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
