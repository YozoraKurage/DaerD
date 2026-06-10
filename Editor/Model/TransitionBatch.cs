using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Bulk transition creation between existing states: chain (A→B→C…), fan-out (one source to
    /// many destinations) and fan-in (many sources to one destination). New transitions receive
    /// the user's configured transition defaults. Self-transitions are skipped.
    /// </summary>
    static class TransitionBatch
    {
        /// <summary>Connects the states in order: states[0]→states[1]→…→states[n-1].</summary>
        public static List<AnimatorStateTransition> Chain(IList<AnimatorState> states)
        {
            var created = new List<AnimatorStateTransition>();
            if (states == null || states.Count < 2) return created;
            using (new UndoScope("Chain Transitions"))
            {
                for (int i = 0; i < states.Count - 1; i++)
                    Connect(states[i], states[i + 1], created);
            }
            return created;
        }

        /// <summary>Creates one transition from <paramref name="source"/> to every target.</summary>
        public static List<AnimatorStateTransition> FanOut(AnimatorState source, IEnumerable<AnimatorState> targets)
        {
            var created = new List<AnimatorStateTransition>();
            if (source == null || targets == null) return created;
            using (new UndoScope("Fan-Out Transitions"))
            {
                foreach (var target in targets)
                    Connect(source, target, created);
            }
            return created;
        }

        /// <summary>Creates one transition from every source to <paramref name="target"/>.</summary>
        public static List<AnimatorStateTransition> FanIn(IEnumerable<AnimatorState> sources, AnimatorState target)
        {
            var created = new List<AnimatorStateTransition>();
            if (sources == null || target == null) return created;
            using (new UndoScope("Fan-In Transitions"))
            {
                foreach (var source in sources)
                    Connect(source, target, created);
            }
            return created;
        }

        static void Connect(AnimatorState source, AnimatorState target, List<AnimatorStateTransition> created)
        {
            if (source == null || target == null || source == target) return;
            Undo.RegisterCompleteObjectUndo(source, "Create Transition");
            var transition = source.AddTransition(target);
            DaerDSettings.ApplyTransitionDefaultsTo(transition);
            EditorUtility.SetDirty(source);
            created.Add(transition);
        }
    }
}
