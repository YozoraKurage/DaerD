using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// Declares what a run watches and writes one sample of it per frame. Shared by the run
    /// that is computed whole and the session that is stepped live, so the two produce traces
    /// of exactly the same shape — which is what lets one viewer, and one set of tests, read
    /// both.
    ///
    /// Everything is recorded rather than what was asked for: which signals are interesting IS
    /// the question, a viewer can hide rows, and a run that recorded only the asked-for ones
    /// would have to be run again to answer the next question.
    /// </summary>
    sealed class TraceRecorder
    {
        /// <summary>How far apart two Floats may be and still count as the same value. The
        /// wire rounds to 8 bits over -1..1, so half a step — about 0.004 — is the most a
        /// remote that HAS caught up can differ by, and a tolerance under that would call it
        /// behind forever.</summary>
        public const float SameEnough = 0.006f;

        sealed class Reader
        {
            public SignalTrace.Signal signal;
            public System.Func<float> read;
        }

        /// <summary>One parameter's answer to "how long has the other person been looking at
        /// something else" — the remote view, as a number.</summary>
        sealed class Lag
        {
            public SignalTrace.Signal signal;
            public string parameter;
            public float lastAgreed;
        }

        readonly List<Reader> _readers = new List<Reader>();
        readonly List<Lag> _lags = new List<Lag>();
        readonly SimClient _local, _remote;
        readonly SignalTrace.Signal _sent, _lost;

        public SignalTrace Trace { get; } = new SignalTrace();

        public TraceRecorder(AnimatorController controller, List<SimClient> clients,
            bool wire, bool lagRows)
        {
            foreach (var client in clients) Declare(controller, client);
            if (clients.Count > 0) _local = clients[0];
            if (clients.Count > 1) _remote = clients[1];

            if (wire)
            {
                _sent = Trace.Declare(Simulation.WireScope, "sample", SignalKind.Bool);
                _lost = Trace.Declare(Simulation.WireScope, "lost", SignalKind.Bool);
            }
            if (lagRows && _remote != null)
                foreach (var parameter in controller.parameters)
                    _lags.Add(new Lag
                    {
                        parameter = parameter.name,
                        signal = Trace.Declare(Simulation.LagScope, parameter.name,
                            SignalKind.Float),
                    });
        }

        void Declare(AnimatorController controller, SimClient client)
        {
            foreach (var parameter in controller.parameters)
            {
                string name = parameter.name;
                var signal = Trace.Declare(client.Scope, name, KindOf(parameter.type));
                _readers.Add(new Reader { signal = signal, read = () => client.Read(name) });
            }

            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                int layer = i;
                var state = Trace.Declare(client.Scope, layers[i].name + "/state",
                    SignalKind.State, client.StateLabels(layer));
                _readers.Add(new Reader { signal = state, read = () => client.CurrentState(layer) });
                // Worth its own row: a layer that spends the run mid-blend looks identical to a
                // settled one if all you can see is which state it is in.
                var moving = Trace.Declare(client.Scope, layers[i].name + "/transition",
                    SignalKind.Bool);
                _readers.Add(new Reader
                {
                    signal = moving,
                    read = () => client.InTransition(layer) ? 1f : 0f,
                });
            }
        }

        /// <summary>One frame of everything.</summary>
        public void Record(float time, float step, bool sampled, bool dropped)
        {
            Trace.Frame(time, step);
            foreach (var reader in _readers) reader.signal.samples.Add(reader.read());
            if (_sent != null) _sent.samples.Add(sampled ? 1f : 0f);
            if (_lost != null) _lost.samples.Add(dropped ? 1f : 0f);

            foreach (var lag in _lags)
            {
                // Agreement, not arrival: a value that never left the wearer and a value that
                // arrived and was overwritten are the same thing to whoever is looking at the
                // avatar, and this row is about them.
                if (Mathf.Abs(_local.Read(lag.parameter) - _remote.Read(lag.parameter))
                    <= SameEnough)
                    lag.lastAgreed = time;
                lag.signal.samples.Add(time - lag.lastAgreed);
            }
        }

        static SignalKind KindOf(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return SignalKind.Bool;
                case AnimatorControllerParameterType.Int:
                    return SignalKind.Int;
                default:
                    return SignalKind.Float;
            }
        }
    }
}
