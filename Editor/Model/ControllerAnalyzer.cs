using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Audits a controller and provides controller-wide bulk fixes.</summary>
    static class ControllerAnalyzer
    {
        /// <summary>Localized display label for an issue kind, resolved at display time.</summary>
        public static string CategoryLabel(IssueKind kind)
        {
            switch (kind)
            {
                case IssueKind.UnusedParameter: return L.Tr("Unused Parameter");
                case IssueKind.InvalidCondition: return L.Tr("Invalid Condition");
                case IssueKind.DeadTransition: return L.Tr("Dead Transition");
                case IssueKind.ExitTimeZero: return L.Tr("Exit Time 0");
                case IssueKind.SoloTransition: return L.Tr("Soloed Transition");
                case IssueKind.AnyStateRetrigger: return L.Tr("Any State Retrigger");
                case IssueKind.UnreachableState: return L.Tr("Unreachable State");
                case IssueKind.DuplicateName: return L.Tr("Duplicate Name");
                case IssueKind.TerminalStates: return L.Tr("Terminal States");
                case IssueKind.WriteDefaults: return L.Tr("WriteDefaults");
                case IssueKind.MissingMotion: return L.Tr("Missing Motion");
                case IssueKind.EmptyLayer: return L.Tr("Empty Layer");
                case IssueKind.LayerWeight: return L.Tr("Layer Weight");
                case IssueKind.MissingBehaviour: return L.Tr("Missing Behaviour");
                case IssueKind.DuplicateCondition: return L.Tr("Duplicate Condition");
                case IssueKind.DirectBlendTree: return L.Tr("Direct Blend Tree");
                case IssueKind.VrcParameters: return L.Tr("VRC Parameters");
                case IssueKind.ClipBindings: return L.Tr("Clip Bindings");
                case IssueKind.AapDriver: return L.Tr("AAP / Driver");
                case IssueKind.AapLayers: return L.Tr("AAP / Layers");
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

        public static List<AnalyzerIssue> Analyze(AnimatorController controller)
        {
            var issues = new List<AnalyzerIssue>();
            if (controller == null) return issues;

            AddUnusedParameterIssues(controller, issues);
            AddConditionIssues(controller, issues);
            AddDeadTransitionIssues(controller, issues);
            AddExitTimeZeroIssues(controller, issues);
            AddSoloTransitionIssues(controller, issues);
            AddAnyStateRetriggerIssues(controller, issues);
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
            // The clip walk behind this is the most expensive thing the analyzer does; both
            // AAP checks read the one result.
            var aapWrites = AapWriteScan.CollectByLayer(controller);
            AddAapDriverIssues(controller, aapWrites, issues);
            AddAapLayerIssues(controller, aapWrites, issues);

            // Parameter-store checks only run against the store the user explicitly
            // associated with this controller (never a scene guess — DaerD is also used on
            // NDMF gimmick controllers that belong to no avatar).
            var store = ParameterStore.Of(controller);
            if (store != null)
                store.Analyze(controller, issues);
            ClipRepather.Analyze(controller, issues);

            return issues;
        }

        static void AddUnusedParameterIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            foreach (var name in FindUnusedParameters(controller))
            {
                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Info,
                    kind = IssueKind.UnusedParameter,
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

        static void AddConditionIssues(AnimatorController controller, List<AnalyzerIssue> issues)
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
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Error,
                            kind = IssueKind.InvalidCondition,
                            message = L.Tr("Condition references missing parameter '{0}'.", c.parameter),
                            context = t,
                        });
                        continue;
                    }
                    if (!IsModeValid(c.mode, type))
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Error,
                            kind = IssueKind.InvalidCondition,
                            message = L.Tr("Mode '{0}' is invalid for {1} parameter '{2}'.", c.mode, type, c.parameter),
                            context = t,
                        });
                }

                if (HasDuplicateConditions(t))
                    issues.Add(new AnalyzerIssue
                    {
                        severity = IssueSeverity.Info,
                        kind = IssueKind.DuplicateCondition,
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

        static void AddDeadTransitionIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            // Walked with owners (unlike AllTransitions) so the fix can actually detach the
            // transition from the state / state machine that holds it.
            foreach (var sm in controller.AllStateMachines())
                foreach (var t in sm.anyStateTransitions)
                    AddDeadTransitionIssue(t, () => RemoveOwnedTransition(sm, t), issues);
            foreach (var state in controller.AllStates())
                foreach (var t in state.transitions)
                    AddDeadTransitionIssue(t, () => RemoveOwnedTransition(state, t), issues);
        }

        /// <summary>
        /// The two ways a transition can be unable to fire: nothing tells it to (no conditions,
        /// no exit time), or its own conditions rule each other out. Same consequence, same
        /// repair, so they share a category and differ only in what the message says.
        /// </summary>
        static void AddDeadTransitionIssue(AnimatorStateTransition t, System.Action fix,
            List<AnalyzerIssue> issues)
        {
            if (t == null) return;
            if (IsDeadTransition(t))
            {
                issues.Add(MakeDeadTransitionIssue(t,
                    L.Tr("Transition {0} has no conditions and no exit time; it can never fire.",
                        ParameterConverter.DescribeTransition(t)), fix));
                return;
            }
            string contradiction = ContradictionIn(t);
            if (contradiction != null)
                issues.Add(MakeDeadTransitionIssue(t,
                    L.Tr("Transition {0} asks for {1} at the same time; it can never fire.",
                        ParameterConverter.DescribeTransition(t), contradiction), fix));
        }

        static bool IsDeadTransition(AnimatorStateTransition t) =>
            t != null && t.conditions.Length == 0 && !t.hasExitTime;

        /// <summary>
        /// The first pair of conditions on a transition that cannot both hold at once. All of a
        /// transition's conditions are ANDed, so one such pair makes the whole transition
        /// unreachable however the parameters move.
        ///
        /// Deliberately blind to parameter types, which keeps it free of false positives at the
        /// cost of a case: "Greater 0 and Less 1" is impossible for an Int and perfectly normal
        /// for a Float, and it is not worth reporting the Int one if the price is ever crying
        /// wolf about the Float.
        /// </summary>
        public static bool Contradict(AnimatorCondition a, AnimatorCondition b)
        {
            if (a.parameter != b.parameter) return false;
            switch (a.mode)
            {
                case AnimatorConditionMode.If:
                    return b.mode == AnimatorConditionMode.IfNot;
                case AnimatorConditionMode.IfNot:
                    return b.mode == AnimatorConditionMode.If;
                case AnimatorConditionMode.Equals:
                    switch (b.mode)
                    {
                        // Two different values at once, or the one value it must not be.
                        case AnimatorConditionMode.Equals: return !Mathf.Approximately(a.threshold, b.threshold);
                        case AnimatorConditionMode.NotEqual: return Mathf.Approximately(a.threshold, b.threshold);
                        case AnimatorConditionMode.Greater: return a.threshold <= b.threshold;
                        case AnimatorConditionMode.Less: return a.threshold >= b.threshold;
                        default: return false;
                    }
                case AnimatorConditionMode.NotEqual:
                    return b.mode == AnimatorConditionMode.Equals
                        && Mathf.Approximately(a.threshold, b.threshold);
                case AnimatorConditionMode.Greater:
                    // Greater and Less are both strict, so a window that does not open is empty.
                    if (b.mode == AnimatorConditionMode.Less) return b.threshold <= a.threshold;
                    if (b.mode == AnimatorConditionMode.Equals) return b.threshold <= a.threshold;
                    return false;
                case AnimatorConditionMode.Less:
                    if (b.mode == AnimatorConditionMode.Greater) return b.threshold >= a.threshold;
                    if (b.mode == AnimatorConditionMode.Equals) return b.threshold >= a.threshold;
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>The first contradicting pair on a transition, rendered, or null.</summary>
        static string ContradictionIn(AnimatorTransitionBase transition)
        {
            var conditions = transition.conditions;
            for (int i = 0; i < conditions.Length; i++)
                for (int j = i + 1; j < conditions.Length; j++)
                    if (Contradict(conditions[i], conditions[j]))
                        return ParameterConverter.DescribeCondition(conditions[i]) + "  /  "
                            + ParameterConverter.DescribeCondition(conditions[j]);
            return null;
        }

        static AnalyzerIssue MakeDeadTransitionIssue(AnimatorStateTransition t, string message,
            System.Action fix) => new AnalyzerIssue
        {
            severity = IssueSeverity.Warning,
            kind = IssueKind.DeadTransition,
            message = message,
            context = t,
            fixLabel = L.Tr("Delete"),
            fixTooltip = L.Tr("Delete this transition"),
            fix = fix,
        };

        /// <summary>
        /// Exit Time 0, which reads as "leave the moment you get here" and is not that. Unity
        /// treats it exactly like Exit Time 1 and fires on the loop boundary — once per length
        /// of the source state's clip, and once a second for a state with no motion at all.
        /// Measured rather than reasoned: PlayModeProbeTests pins "exitTime 0 and exitTime 1 are
        /// the same instruction" in AnExitTimeOfZeroWaitsForTheEndOfALap.
        ///
        /// Kept out of <see cref="IssueKind.DeadTransition"/> on purpose. That box means "this
        /// never fires"; these do fire, just late, and filing them together would make the
        /// category a lie in both directions.
        ///
        /// Any State transitions are left alone: what Exit Time counts against for a transition
        /// with no source state of its own was never measured, and the analyzer does not guess.
        /// Entry, Exit and state-machine transitions carry no exit time to begin with, so they
        /// fall out by themselves.
        /// </summary>
        static void AddExitTimeZeroIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            foreach (var state in controller.AllStates())
                foreach (var t in state.transitions)
                {
                    if (t == null || !t.hasExitTime || t.exitTime > 0f) continue;

                    // With conditions the intent is legible — somebody wanted "when this holds,
                    // go" and wrote the zero to mean "at once" — so the repair is legible too.
                    if (t.conditions.Length > 0)
                    {
                        var transition = t;
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Warning,
                            kind = IssueKind.ExitTimeZero,
                            message = L.Tr("Transition '{0}' {1} waits for the loop boundary even once its "
                                + "conditions hold: Exit Time 0 means the same as Exit Time 1 — once per clip "
                                + "length, once a second in a state with no motion — and never 'immediately'.",
                                state.name, ParameterConverter.DescribeTransition(t)),
                            context = t,
                            fixLabel = L.Tr("Turn Off Exit Time"),
                            fixTooltip = L.Tr("Clear Exit Time so the conditions alone decide when it fires"),
                            fix = () => ClearExitTime(transition),
                        });
                        continue;
                    }

                    // No conditions: this is a fall-through that repeats, which is a real shape
                    // and not necessarily a mistake — hence Info, and hence no Fix. Clearing
                    // Exit Time here would leave a transition with neither conditions nor exit
                    // time, i.e. trade this row for a Dead Transition one. Which of the two
                    // repairs is right depends on whether the author wanted the loop or wanted
                    // "immediately", and that is not on the asset to read.
                    issues.Add(new AnalyzerIssue
                    {
                        severity = IssueSeverity.Info,
                        kind = IssueKind.ExitTimeZero,
                        message = L.Tr("Transition '{0}' {1} has Exit Time 0, which behaves exactly like Exit "
                            + "Time 1: it falls through at every loop boundary (once per clip length, once a "
                            + "second in a state with no motion). If it was meant to fire immediately, give it "
                            + "a condition that always holds and turn Exit Time off.",
                            state.name, ParameterConverter.DescribeTransition(t)),
                        context = t,
                    });
                }
        }

        static void ClearExitTime(AnimatorStateTransition transition)
        {
            if (transition == null) return;
            Undo.RegisterCompleteObjectUndo(transition, "Turn Off Exit Time");
            transition.hasExitTime = false;
            EditorUtility.SetDirty(transition);
        }

        /// <summary>
        /// Solo left switched on. It is a debugging aid — one soloed transition makes the
        /// Animator ignore every other transition leaving the same node — and it survives being
        /// saved, so a controller can ship with most of a state's transitions silently disabled
        /// and nothing on screen saying so. Reported per source, and only when it actually
        /// shuts something out: soloing the only transition a state has changes nothing.
        /// </summary>
        static void AddSoloTransitionIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            foreach (var sm in controller.AllStateMachines())
            {
                AddSoloIssue(sm.anyStateTransitions, "Any State", sm, issues);
                foreach (var state in sm.states)
                    AddSoloIssue(state.state.transitions, state.state.name, state.state, issues);
            }
        }

        static void AddSoloIssue(AnimatorStateTransition[] transitions, string sourceName, Object owner,
            List<AnalyzerIssue> issues)
        {
            if (!EdgeCommands.HasLiveSolo(transitions)) return;

            int shutOut = 0;
            foreach (var t in transitions)
                if (t != null && !t.solo && !t.mute) shutOut++;
            if (shutOut == 0) return;

            issues.Add(new AnalyzerIssue
            {
                severity = IssueSeverity.Warning,
                kind = IssueKind.SoloTransition,
                message = L.Tr("'{0}' has a soloed transition, so its other {1} transition(s) never run.",
                    sourceName, shutOut),
                context = owner,
                fixLabel = L.Tr("Clear Solo"),
                fixTooltip = L.Tr("Turn Solo off on every transition leaving this node"),
                fix = () => ClearSolo(transitions, owner),
            });
        }

        static void ClearSolo(AnimatorStateTransition[] transitions, Object owner)
        {
            using (new UndoScope("Clear Solo"))
            {
                foreach (var t in transitions)
                {
                    if (t == null || !t.solo) continue;
                    Undo.RegisterCompleteObjectUndo(t, "Clear Solo");
                    t.solo = false;
                    EditorUtility.SetDirty(t);
                }
                if (owner != null) EditorUtility.SetDirty(owner);
            }
        }

        /// <summary>
        /// An Any State transition that may interrupt its own destination. While the condition
        /// still holds, the Animator keeps taking the transition, so the destination state
        /// restarts from the beginning instead of playing on — the clip never gets past its
        /// first frames and nothing on screen says why.
        ///
        /// Transitions gated on a Trigger are left alone: a Trigger is consumed when it is read,
        /// so the condition stops holding by itself. Reported once per state machine, with the
        /// count, because a gesture layer holds a dozen of these and a row each would bury the
        /// rest of the report.
        /// </summary>
        static void AddAnyStateRetriggerIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            var triggers = new HashSet<string>();
            foreach (var parameter in controller.parameters)
                if (parameter.type == AnimatorControllerParameterType.Trigger) triggers.Add(parameter.name);

            foreach (var sm in controller.AllStateMachines())
            {
                var retriggering = new List<AnimatorStateTransition>();
                foreach (var t in sm.anyStateTransitions)
                    if (Retriggers(t, triggers)) retriggering.Add(t);
                if (retriggering.Count == 0) continue;

                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Warning,
                    kind = IssueKind.AnyStateRetrigger,
                    message = L.Tr("{0} Any State transition(s) can interrupt themselves; while the "
                        + "condition still holds, the destination state restarts instead of playing on.",
                        retriggering.Count),
                    context = sm,
                    fixLabel = L.Tr("Turn Off"),
                    fixTooltip = L.Tr("Clear Can Transition To Self on these transitions"),
                    fix = () => ClearSelfTransition(retriggering, sm),
                });
            }
        }

        static bool Retriggers(AnimatorStateTransition t, HashSet<string> triggers)
        {
            if (t == null || t.mute || !t.canTransitionToSelf) return false;
            if (t.destinationState == null) return false;
            // No condition at all is the dead / always-on case, reported elsewhere.
            if (t.conditions.Length == 0) return false;
            foreach (var condition in t.conditions)
                if (triggers.Contains(condition.parameter)) return false;
            return true;
        }

        static void ClearSelfTransition(List<AnimatorStateTransition> transitions, Object owner)
        {
            using (new UndoScope("Clear Can Transition To Self"))
            {
                foreach (var t in transitions)
                {
                    Undo.RegisterCompleteObjectUndo(t, "Clear Can Transition To Self");
                    t.canTransitionToSelf = false;
                    EditorUtility.SetDirty(t);
                }
                if (owner != null) EditorUtility.SetDirty(owner);
            }
        }

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

        /// <summary>
        /// States the layer can never enter, walked forward from Entry
        /// (<see cref="ControllerReachability"/>) rather than asked one at a time whether
        /// something points at them. The difference is a whole island: a handful of states
        /// wired to each other, all with incoming transitions, and nothing outside leading
        /// in. The message says which of the two it is, because they are undone differently —
        /// one needs a transition drawn to it, the other needs one drawn to its island.
        /// </summary>
        static void AddUnreachableStateIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            var withIncoming = new HashSet<AnimatorState>();
            foreach (var t in controller.AllTransitions())
                if (t.destinationState != null) withIncoming.Add(t.destinationState);

            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                // A synced layer replays the source layer's states, which the source layer's
                // own pass already covers.
                if (layers[i].syncedLayerIndex >= 0) continue;
                var root = layers[i].stateMachine;
                if (root == null) continue;
                var reachable = ControllerReachability.ReachableStates(root);
                foreach (var sm in root.SelfAndDescendants())
                    foreach (var cs in sm.states)
                    {
                        var state = cs.state;
                        if (state == null || reachable.Contains(state)) continue;
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Warning,
                            kind = IssueKind.UnreachableState,
                            message = withIncoming.Contains(state)
                                ? L.Tr("State '{0}' is only reachable from states that are themselves unreachable.", state.name)
                                : L.Tr("State '{0}' has no incoming transition and is not a default state.", state.name),
                            context = state,
                        });
                    }
            }
        }

        /// <summary>Every state the animator can end up in, across every layer.</summary>
        static HashSet<AnimatorState> CollectReachableStates(AnimatorController controller)
        {
            var all = new HashSet<AnimatorState>();
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                all.UnionWith(ControllerReachability.ReachableStates(
                    ControllerReachability.PlayedMachine(controller, i)));
            return all;
        }

        static void AddDuplicateNameIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            foreach (var sm in controller.AllStateMachines())
            {
                var seen = new HashSet<string>();
                foreach (var cs in sm.states)
                {
                    if (cs.state == null) continue;
                    if (!seen.Add(cs.state.name))
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Warning,
                            kind = IssueKind.DuplicateName,
                            message = L.Tr("State name '{0}' is used more than once in '{1}'.", cs.state.name, sm.name),
                            context = cs.state,
                        });
                }
            }
        }

        static void AddWriteDefaultsIssues(AnimatorController controller, List<AnalyzerIssue> issues)
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
                    issues.Add(new AnalyzerIssue
                    {
                        severity = IssueSeverity.Warning,
                        kind = IssueKind.WriteDefaults,
                        message = L.Tr("Layer '{0}' mixes Write Defaults ON and OFF across its states.", layer.name),
                        context = controller,
                        layerIndex = li,
                    });
            }
        }

        static void AddMissingMotionIssues(AnimatorController controller, List<AnalyzerIssue> issues)
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
                    var issue = new AnalyzerIssue
                    {
                        severity = wdOff ? IssueSeverity.Error : IssueSeverity.Warning,
                        kind = IssueKind.MissingMotion,
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
            HashSet<BlendTree> visited, List<AnalyzerIssue> issues, AnimationClip empty)
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
                var issue = new AnalyzerIssue
                {
                    severity = IssueSeverity.Warning,
                    kind = IssueKind.MissingMotion,
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

        static void AddLayerIssues(AnimatorController controller, List<AnalyzerIssue> issues)
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
                    issues.Add(new AnalyzerIssue
                    {
                        severity = IssueSeverity.Info,
                        kind = IssueKind.EmptyLayer,
                        message = L.Tr("Layer '{0}' contains no states.", layer.name),
                        context = controller,
                        layerIndex = i,
                    });

                // The base layer's weight is forced to 1 at runtime, so only flag the others.
                // Weight-0 layers are sometimes intentional (driven at runtime), hence Info.
                if (i > 0 && layer.defaultWeight == 0f)
                    issues.Add(new AnalyzerIssue
                    {
                        severity = IssueSeverity.Info,
                        kind = IssueKind.LayerWeight,
                        message = L.Tr(
                            "Layer '{0}' has default weight 0; it has no effect until its weight is raised at runtime.",
                            layer.name),
                        context = controller,
                        layerIndex = i,
                    });
            }
        }

        static void AddMissingBehaviourIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            AnalyzerIssue Make(string message, Object context, System.Action fix) => new AnalyzerIssue
            {
                severity = IssueSeverity.Error,
                kind = IssueKind.MissingBehaviour,
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
        static void AddDirectBlendTreeIssues(AnimatorController controller, List<AnalyzerIssue> issues)
        {
            foreach (var s in controller.AllStates())
            {
                if (s.writeDefaultValues || !(s.motion is BlendTree tree) || !ContainsDirectTree(tree))
                    continue;
                var state = s;
                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Error,
                    kind = IssueKind.DirectBlendTree,
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
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Warning,
                            kind = IssueKind.DirectBlendTree,
                            message = L.Tr("Direct blend tree '{0}' has a child with no weight parameter; that child never plays.", bt.name),
                            context = bt,
                        });
                    }
                    else if (!paramTypes.TryGetValue(weight, out var type))
                    {
                        if (!reported.Add(weight)) continue;
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Error,
                            kind = IssueKind.DirectBlendTree,
                            message = L.Tr("Direct blend tree '{0}' weights a child with missing parameter '{1}'.", bt.name, weight),
                            context = bt,
                        });
                    }
                    else if (type != AnimatorControllerParameterType.Float)
                    {
                        if (!reported.Add(weight)) continue;
                        issues.Add(new AnalyzerIssue
                        {
                            severity = IssueSeverity.Warning,
                            kind = IssueKind.DirectBlendTree,
                            message = L.Tr("Weight parameter '{1}' of Direct blend tree '{0}' is not a Float.", bt.name, weight),
                            context = bt,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Parameter Drivers that touch a parameter animation writes (an AAP — see
        /// <see cref="AapWriteScan"/>). A driver lives outside the animation system: it
        /// cannot read the animated value, and anything it writes is overwritten by the
        /// blend tree on the same frame. Both directions are silent failures in game,
        /// which is exactly what an analyzer is for.
        ///
        /// Both sides of the pair are held to the same standard: neither a clip nor a driver
        /// counts unless the state carrying it can be entered. A finding is about two things
        /// meeting at runtime, so either one being unreachable means the meeting never happens.
        ///
        /// Warnings rather than errors because reachability is only half of "does this run".
        /// The other half is the layer's weight, and a weight-0 layer can be raised by a Layer
        /// Control behaviour in a controller this scan never sees — so a clip parked there
        /// still counts, and the finding is not certain enough to be an error.
        /// </summary>
        static void AddAapDriverIssues(AnimatorController controller,
            List<AapWriteScan.LayerWrites> aapWrites, List<AnalyzerIssue> issues)
        {
            var written = new HashSet<string>();
            foreach (var layer in aapWrites) written.UnionWith(layer.parameters);
            if (written.Count == 0) return;

            var live = CollectReachableStates(controller);

            // Collected per parameter and direction rather than per driver: one async-sync
            // layer holds a structurally identical driver on every send state, and a row
            // each would bury the rest of the report under the same sentence.
            var reads = new Dictionary<string, Hit>();
            var writes = new Dictionary<string, Hit>();
            foreach (var state in controller.AllStates())
            {
                if (!live.Contains(state)) continue;
                foreach (var behaviour in state.behaviours)
                    TallyAapDriver(behaviour, state, written, reads, writes);
            }
            foreach (var sm in controller.AllStateMachines())
            {
                // A state machine's behaviours run on entering it, which needs a state inside
                // it to be enterable.
                if (!HasReachableState(sm, live)) continue;
                foreach (var behaviour in sm.behaviours)
                    TallyAapDriver(behaviour, sm, written, reads, writes);
            }

            foreach (var name in SortedKeys(reads))
                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Warning,
                    kind = IssueKind.AapDriver,
                    message = L.Tr(
                        "Animation writes '{0}' (AAP), and {1} Parameter Driver entr(ies) copy from it. A driver can't read an animated value — the copy carries the animator's own, usually the default.",
                        name, reads[name].count),
                    context = reads[name].context,
                });
            foreach (var name in SortedKeys(writes))
                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Warning,
                    kind = IssueKind.AapDriver,
                    message = L.Tr(
                        "Animation writes '{0}' (AAP), and {1} Parameter Driver entr(ies) write it too. The blend tree overwrites the driver every frame, so the driver's value never sticks.",
                        name, writes[name].count),
                    context = writes[name].context,
                });
        }

        static bool HasReachableState(AnimatorStateMachine sm, HashSet<AnimatorState> live)
        {
            foreach (var descendant in sm.SelfAndDescendants())
                foreach (var cs in descendant.states)
                    if (cs.state != null && live.Contains(cs.state)) return true;
            return false;
        }

        /// <summary>
        /// One AAP written from more than one layer. Inside a Direct blend tree the children
        /// add up — that is the whole idiom — but layers do not: an Override layer replaces
        /// whatever the layers below it produced, so the lower gadget's output never leaves
        /// its own layer, at full weight, with nothing in the inspector saying so.
        ///
        /// Only layers that play by default are compared. A weight-0 layer holding a second
        /// writer is the normal way to build a deliberate override, and Additive layers do add
        /// up, so neither is a conflict worth a row.
        /// </summary>
        static void AddAapLayerIssues(AnimatorController controller,
            List<AapWriteScan.LayerWrites> aapWrites, List<AnalyzerIssue> issues)
        {
            var byParameter = new Dictionary<string, List<AapWriteScan.LayerWrites>>();
            foreach (var layer in aapWrites)
            {
                if (layer.blendingMode != AnimatorLayerBlendingMode.Override) continue;
                // The base layer's weight is forced to 1 at runtime whatever the field says.
                if (layer.layerIndex > 0 && layer.defaultWeight <= 0f) continue;
                foreach (var name in layer.parameters)
                {
                    if (!byParameter.TryGetValue(name, out var writers))
                        byParameter[name] = writers = new List<AapWriteScan.LayerWrites>();
                    writers.Add(layer);
                }
            }

            var names = new List<string>(byParameter.Keys);
            names.Sort(System.StringComparer.Ordinal);
            foreach (var name in names)
            {
                var writers = byParameter[name];
                if (writers.Count < 2) continue;
                var winner = writers[writers.Count - 1];   // CollectByLayer comes back in layer order
                var labels = new List<string>();
                foreach (var writer in writers) labels.Add("'" + writer.layerName + "'");
                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Warning,
                    kind = IssueKind.AapLayers,
                    message = L.Tr(
                        "Animation writes '{0}' (AAP) on {1} layers: {2}. Layers replace one another instead of adding up, so only the last one ('{3}') reaches the parameter.",
                        name, writers.Count, string.Join(", ", labels), winner.layerName),
                    context = controller,
                    layerIndex = winner.layerIndex,
                });
            }
        }

        /// <summary>How many driver entries hit one parameter, and the first place to jump to.</summary>
        class Hit
        {
            public int count;
            public Object context;
        }

        static void TallyAapDriver(StateMachineBehaviour behaviour, Object owner,
            HashSet<string> written, Dictionary<string, Hit> reads, Dictionary<string, Hit> writes)
        {
            if (!VrcParameterDriver.Is(behaviour)) return;
            foreach (var entry in VrcParameterDriver.ReadSpec(behaviour).entries)
            {
                // `source` only means something on a Copy entry (kind 3); the other kinds
                // may carry a stale clone value there.
                if (entry.kind == 3 && written.Contains(entry.source ?? string.Empty))
                    Count(reads, entry.source, owner);
                if (written.Contains(entry.name ?? string.Empty))
                    Count(writes, entry.name, owner);
            }
        }

        static void Count(Dictionary<string, Hit> into, string name, Object context)
        {
            if (!into.TryGetValue(name, out var hit))
                into[name] = hit = new Hit { context = context };
            hit.count++;
        }

        /// <summary>Report order must not depend on hash iteration order — the analyzer list
        /// is compared between runs by eye.</summary>
        static List<string> SortedKeys(Dictionary<string, Hit> hits)
        {
            var names = new List<string>(hits.Keys);
            names.Sort(System.StringComparer.Ordinal);
            return names;
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
        public static List<AnalyzerIssue> FindTerminalStateGroups(AnimatorControllerLayer layer)
        {
            var issues = new List<AnalyzerIssue>();
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

            var sccOf = StronglyConnectedComponents.Compute(edges, out int sccCount);

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
                issues.Add(new AnalyzerIssue
                {
                    severity = IssueSeverity.Info,
                    kind = IssueKind.TerminalStates,
                    message = L.Tr("Layer '{0}': once entered, '{1}' can never be left (no outgoing transition or exit).",
                        layer.name, list),
                    context = context[c],
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
