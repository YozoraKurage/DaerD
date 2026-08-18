using System.Collections.Generic;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>What a signal's numbers mean, which is what a viewer needs to draw them.</summary>
    enum SignalKind
    {
        /// <summary>A continuous value — drawn as a line.</summary>
        Float,
        /// <summary>A whole number — drawn as a line, labelled without a fraction.</summary>
        Int,
        /// <summary>0 or 1 — drawn as a square wave.</summary>
        Bool,
        /// <summary>An index into <see cref="SignalTrace.Signal.labels"/> — drawn as a named
        /// band, the way a waveform viewer draws a bus.</summary>
        State,
        /// <summary>0 or 1 like a <see cref="Bool"/>, but a press rather than a state: it goes
        /// up when somebody sets it and comes back down of its own accord when a transition
        /// takes it. Written by pushing a button, never by typing a value — which is the whole
        /// reason it is not a Bool. Last on purpose: a saved run's manifest stores these as
        /// numbers, and every kind that existed before keeps the number it had.</summary>
        Trigger,
    }

    /// <summary>
    /// One run, recorded: every signal's value at every frame, and the clock that produced
    /// them. This is the product — the window is a viewer over it, and a test is a viewer over
    /// it too, which is what lets the hard part be checked without a UI.
    ///
    /// Column-oriented, one array per signal, because everything asked of it is asked down a
    /// column: draw this signal across the run, find where it changed, compare two runs of the
    /// same signal. A row per frame would be the wrong shape for all three and a much larger
    /// number of objects for a run of any length.
    ///
    /// Signals are namespaced by <see cref="Signal.scope"/> — one client today, and the
    /// wearer's and a remote's copies of the same parameter tomorrow, which have to sit side by
    /// side under the same name to be worth looking at.
    /// </summary>
    sealed class SignalTrace
    {
        internal sealed class Signal
        {
            /// <summary>Which client this came from ("Local"). Empty for the run's own signals.</summary>
            public string scope = string.Empty;
            /// <summary>Parameter name, or "layer/state" for a layer's current state.</summary>
            public string name = string.Empty;
            public SignalKind kind;
            /// <summary>The names a <see cref="SignalKind.State"/> signal's values index into.
            /// Null for every other kind.</summary>
            public string[] labels;

            internal readonly List<float> samples = new List<float>();

            /// <summary>Whether this signal ever moved. Kept as it is written rather than
            /// worked out afterwards, because it is asked once per row per repaint and a run
            /// has hundreds of rows — and because a live session would have to answer it again
            /// every frame. Sticky: a signal that moved and then went quiet stays listed, which
            /// is what a reader watching it wants.</summary>
            public bool Moved { get; private set; }

            internal void Push(float value)
            {
                if (!Moved && samples.Count > 0
                    && !Mathf.Approximately(samples[samples.Count - 1], value))
                    Moved = true;
                samples.Add(value);
            }

            public int Frames => samples.Count;

            public float At(int frame) =>
                frame >= 0 && frame < samples.Count ? samples[frame] : 0f;

            /// <summary>The value as the viewer should print it — the label behind a state
            /// index, a whole number for an Int, "on"/"off" for a Bool.</summary>
            public string TextAt(int frame)
            {
                float value = At(frame);
                switch (kind)
                {
                    case SignalKind.State:
                        int at = Mathf.RoundToInt(value);
                        return labels != null && at >= 0 && at < labels.Length
                            ? labels[at] : "—";
                    case SignalKind.Bool:
                    case SignalKind.Trigger:
                        return value != 0f ? "1" : "0";
                    case SignalKind.Int:
                        return Mathf.RoundToInt(value).ToString();
                    default:
                        return value.ToString("0.###");
                }
            }

            /// <summary>Whether this frame differs from the one before it — every edge a
            /// viewer draws and every "when did this change" a test asks.</summary>
            public bool ChangedAt(int frame) =>
                frame > 0 && frame < samples.Count
                    && !Mathf.Approximately(samples[frame], samples[frame - 1]);

            public string Path =>
                string.IsNullOrEmpty(scope) ? name : scope + "/" + name;
        }

        readonly List<Signal> _signals = new List<Signal>();
        readonly List<float> _time = new List<float>();
        readonly List<float> _step = new List<float>();

        public IReadOnlyList<Signal> Signals => _signals;

        /// <summary>Frames recorded. Every signal has exactly this many samples — a signal is
        /// declared before the run and written once per frame, so there are no gaps to
        /// interpolate across.</summary>
        public int Frames => _time.Count;

        /// <summary>
        /// Frames recorded since the trace began, counting the ones <see cref="Trim"/> has
        /// since dropped. <see cref="Frames"/> stops climbing once a live session reaches its
        /// cap, which makes it useless for "has anything happened since I last looked"; this
        /// never stops, so anything caching a reading of the trace can tell how much of it it
        /// has not seen.
        /// </summary>
        public int Recorded { get; private set; }

        /// <summary>Simulated seconds elapsed at the END of this frame. The end rather than the
        /// start because the sample is taken after the frame ran, and a cursor over a waveform
        /// is asking what the value became.</summary>
        public float TimeAt(int frame) =>
            frame >= 0 && frame < _time.Count ? _time[frame] : 0f;

        /// <summary>
        /// When this frame BEGAN. The samples are taken at the end of a frame, so a value that
        /// first reads differently at frame k was caused by something that was true when k
        /// started — which is the moment to say it again if the run is to be repeated.
        /// </summary>
        public float StartOfFrame(int frame) => TimeAt(frame) - StepAt(frame);

        /// <summary>How long this frame was — the jitter, visible.</summary>
        public float StepAt(int frame) =>
            frame >= 0 && frame < _step.Count ? _step[frame] : 0f;

        public float Duration => Frames > 0 ? _time[_time.Count - 1] : 0f;

        public Signal Find(string scope, string name)
        {
            foreach (var signal in _signals)
                if (signal.scope == scope && signal.name == name)
                    return signal;
            return null;
        }

        /// <summary>The frame whose end is at or after this time — what a viewer's cursor and
        /// a "what did it look like at 3.2 s" question both resolve to.</summary>
        public int FrameAt(float seconds)
        {
            for (int i = 0; i < _time.Count; i++)
                if (_time[i] >= seconds) return i;
            return Mathf.Max(0, Frames - 1);
        }

        // ---- recording ------------------------------------------------------

        internal Signal Declare(string scope, string name, SignalKind kind, string[] labels = null)
        {
            var signal = new Signal
            {
                scope = scope ?? string.Empty,
                name = name ?? string.Empty,
                kind = kind,
                labels = labels,
            };
            _signals.Add(signal);
            return signal;
        }

        internal void Frame(float time, float step)
        {
            _time.Add(time);
            _step.Add(step);
            Recorded++;
        }

        /// <summary>
        /// Drops the oldest frames so a session that never ends stays a fixed size. Times are
        /// left as they were — they count from the start of the session, not from the start of
        /// what is still kept, so the ruler goes on counting up and a moment does not change
        /// its name when the window slides past it.
        /// </summary>
        internal void Trim(int keep)
        {
            int drop = Frames - Mathf.Max(1, keep);
            if (drop <= 0) return;
            _time.RemoveRange(0, drop);
            _step.RemoveRange(0, drop);
            foreach (var signal in _signals)
                signal.samples.RemoveRange(0, Mathf.Min(drop, signal.samples.Count));
        }
    }
}
