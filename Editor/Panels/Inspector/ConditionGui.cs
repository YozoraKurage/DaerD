using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Edit;

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

        /// <summary>
        /// Draws the value control for one condition. Bool shows true/false; Trigger shows nothing.
        /// Returns true when the mouse wheel changed something: the wheel is not a GUI edit, so
        /// the <see cref="EditorGUI.EndChangeCheck"/> the callers wrap this in cannot see it.
        /// </summary>
        public static bool DrawConditionValue(TransitionClipboard.ConditionData condition, AnimatorControllerParameterType type,
            bool delayed = false)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                {
                    int index = condition.mode == AnimatorConditionMode.IfNot ? 1 : 0;
                    index = EditorGUILayout.Popup(index, BoolValueLabels, GUILayout.Width(80));
                    int flip = Wheel(GUILayoutUtility.GetLastRect());
                    // Two entries, so a notch either way is the flip the user meant.
                    if (flip != 0) index = Wrap(index + flip, BoolValueLabels.Length);
                    condition.mode = index == 1 ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If;
                    GUILayout.Space(56);
                    return flip != 0;
                }
                case AnimatorControllerParameterType.Trigger:
                {
                    condition.mode = AnimatorConditionMode.If;
                    EditorGUILayout.LabelField(L.Tr("(set)"), EditorStyles.miniLabel, GUILayout.Width(80));
                    GUILayout.Space(56);
                    return false;
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
                    int modeWheel = Wheel(GUILayoutUtility.GetLastRect());
                    if (modeWheel != 0) modeIndex = Wrap(modeIndex + modeWheel, modes.Length);
                    condition.mode = modes[modeIndex];

                    int valueWheel;
                    if (gesture)
                    {
                        int current = (int)Math.Round(condition.threshold);
                        current = EditorGUILayout.Popup(current, GestureValueLabels, GUILayout.Width(80));
                        valueWheel = Wheel(GUILayoutUtility.GetLastRect());
                        // Clamped to the gestures that exist: one notch past the end would make
                        // GestureLabel answer null, and the row would turn into a number field.
                        if (valueWheel != 0)
                            current = Mathf.Clamp(current + valueWheel, 0, GestureValueLabels.Length - 1);
                        condition.threshold = current;
                    }
                    else
                    {
                        condition.threshold = delayed
                            ? EditorGUILayout.DelayedFloatField(condition.threshold, GUILayout.Width(56))
                            : EditorGUILayout.FloatField(condition.threshold, GUILayout.Width(56));
                        valueWheel = Wheel(GUILayoutUtility.GetLastRect());
                        if (valueWheel != 0)
                            condition.threshold = Stepped(condition.threshold, valueWheel, type,
                                fine: Event.current.control || Event.current.command);
                    }
                    return modeWheel != 0 || valueWheel != 0;
                }
            }
        }

        /// <summary>
        /// Wheel notches over <paramref name="rect"/> — one per notch, positive upwards, zero for
        /// every other event. Consumed, so the panel does not scroll at the same moment: that is
        /// why the live area is the control itself and never the whole row, and why the wide
        /// parameter dropdown to its left is left alone to scroll the panel as usual.
        ///
        /// Shift is not available as a modifier here — the window claims Shift+wheel for
        /// switching layers, before any panel sees it.
        /// </summary>
        static int Wheel(Rect rect)
        {
            var e = Event.current;
            if (e.type != EventType.ScrollWheel || !rect.Contains(e.mousePosition)) return 0;
            e.Use();
            // A field being typed into shows its own in-progress text, not the value behind it,
            // so the digits would not move until focus left. Drop the edit and let it redraw.
            TransitionInspector.EndConditionInput();
            return e.delta.y > 0f ? -1 : 1;
        }

        public static int Wrap(int index, int count) => count <= 0 ? 0 : ((index % count) + count) % count;

        /// <summary>
        /// One notch on a threshold: whole numbers, or tenths with Ctrl held. The result is
        /// rounded so ten notches of a tenth land on 1 rather than on 0.99999994.
        ///
        /// An Int ignores the finer step — a threshold of 2.5 on an Int parameter is not a finer
        /// setting, it is one no value can sit on.
        /// </summary>
        public static float Stepped(float value, int notches, AnimatorControllerParameterType type, bool fine)
        {
            if (type == AnimatorControllerParameterType.Int) return Mathf.Round(value) + notches;
            return (float)Math.Round(value + notches * (fine ? 0.1f : 1f), 4);
        }

        public static List<TransitionClipboard.ConditionData> ToDataList(AnimatorTransitionBase transition)
        {
            var list = new List<TransitionClipboard.ConditionData>();
            foreach (var c in transition.conditions)
                list.Add(new TransitionClipboard.ConditionData { mode = c.mode, parameter = c.parameter, threshold = c.threshold });
            return list;
        }

        internal struct SharedConditionEntry
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
