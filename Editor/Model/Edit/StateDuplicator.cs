using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Edit
{
    /// <summary>
    /// Duplicates states in place inside one state machine (Ctrl+D in the graph). Copies every
    /// state field plus the transitions whose source and destination are both in the duplicated
    /// set, without touching the shared copy/paste clipboards.
    /// </summary>
    static class StateDuplicator
    {
        /// <summary>
        /// Creates a copy of each state, offset from the original, and returns the copies in the
        /// same order as <paramref name="states"/>. Motions are shared by reference.
        /// </summary>
        public static List<AnimatorState> Duplicate(AnimatorStateMachine sm, IList<AnimatorState> states,
            Vector2 offset)
        {
            var created = new List<AnimatorState>();
            if (sm == null || states == null || states.Count == 0) return created;

            var positions = new Dictionary<AnimatorState, Vector3>();
            foreach (var cs in sm.states)
                if (cs.state != null) positions[cs.state] = cs.position;

            using (new UndoScope("Duplicate States"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Duplicate States");

                var copyOf = new Dictionary<AnimatorState, AnimatorState>();
                foreach (var source in states)
                {
                    // Skip states that don't belong to this state machine (or were destroyed).
                    if (source == null || !positions.TryGetValue(source, out var position)) continue;
                    var copy = sm.AddState(MakeUniqueName(sm, source.name),
                        position + new Vector3(offset.x, offset.y, 0f));
                    StateMachineCloner.CopyStateFields(source, copy);
                    copyOf[source] = copy;
                    created.Add(copy);
                }

                foreach (var pair in copyOf)
                    foreach (var t in pair.Key.transitions)
                        if (t.destinationState != null && copyOf.TryGetValue(t.destinationState, out var destination))
                        {
                            var transition = pair.Value.AddTransition(destination);
                            TransitionClipboard.Apply(transition, TransitionClipboard.Capture(t));
                        }

                if (created.Count > 0)
                    EditorUtility.SetDirty(sm);
            }
            return created;
        }

        /// <summary>A name not yet used by any state or sub-state machine directly inside <paramref name="sm"/>.</summary>
        public static string MakeUniqueName(AnimatorStateMachine sm, string baseName)
        {
            var taken = new HashSet<string>();
            foreach (var cs in sm.states)
                if (cs.state != null) taken.Add(cs.state.name);
            foreach (var cm in sm.stateMachines)
                if (cm.stateMachine != null) taken.Add(cm.stateMachine.name);
            if (!taken.Contains(baseName)) return baseName;
            int i = 1;
            while (taken.Contains(baseName + " " + i)) i++;
            return baseName + " " + i;
        }
    }
}
