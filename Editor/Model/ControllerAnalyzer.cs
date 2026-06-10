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
                foreach (var issue in FindTerminalStateGroups(layer))
                    issues.Add(issue);

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

        /// <summary>
        /// Finds groups of states that can be entered but never left: strongly connected
        /// components with no transition leaving the group and no Exit transition. The group
        /// containing the layer's default state is excluded — that is just the layer's main loop.
        /// </summary>
        public static List<Issue> FindTerminalStateGroups(AnimatorControllerLayer layer)
        {
            var issues = new List<Issue>();
            if (layer?.stateMachine == null) return issues;

            // Collect every state in the layer and a state→state edge list. A transition to a
            // sub-state machine continues at that machine's default state; an Exit transition
            // counts as leaving (it can re-enter the main loop via Entry).
            var states = new List<AnimatorState>();
            var index = new Dictionary<AnimatorState, int>();
            foreach (var sm in layer.stateMachine.SelfAndDescendants())
                foreach (var cs in sm.states)
                    if (cs.state != null && !index.ContainsKey(cs.state))
                    {
                        index[cs.state] = states.Count;
                        states.Add(cs.state);
                    }
            if (states.Count == 0) return issues;

            var edges = new List<int>[states.Count];
            var hasExit = new bool[states.Count];
            for (int i = 0; i < states.Count; i++)
            {
                edges[i] = new List<int>();
                foreach (var t in states[i].transitions)
                {
                    if (t == null) continue;
                    if (t.isExit) { hasExit[i] = true; continue; }
                    var destination = t.destinationState != null
                        ? t.destinationState
                        : t.destinationStateMachine != null ? t.destinationStateMachine.defaultState : null;
                    if (destination != null && index.TryGetValue(destination, out int di) && di != i)
                        edges[i].Add(di);
                }
            }

            var sccOf = ComputeStronglyConnectedComponents(edges, out int sccCount);

            // A component is trapped when no member exits and no edge leaves the component.
            var trapped = new bool[sccCount];
            for (int c = 0; c < sccCount; c++) trapped[c] = true;
            for (int i = 0; i < states.Count; i++)
            {
                if (hasExit[i]) trapped[sccOf[i]] = false;
                foreach (var j in edges[i])
                    if (sccOf[j] != sccOf[i]) trapped[sccOf[i]] = false;
            }
            if (layer.stateMachine.defaultState != null && index.TryGetValue(layer.stateMachine.defaultState, out int defaultIndex))
                trapped[sccOf[defaultIndex]] = false;

            var members = new List<string>[sccCount];
            var context = new AnimatorState[sccCount];
            for (int i = 0; i < states.Count; i++)
            {
                int c = sccOf[i];
                if (!trapped[c]) continue;
                (members[c] ??= new List<string>()).Add(states[i].name);
                if (context[c] == null) context[c] = states[i];
            }
            for (int c = 0; c < sccCount; c++)
            {
                if (!trapped[c] || members[c] == null) continue;
                string list = string.Join("', '", members[c]);
                issues.Add(new Issue
                {
                    severity = Severity.Info,
                    category = "Terminal States",
                    message = "Layer '" + layer.name + "': once entered, '" + list +
                              "' can never be left (no outgoing transition or exit).",
                    context = context[c],
                });
            }
            return issues;
        }

        /// <summary>Iterative Tarjan; returns each node's component id (count via out parameter).</summary>
        static int[] ComputeStronglyConnectedComponents(List<int>[] edges, out int sccCount)
        {
            int n = edges.Length;
            var ids = new int[n];
            var low = new int[n];
            var onStack = new bool[n];
            var comp = new int[n];
            for (int i = 0; i < n; i++) ids[i] = -1;
            var stack = new Stack<int>();
            int nextId = 0, components = 0;

            // Explicit work stack: (node, next edge index to visit).
            var work = new Stack<(int node, int edge)>();
            for (int start = 0; start < n; start++)
            {
                if (ids[start] != -1) continue;
                work.Push((start, 0));
                while (work.Count > 0)
                {
                    var (node, edge) = work.Pop();
                    if (edge == 0)
                    {
                        ids[node] = low[node] = nextId++;
                        stack.Push(node);
                        onStack[node] = true;
                    }
                    else
                    {
                        // Returning from the recursive visit of edges[node][edge - 1].
                        low[node] = Mathf.Min(low[node], low[edges[node][edge - 1]]);
                    }

                    bool descended = false;
                    while (edge < edges[node].Count)
                    {
                        int next = edges[node][edge];
                        edge++;
                        if (ids[next] == -1)
                        {
                            work.Push((node, edge));
                            work.Push((next, 0));
                            descended = true;
                            break;
                        }
                        if (onStack[next])
                            low[node] = Mathf.Min(low[node], ids[next]);
                    }
                    if (descended) continue;

                    if (low[node] == ids[node])
                    {
                        int member;
                        do
                        {
                            member = stack.Pop();
                            onStack[member] = false;
                            comp[member] = components;
                        } while (member != node);
                        components++;
                    }
                }
            }
            sccCount = components;
            return comp;
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
