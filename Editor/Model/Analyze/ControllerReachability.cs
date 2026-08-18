using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// Which states of a layer the animator can actually end up in. "Has an incoming
    /// transition" is not the same question: a cluster of states that only point at each
    /// other is fully wired and still unreachable, because nothing outside it ever leads in.
    /// This walks forward from where the layer really starts instead.
    ///
    /// The walk follows Unity's own entry rules — Entry picks among its transitions and falls
    /// back to the default state, a transition may target a sub-state machine (which enters
    /// through that machine's own Entry), Exit hands control to the transitions the parent
    /// draws out of the sub-machine and rises further when there are none, and the root's Exit
    /// simply restarts the layer at Entry. Any State is scoped to the machine that owns it, so
    /// its destinations only count once that machine can be active.
    ///
    /// Everything reported from here is about what a state *can* do, never about what it will
    /// do: conditions are not evaluated, so a transition nothing ever satisfies still counts as
    /// a way in. That direction is deliberate — the analyzer may miss a dead state, but it must
    /// not call a live one dead.
    /// </summary>
    static class ControllerReachability
    {
        /// <summary>
        /// The states <paramref name="root"/> can enter; empty only when the layer holds no
        /// states. There is no such thing as a layer with states and no way in — a root with
        /// no state of its own adopts a sub-machine's default state as its own, and Unity puts
        /// it back if you clear it.
        /// </summary>
        public static HashSet<AnimatorState> ReachableStates(AnimatorStateMachine root) =>
            Walk(root, out _);

        /// <summary>
        /// Whether the layer can ever pass its own Exit and start over at Entry. Asked because
        /// a root's Entry conditions are only read on the way back through it — a layer begins
        /// at its default state whatever they say (measured; see the entry-condition probes in
        /// PlayModeProbeTests) — so a layer that never reaches Exit never reads them at all.
        ///
        /// Same walk as <see cref="ReachableStates"/>, which means the same deliberate
        /// blindness: conditions are not evaluated, so a route nothing ever satisfies still
        /// counts as a way to Exit. The error only runs one way. Answering "yes" suppresses the
        /// finding built on top of this, so wherever the walk is too generous — an Exit that
        /// bubbles up out of a sub-machine, say, whose effect on the root's Entry nobody has
        /// measured — the cost is a warning not raised, never a live Entry branch called dead.
        /// </summary>
        public static bool ReachesExit(AnimatorStateMachine root)
        {
            Walk(root, out bool reachesExit);
            return reachesExit;
        }

        static HashSet<AnimatorState> Walk(AnimatorStateMachine root, out bool reachesExit)
        {
            var reachable = new HashSet<AnimatorState>();
            bool passedRootExit = false;
            reachesExit = false;
            if (root == null) return reachable;

            var parent = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var owner = new Dictionary<AnimatorState, AnimatorStateMachine>();
            foreach (var sm in root.SelfAndDescendants())
            {
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null && child.stateMachine != root)
                        parent[child.stateMachine] = sm;
                foreach (var cs in sm.states)
                    if (cs.state != null && !owner.ContainsKey(cs.state))
                        owner[cs.state] = sm;
            }

            var entered = new HashSet<AnimatorStateMachine>();
            var active = new HashSet<AnimatorStateMachine>();
            var pending = new Queue<AnimatorState>();

            // The four mutually recursive steps below only recurse when they mark something
            // new, so the depth is bounded by the nesting of state machines. States never
            // recurse — they queue.
            void AddState(AnimatorState state)
            {
                if (state == null || !reachable.Add(state)) return;
                pending.Enqueue(state);
                if (owner.TryGetValue(state, out var home)) MarkActive(home);
            }

            void MarkActive(AnimatorStateMachine sm)
            {
                while (sm != null && active.Add(sm))
                {
                    foreach (var t in sm.anyStateTransitions) Follow(t, sm);
                    parent.TryGetValue(sm, out sm);
                }
            }

            void EnterMachine(AnimatorStateMachine sm)
            {
                if (sm == null || !entered.Add(sm)) return;
                MarkActive(sm);
                foreach (var t in sm.entryTransitions) Follow(t, sm);
                AddState(sm.defaultState);
            }

            void Follow(AnimatorTransitionBase t, AnimatorStateMachine context)
            {
                if (t == null) return;
                if (t.destinationState != null) AddState(t.destinationState);
                else if (t.destinationStateMachine != null) EnterMachine(t.destinationStateMachine);
                else if (t.isExit) ExitFrom(context);
            }

            void ExitFrom(AnimatorStateMachine sm)
            {
                if (sm == null || sm == root)
                {
                    passedRootExit = true;
                    EnterMachine(root);   // the layer starts over at Entry
                    return;
                }
                if (!parent.TryGetValue(sm, out var up)) return;
                var outgoing = up.GetStateMachineTransitions(sm);
                if (outgoing == null || outgoing.Length == 0) { ExitFrom(up); return; }
                foreach (var t in outgoing) Follow(t, up);
            }

            EnterMachine(root);
            while (pending.Count > 0)
            {
                var state = pending.Dequeue();
                owner.TryGetValue(state, out var home);
                foreach (var t in state.transitions) Follow(t, home ?? root);
            }
            reachesExit = passedRootExit;
            return reachable;
        }

        /// <summary>The state machine a layer plays: its own, or the source layer's when the
        /// layer is synced. Null when there is nothing to walk.</summary>
        public static AnimatorStateMachine PlayedMachine(AnimatorController controller, int layerIndex)
        {
            if (controller == null) return null;
            var layers = controller.layers;
            if (layerIndex < 0 || layerIndex >= layers.Length) return null;
            int source = layers[layerIndex].syncedLayerIndex;
            if (source < 0) return layers[layerIndex].stateMachine;
            return source < layers.Length ? layers[source].stateMachine : null;
        }
    }
}
