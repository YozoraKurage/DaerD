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
    /// </summary>
    static class Simulation
    {
        /// <summary>The scope a single-client run records under.</summary>
        public const string LocalScope = "Local";

        public static SignalTrace Run(AnimatorController controller, SimClock clock = null,
            Stimulus stimulus = null)
        {
            var trace = new SignalTrace();
            if (controller == null) return trace;
            clock = clock ?? new SimClock();

            using (var client = new SimClient(controller, LocalScope, true, clock.seed))
            {
                var readers = Declare(trace, controller, client);
                var steps = clock.Steps();
                var pending = stimulus != null ? stimulus.InOrder() : new List<Stimulus.Entry>();

                int next = 0;
                float time = 0f;
                for (int frame = 0; frame < steps.Length; frame++)
                {
                    // Inputs land before the frame they are timed for, so "at 1.0 s the toggle
                    // went on" means the first frame that starts at or after 1.0 s runs with it
                    // on — never the frame before, and never twice.
                    while (next < pending.Count && pending[next].atSeconds <= time)
                    {
                        var entry = pending[next++];
                        if (string.IsNullOrEmpty(entry.scope) || entry.scope == client.Scope)
                            client.Write(entry.parameter, entry.value);
                    }

                    client.Step(steps[frame]);
                    time += steps[frame];
                    trace.Frame(time, steps[frame]);
                    foreach (var reader in readers) reader.Sample();
                }
            }
            return trace;
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
        static List<Reader> Declare(SignalTrace trace, AnimatorController controller,
            SimClient client)
        {
            var readers = new List<Reader>();
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
            return readers;
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
