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
    /// With a <see cref="SyncWire"/> it runs TWO copies of the avatar — the wearer's and one
    /// other person's — off the same clock, and the only thing crossing between them is what
    /// the wire carries. Everything a remote gets wrong has to come from there, which is what
    /// makes the answers worth anything.
    /// </summary>
    static class Simulation
    {
        /// <summary>The wearer's copy: IsLocal, and the only one a localOnly driver runs on.</summary>
        public const string LocalScope = "Local";

        /// <summary>Somebody else's copy of the same avatar.</summary>
        public const string RemoteScope = "Remote";

        /// <summary>The wire's own signals — when a sample went, and when one was lost.</summary>
        public const string WireScope = "Wire";

        /// <summary>How far behind the other person is, per parameter — the remote view.</summary>
        public const string LagScope = "Lag";

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
                clients.Add(new SimClient(controller, LocalScope, true, clock.seed));
                // A different seed, so a Random driver does not roll the same numbers on both
                // copies — two clients agreeing by accident is the one result that would be
                // read as proof of something.
                if (wire != null)
                    clients.Add(new SimClient(controller, RemoteScope, false,
                        clock.seed ^ 0x2545F491));

                var recorder = new TraceRecorder(controller, clients, wire != null,
                    settings.lagRows);
                var steps = clock.Steps();
                var pending = settings.stimulus != null
                    ? settings.stimulus.InOrder() : new List<Stimulus.Entry>();
                var random = new SimRandom(wire != null ? wire.seed : 0);

                int next = 0;
                float time = 0f;
                float joinAt = wire != null ? Mathf.Max(0f, wire.remoteJoinsAt) : 0f;
                // Zero means everybody loaded together, and then there is nothing to hand
                // over: both copies start from the same defaults and the first thing that
                // crosses is the first sample. An arrival delivery is for somebody who turned
                // up to a session already in progress.
                bool arrived = wire == null || joinAt <= 0f;
                // The first sample is one interval after the other person is there, so a
                // remote starts knowing nothing — which is not a limitation of the model but
                // the situation every remote is actually in when it arrives.
                float nextSample = wire != null ? joinAt + wire.Interval : float.MaxValue;

                for (int frame = 0; frame < steps.Length; frame++)
                {
                    // Inputs land before the frame they are timed for, so "at 1.0 s the toggle
                    // went on" means the first frame that starts at or after 1.0 s runs with it
                    // on — never the frame before, and never twice.
                    while (next < pending.Count && pending[next].atSeconds <= time)
                        Poke(clients, pending[next++]);

                    bool sampled = false, dropped = false;
                    if (!arrived && time >= joinAt)
                    {
                        // Arriving is itself a delivery: a joiner is handed the state of every
                        // synced parameter at once, which is why they decode whatever index
                        // they land on rather than waiting for the next change.
                        arrived = true;
                        sampled = true;
                        Carry(wire, clients[0], clients[1]);
                    }
                    while (time >= nextSample)
                    {
                        nextSample += wire.Interval;
                        sampled = true;
                        if (random.NextChance(wire.dropChance)) dropped = true;
                        else Carry(wire, clients[0], clients[1]);
                    }

                    for (int i = 0; i < clients.Count; i++)
                    {
                        // Somebody who has not arrived is not running: their copy of the avatar
                        // does not exist yet, and a flat line is what that looks like.
                        if (i > 0 && !arrived) continue;
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

        /// <summary>One sample: every synced parameter read off the wearer and written to the
        /// remote, together and in the shape the wire allows.</summary>
        internal static void Carry(SyncWire wire, SimClient from, SimClient to)
        {
            foreach (var name in wire.parameters)
            {
                if (!from.Has(name) || !to.Has(name)) continue;
                to.Write(name, wire.Compress(from.Read(name), from.TypeOf(name)));
            }
        }

    }
}
