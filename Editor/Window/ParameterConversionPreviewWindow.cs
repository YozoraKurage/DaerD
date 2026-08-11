using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Modal preview of a parameter type conversion before it is applied.</summary>
    class ParameterConversionPreviewWindow : EditorWindow
    {
        ParameterConverter.Plan _plan;
        Action _onApplied;
        Vector2 _scroll;

        public static void Open(ParameterConverter.Plan plan, Action onApplied)
        {
            var window = CreateInstance<ParameterConversionPreviewWindow>();
            window.titleContent = new GUIContent(L.Tr("Convert Parameter"));
            window._plan = plan;
            window._onApplied = onApplied;
            window.minSize = new Vector2(520, 340);
            window.ShowUtility();
        }

        void OnGUI()
        {
            if (_plan == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(
                $"Convert  '{_plan.parameterName}'   {_plan.fromType} → {_plan.toType}",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"{_plan.conditionChanges.Count} condition(s) will be rewritten.",
                EditorStyles.miniLabel);

            foreach (var warning in _plan.warnings)
                EditorGUILayout.HelpBox(warning, MessageType.Warning);

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var change in _plan.conditionChanges)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                change.enabled = EditorGUILayout.Toggle(change.enabled, GUILayout.Width(18));
                EditorGUILayout.LabelField(change.label, GUILayout.Width(120));
                EditorGUILayout.LabelField($"{change.oldMode} {change.oldThreshold:0.###}",
                    EditorStyles.miniLabel, GUILayout.Width(110));
                EditorGUILayout.LabelField("→", GUILayout.Width(16));
                using (new EditorGUI.DisabledScope(!change.enabled))
                {
                    change.newMode = (AnimatorConditionMode)
                        EditorGUILayout.EnumPopup(change.newMode, GUILayout.Width(80));
                    change.newThreshold = EditorGUILayout.FloatField(change.newThreshold, GUILayout.Width(56));
                }
                if (change.lossy)
                    GUILayout.Label(new GUIContent("  !", change.note), GUILayout.Width(24));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            if (GUILayout.Button(L.Tr("Apply"), GUILayout.Width(DaerDLayout.DialogButton)))
            {
                ParameterConverter.Apply(_plan);
                _onApplied?.Invoke();
                Close();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
