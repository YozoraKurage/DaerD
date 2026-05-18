using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Audits a controller and provides controller-wide bulk fixes.</summary>
    static class ControllerAnalyzer
    {
        public enum Severity { Info, Warning, Error }

        public class Issue
        {
            public Severity severity;
            public string category;
            public string message;
            public Object context;
        }

        public static HashSet<string> CollectReferencedParameters(AnimatorController controller)
        {
            var set = new HashSet<string>();
            if (controller == null) return set;

            foreach (var t in controller.AllTransitions())
                foreach (var c in t.conditions)
                    if (!string.IsNullOrEmpty(c.parameter)) set.Add(c.parameter);

            foreach (var bt in controller.AllBlendTrees())
            {
                if (!string.IsNullOrEmpty(bt.blendParameter)) set.Add(bt.blendParameter);
                if (!string.IsNullOrEmpty(bt.blendParameterY)) set.Add(bt.blendParameterY);
                foreach (var child in bt.children)
                    if (!string.IsNullOrEmpty(child.directBlendParameter)) set.Add(child.directBlendParameter);
            }

            foreach (var s in controller.AllStates())
            {
                if (s.speedParameterActive) set.Add(s.speedParameter);
                if (s.timeParameterActive) set.Add(s.timeParameter);
                if (s.cycleOffsetParameterActive) set.Add(s.cycleOffsetParameter);
                if (s.mirrorParameterActive) set.Add(s.mirrorParameter);
            }
            return set;
        }

        public static List<string> FindUnusedParameters(AnimatorController controller)
        {
            var used = CollectReferencedParameters(controller);
            var unused = new List<string>();
            if (controller == null) return unused;
            foreach (var p in controller.parameters)
                if (!used.Contains(p.name)) unused.Add(p.name);
            return unused;
        }

        public static List<Issue> Analyze(AnimatorController controller)
        {
            var issues = new List<Issue>();
            if (controller == null) return issues;

            foreach (var name in FindUnusedParameters(controller))
                issues.Add(new Issue
                {
                    severity = Severity.Info,
                    category = "Unused Parameter",
                    message = "Parameter '" + name + "' is never referenced.",
                    context = controller,
                });

            var paramTypes = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in controller.parameters) paramTypes[p.name] = p.type;

            foreach (var t in controller.AllTransitions())
            {
                foreach (var c in t.conditions)
                {
                    if (string.IsNullOrEmpty(c.parameter)) continue;
                    if (!paramTypes.TryGetValue(c.parameter, out var type))
                    {
                        issues.Add(new Issue
                        {
                            severity = Severity.Error,
                            category = "Invalid Condition",
                            message = "Condition references missing parameter '" + c.parameter + "'.",
                            context = t,
                        });
                        continue;
                    }
                    if (!IsModeValid(c.mode, type))
                        issues.Add(new Issue
                        {
                            severity = Severity.Error,
                            category = "Invalid Condition",
                            message = "Mode '" + c.mode + "' is invalid for " + type + " parameter '" + c.parameter + "'.",
                            context = t,
                        });
                }

                if (t is AnimatorStateTransition st && t.conditions.Length == 0 && !st.hasExitTime)
                    issues.Add(new Issue
                    {
                        severity = Severity.Warning,
                        category = "Dead Transition",
                        message = "Transition " + ParameterConverter.DescribeTransition(t) +
                                  " has no conditions and no exit time; it can never fire.",
                        context = t,
                    });
            }

            var reachable = new HashSet<AnimatorState>();
            foreach (var sm in controller.AllStateMachines())
                if (sm.defaultState != null) reachable.Add(sm.defaultState);
            foreach (var t in controller.AllTransitions())
                if (t.destinationState != null) reachable.Add(t.destinationState);
            foreach (var s in controller.AllStates())
                if (!reachable.Contains(s))
                    issues.Add(new Issue
                    {
                        severity = Severity.Warning,
                        category = "Unreachable State",
                        message = "State '" + s.name + "' has no incoming transition and is not a default state.",
                        context = s,
                    });

            foreach (var sm in controller.AllStateMachines())
            {
                var seen = new HashSet<string>();
                foreach (var cs in sm.states)
                {
                    if (cs.state == null) continue;
                    if (!seen.Add(cs.state.name))
                        issues.Add(new Issue
                        {
                            severity = Severity.Warning,
                            category = "Duplicate Name",
                            message = "State name '" + cs.state.name + "' is used more than once in '" + sm.name + "'.",
                            context = cs.state,
                        });
                }
            }

            foreach (var layer in controller.layers)
            {
                bool hasTrue = false, hasFalse = false;
                foreach (var sm in layer.stateMachine.SelfAndDescendants())
                    foreach (var cs in sm.states)
                    {
                        if (cs.state == null) continue;
                        if (cs.state.writeDefaultValues) hasTrue = true;
                        else hasFalse = true;
                    }
                if (hasTrue && hasFalse)
                    issues.Add(new Issue
                    {
                        severity = Severity.Warning,
                        category = "WriteDefaults",
                        message = "Layer '" + layer.name + "' mixes Write Defaults ON and OFF across its states.",
                        context = controller,
                    });
            }

            return issues;
        }

        public static bool IsModeValid(AnimatorConditionMode mode, AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return mode == AnimatorConditionMode.If || mode == AnimatorConditionMode.IfNot;
                case AnimatorControllerParameterType.Int:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less
                        || mode == AnimatorConditionMode.Equals || mode == AnimatorConditionMode.NotEqual;
                case AnimatorControllerParameterType.Float:
                    return mode == AnimatorConditionMode.Greater || mode == AnimatorConditionMode.Less;
            }
            return false;
        }

        /// <summary>True when the layer has states and every one of them is a Direct blend tree.</summary>
        public static bool IsDirectBlendTreeOnlyLayer(AnimatorControllerLayer layer)
        {
            if (layer == null || layer.stateMachine == null) return false;
            bool hasState = false;
            foreach (var sm in layer.stateMachine.SelfAndDescendants())
                foreach (var child in sm.states)
                {
                    if (child.state == null) continue;
                    hasState = true;
                    if (!(child.state.motion is BlendTree bt && bt.blendType == BlendTreeType.Direct))
                        return false;
                }
            return hasState;
        }

        /// <summary>
        /// Bulk-sets Write Defaults on every state. When turning OFF, layers that contain only
        /// Direct blend trees are kept ON, because Write Defaults must stay ON for those.
        /// </summary>
        public static void SetAllWriteDefaults(AnimatorController controller, bool value)
        {
            if (controller == null) return;
            using (new UndoScope(value ? "Write Defaults ON" : "Write Defaults OFF"))
            {
                foreach (var layer in controller.layers)
                {
                    if (layer.stateMachine == null) continue;
                    bool directOnly = IsDirectBlendTreeOnlyLayer(layer);
                    foreach (var sm in layer.stateMachine.SelfAndDescendants())
                        foreach (var child in sm.states)
                        {
                            if (child.state == null) continue;
                            bool target = !value && directOnly ? true : value;
                            if (child.state.writeDefaultValues == target) continue;
                            Undo.RegisterCompleteObjectUndo(child.state, "Set Write Defaults");
                            child.state.writeDefaultValues = target;
                            EditorUtility.SetDirty(child.state);
                        }
                }
            }
        }
    }
}
