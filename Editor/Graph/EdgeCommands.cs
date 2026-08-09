using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Creating, removing and rewiring transitions. Every end is a <see cref="TransitionEnd"/>
    /// rather than a graph node, so this runs headlessly; <see cref="GraphSync"/> keeps the
    /// wrappers that read the edge under the cursor and rebuild the graph afterwards.
    /// </summary>
    class EdgeCommands
    {
        readonly DaerDContext _context;

        public EdgeCommands(DaerDContext context)
        {
            _context = context;
        }

        // ---- create / remove -------------------------------------------------

        public AnimatorTransitionBase CreateTransition(TransitionEnd source, TransitionEnd destination)
        {
            var sm = _context.CurrentStateMachine;
            AnimatorTransitionBase created;
            using (new UndoScope("Create Transition"))
                created = CreateTransitionCore(source, destination, sm);
            return created;
        }

        /// <summary>
        /// Inner shared by single and batch transition creation. The caller is responsible for
        /// opening an <see cref="UndoScope"/> so the batch name (e.g. "Chain Transitions") wins
        /// over the per-pair undo label.
        /// </summary>
        AnimatorTransitionBase CreateTransitionCore(TransitionEnd source, TransitionEnd destination,
            AnimatorStateMachine sm)
        {
            AnimatorTransitionBase created = null;
            if (source.Kind == TransitionEndKind.State)
            {
                Undo.RegisterCompleteObjectUndo(source.State, "Create Transition");
                if (destination.Kind == TransitionEndKind.State) created = source.State.AddTransition(destination.State);
                else if (destination.Kind == TransitionEndKind.SubStateMachine) created = source.State.AddTransition(destination.StateMachine);
                else if (destination.Kind == TransitionEndKind.Exit) created = source.State.AddExitTransition();
            }
            else if (source.Kind == TransitionEndKind.AnyState)
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Transition");
                if (destination.Kind == TransitionEndKind.State) created = sm.AddAnyStateTransition(destination.State);
                else if (destination.Kind == TransitionEndKind.SubStateMachine) created = sm.AddAnyStateTransition(destination.StateMachine);
            }
            else if (source.Kind == TransitionEndKind.Entry)
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Transition");
                if (destination.Kind == TransitionEndKind.State) created = sm.AddEntryTransition(destination.State);
                else if (destination.Kind == TransitionEndKind.SubStateMachine) created = sm.AddEntryTransition(destination.StateMachine);
            }
            else if (source.Kind == TransitionEndKind.SubStateMachine)
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Transition");
                if (destination.Kind == TransitionEndKind.State) created = sm.AddStateMachineTransition(source.StateMachine, destination.State);
                else if (destination.Kind == TransitionEndKind.SubStateMachine) created = sm.AddStateMachineTransition(source.StateMachine, destination.StateMachine);
                else if (destination.Kind == TransitionEndKind.Exit) created = sm.AddStateMachineExitTransition(source.StateMachine);
            }

            if (created is AnimatorStateTransition newStateTransition)
                DaerDSettings.ApplyTransitionDefaultsTo(newStateTransition);

            if (created != null && _context.Controller != null)
                EditorUtility.SetDirty(_context.Controller);
            return created;
        }

        public static void RemoveTransitionFrom(TransitionEnd source, AnimatorTransitionBase t, AnimatorStateMachine sm)
        {
            if (t == null) return;
            if (source.Kind == TransitionEndKind.State && source.State != null && t is AnimatorStateTransition stateTransition)
                source.State.RemoveTransition(stateTransition);
            else if (source.Kind == TransitionEndKind.AnyState && t is AnimatorStateTransition anyTransition)
                sm.RemoveAnyStateTransition(anyTransition);
            else if (source.Kind == TransitionEndKind.Entry && t is AnimatorTransition entryTransition)
                sm.RemoveEntryTransition(entryTransition);
            else if (source.Kind == TransitionEndKind.SubStateMachine && t is AnimatorTransition smTransition)
                sm.RemoveStateMachineTransition(source.StateMachine, smTransition);
        }

        // ---- reverse / redirect / replicate ----------------------------------

        /// <summary>
        /// Recreates every given transition running from destination back to source. Returns null
        /// when there is no state machine to edit — nothing happened, so the caller skips its
        /// rebuild; an empty list means the work ran but produced nothing.
        /// </summary>
        public List<AnimatorTransitionBase> Reverse(TransitionEnd source, TransitionEnd destination,
            IList<AnimatorTransitionBase> transitions)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;

            var snapshots = new List<TransitionClipboard.Snapshot>();
            foreach (var t in transitions)
                if (t != null) snapshots.Add(TransitionClipboard.Capture(t));
            var originals = new List<AnimatorTransitionBase>(transitions);

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Reverse Transition"))
            {
                RegisterRemoveUndo(source, sm, "Reverse Transition");
                foreach (var t in originals)
                    RemoveTransitionFrom(source, t, sm);

                foreach (var snap in snapshots)
                {
                    var t = CreateTransition(destination, source);
                    if (t != null)
                    {
                        TransitionClipboard.Apply(t, snap);
                        created.Add(t);
                    }
                }
                EditorUtility.SetDirty(sm);
            }
            return created;
        }

        /// <summary>Points every given transition at a new destination.</summary>
        public void Redirect(IEnumerable<AnimatorTransitionBase> transitions, TransitionEnd newDestination)
        {
            using (new UndoScope("Redirect Transition"))
            {
                foreach (var t in transitions)
                {
                    if (t == null) continue;
                    Undo.RegisterCompleteObjectUndo(t, "Redirect Transition");
                    AssignDestination(t, newDestination);
                    EditorUtility.SetDirty(t);
                }
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }
        }

        /// <summary>Adds a duplicate of every given transition alongside the originals.</summary>
        public List<AnimatorTransitionBase> Replicate(TransitionEnd source, TransitionEnd destination,
            IList<AnimatorTransitionBase> transitions)
        {
            var snapshots = new List<TransitionClipboard.Snapshot>();
            foreach (var t in transitions)
                if (t != null) snapshots.Add(TransitionClipboard.Capture(t));

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Replicate Transition"))
            {
                foreach (var snap in snapshots)
                {
                    var t = CreateTransition(source, destination);
                    if (t != null)
                    {
                        TransitionClipboard.Apply(t, snap);
                        created.Add(t);
                    }
                }
            }
            return created;
        }

        static void AssignDestination(AnimatorTransitionBase transition, TransitionEnd destination)
        {
            switch (destination.Kind)
            {
                case TransitionEndKind.State:
                    transition.destinationStateMachine = null;
                    transition.isExit = false;
                    transition.destinationState = destination.State;
                    break;
                case TransitionEndKind.SubStateMachine:
                    transition.destinationState = null;
                    transition.isExit = false;
                    transition.destinationStateMachine = destination.StateMachine;
                    break;
                case TransitionEndKind.Exit:
                    transition.destinationState = null;
                    transition.destinationStateMachine = null;
                    transition.isExit = true;
                    break;
            }
        }

        static void RegisterRemoveUndo(TransitionEnd source, AnimatorStateMachine sm, string name)
        {
            Undo.RegisterCompleteObjectUndo(sm, name);
            if (source.Kind == TransitionEndKind.State && source.State != null)
                Undo.RegisterCompleteObjectUndo(source.State, name);
        }

        // ---- chain / fan transitions --------------------------------------------

        public List<AnimatorTransitionBase> Chain(IList<TransitionEnd> nodes, bool seeded)
        {
            var created = new List<AnimatorTransitionBase>();
            if (nodes == null || nodes.Count < 2) return created;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return created;
            using (new UndoScope("Chain Transitions"))
            {
                for (int i = 0; i < nodes.Count - 1; i++)
                    AddBatchTransition(nodes[i], nodes[i + 1], sm, created);
                if (seeded) SeedCreated(created);
            }
            return created;
        }

        public List<AnimatorTransitionBase> FanOut(TransitionEnd source, IEnumerable<TransitionEnd> targets, bool seeded)
        {
            var created = new List<AnimatorTransitionBase>();
            if (targets == null) return created;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return created;
            using (new UndoScope("Fan-Out Transitions"))
            {
                foreach (var target in targets)
                    AddBatchTransition(source, target, sm, created);
                if (seeded) SeedCreated(created);
            }
            return created;
        }

        public List<AnimatorTransitionBase> FanIn(IEnumerable<TransitionEnd> sources, TransitionEnd target, bool seeded)
        {
            var created = new List<AnimatorTransitionBase>();
            if (sources == null) return created;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return created;
            using (new UndoScope("Fan-In Transitions"))
            {
                foreach (var source in sources)
                    AddBatchTransition(source, target, sm, created);
                if (seeded) SeedCreated(created);
            }
            return created;
        }

        public List<AnimatorTransitionBase> CrossProduct(IList<TransitionEnd> sources, IList<TransitionEnd> targets,
            bool seeded)
        {
            var created = new List<AnimatorTransitionBase>();
            if (sources == null || targets == null || sources.Count == 0 || targets.Count == 0) return created;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return created;
            using (new UndoScope("Multi Transition"))
            {
                foreach (var source in sources)
                    foreach (var target in targets)
                        AddBatchTransition(source, target, sm, created);
                if (seeded) SeedCreated(created);
            }
            return created;
        }

        /// <summary>Seeded batch creation: the first copied transition's settings and
        /// conditions stamp every created transition.</summary>
        static void SeedCreated(List<AnimatorTransitionBase> created)
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            foreach (var transition in created)
                TransitionClipboard.Apply(transition, snapshot);
        }

        /// <summary>
        /// One step of a chain / fan / cross batch. Skips invalid pairs (per
        /// <see cref="TransitionEnd.CanConnect"/>) and self-loops on plain states, so
        /// overlapping selections never produce nonsense transitions.
        /// </summary>
        void AddBatchTransition(TransitionEnd source, TransitionEnd destination, AnimatorStateMachine sm,
            List<AnimatorTransitionBase> created)
        {
            if (source.Kind == TransitionEndKind.None || destination.Kind == TransitionEndKind.None) return;
            if (source.SameAs(destination)) return;
            if (!TransitionEnd.CanConnect(source, destination)) return;
            var transition = CreateTransitionCore(source, destination, sm);
            if (transition != null) created.Add(transition);
        }
    }
}
