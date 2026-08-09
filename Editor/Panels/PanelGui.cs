using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Small IMGUI pieces shared by the side panels: separators, the selection tint and
    /// the parameter lookups every parameter popup is built from.</summary>
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
