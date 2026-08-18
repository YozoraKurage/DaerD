using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Small IMGUI pieces shared by the side panels: separators, the narrow-column
    /// label scope and the parameter lookups every parameter popup is built from.</summary>
    static class PanelGui
    {
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
            EditorGUI.DrawRect(rect, DaerDColors.Separator);
            EditorGUILayout.Space(5);
        }

        /// <summary>
        /// Narrows the prefix labels inside the scope, for a column too tight for the default
        /// width to leave a usable field beside it.
        /// Usage: <c>using (new PanelGui.LabelWidthScope(110f)) { ... }</c>
        ///
        /// A scope rather than a save / restore pair because the fields it wraps can abandon
        /// the layout pass with <see cref="GUIUtility.ExitGUI"/> (the home screen's store slot
        /// Detect does), which would jump straight over a trailing restore and leave every other
        /// panel drawing with this width.
        /// </summary>
        internal readonly struct LabelWidthScope : IDisposable
        {
            readonly float _previous;

            public LabelWidthScope(float width)
            {
                _previous = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = width;
            }

            public void Dispose() => EditorGUIUtility.labelWidth = _previous;
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

        /// <summary>
        /// The comparison names as Unity writes them — Greater, Less, Equals, NotEqual — and
        /// deliberately not translated. They are the words on the same popup in the Animator
        /// window and in every guide written about it; a translated pair like "より大 / より小"
        /// reads as a different control than the one being described.
        /// </summary>
        public static string[] ModeLabels(AnimatorConditionMode[] modes)
        {
            var labels = new string[modes.Length];
            for (int i = 0; i < modes.Length; i++)
                labels[i] = modes[i].ToString();
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
