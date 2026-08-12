using System.Collections.Generic;
using UnityEditor.Animations;

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

        public static SignalTrace Run(AnimatorController controller, SimClock clock = null,
            Stimulus stimulus = null) =>
            Run(controller, new SimSettings
            {
                clock = clock ?? new SimClock(),
                stimulus = stimulus ?? new Stimulus(),
            });

        public static SignalTrace Run(AnimatorController controller, SimSettings settings)
        {
            var trace = new SignalTrace();
            if (controller == null) return trace;
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

                var readers = new List<Reader>();
                foreach (var client in clients)
                    Declare(trace, controller, client, readers);
                var sent = wire != null ? trace.Declare(WireScope, "sample", SignalKind.Bool) : null;
                var lost = wire != null ? trace.Declare(WireScope, "lost", SignalKind.Bool) : null;

                var steps = clock.Steps();
                var pending = settings.stimulus != null
                    ? settings.stimulus.InOrder() : new List<Stimulus.Entry>();
                var random = new SimRandom(wire != null ? wire.seed : 0);

                int next = 0;
                float time = 0f;
                // The first sample is one interval in, so a remote starts the run knowing
                // nothing — which is not a limitation of the model but the situation every
                // remote is actually in when it arrives.
                float nextSample = wire != null ? wire.Interval : float.MaxValue;

                for (int frame = 0; frame < steps.Length; frame++)
                {
                    // Inputs land before the frame they are timed for, so "at 1.0 s the toggle
                    // went on" means the first frame that starts at or after 1.0 s runs with it
                    // on — never the frame before, and never twice.
                    while (next < pending.Count && pending[next].atSeconds <= time)
                        Poke(clients, pending[next++]);

                    bool sampled = false, dropped = false;
                    while (time >= nextSample)
                    {
                        nextSample += wire.Interval;
                        sampled = true;
                        if (random.NextChance(wire.dropChance)) dropped = true;
                        else Carry(wire, clients[0], clients[1]);
                    }

                    foreach (var client in clients) client.Step(steps[frame]);
                    time += steps[frame];
                    trace.Frame(time, steps[frame]);

                    foreach (var reader in readers) reader.Sample();
                    if (sent != null) sent.samples.Add(sampled ? 1f : 0f);
                    if (lost != null) lost.samples.Add(dropped ? 1f : 0f);
                }
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
            }
            return trace;
        }

        /// <summary>
        /// An input reaches the wearer unless it names someone else. The wearer is who is
        /// pressing things — and a run where a menu toggle appeared on a remote by itself
        /// would hide the very thing worth watching, which is whether it ever gets there.
        /// </summary>
        static void Poke(List<SimClient> clients, Stimulus.Entry entry)
        {
            foreach (var client in clients)
                if (string.IsNullOrEmpty(entry.scope)
                    ? client.IsLocal
                    : client.Scope == entry.scope)
                    client.Write(entry.parameter, entry.value);
        }

        /// <summary>One sample: every synced parameter read off the wearer and written to the
        /// remote, together and in the shape the wire allows.</summary>
        static void Carry(SyncWire wire, SimClient from, SimClient to)
        {
            foreach (var name in wire.parameters)
            {
                if (!from.Has(name) || !to.Has(name)) continue;
                to.Write(name, wire.Compress(from.Read(name), from.TypeOf(name)));
            }
        }

        /// <summary>One signal and where its next value comes from, paired up before the run so
        /// the loop above does no lookups per frame.</summary>
        sealed class Reader
        {
            public SignalTrace.Signal signal;
            public System.Func<float> read;
            public void Sample() => signal.samples.Add(read());
        }

        /// <summary>
        /// Everything worth watching, declared up front: every parameter the controller has,
        /// and for every layer both which state it is in and whether it is between two.
        ///
        /// Every parameter rather than the interesting ones, because which ones are interesting
        /// is the question being asked — a viewer can hide rows, and a run that recorded only
        /// what it was asked for would have to be run again to answer the next question.
        /// </summary>
        static void Declare(SignalTrace trace, AnimatorController controller, SimClient client,
            List<Reader> readers)
        {
            foreach (var parameter in controller.parameters)
            {
                string name = parameter.name;
                var signal = trace.Declare(client.Scope, name, KindOf(parameter.type));
                readers.Add(new Reader { signal = signal, read = () => client.Read(name) });
            }

            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                int layer = i;
                var state = trace.Declare(client.Scope, layers[i].name + "/state",
                    SignalKind.State, client.StateLabels(layer));
                readers.Add(new Reader
                {
                    signal = state,
                    read = () => client.CurrentState(layer),
                });
                // Worth its own row: a layer that spends the run mid-blend looks identical to a
                // settled one if all you can see is which state it is in.
                var moving = trace.Declare(client.Scope, layers[i].name + "/transition",
                    SignalKind.Bool);
                readers.Add(new Reader
                {
                    signal = moving,
                    read = () => client.InTransition(layer) ? 1f : 0f,
                });
            }
        }

        static SignalKind KindOf(UnityEngine.AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case UnityEngine.AnimatorControllerParameterType.Bool:
                case UnityEngine.AnimatorControllerParameterType.Trigger:
                    return SignalKind.Bool;
                case UnityEngine.AnimatorControllerParameterType.Int:
                    return SignalKind.Int;
                default:
                    return SignalKind.Float;
            }
        }
    }
}
