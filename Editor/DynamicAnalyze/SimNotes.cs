using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What this run cannot promise about THIS controller, said before it is read rather than
    /// discovered afterwards.
    ///
    /// A simulator that is wrong is a nuisance; a simulator that is silently wrong is worse
    /// than none, because every answer it gives is then worth less than it looks. Each of
    /// these is a place where the run and a headset are known to part company, and naming them
    /// turns a silent divergence into a stated assumption — which is the difference between a
    /// result and a guess.
    ///
    /// Pure: reads the controller and writes nothing, so the window can ask on every repaint
    /// and a test can ask without running anything.
    /// </summary>
    static class SimNotes
    {
        /// <param name="withRemote">Whether this run has somebody else in it. A divergence
        /// about what the other person reads is not worth saying to a run that has no other
        /// person — and a note nobody can act on is how a list of them stops being read.</param>
        public static List<string> For(AnimatorController controller, bool withRemote = true)
        {
            var notes = new List<string>();
            if (controller == null) return notes;
            EntryConditions(controller, notes);
            SelfTransitions(controller, notes);
            CrossLayerDrivers(controller, notes);
            if (withRemote) Playspace(controller, notes);
            Behaviours(controller, notes);
            return notes;
        }

        /// <summary>
        /// The built-ins whose two copies are meant to disagree. VRChat puts the locomotion
        /// values on the IK channel — this run carries them there too, ten times a second and
        /// interpolated, which is right — but playspace movement is counted on somebody else's
        /// copy of you and not on your own, so a headset shows the wearer and the remote
        /// reading different numbers from the same motion. Nothing here can produce that: a
        /// channel carries a value correctly or not at all, and this is the one shape where
        /// carrying it correctly is the divergence.
        /// </summary>
        static void Playspace(AnimatorController controller, List<string> notes)
        {
            var names = new List<string>();
            foreach (var parameter in controller.parameters)
                if (VrcParameters.PlayspaceDiffers(parameter.name)) names.Add(parameter.name);
            if (names.Count == 0) return;
            notes.Add(L.Tr(
                "{0} locomotion parameter(s) carry the wearer's own number to the other person here ({1}). On a headset the other person's copy counts playspace movement and the wearer's own does not, so those two numbers are not meant to agree.",
                names.Count, Join(names)));
        }

        /// <summary>
        /// The big one. A conditional entry transition is not taken here — every layer that has
        /// one begins in its default state instead — and a controller that splits the wearer
        /// from a remote at Entry therefore runs down one side of itself for the whole session.
        /// Nothing about the result would look wrong; it would just be answering a different
        /// question.
        /// </summary>
        static void EntryConditions(AnimatorController controller, List<string> notes)
        {
            var layers = new List<string>();
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine == null) continue;
                bool conditioned = false;
                foreach (var machine in layer.stateMachine.SelfAndDescendants())
                    foreach (var entry in machine.entryTransitions)
                        if (entry != null && entry.conditions != null && entry.conditions.Length > 0)
                            conditioned = true;
                if (conditioned) layers.Add(layer.name);
            }
            if (layers.Count == 0) return;
            notes.Add(L.Tr(
                "{0} layer(s) choose where to begin with a condition on Entry ({1}). This run does not take those routes — each of those layers starts in its default state, whatever the condition says.",
                layers.Count, Join(layers)));
        }

        /// <summary>A state that can be entered while it is already the current one is
        /// re-entered on a headset, drivers and all. Here the state never changes, so nothing
        /// notices. Only a route back to the state itself counts — an ordinary transition
        /// carries canTransitionToSelf too, and it means nothing there.</summary>
        static void SelfTransitions(AnimatorController controller, List<string> notes)
        {
            var states = new List<string>();
            void Note(AnimatorState state)
            {
                if (state != null && HasDriver(state) && !states.Contains(state.name))
                    states.Add(state.name);
            }

            foreach (var machine in controller.AllStateMachines())
            {
                foreach (var transition in machine.anyStateTransitions)
                    if (transition != null && transition.canTransitionToSelf)
                        Note(transition.destinationState);
                foreach (var child in machine.states)
                {
                    if (child.state == null) continue;
                    foreach (var transition in child.state.transitions)
                        if (transition != null && transition.destinationState == child.state)
                            Note(child.state);
                }
            }
            if (states.Count == 0) return;
            notes.Add(L.Tr(
                "{0} state(s) with a Parameter Driver can be entered from themselves ({1}). This run cannot see a state re-entered, so those drivers fire once instead of every time.",
                states.Count, Join(states)));
        }

        /// <summary>
        /// The remaining timing divergence, and only when a controller actually depends on it:
        /// a driver in one layer writing something another layer's transitions read. A headset
        /// serves both inside one frame; here the write lands after the frame, so the chain
        /// takes a frame per link.
        /// </summary>
        static void CrossLayerDrivers(AnimatorController controller, List<string> notes)
        {
            var written = new Dictionary<string, int>();
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].stateMachine == null) continue;
                foreach (var machine in layers[i].stateMachine.SelfAndDescendants())
                    foreach (var child in machine.states)
                    {
                        if (child.state == null) continue;
                        foreach (var behaviour in child.state.behaviours)
                        {
                            if (!VrcParameterDriver.Is(behaviour)) continue;
                            foreach (var entry in VrcParameterDriver.ReadSpec(behaviour).entries)
                                if (!string.IsNullOrEmpty(entry.name))
                                    written[entry.name] = i;
                        }
                    }
            }
            if (written.Count == 0) return;

            var crossed = new List<string>();
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].stateMachine == null) continue;
                foreach (var machine in layers[i].stateMachine.SelfAndDescendants())
                    foreach (var transition in Transitions(machine))
                        foreach (var condition in transition.conditions)
                            if (written.TryGetValue(condition.parameter, out int from)
                                && from != i && !crossed.Contains(condition.parameter))
                                crossed.Add(condition.parameter);
            }
            if (crossed.Count == 0) return;
            notes.Add(L.Tr(
                "{0} parameter(s) are driven in one layer and read by another's transitions ({1}). A driver's write reaches the other layer on the next frame here, and inside the same one on a headset.",
                crossed.Count, Join(crossed)));
        }

        /// <summary>
        /// The behaviours nobody here runs. Split in two, because only one of them can change
        /// what a run records: a layer's weight scales what that layer's animation contributes,
        /// including the parameters it writes.
        /// </summary>
        static void Behaviours(AnimatorController controller, List<string> notes)
        {
            var counted = new Dictionary<string, int>();
            foreach (var behaviour in controller.AllBehaviours())
            {
                if (behaviour == null) continue;
                string name = behaviour.GetType().Name;
                if (name == VrcBehaviours.ParameterDriver || !VrcBehaviours.IsVrcType(name))
                    continue;
                counted.TryGetValue(name, out int seen);
                counted[name] = seen + 1;
            }
            if (counted.Count == 0) return;

            if (counted.TryGetValue(VrcBehaviours.LayerControl, out int weights))
                notes.Add(L.Tr(
                    "{0} Animator Layer Control(s) are not run here. A layer's weight scales everything it writes, so a run can report values a headset would not.",
                    weights));

            var quiet = new List<string>();
            foreach (var pair in counted)
                if (pair.Key != VrcBehaviours.LayerControl)
                    quiet.Add(pair.Key + " ×" + pair.Value);
            if (quiet.Count > 0)
                notes.Add(L.Tr(
                    "Not run here, and nothing recorded depends on them: {0}. They reach the player rather than the controller — so a run cannot show tracking left off, only that it was never turned back on in the parameters.",
                    Join(quiet)));
        }

        static IEnumerable<AnimatorStateTransition> Transitions(AnimatorStateMachine machine)
        {
            foreach (var transition in machine.anyStateTransitions)
                if (transition != null) yield return transition;
            foreach (var child in machine.states)
            {
                if (child.state == null) continue;
                foreach (var transition in child.state.transitions)
                    if (transition != null) yield return transition;
            }
        }

        static bool HasDriver(AnimatorState state)
        {
            foreach (var behaviour in state.behaviours)
                if (VrcParameterDriver.Is(behaviour)) return true;
            return false;
        }

        /// <summary>Names, with a tail rather than a wall of them.</summary>
        static string Join(List<string> names)
        {
            const int shown = 3;
            if (names.Count <= shown) return string.Join(", ", names.ToArray());
            var head = names.GetRange(0, shown);
            return string.Join(", ", head.ToArray())
                + L.Tr(" and {0} more", names.Count - shown);
        }
    }
}
