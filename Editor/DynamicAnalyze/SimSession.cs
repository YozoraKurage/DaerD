using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The same run, kept open. A <see cref="Simulation"/> run is an experiment written down in
    /// advance and read afterwards; a session is one you are standing inside — the clients stay
    /// alive between frames, the editor's own update drives them, and a value poked while it
    /// runs takes effect in the very next frame it steps.
    ///
    /// Which is the point: a controller is easiest to understand by pushing on it and watching,
    /// and no amount of writing a stimulus down in advance replaces having done that once.
    ///
    /// Everything else is the batch run's — the same clients, the same wire, the same recorder,
    /// so the trace a session produces is the same shape a run produces and the same viewer
    /// reads both. The only differences are that time comes from the editor rather than from an
    /// array, and that the recording is a window rather than the whole thing: a session left
    /// open all afternoon keeps its last <see cref="Window"/> frames and forgets the rest.
    /// </summary>
    sealed class SimSession : IDisposable
    {
        /// <summary>Frames one Advance will ever step, however long the editor was away. A
        /// session that tried to catch up on a domain reload would freeze the editor doing it,
        /// and the honest answer to "you were gone" is that the run went on without you.</summary>
        public const int MaxCatchUp = 16;

        readonly List<SimClient> _clients = new List<SimClient>();
        readonly TraceRecorder _recorder;
        readonly SimSettings _settings;
        SimRandom _jitter;
        readonly SimRandom[] _loss;
        readonly bool[] _arrived;
        readonly bool[] _dropped;
        float _time;
        float _carry;
        float _nextSample;

        public SignalTrace Trace => _recorder.Trace;

        /// <summary>How many frames of history to keep. The clock's own frame count, so
        /// "60 fps for 10 seconds" describes the window as well as it described the run.</summary>
        public int Window { get; set; }

        /// <summary>Simulated seconds since the session opened. Keeps counting across a
        /// trimmed window — a moment does not change its name because the history slid.</summary>
        public float Time => _time;

        public bool HasRemote => _clients.Count > 1;

        /// <summary>How many other people this session is running.</summary>
        public int Remotes => Mathf.Max(0, _clients.Count - 1);

        public SimSession(AnimatorController controller, SimSettings settings)
        {
            _settings = settings ?? new SimSettings();
            var clock = _settings.clock ?? new SimClock();
            var wire = _settings.wire;
            _jitter = new SimRandom(clock.seed);
            Window = Mathf.Max(60, clock.Frames);

            int remotes = wire != null ? wire.Remotes : 0;
            _clients.Add(new SimClient(controller, Simulation.LocalScope, true, clock.seed));
            for (int i = 0; i < remotes; i++)
                _clients.Add(new SimClient(controller, Simulation.RemoteScopeAt(i), false,
                    Simulation.ClientSeed(clock.seed, i)));
            _recorder = new TraceRecorder(controller, _clients, wire != null, _settings.lagRows);

            _loss = new SimRandom[remotes];
            _arrived = new bool[remotes];
            _dropped = new bool[remotes];
            for (int i = 0; i < remotes; i++)
            {
                _loss[i] = new SimRandom(Simulation.LossSeed(wire.seed, i));
                // See Simulation.Run: joining at zero is loading with the wearer, which hands
                // nothing over because there is nothing yet to hand.
                _arrived[i] = wire.JoinsAt(i) <= 0f;
            }
            _nextSample = remotes > 0 ? wire.EarliestJoin + wire.Interval : float.MaxValue;
        }

        /// <summary>
        /// Steps as many frames as the elapsed wall-clock time has paid for, and keeps the
        /// remainder for next time so a fast editor and a slow one run the same simulation at
        /// the same speed.
        /// </summary>
        public int Advance(float realSeconds)
        {
            if (realSeconds <= 0f) return 0;
            _carry += realSeconds;
            int stepped = 0;
            while (stepped < MaxCatchUp)
            {
                float step = NextStep();
                if (_carry < step) break;
                _carry -= step;
                StepOnce(step);
                stepped++;
            }
            // Whatever is left after the cap is time the session was not there for. Keeping it
            // would only make the next Advance longer, and the one after that longer again.
            if (stepped >= MaxCatchUp) _carry = 0f;
            return stepped;
        }

        /// <summary>One frame, whatever asked for it — the transport's step button and the
        /// clock both come through here.</summary>
        public void StepOnce() => StepOnce(NextStep());

        void StepOnce(float step)
        {
            var wire = _settings.wire;
            bool sampled = false;
            for (int i = 0; i < _arrived.Length; i++) _dropped[i] = false;
            for (int i = 0; i < _arrived.Length; i++)
            {
                if (_arrived[i] || _time < wire.JoinsAt(i)) continue;
                // Arriving is itself a delivery — see Simulation.Run.
                _arrived[i] = true;
                sampled = true;
                Simulation.Carry(wire, _clients[0], _clients[i + 1]);
            }
            while (_time >= _nextSample)
            {
                _nextSample += wire.Interval;
                sampled = true;
                for (int i = 0; i < _arrived.Length; i++)
                {
                    if (!_arrived[i]) continue;
                    if (_loss[i].NextChance(wire.dropChance)) _dropped[i] = true;
                    else Simulation.Carry(wire, _clients[0], _clients[i + 1]);
                }
            }

            for (int i = 0; i < _clients.Count; i++)
            {
                if (i > 0 && !_arrived[i - 1]) continue;
                _clients[i].Step(step);
            }
            _time += step;
            _recorder.Record(_time, step, sampled, _dropped, _arrived);
            _recorder.Trace.Trim(Window);
        }

        float NextStep()
        {
            var clock = _settings.clock ?? new SimClock();
            float spread = Mathf.Clamp(clock.jitter, 0f, SimClock.MaximumJitter);
            return spread <= 0f
                ? clock.NominalStep
                : Mathf.Max(SimClock.MinimumStep,
                    clock.NominalStep * (1f + spread * _jitter.NextSigned()));
        }

        // ---- reaching in ----------------------------------------------------

        /// <summary>Every parameter, in the order the controller declares them.</summary>
        public IEnumerable<string> Parameters(AnimatorController controller)
        {
            foreach (var parameter in controller.parameters) yield return parameter.name;
        }

        public bool Has(string parameter) => _clients.Count > 0 && _clients[0].Has(parameter);

        public AnimatorControllerParameterType TypeOf(string parameter) =>
            _clients.Count > 0 ? _clients[0].TypeOf(parameter)
                : AnimatorControllerParameterType.Float;

        public float Read(string scope, string parameter)
        {
            foreach (var client in _clients)
                if (client.Scope == scope) return client.Read(parameter);
            return 0f;
        }

        /// <summary>A poke, live. Lands on the next frame the session steps — never inside the
        /// one already recorded, which would make the trace disagree with itself.</summary>
        public void Write(string scope, string parameter, float value)
        {
            Simulation.Poke(_clients, new Stimulus.Entry
            {
                scope = scope ?? string.Empty,
                parameter = parameter,
                value = value,
            });
        }

        public void Dispose()
        {
            foreach (var client in _clients) client.Dispose();
            _clients.Clear();
        }
    }
}
