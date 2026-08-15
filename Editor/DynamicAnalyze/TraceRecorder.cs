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
        /// <summary>One list per remote, because "how far behind" is a question about one
        /// person: two of them are behind on different things at different moments, and a row
        /// that averaged them would describe nobody.</summary>
        readonly List<Lag>[] _lags;
        readonly SimClient _local;
        readonly SimClient[] _remotes;
        readonly SignalTrace.Signal _sent;
        readonly SignalTrace.Signal[] _lost, _here;

        public SignalTrace Trace { get; } = new SignalTrace();

        public TraceRecorder(AnimatorController controller, List<SimClient> clients,
            bool wire, bool lagRows)
        {
            foreach (var client in clients) Declare(controller, client);
            if (clients.Count > 0) _local = clients[0];
            _remotes = new SimClient[Mathf.Max(0, clients.Count - 1)];
            for (int i = 0; i < _remotes.Length; i++) _remotes[i] = clients[i + 1];

            if (wire)
            {
                // One row for the send, because there is one send: the wearer reads its values
                // once and everybody gets that reading.
                _sent = Trace.Declare(Simulation.WireScope, "sample", SignalKind.Bool);
                _lost = new SignalTrace.Signal[_remotes.Length];
                _here = new SignalTrace.Signal[_remotes.Length];
                for (int i = 0; i < _remotes.Length; i++)
                    _lost[i] = Trace.Declare(Simulation.WireScope,
                        Simulation.WireRowAt("lost", i), SignalKind.Bool);
                // When each other person turned up. Nothing about their copy means anything
                // before this, and a flat line that suddenly starts is the clearest way to say
                // so on a waveform.
                for (int i = 0; i < _remotes.Length; i++)
                    _here[i] = Trace.Declare(Simulation.WireScope,
                        Simulation.WireRowAt("remote here", i), SignalKind.Bool);
            }
            if (!lagRows || _remotes.Length == 0) return;
            _lags = new List<Lag>[_remotes.Length];
            for (int i = 0; i < _remotes.Length; i++)
            {
                _lags[i] = new List<Lag>();
                foreach (var parameter in controller.parameters)
                    _lags[i].Add(new Lag
                    {
                        parameter = parameter.name,
                        signal = Trace.Declare(Simulation.LagScopeAt(i), parameter.name,
                            SignalKind.Float),
                    });
            }
        }

        void Declare(AnimatorController controller, SimClient client)
        {
            foreach (var parameter in controller.parameters)
            {
                string name = parameter.name;
                var signal = Trace.Declare(client.Scope, name, KindOf(parameter.type));
                _readers.Add(new Reader { signal = signal, read = () => client.Sample(name) });
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
                // WHICH transition, which the pair above cannot say between them: the state row
                // names where the layer arrived, and a state can be arrived at by several
                // routes. Added beside them rather than folded into either, because a saved run
                // and a ghost comparison are read by row name — the two that were there before
                // go on meaning exactly what they meant.
                var via = Trace.Declare(client.Scope, layers[i].name + "/via",
                    SignalKind.State, client.TransitionLabels(layer));
                _readers.Add(new Reader
                {
                    signal = via,
                    read = () => client.CurrentTransition(layer),
                });
            }
        }

        /// <summary>One frame of everything. The two per-remote arrays are indexed the way the
        /// wire indexes its remotes; null is a run with no wire at all.</summary>
        public void Record(float time, float step, bool sampled, bool[] dropped, bool[] here)
        {
            Trace.Frame(time, step);
            foreach (var reader in _readers) reader.signal.Push(reader.read());
            if (_sent != null) _sent.Push(sampled ? 1f : 0f);
            for (int i = 0; _lost != null && i < _lost.Length; i++)
                _lost[i].Push(dropped != null && i < dropped.Length && dropped[i] ? 1f : 0f);
            for (int i = 0; _here != null && i < _here.Length; i++)
                _here[i].Push(Present(here, i) ? 1f : 0f);

            for (int i = 0; _lags != null && i < _lags.Length; i++)
            {
                bool present = Present(here, i);
                foreach (var lag in _lags[i])
                {
                    // Nobody to be behind until they are there.
                    if (!present)
                    {
                        lag.lastAgreed = time;
                        lag.signal.Push(0f);
                        continue;
                    }
                    // Agreement, not arrival: a value that never left the wearer and a value
                    // that arrived and was overwritten are the same thing to whoever is looking
                    // at the avatar, and this row is about them.
                    if (Mathf.Abs(_local.Sample(lag.parameter) - _remotes[i].Sample(lag.parameter))
                        <= SameEnough)
                        lag.lastAgreed = time;
                    lag.signal.Push(time - lag.lastAgreed);
                }
            }
        }

        static bool Present(bool[] here, int index) =>
            here == null || index >= here.Length || here[index];

        static SignalKind KindOf(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                    return SignalKind.Bool;
                // Its own kind rather than a Bool that happens to blink: a trigger is written by
                // pressing rather than by setting, and a row that offered a checkbox for one
                // would be offering the wrong control.
                case AnimatorControllerParameterType.Trigger:
                    return SignalKind.Trigger;
                case AnimatorControllerParameterType.Int:
                    return SignalKind.Int;
                default:
                    return SignalKind.Float;
            }
        }
    }
}
