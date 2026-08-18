using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Edit
{
    /// <summary>
    /// Re-types an animator parameter and rewrites every referencing condition so the
    /// controller stays valid. The mode/threshold mapping is pure and unit-tested.
    /// </summary>
    static class ParameterConverter
    {
        public struct ConditionResult
        {
            public AnimatorConditionMode mode;
            public float threshold;
            public bool lossy;
            public string note;
        }

        public class ConditionChange
        {
            public AnimatorTransitionBase transition;
            public int index;
            public string parameterName;
            public AnimatorConditionMode oldMode;
            public float oldThreshold;
            public AnimatorConditionMode newMode;
            public float newThreshold;
            public bool lossy;
            public string note;
            public string label;
            public bool enabled = true;
        }

        public class Plan
        {
            public AnimatorController controller;
            public string parameterName;
            public AnimatorControllerParameterType fromType;
            public AnimatorControllerParameterType toType;
            public readonly List<ConditionChange> conditionChanges = new List<ConditionChange>();
            public readonly List<string> warnings = new List<string>();
            public bool HasChanges => conditionChanges.Count > 0;
        }

        static bool IsBoolean(AnimatorControllerParameterType t) =>
            t == AnimatorControllerParameterType.Bool || t == AnimatorControllerParameterType.Trigger;

        /// <summary>Pure mode/threshold mapping. Unit-tested — keep free of side effects.</summary>
        public static ConditionResult ConvertCondition(AnimatorConditionMode mode, float threshold,
            AnimatorControllerParameterType toType)
        {
            bool modeIsBoolean = mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;
            var r = new ConditionResult { mode = mode, threshold = threshold };

            if (IsBoolean(toType))
            {
                if (modeIsBoolean)
                {
                    r.threshold = 0f;
                }
                else
                {
                    bool truthy = mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Equals;
                    r.mode = truthy ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot;
                    r.threshold = 0f;
                    r.lossy = true;
                    r.note = "numeric comparison reduced to a boolean check";
                }
            }
            else if (toType == AnimatorControllerParameterType.Int)
            {
                if (modeIsBoolean)
                {
                    r.mode = AnimatorConditionMode.Equals;
                    r.threshold = mode == AnimatorConditionMode.If ? 1f : 0f;
                }
                // Greater / Less / Equals / NotEqual are all valid for Int — kept as-is.
            }
            else // Float
            {
                if (modeIsBoolean)
                {
                    r.mode = mode == AnimatorConditionMode.If ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less;
                    r.threshold = 0.5f;
                }
                else if (mode == AnimatorConditionMode.Equals)
                {
                    r.mode = AnimatorConditionMode.Greater;
                    r.lossy = true;
                    r.note = "Equals is not valid for Float; converted to Greater";
                }
                else if (mode == AnimatorConditionMode.NotEqual)
                {
                    r.mode = AnimatorConditionMode.Less;
                    r.lossy = true;
                    r.note = "NotEqual is not valid for Float; converted to Less";
                }
                // Greater / Less are valid for Float — kept as-is.
            }
            return r;
        }

        public static Plan ComputeConversion(AnimatorController controller, string parameterName,
            AnimatorControllerParameterType toType)
        {
            var plan = new Plan { controller = controller, parameterName = parameterName, toType = toType };

            AnimatorControllerParameter param = null;
            foreach (var p in controller.parameters)
                if (p.name == parameterName) { param = p; break; }
            if (param == null)
            {
                plan.warnings.Add("Parameter '" + parameterName + "' was not found.");
                return plan;
            }
            plan.fromType = param.type;
            if (param.type == toType) return plan;

            foreach (var t in controller.AllTransitions())
            {
                var conditions = t.conditions;
                for (int i = 0; i < conditions.Length; i++)
                {
                    if (conditions[i].parameter != parameterName) continue;
                    var res = ConvertCondition(conditions[i].mode, conditions[i].threshold, toType);
                    if (res.mode == conditions[i].mode && Mathf.Approximately(res.threshold, conditions[i].threshold))
                        continue;
                    plan.conditionChanges.Add(new ConditionChange
                    {
                        transition = t,
                        index = i,
                        parameterName = parameterName,
                        oldMode = conditions[i].mode,
                        oldThreshold = conditions[i].threshold,
                        newMode = res.mode,
                        newThreshold = res.threshold,
                        lossy = res.lossy,
                        note = res.note,
                        label = DescribeTransition(t),
                    });
                }
            }

            if (toType != AnimatorControllerParameterType.Float)
            {
                foreach (var bt in controller.AllBlendTrees())
                {
                    if (bt.blendParameter == parameterName)
                        plan.warnings.Add("Blend tree '" + bt.name + "' uses this parameter as its X blend axis; blend axes must stay Float.");
                    if (bt.blendParameterY == parameterName)
                        plan.warnings.Add("Blend tree '" + bt.name + "' uses this parameter as its Y blend axis; blend axes must stay Float.");
                    foreach (var child in bt.children)
                        if (child.directBlendParameter == parameterName)
                            plan.warnings.Add("Blend tree '" + bt.name + "' uses this parameter as a Direct blend weight; it must stay Float.");
                }
            }

            foreach (var state in controller.AllStates())
            {
                if (state.speedParameterActive && state.speedParameter == parameterName && toType != AnimatorControllerParameterType.Float)
                    plan.warnings.Add("State '" + state.name + "' uses this parameter as a Speed multiplier (expects Float).");
                if (state.timeParameterActive && state.timeParameter == parameterName && toType != AnimatorControllerParameterType.Float)
                    plan.warnings.Add("State '" + state.name + "' uses this parameter as Motion Time (expects Float).");
                if (state.cycleOffsetParameterActive && state.cycleOffsetParameter == parameterName && toType != AnimatorControllerParameterType.Float)
                    plan.warnings.Add("State '" + state.name + "' uses this parameter as Cycle Offset (expects Float).");
                if (state.mirrorParameterActive && state.mirrorParameter == parameterName && !IsBoolean(toType))
                    plan.warnings.Add("State '" + state.name + "' uses this parameter as Mirror (expects Bool).");
            }

            return plan;
        }

        public static void Apply(Plan plan)
        {
            if (plan == null || plan.controller == null) return;
            using (new UndoScope("Convert Parameter Type"))
            {
                Undo.RegisterCompleteObjectUndo(plan.controller, "Convert Parameter Type");

                var byTransition = new Dictionary<AnimatorTransitionBase, List<ConditionChange>>();
                foreach (var ch in plan.conditionChanges)
                {
                    if (!ch.enabled || ch.transition == null) continue;
                    if (!byTransition.TryGetValue(ch.transition, out var list))
                        byTransition[ch.transition] = list = new List<ConditionChange>();
                    list.Add(ch);
                }

                foreach (var pair in byTransition)
                {
                    var transition = pair.Key;
                    Undo.RegisterCompleteObjectUndo(transition, "Convert Parameter Type");
                    var current = transition.conditions;
                    var rebuilt = new List<TransitionClipboard.ConditionData>(current.Length);
                    for (int i = 0; i < current.Length; i++)
                        rebuilt.Add(new TransitionClipboard.ConditionData
                        {
                            mode = current[i].mode,
                            parameter = current[i].parameter,
                            threshold = current[i].threshold,
                        });
                    foreach (var ch in pair.Value)
                    {
                        if (ch.index < 0 || ch.index >= rebuilt.Count) continue;
                        rebuilt[ch.index].mode = ch.newMode;
                        rebuilt[ch.index].threshold = ch.newThreshold;
                    }
                    TransitionClipboard.SetConditions(transition, rebuilt);
                    EditorUtility.SetDirty(transition);
                }

                var parameters = plan.controller.parameters;
                foreach (var p in parameters)
                {
                    if (p.name != plan.parameterName) continue;
                    MigrateDefaultValue(p, plan.toType);
                    p.type = plan.toType;
                    break;
                }
                plan.controller.parameters = parameters;
                EditorUtility.SetDirty(plan.controller);
            }
        }

        static void MigrateDefaultValue(AnimatorControllerParameter p, AnimatorControllerParameterType toType)
        {
            bool currentBool;
            float currentFloat;
            switch (p.type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    currentBool = p.defaultBool;
                    currentFloat = p.defaultBool ? 1f : 0f;
                    break;
                case AnimatorControllerParameterType.Int:
                    currentBool = p.defaultInt != 0;
                    currentFloat = p.defaultInt;
                    break;
                default:
                    currentBool = p.defaultFloat != 0f;
                    currentFloat = p.defaultFloat;
                    break;
            }
            switch (toType)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    p.defaultBool = currentBool;
                    break;
                case AnimatorControllerParameterType.Int:
                    p.defaultInt = Mathf.RoundToInt(currentFloat);
                    break;
                case AnimatorControllerParameterType.Float:
                    p.defaultFloat = currentFloat;
                    break;
            }
        }

        /// <summary>
        /// What a transition is waiting for, in one line: its conditions, or its exit time when
        /// that is all it has. Drawn on an edge in the graph and on the row that names the
        /// transition in the inspector — where an arrow carrying several is otherwise a column
        /// of rows reading exactly the same thing.
        /// </summary>
        public static string SummarizeConditions(AnimatorTransitionBase transition)
        {
            if (transition == null) return string.Empty;
            var conditions = transition.conditions;
            if (conditions.Length == 0)
            {
                if (transition is AnimatorStateTransition st && st.hasExitTime)
                    return "exit @ " + st.exitTime.ToString("0.##");
                return L.Tr("(no conditions)");
            }
            string text = DescribeCondition(conditions[0]);
            if (conditions.Length == 2) text += "  ·  " + DescribeCondition(conditions[1]);
            else if (conditions.Length > 2) text += "  +" + (conditions.Length - 1);
            return text;
        }

        /// <summary>
        /// One condition as the graph and the analyzer both spell it: "Wet > 0.5", "!Seated",
        /// "GestureLeft = Fist". Written once here because an edge label and an analyzer
        /// message naming the same condition have to be recognisably the same sentence.
        /// </summary>
        public static string DescribeCondition(AnimatorCondition condition)
        {
            switch (condition.mode)
            {
                case AnimatorConditionMode.If: return condition.parameter;
                case AnimatorConditionMode.IfNot: return "!" + condition.parameter;
                case AnimatorConditionMode.Greater: return condition.parameter + " > " + DescribeThreshold(condition);
                case AnimatorConditionMode.Less: return condition.parameter + " < " + DescribeThreshold(condition);
                case AnimatorConditionMode.Equals: return condition.parameter + " = " + DescribeThreshold(condition);
                case AnimatorConditionMode.NotEqual: return condition.parameter + " ≠ " + DescribeThreshold(condition);
                default: return condition.parameter;
            }
        }

        /// <summary>GestureLeft / GestureRight values read as gesture names ("Fist"), other
        /// thresholds as plain numbers.</summary>
        public static string DescribeThreshold(AnimatorCondition condition)
        {
            if (VrcParameters.IsGestureParameter(condition.parameter))
            {
                string name = VrcParameters.GestureLabel(condition.threshold);
                if (name != null) return name;
            }
            return condition.threshold.ToString("0.##");
        }

        public static string DescribeTransition(AnimatorTransitionBase t)
        {
            if (t == null) return "(transition)";
            if (t.isExit) return "→ Exit";
            if (t.destinationState != null) return "→ " + t.destinationState.name;
            if (t.destinationStateMachine != null) return "→ " + t.destinationStateMachine.name;
            return "(transition)";
        }
    }
}
