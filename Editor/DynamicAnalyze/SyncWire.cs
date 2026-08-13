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
    ///
    /// With more than one remote it is ONE SAMPLE, MANY DELIVERIES. The wearer's values are read
    /// once per interval — a broadcast, not a letter each — and what is per-person is only what
    /// happens to that copy of it: when they turned up, and whether their copy of a given sample
    /// arrived at all. Modelling it the other way round, with a sampling schedule per remote,
    /// would let one remote see a change that came and went between another remote's samples,
    /// and that is precisely the class of bug the periodic-sample model exists to expose.
    /// </summary>
    sealed class SyncWire
    {
        /// <summary>Seconds between samples. VRChat's real cadence moves with how much is being
        /// sent and how many people are present; a run picks one and says so.</summary>
        public float intervalSeconds = 0.2f;

        /// <summary>Chance that a whole sample is lost, 0 to 1. A whole one rather than a
        /// parameter of one: the set travels together, so it survives or misses together. The
        /// sample, and not the built-ins VRChat streams beside it — losing a tick of those
        /// would be undone by the next frame, so a run that modelled it would show nothing but
        /// a frame of delay.</summary>
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

        /// <summary>
        /// When the other person arrives, in seconds. Zero is everybody loading together,
        /// which is the one case that never happens after the first minute of an instance —
        /// and the case a cycle is least likely to be wrong in, because both copies start from
        /// the same defaults.
        ///
        /// A late arrival is where the interesting failures live: they decode whatever index
        /// they land on, they have no history to compare it against, and every flag about
        /// being caught up exists because of them.
        /// </summary>
        public float remoteJoinsAt;

        /// <summary>
        /// Everybody who turned up after the first one, in seconds, in the order the trace names
        /// them — "Remote 2", "Remote 3", and so on. Empty is the one-remote run every question
        /// about a wire started as.
        ///
        /// A second person is not a second copy of the first: they arrive at their own moment
        /// and they lose their own samples, and the failures worth running two for are the ones
        /// where that matters — a group that commits on a decode taken before anybody sent
        /// anything is a hole a run with one remote in it can only find at the very first frame,
        /// and a run with two finds in the middle of a lap.
        ///
        /// Each one is a real Animator running a real controller, so this costs linearly and a
        /// number typed here is a number of avatars.
        /// </summary>
        public readonly List<float> laterJoins = new List<float>();

        /// <summary>
        /// The parameters that travel in the sample — the avatar's synced expression
        /// parameters. Taken as names rather than read out of a parameter store, so this module
        /// stays independent of how the project stores them.
        ///
        /// Not the whole of what reaches a remote, and deliberately not: VRChat's own built-ins
        /// cross by its arrangement rather than the avatar's, on other channels and at another
        /// cadence, and <see cref="Simulation.CarryBroadcast"/> is where that happens. A
        /// built-in named here is ignored — see <see cref="Simulation.Carry"/>.
        /// </summary>
        public readonly List<string> parameters = new List<string>();

        public SyncWire Syncs(params string[] names)
        {
            foreach (var name in names)
                if (!string.IsNullOrEmpty(name) && !parameters.Contains(name))
                    parameters.Add(name);
            return this;
        }

        /// <summary>Adds another person, arriving at the given second. Reads as what it is at a
        /// call site: <c>wire.Joining(4f)</c> is somebody walking in four seconds in.</summary>
        public SyncWire Joining(params float[] seconds)
        {
            foreach (float at in seconds) laterJoins.Add(at);
            return this;
        }

        /// <summary>How many other people are in the instance. Never zero: a wire at all is
        /// somebody to send to.</summary>
        public int Remotes => 1 + laterJoins.Count;

        /// <summary>When remote <paramref name="index"/> turns up. Negative times are zero —
        /// arriving before the run began is arriving with it.</summary>
        public float JoinsAt(int index) =>
            Mathf.Max(0f, index <= 0 || index > laterJoins.Count
                ? remoteJoinsAt : laterJoins[index - 1]);

        /// <summary>
        /// When the wearer's stream starts, which is when the first person is there to receive
        /// it. The cadence is the wearer's own and everyone rides the one they find: somebody
        /// who arrives at 3.55 s does not restart it, which is why their first regular delivery
        /// lands wherever the existing rhythm puts it rather than politely one interval after
        /// they knocked.
        /// </summary>
        public float EarliestJoin
        {
            get
            {
                float first = JoinsAt(0);
                for (int i = 1; i < Remotes; i++) first = Mathf.Min(first, JoinsAt(i));
                return first;
            }
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

        /// <summary>
        /// Record, per parameter, how long the other person has been looking at a different
        /// value. That IS the remote view: for a multiplexed target it is the age of their
        /// copy, and for a synced one it is the sawtooth of the wire's own cadence. Costs a row
        /// per parameter, and means nothing without a second client.
        /// </summary>
        public bool lagRows = true;
    }
}
