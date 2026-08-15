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
    /// A note only earns its place while it is true, and two of them turned out not to be. A
    /// conditional Entry at the top of a layer, and a driver in one layer read by another's
    /// transitions, are both things this run does exactly as Mecanim does them — measured in
    /// play mode and out of it by PlayModeProbeTests, which is where the evidence lives now
    /// that the sentences are gone. Warning about faithful behaviour is the same disservice as
    /// staying quiet about unfaithful behaviour: it teaches a reader to discount the list.
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
            SelfTransitions(controller, notes);
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
        /// A state entered from itself is entered again on a headset, drivers and all, and a
        /// run serves those drivers too — as long as the route back has a length. What it
        /// cannot serve is a route of no length: measured, a transition of duration 0 is over
        /// inside the step it starts on, so the layer ends that step in the state it was
        /// already in, aiming nowhere. No frame of the run carries any evidence that anything
        /// happened, which is why this is a note and not a bug — there is nothing to look at.
        ///
        /// Hence the duration test, which is what narrows this from "every state something can
        /// re-enter" to the shape that actually loses a drive. Only a route back to the state
        /// itself counts, and an ordinary transition carries canTransitionToSelf too, where it
        /// means nothing.
        ///
        /// One case slips past the filter in the other direction, and is left unsaid on
        /// purpose: a blended self transition taken again before the previous blend finishes
        /// counts as one entry rather than two (see SimClient's _served). Saying so would mean
        /// naming every driven state with a blended self route — the whole width this note just
        /// shed — to warn about a case that needs two presses inside one blend, and a reader
        /// who is told about everything is being told about nothing.
        /// </summary>
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
                    if (transition != null && transition.canTransitionToSelf && Snaps(transition))
                        Note(transition.destinationState);
                foreach (var child in machine.states)
                {
                    if (child.state == null) continue;
                    foreach (var transition in child.state.transitions)
                        if (transition != null && transition.destinationState == child.state
                            && Snaps(transition))
                            Note(child.state);
                }
            }
            if (states.Count == 0) return;
            notes.Add(L.Tr(
                "{0} state(s) with a Parameter Driver can be entered from themselves by a transition of no length ({1}). A transition of no length is finished inside the frame it begins on, so this run has no frame in which to see the re-entry and those drivers fire once instead of every time.",
                states.Count, Join(states)));
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

        /// <summary>Whether the transition is over the instant it is taken. The duration is
        /// read the same way whether it is fixed or a fraction of the source clip, because no
        /// time is no time either way.</summary>
        static bool Snaps(AnimatorStateTransition transition) => transition.duration <= 0f;

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
