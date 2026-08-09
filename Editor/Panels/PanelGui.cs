using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Small IMGUI pieces shared by the side panels: separators, the selection tint,
    /// the parameter-store slot and the parameter lookups every parameter popup is built
    /// from.</summary>
    static class PanelGui
    {
        /// <summary>Background tint of a selected row, wherever a panel draws one.</summary>
        public static readonly Color SelectionTint = new Color(0.40f, 0.60f, 0.90f);

        static readonly AnimatorConditionMode[] IntModes =
        {
            AnimatorConditionMode.Greater, AnimatorConditionMode.Less,
            AnimatorConditionMode.Equals, AnimatorConditionMode.NotEqual,
        };
        static readonly AnimatorConditionMode[] FloatModes = { AnimatorConditionMode.Greater, AnimatorConditionMode.Less };

        public static void HorizontalLine()
        {
            EditorGUILayout.Space(5);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.35f));
            EditorGUILayout.Space(5);
        }

        /// <summary>
        /// Narrows the prefix labels inside the scope, for a column too tight for the default
        /// width to leave a usable field beside it.
        /// Usage: <c>using (new PanelGui.LabelWidthScope(110f)) { ... }</c>
        ///
        /// A scope rather than a save / restore pair because the fields it wraps can abandon
        /// the layout pass with <see cref="GUIUtility.ExitGUI"/> (the store slot's Detect does),
        /// which would jump straight over a trailing restore and leave every other panel drawing
        /// with this width.
        /// </summary>
        public readonly struct LabelWidthScope : IDisposable
        {
            readonly float _previous;

            public LabelWidthScope(float width)
            {
                _previous = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = width;
            }

            public void Dispose() => EditorGUIUtility.labelWidth = _previous;
        }

        /// <summary>
        /// The explicit parameter-store slot (a VRC Expression Parameters asset, or a
        /// GameObject / component carrying MA Parameters) plus the opt-in Detect button.
        /// Drawn from both the parameters panel, where the budget hangs off it, and the home
        /// screen, where it sits with the controller's other associations — one row, so the
        /// two can't drift apart. <paramref name="onChanged"/> runs after the association was
        /// actually rewritten, for the caller's cached store.
        /// </summary>
        public static void ParameterStoreField(AnimatorController controller, Action onChanged)
        {
            EditorGUILayout.BeginHorizontal();
            var current = GraphFrameData.GetParameterStore(controller);
            var picked = EditorGUILayout.ObjectField(
                new GUIContent(L.Tr("Params"),
                    L.Tr("The parameter store this controller belongs to: a VRC Expression Parameters asset, or a GameObject carrying an MA Parameters component. Assigned explicitly — DaerD never guesses it from the scene.")),
                current, typeof(UnityEngine.Object), true);
            if (picked != current)
            {
                var wrapped = ParameterStore.TryWrap(picked);
                if (picked != null && wrapped == null)
                    EditorUtility.DisplayDialog(L.Tr("Parameter Store"),
                        L.Tr("Assign a VRC Expression Parameters asset or an object with an MA Parameters component."), "OK");
                else
                {
                    // Store the wrapped component (not the whole GameObject) so the slot
                    // shows exactly what will be edited.
                    GraphFrameData.SetParameterStore(controller, wrapped != null ? wrapped.Target : null);
                    onChanged?.Invoke();
                }
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Detect"),
                    L.Tr("Search the scene for an exact match: an avatar running this controller, or an MA Merge Animator referencing it. Nothing is picked up automatically without this button.")),
                    EditorStyles.miniButton, GUILayout.Width(52)))
            {
                var detected = ParameterStore.DetectFor(controller);
                if (detected == null)
                    EditorUtility.DisplayDialog(L.Tr("Parameter Store"),
                        L.Tr("No exact match in the scene — no avatar or MA Merge Animator references this controller."), "OK");
                else
                {
                    GraphFrameData.SetParameterStore(controller, detected);
                    onChanged?.Invoke();
                }
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        public static AnimatorConditionMode[] ModesFor(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return new[] { AnimatorConditionMode.If, AnimatorConditionMode.IfNot };
                case AnimatorControllerParameterType.Int:
                    return IntModes;
                default:
                    return FloatModes;
            }
        }

        public static string[] ModeLabels(AnimatorConditionMode[] modes)
        {
            var labels = new string[modes.Length];
            for (int i = 0; i < modes.Length; i++)
                labels[i] = L.Tr(modes[i].ToString());
            return labels;
        }

        public static string[] AllParameterNames(AnimatorController controller)
        {
            var parameters = controller.parameters;
            var names = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                names[i] = parameters[i].name;
            return names;
        }

        public static string[] ParameterNamesOfType(AnimatorController controller, AnimatorControllerParameterType type)
        {
            var names = new List<string>();
            foreach (var p in controller.parameters)
                if (p.type == type)
                    names.Add(p.name);
            if (names.Count == 0) names.Add(string.Empty);
            return names.ToArray();
        }

        public static Dictionary<string, AnimatorControllerParameterType> ParameterTypeMap(AnimatorController controller)
        {
            var map = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in controller.parameters)
                map[p.name] = p.type;
            return map;
        }
    }
}
