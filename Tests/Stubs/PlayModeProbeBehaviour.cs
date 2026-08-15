using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// A tally of state entries and exits, and — optionally — one parameter written on the way
    /// in. The smallest thing that can answer "did Mecanim notice", because a
    /// StateMachineBehaviour is the only witness to a state being entered that Mecanim offers,
    /// and outside Play mode it is never asked.
    ///
    /// The counts are static and the frame ordinal is set from outside, because the interesting
    /// question is not how many times a state was entered but WHICH step it was entered on —
    /// and the callback has no way to know that on its own. A probe stamps the ordinal before
    /// each step and reads the stamps afterwards.
    ///
    /// It lives beside the other test behaviours rather than in the Editor-only test assembly
    /// so that a Play mode run instantiates it the same way a build would.
    /// </summary>
    public class PlayModeProbeBehaviour : StateMachineBehaviour
    {
        /// <summary>What to file this state's entries under. State names are not unique across
        /// layers and a label is, which is what a cross-layer measurement needs.</summary>
        public string label;

        /// <summary>A Bool parameter to raise from <see cref="OnStateEnter"/>, or empty. This is
        /// the shape a VRChat Parameter Driver has: a behaviour writing parameters from inside
        /// the Animator's own update, which is the whole reason its timing is worth measuring.</summary>
        public string writeBool;

        static readonly Dictionary<string, int> Entered = new Dictionary<string, int>();
        static readonly Dictionary<string, int> Exited = new Dictionary<string, int>();
        static readonly Dictionary<string, int> FirstEnteredOn = new Dictionary<string, int>();

        /// <summary>The step the probe is about to take, stamped by the probe. 0 means "before
        /// any step" — which is where a callback fired by Rebind rather than by Update lands.</summary>
        public static int Step;

        public static void Forget()
        {
            Entered.Clear();
            Exited.Clear();
            FirstEnteredOn.Clear();
            Step = 0;
        }

        public static int Enters(string label) => Read(Entered, label);

        public static int Exits(string label) => Read(Exited, label);

        /// <summary>The step the state was first entered on, or -1 if it never was.</summary>
        public static int EnteredOn(string label) =>
            FirstEnteredOn.TryGetValue(label, out int step) ? step : -1;

        public override void OnStateEnter(Animator animator, AnimatorStateInfo info, int layer)
        {
            Bump(Entered, label);
            if (!FirstEnteredOn.ContainsKey(label)) FirstEnteredOn[label] = Step;
            if (!string.IsNullOrEmpty(writeBool)) animator.SetBool(writeBool, true);
        }

        public override void OnStateExit(Animator animator, AnimatorStateInfo info, int layer)
        {
            Bump(Exited, label);
        }

        static void Bump(Dictionary<string, int> into, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            into.TryGetValue(key, out int seen);
            into[key] = seen + 1;
        }

        static int Read(Dictionary<string, int> from, string key) =>
            from.TryGetValue(key, out int seen) ? seen : 0;
    }
}
