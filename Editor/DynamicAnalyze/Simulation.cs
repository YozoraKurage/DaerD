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
        /// What to call the nth other person. "Remote", then "Remote 2", "Remote 3" — the first
        /// keeps the bare name because a single-remote run is still what most questions are, and
        /// every trace, saved clip and test that already says "Remote" goes on meaning it.
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

                var broadcast = remotes > 0 ? Broadcast(clients[0]) : new List<string>();
                var loss = new SimRandom[remotes];
                // Zero means somebody loaded with the wearer, and then there is nothing to hand
                // over: both copies start from the same defaults and the first thing that
                // crosses is the first sample. An arrival delivery is for somebody who turned
                // up to a session already in progress.
                var arrived = new bool[remotes];
                var dropped = new bool[remotes];
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
                        Carry(wire, clients[0], clients[i + 1]);
                    }
                    while (time >= nextSample)
                    {
                        nextSample += wire.Interval;
                        sampled = true;
                        for (int i = 0; i < remotes; i++)
                        {
                            if (!arrived[i]) continue;
                            if (loss[i].NextChance(wire.dropChance)) dropped[i] = true;
                            else Carry(wire, clients[0], clients[i + 1]);
                        }
                    }

                    // Whatever VRChat syncs on its own, before the frame that reads it. Not on
                    // the sample and not subject to its loss — see CarryBroadcast.
                    for (int i = 0; i < remotes; i++)
                        if (arrived[i]) CarryBroadcast(broadcast, clients[0], clients[i + 1]);

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
                if (string.IsNullOrEmpty(entry.scope)
                    ? client.IsLocal
                    : client.Scope == entry.scope)
                    client.Write(entry.parameter, entry.value);
        }

        /// <summary>
        /// One sample: every synced parameter read off the wearer and written to the remote,
        /// together and in the shape the wire allows.
        ///
        /// A built-in is skipped however it got into the list. VRChat feeds those itself, on
        /// its own channels — a store that names one is a mistake people make, and honouring
        /// it here would send AvatarVersion over a wire that never carries it and round a
        /// Velocity into a range it does not live in.
        /// </summary>
        internal static void Carry(SyncWire wire, SimClient from, SimClient to)
        {
            foreach (var name in wire.parameters)
            {
                if (!from.Has(name) || !to.Has(name)) continue;
                if (VrcParameters.IsBuiltIn(name)) continue;
                to.Write(name, wire.Compress(from.Read(name), from.TypeOf(name)));
            }
        }

        /// <summary>
        /// The built-ins this controller reads that VRChat keeps in step by itself. Worked out
        /// once per run rather than per frame: it is the same answer for the length of it.
        /// </summary>
        internal static List<string> Broadcast(SimClient client)
        {
            var names = new List<string>();
            if (client == null) return names;
            foreach (var definition in VrcParameters.All)
                if (definition.sync == VrcParameters.Sync.Broadcast && client.Has(definition.name))
                    names.Add(definition.name);
            return names;
        }

        /// <summary>
        /// What the platform syncs whether or not the avatar asked. Gesture, Viseme, Grounded,
        /// the scale family — the values most controllers are actually built on — reach every
        /// other client continuously, and a run that only carried the expression sample showed
        /// a remote whose hand never moved. It is the commonest shape there is, so getting it
        /// wrong was wrong about nearly every avatar.
        ///
        /// Every frame rather than on the sample, and uncompressed, because these do not ride
        /// the expression channel: they are neither paced by its cadence nor rounded to its
        /// eight bits over -1..1 — a rule that would clamp VelocityZ to a metre a second and
        /// invent a bug that no headset has. For the same reason a lost sample does not take
        /// them with it: a dropped tick of a continuous stream is replaced by the next frame's,
        /// so modelling it would produce a one-frame delay and nothing else.
        /// </summary>
        internal static void CarryBroadcast(List<string> names, SimClient from, SimClient to)
        {
            foreach (var name in names)
            {
                if (!to.Has(name)) continue;
                to.Write(name, from.Read(name));
            }
        }
    }
}
