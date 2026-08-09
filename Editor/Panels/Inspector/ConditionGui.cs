using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- condition / value helpers ---------------------------------------

    /// <summary>Condition rows and the comparisons behind them, shared by the single- and
    /// multi-transition editors.</summary>
    static class ConditionGui
    {
        static readonly string[] BoolValueLabels = { "true", "false" };
        static readonly string[] GestureValueLabels = BuildGestureValueLabels();

        static string[] BuildGestureValueLabels()
        {
            var names = VrcParameters.GestureNames;
            var labels = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
                labels[i] = i + ": " + names[i];
            return labels;
        }

        /// <summary>Draws the value control for one condition. Bool shows true/false; Trigger shows nothing.</summary>
        public static void DrawConditionValue(TransitionClipboard.ConditionData condition, AnimatorControllerParameterType type,
            bool delayed = false)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                {
                    int index = condition.mode == AnimatorConditionMode.IfNot ? 1 : 0;
                    index = EditorGUILayout.Popup(index, BoolValueLabels, GUILayout.Width(80));
                    condition.mode = index == 1 ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If;
                    GUILayout.Space(56);
                    break;
                }
                case AnimatorControllerParameterType.Trigger:
                {
                    condition.mode = AnimatorConditionMode.If;
                    EditorGUILayout.LabelField(L.Tr("(set)"), EditorStyles.miniLabel, GUILayout.Width(80));
                    GUILayout.Space(56);
                    break;
                }
                default:
                {
                    var modes = PanelGui.ModesFor(type);
                    // GestureLeft / GestureRight thresholds read as an enum ("1: Fist"…), so
                    // give the value popup the wider slot and shrink the mode popup — the
                    // row's total width stays aligned with the plain numeric layout.
                    bool gesture = type == AnimatorControllerParameterType.Int
                        && VrcParameters.IsGestureParameter(condition.parameter)
                        && VrcParameters.GestureLabel(condition.threshold) != null;
                    int modeIndex = Mathf.Max(0, Array.IndexOf(modes, condition.mode));
                    modeIndex = EditorGUILayout.Popup(modeIndex, PanelGui.ModeLabels(modes), GUILayout.Width(gesture ? 56 : 80));
                    condition.mode = modes[modeIndex];
                    if (gesture)
                    {
                        int current = (int)Math.Round(condition.threshold);
                        condition.threshold = EditorGUILayout.Popup(current, GestureValueLabels, GUILayout.Width(80));
                    }
                    else
                    {
                        condition.threshold = delayed
                            ? EditorGUILayout.DelayedFloatField(condition.threshold, GUILayout.Width(56))
                            : EditorGUILayout.FloatField(condition.threshold, GUILayout.Width(56));
                    }
                    break;
                }
            }
        }

        public static List<TransitionClipboard.ConditionData> ToDataList(AnimatorTransitionBase transition)
        {
            var list = new List<TransitionClipboard.ConditionData>();
            foreach (var c in transition.conditions)
                list.Add(new TransitionClipboard.ConditionData { mode = c.mode, parameter = c.parameter, threshold = c.threshold });
            return list;
        }

        public struct SharedConditionEntry
        {
            public TransitionClipboard.ConditionData data;
            public int count;
            public int order;
        }

        /// <summary>
        /// Every distinct condition across the selected transitions, with how many of them contain
        /// it. Conditions present in every transition are listed first; ties keep first-seen order.
        /// </summary>
        public static List<SharedConditionEntry> SharedConditions(List<AnimatorTransitionBase> transitions)
        {
            var result = new List<SharedConditionEntry>();
            foreach (var t in transitions)
            {
                if (t == null) continue;
                foreach (var c in t.conditions)
                {
                    var data = new TransitionClipboard.ConditionData { mode = c.mode, parameter = c.parameter, threshold = c.threshold };
                    int idx = result.FindIndex(e => Same(e.data, data));
                    if (idx >= 0)
                    {
                        var e = result[idx];
                        e.count++;
                        result[idx] = e;
                    }
                    else
                    {
                        result.Add(new SharedConditionEntry { data = data, count = 1, order = result.Count });
                    }
                }
            }
            result.Sort((a, b) =>
            {
                int byCount = b.count.CompareTo(a.count);
                return byCount != 0 ? byCount : a.order.CompareTo(b.order);
            });
            return result;
        }

        public static bool Same(TransitionClipboard.ConditionData a, TransitionClipboard.ConditionData b)
        {
            return a.parameter == b.parameter && a.mode == b.mode && Mathf.Approximately(a.threshold, b.threshold);
        }
    }
}
