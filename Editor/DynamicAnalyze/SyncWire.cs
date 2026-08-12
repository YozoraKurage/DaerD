using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What the wearer's client sends to everyone else's, and how badly. Every difference
    /// between the two copies of an avatar comes through here, so this is where a run stops
    /// being an Animator debugger and starts being about VRChat.
    ///
    /// Modelled as a PERIODIC SAMPLE rather than as a stream of changes, because that is what
    /// the network is: the wearer's values are read every <see cref="intervalSeconds"/> and the
    /// whole set is handed over together. Two consequences fall out of that shape rather than
    /// being modelled on top of it, and both are what people come here to see —
    ///
    /// a change that happens and is undone inside one interval never leaves the wearer at all,
    /// and two changes to one parameter inside one interval reach a remote as the second one
    /// only. That is exactly why a decoder that fires on a value CHANGING can miss a step, and
    /// why async sync warns about steps shorter than the cadence.
    ///
    /// The set arrives coherently — every parameter of one sample lands on the same frame — so
    /// an index and the channels it describes can never be read half-updated. That much VRChat
    /// does promise, and a model that broke it would invent bugs.
    /// </summary>
    sealed class SyncWire
    {
        /// <summary>Seconds between samples. VRChat's real cadence moves with how much is being
        /// sent and how many people are present; a run picks one and says so.</summary>
        public float intervalSeconds = 0.2f;

        /// <summary>Chance that a whole sample is lost, 0 to 1. A whole one rather than a
        /// parameter of one: the set travels together, so it survives or misses together.</summary>
        public float dropChance;

        /// <summary>
        /// Round values on the way over, the way the wire does: a Float to 8 bits across
        /// -1..1, an Int to 0..255, a Bool to a bit. On by default because it is not an
        /// approximation of VRChat — it is VRChat, and a run that skipped it would show a
        /// remote holding a value no remote can hold.
        /// </summary>
        public bool quantize = true;

        /// <summary>Fixes which samples are lost. Its own seed, so changing the clock's does
        /// not reshuffle the drops and vice versa.</summary>
        public int seed = 1;

        /// <summary>The parameters that actually travel — the avatar's synced expression
        /// parameters. Taken as names rather than read out of a parameter store, so this module
        /// stays independent of how the project stores them.</summary>
        public readonly List<string> parameters = new List<string>();

        public SyncWire Syncs(params string[] names)
        {
            foreach (var name in names)
                if (!string.IsNullOrEmpty(name) && !parameters.Contains(name))
                    parameters.Add(name);
            return this;
        }

        public float Interval => Mathf.Max(1e-4f, intervalSeconds);

        /// <summary>
        /// One value as a remote can hold it. Bool is a bit; Int is a byte, so 300 arrives as
        /// 255 and -1 as 0; Float is 8 bits spread over -1..1, which is 255 steps of about
        /// 0.0078 — and anything outside that range does not survive the trip, which is the
        /// single most surprising thing about multiplexing a Float.
        /// </summary>
        public float Compress(float value, AnimatorControllerParameterType type)
        {
            if (!quantize) return value;
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return value != 0f ? 1f : 0f;
                case AnimatorControllerParameterType.Int:
                    return Mathf.Clamp(Mathf.Round(value), 0f, 255f);
                default:
                    float clamped = Mathf.Clamp(value, -1f, 1f);
                    return Mathf.Round((clamped + 1f) * 0.5f * 255f) / 255f * 2f - 1f;
            }
        }
    }

    /// <summary>Everything a run is: the time, the inputs, and the wire. One object because
    /// the window edits one thing and a saved experiment is one thing.</summary>
    sealed class SimSettings
    {
        public SimClock clock = new SimClock();
        public Stimulus stimulus = new Stimulus();
        /// <summary>Null for a single client — an Animator question rather than a VRChat
        /// one.</summary>
        public SyncWire wire;
    }
}
