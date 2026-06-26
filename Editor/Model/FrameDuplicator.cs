using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Duplicates a Frame, every state and note lying fully inside it, and the transitions whose
    /// source and destination are both in the duplicated state set. Transitions that cross the
    /// frame's edge (one endpoint outside) are dropped so the copy is self-contained.
    /// </summary>
    static class FrameDuplicator
    {
        // Mirrors the per-state Ctrl+D offset so the copy is visible but recognisably the same
        // group of nodes.
        static readonly Vector2 Offset = new Vector2(40f, 40f);

        public static GraphFrameData.Frame Duplicate(GraphFrameData data, AnimatorController controller,
            AnimatorStateMachine sm, GraphFrameData.Frame source,
            IList<AnimatorState> statesInside, IList<GraphFrameData.Note> notesInside)
        {
            if (data == null || sm == null || source == null) return null;

            GraphFrameData.Frame newFrame;
            using (new UndoScope("Duplicate Frame"))
            {
                Undo.RegisterCompleteObjectUndo(data, "Duplicate Frame");

                var newBounds = new Rect(source.bounds.x + Offset.x, source.bounds.y + Offset.y,
                    source.bounds.width, source.bounds.height);
                newFrame = new GraphFrameData.Frame
                {
                    title = MakeUniqueTitle(data, source.title),
                    bounds = newBounds,
                    color = source.color,
                    moveNodesWithFrame = source.moveNodesWithFrame,
                    // Locked state is reset — a freshly created duplicate should be editable so the
                    // user can drag/rename it; the original keeps its lock.
                    locked = false,
                    stateMachine = sm,
                };
                data.frames.Add(newFrame);

                if (statesInside != null && statesInside.Count > 0)
                    DuplicateStatesAndInternalTransitions(sm, controller, statesInside);

                if (notesInside != null)
                {
                    foreach (var note in notesInside)
                    {
                        if (note == null) continue;
                        var copyBounds = new Rect(note.bounds.x + Offset.x, note.bounds.y + Offset.y,
                            note.bounds.width, note.bounds.height);
                        data.notes.Add(new GraphFrameData.Note
                        {
                            text = note.text,
                            color = note.color,
                            fontSize = note.fontSize,
                            bounds = copyBounds,
                            stateMachine = sm,
                        });
                    }
                }

                EditorUtility.SetDirty(data);
                if (sm != null) EditorUtility.SetDirty(sm);
                if (controller != null) EditorUtility.SetDirty(controller);
            }
            return newFrame;
        }

        /// <summary>
        /// Adds an offset copy of each state in <paramref name="originals"/> to <paramref name="sm"/>
        /// and replicates the transitions touching the duplicated set: state→state internal
        /// transitions, state→Exit transitions, plus Entry→state and AnyState→state transitions
        /// targeting any duplicate. Entry/Exit/AnyState are singletons per state machine, so the
        /// duplicates point at the same special nodes as the originals. Returns the
        /// originals → copies map (for testability — the live caller only needs the side effect).
        /// </summary>
        public static Dictionary<AnimatorState, AnimatorState> DuplicateStatesAndInternalTransitions(
            AnimatorStateMachine sm, AnimatorController controller, IList<AnimatorState> originals)
        {
            var copyOf = new Dictionary<AnimatorState, AnimatorState>();
            if (sm == null || originals == null || originals.Count == 0) return copyOf;

            // Cache positions before adding any states, so the duplicates inherit the originals'
            // positions even if MakeUniqueName-driven re-ordering happens later.
            var positions = new Dictionary<AnimatorState, Vector3>();
            foreach (var cs in sm.states)
                if (cs.state != null) positions[cs.state] = cs.position;

            Undo.RegisterCompleteObjectUndo(sm, "Duplicate Frame");

            // First pass: create the duplicated states themselves.
            foreach (var original in originals)
            {
                if (original == null || copyOf.ContainsKey(original)) continue;
                if (!positions.TryGetValue(original, out var position)) continue;

                var copy = sm.AddState(StateDuplicator.MakeUniqueName(sm, original.name),
                    position + new Vector3(Offset.x, Offset.y, 0f));
                StateMachineCloner.CopyStateFields(original, copy);
                copyOf[original] = copy;
            }

            // Second pass: walk each original's outgoing transitions and reproduce them on the
            // duplicate. We register undo on each duplicate before mutating its transitions list
            // and snapshot every relevant setting via the existing TransitionClipboard helpers so
            // the copies carry conditions, exit time, duration, mute/solo, etc.
            foreach (var pair in copyOf)
            {
                var originalState = pair.Key;
                var copyState = pair.Value;
                Undo.RegisterCompleteObjectUndo(copyState, "Duplicate Frame");

                foreach (var t in originalState.transitions)
                {
                    if (t == null) continue;

                    AnimatorStateTransition newTransition = null;
                    if (t.isExit)
                    {
                        // state → Exit. Exit is a singleton per state machine, so the duplicate
                        // points at the same Exit node — the copy keeps the same outgoing-to-Exit
                        // behaviour the original had.
                        newTransition = copyState.AddExitTransition();
                    }
                    else if (t.destinationState != null
                        && copyOf.TryGetValue(t.destinationState, out var destinationCopy))
                    {
                        newTransition = copyState.AddTransition(destinationCopy);
                    }
                    // Transitions to a sub-state machine (or to a state outside the duplicated
                    // set) are intentionally dropped — the copy is meant to be self-contained.

                    if (newTransition == null) continue;
                    TransitionClipboard.Apply(newTransition, TransitionClipboard.Capture(t));
                    EditorUtility.SetDirty(newTransition);
                }

                EditorUtility.SetDirty(copyState);
            }

            // Third pass: Entry → state and AnyState → state transitions whose destination is
            // any duplicated state get cloned to point at the duplicate. This keeps the new
            // states reachable from Entry / AnyState the same way the originals were.
            foreach (var t in sm.entryTransitions)
            {
                if (t == null || t.destinationState == null) continue;
                if (!copyOf.TryGetValue(t.destinationState, out var destinationCopy)) continue;
                var newTransition = sm.AddEntryTransition(destinationCopy);
                if (newTransition == null) continue;
                TransitionClipboard.Apply(newTransition, TransitionClipboard.Capture(t));
                EditorUtility.SetDirty(newTransition);
            }

            foreach (var t in sm.anyStateTransitions)
            {
                if (t == null || t.destinationState == null) continue;
                if (!copyOf.TryGetValue(t.destinationState, out var destinationCopy)) continue;
                var newTransition = sm.AddAnyStateTransition(destinationCopy);
                if (newTransition == null) continue;
                TransitionClipboard.Apply(newTransition, TransitionClipboard.Capture(t));
                EditorUtility.SetDirty(newTransition);
            }

            return copyOf;
        }

        static string MakeUniqueTitle(GraphFrameData data, string baseTitle)
        {
            if (data == null) return baseTitle;
            var taken = new HashSet<string>();
            foreach (var f in data.frames)
                if (f != null) taken.Add(f.title);
            if (!taken.Contains(baseTitle)) return baseTitle;
            int i = 1;
            while (taken.Contains(baseTitle + " " + i)) i++;
            return baseTitle + " " + i;
        }
    }
}
