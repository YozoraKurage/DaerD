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

        /// <summary>Stable machine-readable issue type; <see cref="CategoryLabel"/> gives its localized label.</summary>
        public enum Kind
        {
            UnusedParameter,
            InvalidCondition,
            DeadTransition,
            UnreachableState,
            DuplicateName,
            TerminalStates,
            WriteDefaults,
            MissingMotion,
            EmptyLayer,
            LayerWeight,
            MissingBehaviour,
            DuplicateCondition,
            DirectBlendTree,
        }

        public class Issue
        {
            public Kind kind;
            public Severity severity;
            public string message;
            public Object context;
            /// <summary>For layer-scoped issues whose context is the controller itself, the
            /// layer the message talks about — lets Ping open that layer. -1 when unset.</summary>
            public int layerIndex = -1;
            /// <summary>Optional one-click repair. Runs its own Undo registration; the caller
            /// re-analyzes afterwards, so the delegate doesn't need to update any UI.</summary>
            public System.Action fix;
            public string fixLabel;
            public string fixTooltip;
        }

        /// <summary>Localized display label for an issue kind, resolved at display time.</summary>
        public static string CategoryLabel(Kind kind)
        {
            switch (kind)
            {
                case Kind.UnusedParameter: return L.Tr("Unused Parameter");
                case Kind.InvalidCondition: return L.Tr("Invalid Condition");
                case Kind.DeadTransition: return L.Tr("Dead Transition");
                case Kind.UnreachableState: return L.Tr("Unreachable State");
                case Kind.DuplicateName: return L.Tr("Duplicate Name");
                case Kind.TerminalStates: return L.Tr("Terminal States");
                case Kind.WriteDefaults: return L.Tr("WriteDefaults");
                case Kind.MissingMotion: return L.Tr("Missing Motion");
                case Kind.EmptyLayer: return L.Tr("Empty Layer");
                case Kind.LayerWeight: return L.Tr("Layer Weight");
                case Kind.MissingBehaviour: return L.Tr("Missing Behaviour");
                case Kind.DuplicateCondition: return L.Tr("Duplicate Condition");
                case Kind.DirectBlendTree: return L.Tr("Direct Blend Tree");
            }
            return kind.ToString();
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

            // A parameter only touched by a VRC Parameter Driver is still in use — without
            // this, the unused-parameter fix would offer to delete it and break the driver.
            foreach (var behaviour in controller.AllBehaviours())
                VrcParameterDriver.CollectReferencedParameters(behaviour, set);

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

            AddUnusedParameterIssues(controller, issues);
            AddConditionIssues(controller, issues);
            AddDeadTransitionIssues(controller, issues);
            AddUnreachableStateIssues(controller, issues);
            AddDuplicateNameIssues(controller, issues);

            foreach (var layer in controller.layers)
                foreach (var issue in FindTerminalStateGroups(layer))
                    issues.Add(issue);

            AddWriteDefaultsIssues(controller, issues);
            AddMissingMotionIssues(controller, issues);
            AddLayerIssues(controller, issues);
            AddMissingBehaviourIssues(controller, issues);
            AddDirectBlendTreeIssues(controller, issues);

            return issues;
        }

        static void AddUnusedParameterIssues(AnimatorController controller, List<Issue> issues)
        {
            foreach (var name in FindUnusedParameters(controller))
            {
                issues.Add(new Issue
                {
                    severity = Severity.Info,
                    kind = Kind.UnusedParameter,
                    message = L.Tr("Parameter '{0}' is never referenced.", name),
                    context = controller,
                    fixLabel = L.Tr("Delete"),
                    fixTooltip = L.Tr("Delete this unused parameter"),
                    fix = () => RemoveParameterByName(controller, name),
                });
            }
        }

        static void RemoveParameterByName(AnimatorController controller, string name)
        {
            if (controller == null) return;
            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].name != name) continue;
                Undo.RegisterCompleteObjectUndo(controller, "Remove Parameter");
                controller.RemoveParameter(i);
                EditorUtility.SetDirty(controller);
                return;
            }
        }

        static void AddConditionIssues(AnimatorController controller, List<Issue> issues)
        {
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
                            kind = Kind.InvalidCondition,
                            message = L.Tr("Condition references missing parameter '{0}'.", c.parameter),
                            context = t,
                        });
                        continue;
                    }
                    if (!IsModeValid(c.mode, type))
                        issues.Add(new Issue
                        {
                            severity = Severity.Error,
                            kind = Kind.InvalidCondition,
                            message = L.Tr("Mode '{0}' is invalid for {1} parameter '{2}'.", c.mode, type, c.parameter),
                            context = t,
                        });
                }

                if (HasDuplicateConditions(t))
                    issues.Add(new Issue
                    {
                        severity = Severity.Info,
                        kind = Kind.DuplicateCondition,
                        message = L.Tr("Transition {0} has duplicate conditions.",
                            ParameterConverter.DescribeTransition(t)),
                        context = t,
                        fixLabel = L.Tr("Fix"),
                        fixTooltip = L.Tr("Remove the duplicate conditions"),
                        fix = () => RemoveDuplicateConditions(t),
                    });
            }
        }

        static bool HasDuplicateConditions(AnimatorTransitionBase t)
        {
            var conditions = t.conditions;
            for (int i = 0; i < conditions.Length; i++)
                for (int j = i + 1; j < conditions.Length; j++)
                    if (ConditionsEqual(conditions[i], conditions[j]))
                        return true;
            return false;
        }

        static bool ConditionsEqual(AnimatorCondition a, AnimatorCondition b) =>
            a.parameter == b.parameter && a.mode == b.mode && Mathf.Approximately(a.threshold, b.threshold);

        /// <summary>Drops exact duplicates, keeping the first occurrence and the original order.</summary>
        public static void RemoveDuplicateConditions(AnimatorTransitionBase transition)
        {
            if (transition == null) return;
            var kept = new List<AnimatorCondition>();
            foreach (var c in transition.conditions)
            {
                bool duplicate = false;
                foreach (var k in kept)
                    if (ConditionsEqual(c, k)) { duplicate = true; break; }
                if (!duplicate) kept.Add(c);
            }
            if (kept.Count == transition.conditions.Length) return;
            Undo.RegisterCompleteObjectUndo(transition, "Remove Duplicate Conditions");
            transition.conditions = kept.ToArray();
            EditorUtility.SetDirty(transition);
        }

        static void AddDeadTransitionIssues(AnimatorController controller, List<Issue> issues)
        {
            // Walked with owners (unlike AllTransitions) so the fix can actually detach the
            // transition from the state / state machine that holds it.
            foreach (var sm in controller.AllStateMachines())
                foreach (var t in sm.anyStateTransitions)
                    if (IsDeadTransition(t))
                        issues.Add(MakeDeadTransitionIssue(t, () => RemoveOwnedTransition(sm, t)));
            foreach (var state in controller.AllStates())
                foreach (var t in state.transitions)
                    if (IsDeadTransition(t))
                        issues.Add(MakeDeadTransitionIssue(t, () => RemoveOwnedTransition(state, t)));
        }

        static bool IsDeadTransition(AnimatorStateTransition t) =>
            t != null && t.conditions.Length == 0 && !t.hasExitTime;

        static Issue MakeDeadTransitionIssue(AnimatorStateTransition t, System.Action fix) => new Issue
        {
            severity = Severity.Warning,
            kind = Kind.DeadTransition,
            message = L.Tr("Transition {0} has no conditions and no exit time; it can never fire.",
                ParameterConverter.DescribeTransition(t)),
            context = t,
            fixLabel = L.Tr("Delete"),
            fixTooltip = L.Tr("Delete this transition"),
            fix = fix,
        };

        static void RemoveOwnedTransition(AnimatorStateMachine anyStateOwner, AnimatorStateTransition transition)
        {
            if (anyStateOwner == null || transition == null) return;
            Undo.RegisterCompleteObjectUndo(anyStateOwner, "Delete Transition");
            anyStateOwner.RemoveAnyStateTransition(transition);
            EditorUtility.SetDirty(anyStateOwner);
        }

        static void RemoveOwnedTransition(AnimatorState owner, AnimatorStateTransition transition)
        {
            if (owner == null || transition == null) return;
            Undo.RegisterCompleteObjectUndo(owner, "Delete Transition");
            owner.RemoveTransition(transition);
            EditorUtility.SetDirty(owner);
        }

        static void AddUnreachableStateIssues(AnimatorController controller, List<Issue> issues)
        {
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
                        kind = Kind.UnreachableState,
                        message = L.Tr("State '{0}' has no incoming transition and is not a default state.", s.name),
                        context = s,
                    });
        }

        static void AddDuplicateNameIssues(AnimatorController controller, List<Issue> issues)
        {
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
                            kind = Kind.DuplicateName,
                            message = L.Tr("State name '{0}' is used more than once in '{1}'.", cs.state.name, sm.name),
                            context = cs.state,
                        });
                }
            }
        }

        static void AddWriteDefaultsIssues(AnimatorController controller, List<Issue> issues)
        {
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
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
                        kind = Kind.WriteDefaults,
                        message = L.Tr("Layer '{0}' mixes Write Defaults ON and OFF across its states.", layer.name),
                        context = controller,
                        layerIndex = li,
                    });
            }
        }

        static void AddMissingMotionIssues(AnimatorController controller, List<Issue> issues)
        {
            // A shared blend tree is reported once (for the first state found using it).
            var visited = new HashSet<BlendTree>();
            // The controller's designated Empty clip enables a one-click "fill the hole" fix.
            var empty = GraphFrameData.GetEmptyClip(controller);
            foreach (var s in controller.AllStates())
            {
                if (s.motion == null)
                {
                    // With Write Defaults OFF an empty state samples nothing and writes
                    // nothing back, so every animated property freezes at its last value —
                    // that's a real malfunction, not a cosmetic gap.
                    bool wdOff = !s.writeDefaultValues;
                    var issue = new Issue
                    {
                        severity = wdOff ? Severity.Error : Severity.Warning,
                        kind = Kind.MissingMotion,
                        message = wdOff
                            ? L.Tr("State '{0}' has Write Defaults OFF and no motion; animated properties freeze at their last value while it plays.", s.name)
                            : L.Tr("State '{0}' has no motion assigned.", s.name),
                        context = s,
                    };
                    if (empty != null)
                    {
                        var state = s;
                        issue.fixLabel = L.Tr("Fill");
                        issue.fixTooltip = L.Tr("Assign this controller's Empty clip");
                        issue.fix = () => AssignEmptyClip(state, empty);
                    }
                    issues.Add(issue);
                    continue;
                }
                AddEmptyBlendTreeSlots(s.motion, s, visited, issues, empty);
            }
        }

        static void AddEmptyBlendTreeSlots(Motion motion, AnimatorState owner,
            HashSet<BlendTree> visited, List<Issue> issues, AnimationClip empty)
        {
            if (!(motion is BlendTree tree) || !visited.Add(tree)) return;
            bool hasEmptySlot = false;
            foreach (var child in tree.children)
            {
                if (child.motion == null) hasEmptySlot = true;
                else AddEmptyBlendTreeSlots(child.motion, owner, visited, issues, empty);
            }
            if (hasEmptySlot)
            {
                var issue = new Issue
                {
                    severity = Severity.Warning,
                    kind = Kind.MissingMotion,
                    message = L.Tr("Blend tree '{0}' in state '{1}' has a child slot with no motion.",
                        tree.name, owner.name),
                    context = tree,
                };
                if (empty != null)
                {
                    issue.fixLabel = L.Tr("Fill");
                    issue.fixTooltip = L.Tr("Fill the empty child slots with this controller's Empty clip");
                    issue.fix = () => FillEmptySlots(tree, empty);
                }
                issues.Add(issue);
            }
        }

        public static void AssignEmptyClip(AnimatorState state, AnimationClip clip)
        {
            if (state == null || clip == null || state.motion != null) return;
            Undo.RegisterCompleteObjectUndo(state, "Assign Empty Clip");
            state.motion = clip;
            EditorUtility.SetDirty(state);
        }

        /// <summary>Fills every empty child slot of the tree (direct children only) with the clip.</summary>
        public static void FillEmptySlots(BlendTree tree, AnimationClip clip)
        {
            if (tree == null || clip == null) return;
            var children = tree.children;
            bool changed = false;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion != null) continue;
                children[i].motion = clip;
                changed = true;
            }
            if (!changed) return;
            Undo.RegisterCompleteObjectUndo(tree, "Fill Empty Slots");
            tree.children = children;
            EditorUtility.SetDirty(tree);
        }

        static void AddLayerIssues(AnimatorController controller, List<Issue> issues)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];

                // A synced layer mirrors its source layer's states and has no state machine
                // of its own — "contains no states" would be a false positive there.
                bool synced = layer.syncedLayerIndex >= 0;

                bool hasState = false;
                if (layer.stateMachine != null)
                    foreach (var sm in layer.stateMachine.SelfAndDescendants())
                        if (sm.states.Length > 0) { hasState = true; break; }
                if (!hasState && !synced)
                    issues.Add(new Issue
                    {
                        severity = Severity.Info,
                        kind = Kind.EmptyLayer,
                        message = L.Tr("Layer '{0}' contains no states.", layer.name),
                        context = controller,
                        layerIndex = i,
                    });

                // The base layer's weight is forced to 1 at runtime, so only flag the others.
                // Weight-0 layers are sometimes intentional (driven at runtime), hence Info.
                if (i > 0 && layer.defaultWeight == 0f)
                    issues.Add(new Issue
                    {
                        severity = Severity.Info,
                        kind = Kind.LayerWeight,
                        message = L.Tr(
                            "Layer '{0}' has default weight 0; it has no effect until its weight is raised at runtime.",
                            layer.name),
                        context = controller,
                        layerIndex = i,
                    });
            }
        }

        static void AddMissingBehaviourIssues(AnimatorController controller, List<Issue> issues)
        {
            Issue Make(string message, Object context, System.Action fix) => new Issue
            {
                severity = Severity.Error,
                kind = Kind.MissingBehaviour,
                message = message,
                context = context,
                fixLabel = L.Tr("Fix"),
                fixTooltip = L.Tr("Remove the missing behaviour entries"),
                fix = fix,
            };

            foreach (var s in controller.AllStates())
                if (HasNullEntry(s.behaviours))
                    issues.Add(Make(L.Tr("State '{0}' has a missing (null) behaviour script.", s.name),
                        s, () => StripNullBehaviours(s)));
            foreach (var sm in controller.AllStateMachines())
                if (HasNullEntry(sm.behaviours))
                    issues.Add(Make(L.Tr("State machine '{0}' has a missing (null) behaviour script.", sm.name),
                        sm, () => StripNullBehaviours(sm)));
        }

        static bool HasNullEntry(StateMachineBehaviour[] behaviours)
        {
            if (behaviours == null) return false;
            foreach (var b in behaviours)
                if (b == null) return true;
            return false;
        }

        static void StripNullBehaviours(AnimatorState state)
        {
            if (state == null) return;
            Undo.RegisterCompleteObjectUndo(state, "Remove Missing Behaviours");
            state.behaviours = WithoutNulls(state.behaviours);
            EditorUtility.SetDirty(state);
        }

        static void StripNullBehaviours(AnimatorStateMachine sm)
        {
            if (sm == null) return;
            Undo.RegisterCompleteObjectUndo(sm, "Remove Missing Behaviours");
            sm.behaviours = WithoutNulls(sm.behaviours);
            EditorUtility.SetDirty(sm);
        }

        static StateMachineBehaviour[] WithoutNulls(StateMachineBehaviour[] behaviours)
        {
            var kept = new List<StateMachineBehaviour>();
            foreach (var b in behaviours)
                if (b != null) kept.Add(b);
            return kept.ToArray();
        }

        /// <summary>
        /// Health checks for the Direct-blend-tree (AAP gadget) idiom: a state hosting a
        /// Direct tree must run with Write Defaults ON (additive weight mixing and AAP
        /// writes silently misbehave otherwise), and every Direct child needs an existing
        /// Float weight parameter — a missing or unset one pins that child's weight to 0.
        /// </summary>
        static void AddDirectBlendTreeIssues(AnimatorController controller, List<Issue> issues)
        {
            foreach (var s in controller.AllStates())
            {
                if (s.writeDefaultValues || !(s.motion is BlendTree tree) || !ContainsDirectTree(tree))
                    continue;
                var state = s;
                issues.Add(new Issue
                {
                    severity = Severity.Error,
                    kind = Kind.DirectBlendTree,
                    message = L.Tr("State '{0}' plays a Direct blend tree but has Write Defaults OFF.", s.name),
                    context = s,
                    fixLabel = L.Tr("Fix"),
                    fixTooltip = L.Tr("Turn Write Defaults ON for this state"),
                    fix = () => SetWriteDefaults(state, true),
                });
            }

            var paramTypes = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in controller.parameters) paramTypes[p.name] = p.type;

            foreach (var bt in controller.AllBlendTrees())
            {
                if (bt.blendType != BlendTreeType.Direct) continue;
                // One issue per distinct problem per tree — a shared weight parameter that
                // is missing would otherwise repeat for every child using it.
                bool reportedEmpty = false;
                var reported = new HashSet<string>();
                foreach (var child in bt.children)
                {
                    var weight = child.directBlendParameter;
                    if (string.IsNullOrEmpty(weight))
                    {
                        if (reportedEmpty) continue;
                        reportedEmpty = true;
                        issues.Add(new Issue
                        {
                            severity = Severity.Warning,
                            kind = Kind.DirectBlendTree,
                            message = L.Tr("Direct blend tree '{0}' has a child with no weight parameter; that child never plays.", bt.name),
                            context = bt,
                        });
                    }
                    else if (!paramTypes.TryGetValue(weight, out var type))
                    {
                        if (!reported.Add(weight)) continue;
                        issues.Add(new Issue
                        {
                            severity = Severity.Error,
                            kind = Kind.DirectBlendTree,
                            message = L.Tr("Direct blend tree '{0}' weights a child with missing parameter '{1}'.", bt.name, weight),
                            context = bt,
                        });
                    }
                    else if (type != AnimatorControllerParameterType.Float)
                    {
                        if (!reported.Add(weight)) continue;
                        issues.Add(new Issue
                        {
                            severity = Severity.Warning,
                            kind = Kind.DirectBlendTree,
                            message = L.Tr("Weight parameter '{1}' of Direct blend tree '{0}' is not a Float.", bt.name, weight),
                            context = bt,
                        });
                    }
                }
            }
        }

        /// <summary>True when the motion tree contains a Direct blend tree at any depth.</summary>
        static bool ContainsDirectTree(BlendTree root)
        {
            var visited = new HashSet<BlendTree>();
            var stack = new Stack<BlendTree>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var tree = stack.Pop();
                if (tree == null || !visited.Add(tree)) continue;
                if (tree.blendType == BlendTreeType.Direct) return true;
                foreach (var child in tree.children)
                    if (child.motion is BlendTree nested)
                        stack.Push(nested);
            }
            return false;
        }

        static void SetWriteDefaults(AnimatorState state, bool value)
        {
            if (state == null) return;
            Undo.RegisterCompleteObjectUndo(state, "Set Write Defaults");
            state.writeDefaultValues = value;
            EditorUtility.SetDirty(state);
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
                    kind = Kind.TerminalStates,
                    message = L.Tr("Layer '{0}': once entered, '{1}' can never be left (no outgoing transition or exit).",
                        layer.name, list),
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
