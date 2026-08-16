using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// Runs a controller against a clock and hands back what happened. The whole of
    /// DynamicAnalyze's product is this call's return value — the window is a viewer over a
    /// <see cref="SignalTrace"/>, and so is a test, which is what lets the part that can be
    /// wrong be checked without drawing anything.
    ///
    /// Deliberately not incremental: a run is computed whole, from settings that are all data,
    /// so it can be repeated, compared against another run, and thrown away. Play and pause
    /// belong to the viewer moving a cursor along a finished trace, not to the engine holding
    /// its breath between frames.
    ///
    /// With a <see cref="SyncWire"/> it runs the wearer's copy of the avatar and one copy per
    /// other person in the instance off the same clock, and the only thing crossing between them
    /// is what the wire carries. Everything a remote gets wrong has to come from there, which is
    /// what makes the answers worth anything.
    /// </summary>
    static class Simulation
    {
        /// <summary>The wearer's copy: IsLocal, and the only one a localOnly driver runs on.</summary>
        public const string LocalScope = "Local";

        /// <summary>Somebody else's copy of the same avatar. The first one keeps this name on
        /// its own, so a run with one remote is spelt exactly as it always was.</summary>
        public const string RemoteScope = "Remote";

        /// <summary>The wire's own signals — when a sample went, and when one was lost.</summary>
        public const string WireScope = "Wire";

        /// <summary>How far behind the other person is, per parameter — the remote view.</summary>
        public const string LagScope = "Lag";

        /// <summary>
        /// Rows taken off an avatar that was really running — a recording rather than a run.
        ///
        /// Deliberately not one of the client scopes <see cref="IsClient"/> answers for. A
        /// client is a copy of the avatar this module MADE, and the offer that comes with being
        /// one is that a reader may type into its cells; nothing here can reach into somebody
        /// else's Play mode and set a parameter, so a recorded row that offered a field would be
        /// offering a control that does nothing. A row nobody can edit is the true shape of a
        /// recording.
        /// </summary>
        public const string PlayScope = "Play";

        /// <summary>
        /// Somebody else's copy of a recorded avatar — Av3Emulator's non-local clone, driven by
        /// what crossed the wire rather than by the wearer.
        ///
        /// Its own scope rather than rows named differently, for the reason every other scope
        /// has one: a reader folds away the people they are not asking about, and the wearer's
        /// row and the copy's row have to sit under the same name to be worth laying over each
        /// other. Deliberately NOT <see cref="RemoteScope"/>, which is a copy this module
        /// simulated and can be poked; this one is a recording of somebody else's Play mode and
        /// can only be read.
        /// </summary>
        public const string PlayRemoteScope = "Play Remote";

        /// <summary>What to call the nth recorded copy — "Play Remote", "Play Remote 2" — spelt
        /// by the same rule as <see cref="RemoteScopeAt"/> so a reader who has learnt one has
        /// learnt the other.</summary>
        public static string PlayRemoteScopeAt(int index) =>
            index <= 0 ? PlayRemoteScope : PlayRemoteScope + " " + (index + 1);

        /// <summary>Whether this scope's rows were recorded off somebody's running avatar rather
        /// than computed — the wearer's or one of the copies beside them.</summary>
        public static bool IsPlay(string scope) =>
            scope == PlayScope || scope == PlayRemoteScope
            || (scope != null
                && scope.StartsWith(PlayRemoteScope + " ", System.StringComparison.Ordinal));

        /// <summary>
        /// What to call the nth other person. "Remote", then "Remote 2", "Remote 3" — the first
        /// keeps the bare name because a single-remote run is still what most questions are, and
        /// every trace, saved run and test that already says "Remote" goes on meaning it.
        /// </summary>
        public static string RemoteScopeAt(int index) =>
            index <= 0 ? RemoteScope : RemoteScope + " " + (index + 1);

        /// <summary>The lag rows of the nth other person. Their own scope rather than their own
        /// row names, so a reader folds away everyone they are not asking about.</summary>
        public static string LagScopeAt(int index) =>
            index <= 0 ? LagScope : LagScope + " " + (index + 1);

        /// <summary>A wire row that exists once per remote — "lost", "lost 2".</summary>
        public static string WireRowAt(string name, int index) =>
            index <= 0 ? name : name + " " + (index + 1);

        /// <summary>Whether this scope is somebody else's copy of the avatar, whichever of them
        /// it is. Asked by anything that offers to poke a client rather than read one.</summary>
        public static bool IsRemote(string scope) =>
            scope == RemoteScope
            || (scope != null && scope.StartsWith(RemoteScope + " ", System.StringComparison.Ordinal));

        /// <summary>Whether this scope is a running copy of the avatar at all — the wearer's or
        /// anyone else's — as opposed to the wire's own signals or a lag row.</summary>
        public static bool IsClient(string scope) => scope == LocalScope || IsRemote(scope);

        /// <summary>Whether this scope's rows came off an avatar that was running a controller
        /// — a copy this module made, or one it recorded somebody else running. Wider than
        /// <see cref="IsClient"/> and asked by anything reading state rows rather than offering
        /// to write to them: what a layer did is a question about any avatar, and what may be
        /// poked is a question about ours. Every recorded copy is in, not only the wearer — a
        /// finding about states nobody entered is worth exactly as much about the copy, and is
        /// the comparison a two-scope recording was taken for.</summary>
        public static bool IsAvatar(string scope) => IsClient(scope) || IsPlay(scope);

        /// <summary>Whether this scope is somebody's lag rows.</summary>
        public static bool IsLag(string scope) =>
            scope == LagScope
            || (scope != null && scope.StartsWith(LagScope + " ", System.StringComparison.Ordinal));

        /// <summary>
        /// A remote's own seed for whatever it rolls. Derived rather than shared, so a Random
        /// driver does not roll the same numbers on two copies — two clients agreeing by
        /// accident is the one result that would be read as proof of something — and derived by
        /// MULTIPLYING the mixer, so the first remote's seed is the one it has always had and
        /// adding a second person does not reshuffle the first one's run.
        /// </summary>
        internal static int ClientSeed(int seed, int index) =>
            unchecked((int)((uint)seed ^ 0x2545F491u * (uint)(index + 1)));

        /// <summary>Which of the wearer's samples this remote misses. One stream each, so a
        /// remote losing a sample cannot shift what anybody else receives — and the first
        /// remote's stream is the wire's own seed, unshifted, so its run is unchanged by
        /// whoever else turned up.</summary>
        internal static int LossSeed(int seed, int index) =>
            unchecked((int)((uint)seed ^ 0x9E3779B9u * (uint)index));

        /// <summary>
        /// One reading of the wearer's synced values, on its way to one person.
        ///
        /// The values themselves rather than a way back to the wearer, because what a remote
        /// receives is what was true when the sample was READ: a sample in flight does not go
        /// back for the newer number, and a run that let it would hide the whole class of bug
        /// where somebody acts on a value the wearer has already moved on from.
        ///
        /// One reading is shared by every delivery it made — a broadcast, not a letter each —
        /// so the dictionary is never written to after it is queued.
        /// </summary>
        internal struct WireDelivery
        {
            /// <summary>The second it lands. Read against the start of a frame the way a
            /// stimulus is, so it takes effect on the first frame that has reached it — never
            /// the frame before, and never twice.</summary>
            public float at;
            public Dictionary<string, float> values;
        }

        /// <summary>A queue per person. Theirs alone, because a sample one of them lost is
        /// still on its way to everybody else.</summary>
        internal static Queue<WireDelivery>[] InFlight(int remotes)
        {
            var queues = new Queue<WireDelivery>[Mathf.Max(0, remotes)];
            for (int i = 0; i < queues.Length; i++) queues[i] = new Queue<WireDelivery>();
            return queues;
        }

        // ---- the channels VRChat carries its own parameters on -----------------

        /// <summary>
        /// Seconds between IK updates. VRChat's number and not a setting: the channel is
        /// documented as "always updated every 0.1 seconds (10 times per second)", and a field
        /// for it would invite somebody to answer a question about VRChat with a cadence
        /// VRChat does not have. It is deliberately not tied to <see cref="SyncWire.Interval"/>
        /// either — the wearer's expression cadence has nothing to do with it, and a run where
        /// turning one up slowed a gesture down would be inventing a coupling.
        /// Source: https://creators.vrchat.com/avatars/animator-parameters/
        /// </summary>
        internal const float IkSyncInterval = 0.1f;

        /// <summary>
        /// The built-ins this controller reads, split by the channel VRChat carries each on.
        /// Worked out once per run rather than per frame: it is the same answer for the length
        /// of it, and three lists are what every frame then walks.
        ///
        /// Three lists rather than one, because the channels differ in each of the things a run
        /// is for: when a value leaves, whether it can be lost, and what the other person's
        /// copy does with it in between. One list carried every frame — which is what this was
        /// — is all three answered with the fastest of them, so a gesture crossed on the frame
        /// the hand moved and nobody could see the tenth of a second a headset spends on it.
        /// </summary>
        internal sealed class BuiltIns
        {
            /// <summary>With the pose, every <see cref="IkSyncInterval"/>, Floats interpolated
            /// on the far side.</summary>
            public readonly List<string> ik = new List<string>();

            /// <summary>On the same channel as an expression parameter, so: in the wearer's
            /// sample, at the wearer's cadence, lost when that sample is lost.</summary>
            public readonly List<string> playable = new List<string>();

            /// <summary>Not sent at all — computed on each side from audio that is crossing
            /// anyway.</summary>
            public readonly List<string> speech = new List<string>();

            /// <summary>A run with nobody to carry anything to. Never written after this.
            /// </summary>
            public static readonly BuiltIns None = new BuiltIns();

            public static BuiltIns For(SimClient client)
            {
                var found = new BuiltIns();
                if (client == null) return found;
                foreach (var definition in VrcParameters.All)
                {
                    if (!client.Has(definition.name)) continue;
                    switch (definition.sync)
                    {
                        case VrcParameters.Sync.Ik: found.ik.Add(definition.name); break;
                        case VrcParameters.Sync.Playable: found.playable.Add(definition.name); break;
                        case VrcParameters.Sync.Speech: found.speech.Add(definition.name); break;
                    }
                }
                return found;
            }
        }

        /// <summary>
        /// One IK Float on its way to the value that last landed. VRChat interpolates these on
        /// the receiving client, which is why somebody else's Upright moves smoothly on a
        /// headset instead of stepping ten times a second — and a run that snapped instead
        /// would show a stair no eye has ever seen, in exactly the parameters most likely to be
        /// driving a blend tree.
        ///
        /// Straight line, over exactly one interval. The real curve is not published; a guessed
        /// ease would be a shape this module invented turning up in somebody's answer about
        /// their avatar, and a line at least says plainly what it is.
        /// </summary>
        internal struct IkFloat
        {
            public float from;
            public float to;
            /// <summary>When it landed. The interpolation is read against the clock rather than
            /// stepped per frame, so a jittering frame length cannot change where it has got
            /// to.</summary>
            public float since;
        }

        /// <summary>One person's IK channel: what is on its way to them, and where each Float
        /// of theirs has got to. Per person for the same reason the sample queue is — how far a
        /// stream has travelled is a fact about one copy of the avatar.</summary>
        internal sealed class IkStream
        {
            public readonly Queue<WireDelivery> inFlight = new Queue<WireDelivery>();
            public readonly Dictionary<string, IkFloat> floats = new Dictionary<string, IkFloat>();

            /// <summary>Where a Float is at this moment: the value that landed, approached over
            /// one interval from wherever the previous one had got to.</summary>
            public float At(IkFloat moving, float time) =>
                Mathf.Lerp(moving.from, moving.to,
                    Mathf.Clamp01((time - moving.since) / IkSyncInterval));
        }

        /// <summary>A stream per person, made the way <see cref="InFlight"/> makes queues, so a
        /// run and a live session cannot start from different shapes.</summary>
        internal static IkStream[] IkStreams(int remotes)
        {
            var streams = new IkStream[Mathf.Max(0, remotes)];
            for (int i = 0; i < streams.Length; i++) streams[i] = new IkStream();
            return streams;
        }

        /// <summary>
        /// One sample leaving the wearer: read once, then queued for everybody who is there to
        /// receive it, to land <see cref="SyncWire.Latency"/> seconds later.
        ///
        /// Losses are rolled HERE — at the sample, per person, in person order — which is
        /// exactly where and in what order they were rolled before deliveries could be in
        /// flight. That is what makes a wire with no latency not merely similar to the run it
        /// used to do but the same one: the dice are drawn the same number of times in the
        /// same order, and a queue that empties immediately writes what an immediate hand-over
        /// wrote. A wire that lost its samples on arrival instead would read better and would
        /// silently reshuffle every seeded run that already exists.
        /// </summary>
        internal static void Send(SyncWire wire, BuiltIns builtIns, List<SimClient> clients,
            SimRandom[] loss, bool[] arrived, bool[] dropped, Queue<WireDelivery>[] inFlight,
            float time)
        {
            var values = Sample(wire, builtIns, clients[0]);
            float at = time + wire.Latency;
            for (int i = 0; i < arrived.Length; i++)
            {
                if (!arrived[i]) continue;
                if (loss[i].NextChance(wire.dropChance)) dropped[i] = true;
                else inFlight[i].Enqueue(new WireDelivery { at = at, values = values });
            }
        }

        /// <summary>Everything whose time has come, in the order it was sent. One latency for
        /// the whole wire, so a queue is always in the order it was filled and the first thing
        /// in it is the first thing due.</summary>
        internal static void Land(List<SimClient> clients, Queue<WireDelivery>[] inFlight,
            float time)
        {
            for (int i = 0; i < inFlight.Length; i++)
                while (inFlight[i].Count > 0 && inFlight[i].Peek().at <= time)
                    Apply(inFlight[i].Dequeue().values, clients[i + 1]);
        }

        /// <summary>
        /// One IK update leaving the wearer: read once, queued for everybody who is there to
        /// receive it, landing <see cref="SyncWire.Latency"/> seconds later.
        ///
        /// Deliberately the same shape as <see cref="Send"/> — one reading, many deliveries,
        /// through the same queue and the same delay — because it is the same network. What it
        /// does NOT share is the schedule (its own fixed tenth of a second, not the wearer's
        /// expression cadence), the dice (nothing is dropped here) and the rounding (none).
        ///
        /// No loss, and that is a modelling decision rather than a claim that packets do not go
        /// missing: a lost tick of a stream that repeats every 0.1 s is overwritten by the next
        /// one, so putting it in would buy a one-tick delay and the illusion that a run can
        /// tell you how often it happens. A dropped expression sample is worth modelling
        /// because a decoder can miss a step it will never be shown again; an IK tick has no
        /// such step to miss.
        /// </summary>
        internal static void SendIk(SyncWire wire, BuiltIns builtIns, List<SimClient> clients,
            bool[] arrived, IkStream[] streams, float time)
        {
            if (builtIns.ik.Count == 0) return;
            var values = new Dictionary<string, float>();
            foreach (var name in builtIns.ik) values[name] = clients[0].Read(name);
            float at = time + wire.Latency;
            for (int i = 0; i < arrived.Length; i++)
                if (arrived[i])
                    streams[i].inFlight.Enqueue(new WireDelivery { at = at, values = values });
        }

        /// <summary>
        /// The IK channel as one person receives it: whatever has finished travelling, and then
        /// this frame's position along whatever is still on its way there.
        ///
        /// Landing and interpolating in one call because they are one thing — an update does
        /// not set a value, it sets where a value is heading — and separating them would let a
        /// live session step the two in a different order from a batch run and be almost
        /// right.
        /// </summary>
        internal static void CarryIk(List<SimClient> clients, IkStream[] streams, bool[] arrived,
            float time)
        {
            for (int i = 0; i < streams.Length; i++)
            {
                if (!arrived[i]) continue;
                var stream = streams[i];
                var to = clients[i + 1];
                while (stream.inFlight.Count > 0 && stream.inFlight.Peek().at <= time)
                    LandIk(stream, stream.inFlight.Dequeue().values, to, time);
                foreach (var pair in stream.floats)
                    to.Write(pair.Key, stream.At(pair.Value, time));
            }
        }

        /// <summary>
        /// One IK update arriving. An Int or a Bool changes on the spot — there is nothing
        /// between two gesture indices to be halfway through — and a Float becomes the
        /// destination of a new interpolation instead.
        ///
        /// Where that interpolation STARTS is read out of the previous one rather than off the
        /// client, which is not the same thing by a frame: updates land a whole interval apart,
        /// so the last one has just finished at this instant while the last frame to write
        /// anything was up to a frame earlier. Reading the client back would leave every Float
        /// permanently a frame short of the wearer's, and a settled value would approach a
        /// number it never reached.
        /// </summary>
        static void LandIk(IkStream stream, Dictionary<string, float> values, SimClient to,
            float time)
        {
            foreach (var pair in values)
            {
                if (!to.Has(pair.Key)) continue;
                if (to.TypeOf(pair.Key) != AnimatorControllerParameterType.Float)
                {
                    to.Write(pair.Key, pair.Value);
                    continue;
                }
                float from = stream.floats.TryGetValue(pair.Key, out var moving)
                    ? stream.At(moving, time) : to.Read(pair.Key);
                stream.floats[pair.Key] = new IkFloat
                {
                    from = from,
                    to = pair.Value,
                    since = time,
                };
            }
        }

        public static SignalTrace Run(AnimatorController controller, SimClock clock = null,
            Stimulus stimulus = null) =>
            Run(controller, new SimSettings
            {
                clock = clock ?? new SimClock(),
                stimulus = stimulus ?? new Stimulus(),
            });

        public static SignalTrace Run(AnimatorController controller, SimSettings settings)
        {
            if (controller == null) return new SignalTrace();
            settings = settings ?? new SimSettings();
            var clock = settings.clock ?? new SimClock();
            var wire = settings.wire;

            var clients = new List<SimClient>();
            try
            {
                int remotes = wire != null ? wire.Remotes : 0;
                clients.Add(new SimClient(controller, LocalScope, true, clock.seed));
                for (int i = 0; i < remotes; i++)
                    clients.Add(new SimClient(controller, RemoteScopeAt(i), false,
                        ClientSeed(clock.seed, i)));

                var recorder = new TraceRecorder(controller, clients, wire != null,
                    settings.lagRows);
                var steps = clock.Steps();
                var pending = settings.stimulus != null
                    ? settings.stimulus.InOrder() : new List<Stimulus.Entry>();

                var builtIns = remotes > 0 ? BuiltIns.For(clients[0]) : BuiltIns.None;
                var ik = IkStreams(remotes);
                var loss = new SimRandom[remotes];
                // Zero means somebody loaded with the wearer, and then there is nothing to hand
                // over: both copies start from the same defaults and the first thing that
                // crosses is the first sample. An arrival delivery is for somebody who turned
                // up to a session already in progress.
                var arrived = new bool[remotes];
                var dropped = new bool[remotes];
                var inFlight = InFlight(remotes);
                for (int i = 0; i < remotes; i++)
                {
                    loss[i] = new SimRandom(LossSeed(wire.seed, i));
                    arrived[i] = wire.JoinsAt(i) <= 0f;
                }

                int next = 0;
                float time = 0f;
                // The first sample is one interval after there is somebody to send it to, so a
                // remote starts knowing nothing — which is not a limitation of the model but
                // the situation every remote is actually in when it arrives. One schedule for
                // everyone: the wearer reads its own values once and the reading is broadcast.
                float nextSample = remotes > 0
                    ? wire.EarliestJoin + wire.Interval : float.MaxValue;
                // The IK stream keeps its own time. It starts where the wearer's does — with
                // somebody there to receive it — and then runs on VRChat's tenth of a second
                // whatever the wire is set to.
                float nextIk = remotes > 0
                    ? wire.EarliestJoin + IkSyncInterval : float.MaxValue;

                for (int frame = 0; frame < steps.Length; frame++)
                {
                    // Inputs land before the frame they are timed for, so "at 1.0 s the toggle
                    // went on" means the first frame that starts at or after 1.0 s runs with it
                    // on — never the frame before, and never twice.
                    while (next < pending.Count && pending[next].atSeconds <= time)
                        Poke(clients, pending[next++]);

                    bool sampled = false;
                    for (int i = 0; i < remotes; i++) dropped[i] = false;
                    for (int i = 0; i < remotes; i++)
                    {
                        if (arrived[i] || time < wire.JoinsAt(i)) continue;
                        // Arriving is itself a delivery: a joiner is handed the state of every
                        // synced parameter at once, which is why they decode whatever index
                        // they land on rather than waiting for the next change.
                        arrived[i] = true;
                        sampled = true;
                        Carry(wire, builtIns, clients[0], clients[i + 1]);
                    }
                    while (time >= nextSample)
                    {
                        nextSample += wire.Interval;
                        sampled = true;
                        Send(wire, builtIns, clients, loss, arrived, dropped, inFlight, time);
                    }
                    // The IK stream, on its own schedule. It does not raise `sampled`: the
                    // wire's row means the wearer's expression sample went, which is what
                    // every reader of it takes it for, and a second thing ticking on that row
                    // would make a cadence somebody typed unreadable.
                    while (time >= nextIk)
                    {
                        nextIk += IkSyncInterval;
                        SendIk(wire, builtIns, clients, arrived, ik, time);
                    }
                    // What has finished travelling. There is no row for it: the wire's row
                    // says a sample WENT — which is what every reader of it, the findings
                    // included, takes it for — and a landing shows up as the other person's
                    // values moving, which is the thing anybody came here to look at.
                    Land(clients, inFlight, time);
                    CarryIk(clients, ik, arrived, time);

                    // The voice's shadow, before the frame that reads it — see CarrySpeech.
                    for (int i = 0; i < remotes; i++)
                        if (arrived[i]) CarrySpeech(builtIns, clients[0], clients[i + 1]);

                    for (int i = 0; i < clients.Count; i++)
                    {
                        // Somebody who has not arrived is not running: their copy of the avatar
                        // does not exist yet, and a flat line is what that looks like.
                        if (i > 0 && !arrived[i - 1]) continue;
                        clients[i].Step(steps[frame]);
                    }
                    time += steps[frame];
                    recorder.Record(time, steps[frame], sampled, dropped, arrived);
                }
                return recorder.Trace;
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }
        }

        /// <summary>
        /// An input reaches the wearer unless it names someone else. The wearer is who is
        /// pressing things — and a run where a menu toggle appeared on a remote by itself
        /// would hide the very thing worth watching, which is whether it ever gets there.
        /// </summary>
        internal static void Poke(List<SimClient> clients, Stimulus.Entry entry)
        {
            foreach (var client in clients)
                if (Targets(client, entry.scope))
                    client.Write(entry.parameter, entry.value);
        }

        /// <summary>Whether something aimed at this scope reaches this client. Empty names the
        /// wearer — see Stimulus.Entry. Shared with the live session's own writes, so a poke
        /// and a weight turned by hand agree about who they are for.</summary>
        internal static bool Targets(SimClient client, string scope) =>
            string.IsNullOrEmpty(scope) ? client.IsLocal : client.Scope == scope;

        /// <summary>
        /// One sample handed over with no journey in between: read off the wearer and written
        /// to the remote in the same breath.
        ///
        /// This is what ARRIVING is. Somebody who walks in is given the state of every synced
        /// parameter at once, and that hand-over is deliberately not put through the delivery
        /// queue: the join handshake is its own piece of machinery with its own timing, and
        /// this module has no model of it. Delaying it by the sample latency would be
        /// inventing one, and inventing one is how a run starts answering questions it was
        /// never entitled to answer.
        ///
        /// Every channel at once, the built-ins included and whatever cadence each of them
        /// normally keeps: a joiner is shown a pose and a face, not a blank avatar that fills
        /// in over the next tenth of a second. Which is also why the IK Floats are set here
        /// rather than started on an interpolation — there is nothing yet for them to be
        /// interpolating from.
        /// </summary>
        internal static void Carry(SyncWire wire, BuiltIns builtIns, SimClient from, SimClient to)
        {
            Apply(Sample(wire, builtIns, from), to);
            Hand(builtIns.ik, from, to);
            Hand(builtIns.speech, from, to);
        }

        /// <summary>The wearer's current values for these names, written straight across. What
        /// a channel with no journey in it does.</summary>
        static void Hand(List<string> names, SimClient from, SimClient to)
        {
            foreach (var name in names)
                if (to.Has(name)) to.Write(name, from.Read(name));
        }

        /// <summary>
        /// The wearer's synced values as the wire would carry them — rounded on the way out,
        /// because that is when the wire has them, and a value read now cannot be rounded
        /// differently by arriving later.
        ///
        /// The playable built-ins ride along in it, because VRChat puts them on the same
        /// channel as an expression parameter: same cadence, same delivery, gone with the same
        /// lost sample. They are NOT rounded with it. The eight bits over -1..1 are the price
        /// of a place in the avatar's parameter budget, and a built-in is outside that budget —
        /// so quantizing GestureLeftWeight here would charge an avatar for a bit it never
        /// spent, and clamping EyeHeightAsMeters to a metre would be a bug no headset has.
        ///
        /// A built-in NAMED IN THE STORE is still skipped above and then carried by its own
        /// channel below. Listing one is a mistake people make, and honouring it would round a
        /// Velocity into a range it does not live in.
        /// </summary>
        internal static Dictionary<string, float> Sample(SyncWire wire, BuiltIns builtIns,
            SimClient from)
        {
            var values = new Dictionary<string, float>();
            foreach (var name in wire.parameters)
            {
                if (!from.Has(name)) continue;
                if (VrcParameters.IsBuiltIn(name)) continue;
                values[name] = wire.Compress(from.Read(name), from.TypeOf(name));
            }
            foreach (var name in builtIns.playable) values[name] = from.Read(name);
            return values;
        }

        /// <summary>One sample landing: whatever of it this copy of the avatar has a parameter
        /// for. Order does not come into it — the writes are one per name and a name is in a
        /// sample once, so the set lands as a set, which is the promise the whole model
        /// rests on.</summary>
        internal static void Apply(Dictionary<string, float> values, SimClient to)
        {
            foreach (var pair in values)
                if (to.Has(pair.Key)) to.Write(pair.Key, pair.Value);
        }

        /// <summary>
        /// The voice's shadow: Viseme and Voice, every frame, uncompressed and with no delay.
        ///
        /// The only channel modelled as instant, and for the opposite reason to the others —
        /// nothing about these is sent. Each client computes them from the audio it is already
        /// receiving, so the wearer's mouth and the remote's move together for as long as the
        /// voice is getting through at all. The approximation is that this run has no audio and
        /// therefore no way to be behind: a headset's remote viseme is as late as the voice is,
        /// and a run cannot tell anybody how late that is.
        /// </summary>
        internal static void CarrySpeech(BuiltIns builtIns, SimClient from, SimClient to) =>
            Hand(builtIns.speech, from, to);
    }
}
