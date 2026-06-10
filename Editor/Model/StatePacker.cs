using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Moves states between a state machine and a nested sub-state machine without recreating
    /// them: membership lives in the <c>states</c> arrays, so reassigning those arrays moves the
    /// states while every transition object stays intact (cross-level transitions are valid in
    /// Unity's animator).
    /// </summary>
    static class StatePacker
    {
        /// <summary>
        /// Packs the given states of <paramref name="parent"/> into a new sub-state machine placed
        /// at their centroid. Transitions are left untouched — ones crossing the new boundary keep
        /// working as cross-level transitions. Returns the new sub-state machine, or null.
        /// </summary>
        public static AnimatorStateMachine Pack(AnimatorStateMachine parent, IList<AnimatorState> states,
            string name = "Packed States")
        {
            if (parent == null || states == null) return null;

            var packed = new HashSet<AnimatorState>();
            foreach (var s in states)
                if (s != null) packed.Add(s);
            if (packed.Count == 0) return null;

            var parentStates = parent.states;
            var keep = new List<ChildAnimatorState>();
            var move = new List<ChildAnimatorState>();
            foreach (var child in parentStates)
            {
                if (child.state != null && packed.Contains(child.state)) move.Add(child);
                else keep.Add(child);
            }
            if (move.Count == 0) return null;

            // Centroid of the packed nodes becomes the sub-state machine's node position.
            var centroid = Vector3.zero;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var child in move)
            {
                centroid += child.position;
                min = Vector2.Min(min, new Vector2(child.position.x, child.position.y));
            }
            centroid /= move.Count;

            AnimatorStateMachine child2;
            using (new UndoScope("Pack Into Sub-State Machine"))
            {
                Undo.RegisterCompleteObjectUndo(parent, "Pack Into Sub-State Machine");
                child2 = parent.AddStateMachine(StateDuplicator.MakeUniqueName(parent, name), centroid);
                Undo.RegisterCompleteObjectUndo(child2, "Pack Into Sub-State Machine");

                // Re-base the packed nodes near the child graph's origin so they don't end up
                // far off-screen, preserving their relative layout.
                var rebased = new List<ChildAnimatorState>(move.Count);
                foreach (var c in move)
                {
                    var copy = c;
                    copy.position = c.position - new Vector3(min.x, min.y, 0f) + new Vector3(80f, 80f, 0f);
                    rebased.Add(copy);
                }

                bool packedDefault = parent.defaultState != null && packed.Contains(parent.defaultState);
                var oldDefault = parent.defaultState;

                parent.states = keep.ToArray();
                child2.states = rebased.ToArray();

                // The packed set keeps its own entry point; the parent falls back to a remaining state.
                child2.defaultState = packedDefault ? oldDefault : rebased[0].state;
                if (packedDefault)
                    parent.defaultState = keep.Count > 0 ? keep[0].state : null;

                EditorUtility.SetDirty(parent);
                EditorUtility.SetDirty(child2);
            }
            return child2;
        }

        /// <summary>
        /// Dissolves <paramref name="child"/> back into <paramref name="parent"/>: its states and
        /// nested sub-state machines move up (keeping their relative layout around the child's node
        /// position), transitions that targeted the child are re-pointed at its default state, and
        /// its Any State transitions are recreated on the parent. Returns warnings about semantics
        /// that could not be preserved exactly.
        /// </summary>
        public static List<string> Unpack(AnimatorStateMachine parent, AnimatorStateMachine child,
            AnimatorController controller)
        {
            var warnings = new List<string>();
            if (parent == null || child == null) return warnings;

            ChildAnimatorStateMachine childEntry = default;
            bool found = false;
            foreach (var cm in parent.stateMachines)
                if (cm.stateMachine == child) { childEntry = cm; found = true; break; }
            if (!found) return warnings;

            using (new UndoScope("Unpack Sub-State Machine"))
            {
                Undo.RegisterCompleteObjectUndo(parent, "Unpack Sub-State Machine");
                Undo.RegisterCompleteObjectUndo(child, "Unpack Sub-State Machine");

                var movedStates = child.states;
                var movedMachines = child.stateMachines;
                var defaultState = child.defaultState;

                // Place the unpacked nodes around the child's node position, keeping their layout.
                var centroid = Vector3.zero;
                int count = 0;
                foreach (var c in movedStates) { centroid += c.position; count++; }
                foreach (var c in movedMachines) { centroid += c.position; count++; }
                if (count > 0) centroid /= count;
                var offset = childEntry.position - centroid;

                var parentStates = new List<ChildAnimatorState>(parent.states);
                foreach (var c in movedStates)
                {
                    var copy = c;
                    copy.position += offset;
                    parentStates.Add(copy);
                }
                // The child itself stays in the list for now: RemoveStateMachine below both
                // destroys it and cleans up the parent-level transitions attached to it.
                var parentMachines = new List<ChildAnimatorStateMachine>(parent.stateMachines);
                foreach (var cm in movedMachines)
                {
                    var copy = cm;
                    copy.position += offset;
                    parentMachines.Add(copy);
                }

                // Where did leaving the child lead? With exactly one unambiguous target we can
                // rewrite the child's exit transitions to it; otherwise they keep exiting (now the
                // parent), which is close but not identical.
                var exitTargets = parent.GetStateMachineTransitions(child);
                AnimatorTransition soleExit = exitTargets.Length == 1 ? exitTargets[0] : null;

                // Detach content before removing the (now empty) child so nothing is destroyed.
                child.states = new ChildAnimatorState[0];
                child.stateMachines = new ChildAnimatorStateMachine[0];
                parent.states = parentStates.ToArray();
                parent.stateMachines = parentMachines.ToArray();

                if (parent.defaultState == null && defaultState != null)
                    parent.defaultState = defaultState;

                // Any State rules defined inside the child applied to the whole layer; keep them.
                foreach (var t in child.anyStateTransitions)
                {
                    if (t == null) continue;
                    AnimatorStateTransition created = null;
                    if (t.destinationState != null) created = parent.AddAnyStateTransition(t.destinationState);
                    else if (t.destinationStateMachine != null) created = parent.AddAnyStateTransition(t.destinationStateMachine);
                    if (created != null) TransitionClipboard.Apply(created, TransitionClipboard.Capture(t));
                }

                // Re-point every transition in the controller that targeted the child machine.
                if (controller != null)
                {
                    foreach (var t in controller.AllTransitions())
                    {
                        if (t == null || t.destinationStateMachine != child) continue;
                        if (defaultState == null) continue;
                        Undo.RegisterCompleteObjectUndo(t, "Unpack Sub-State Machine");
                        t.destinationStateMachine = null;
                        t.destinationState = defaultState;
                        EditorUtility.SetDirty(t);
                    }
                    if (defaultState == null)
                        warnings.Add("Transitions into '" + child.name + "' could not be re-pointed (it had no default state).");
                }

                // Exit transitions inside the moved states: rewrite when the child had a single,
                // unambiguous exit target.
                foreach (var c in movedStates)
                {
                    if (c.state == null) continue;
                    foreach (var t in c.state.transitions)
                    {
                        if (t == null || !t.isExit) continue;
                        if (soleExit != null && soleExit.destinationState != null)
                        {
                            Undo.RegisterCompleteObjectUndo(t, "Unpack Sub-State Machine");
                            t.isExit = false;
                            t.destinationState = soleExit.destinationState;
                            EditorUtility.SetDirty(t);
                        }
                        else
                        {
                            warnings.Add("State '" + c.state.name + "' kept its Exit transition; it now exits '" +
                                         parent.name + "' instead of '" + child.name + "'.");
                        }
                    }
                }

                parent.RemoveStateMachine(child);
                EditorUtility.SetDirty(parent);
            }
            return warnings;
        }
    }
}
