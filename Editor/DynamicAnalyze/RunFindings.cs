using System.Collections.Generic;
using UnityEngine;
using Yozolab.DaerD.Analyze;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// What this run DID find, read back off the finished trace.
    ///
    /// <see cref="SimNotes"/> is the other half of the pair and speaks before the run: it says
    /// what a result cannot promise. Nothing said what the result actually contained — a trace
    /// is hundreds of rows over thousands of frames, and every question worth asking of it
    /// ("does this state ever happen", "does this change survive the wire") was a question a
    /// person had to answer by eye, on a picture, once per run. These are the ones the trace can
    /// answer by itself.
    ///
    /// Pure, and over the trace alone: it reads what was recorded rather than the controller, so
    /// a finding is about the run that happened and not about a run that could. That is the
    /// whole difference from static analysis — <c>ControllerReachability</c> can say a state has
    /// no route to it, and only this can say a state that has one was never in it.
    ///
    /// <paramref name="settings"/> may be null, which is a trace whose provenance is gone (a
    /// clip opened from disk). The findings that need to know what was synced and what was
    /// pressed are then skipped rather than guessed at: half of a run's settings inferred from
    /// its own output would produce findings that read exactly like the ones that mean
    /// something.
    ///
    /// NOT FOUND HERE, on purpose:
    ///
    /// - Anything about async sync's own machinery — Ready and Stale flags, a lap of the ring,
    ///   a step that never sent. Reading those means knowing which parameters are a cycle's, and
    ///   that lives in AsyncSync*, on the core side. DynamicAnalyze is kept liftable into its own
    ///   assembly, and a finding is not worth a dependency running the wrong way; if it becomes
    ///   worth it, the direction to add is a description the core hands down, not a lookup this
    ///   module makes.
    /// - Cross-checking against the static reachability analysis ("unreachable and never
    ///   entered", "reachable and never entered"). Same dependency, and the more useful half of
    ///   it is already here: a state never entered is worth reading whether or not anything
    ///   could have entered it.
    /// - Parameters that never moved. The waveform's Moved filter has answered that from the
    ///   beginning, in the place where a reader is already looking, and a finding repeating it
    ///   would be a line of text saying what an empty row says better.
    /// </summary>
    static class RunFindings
    {
        /// <summary>The row a layer's current state is recorded on, and the row naming the
        /// transition it is blending through. Findings are matched by row name because the trace
        /// is the product: a run reloaded from a file has the same rows and no controller
        /// behind them.</summary>
        const string StateRow = "/state";

        const string ViaRow = "/via";

        /// <summary>The wire's "a sample went out on this frame" row. See TraceRecorder, which
        /// declares it — one row for the send, because there is one send.</summary>
        const string SampleRow = "sample";

        public static List<string> For(SignalTrace trace, SimSettings settings)
        {
            var findings = new List<string>();
            if (trace == null || trace.Frames == 0) return findings;

            StatesNeverEntered(trace, findings);
            TransitionsNeverSeen(trace, findings);

            var wire = settings != null ? settings.wire : null;
            if (wire == null) return findings;
            ChangesThatDiedInsideOnePeriod(trace, wire, findings);
            ValuesTheWireCouldNotCarry(trace, wire, findings);
            InputsThatNeverLeave(trace, settings.stimulus, wire, findings);
            return findings;
        }

        /// <summary>
        /// States nobody was ever in. Per client and per layer, because those are the two things
        /// that make it a different answer: a state the wearer reaches and a remote does not is
        /// the finding a two-client run exists for, and a layer is the unit a reader thinks in.
        ///
        /// A state the run was still blending INTO when it ended counts as not entered, which is
        /// true — the state row names where the layer is, and it was not there yet.
        /// </summary>
        static void StatesNeverEntered(SignalTrace trace, List<string> findings)
        {
            foreach (var signal in trace.Signals)
            {
                string layer = LayerOf(signal, StateRow);
                if (layer == null) continue;
                var missing = Unseen(signal);
                if (missing.Count == 0) continue;
                findings.Add(L.Tr(
                    "{0} never enters {1} of layer {2}'s states ({3}). Nothing this run did asked for them, which is not the same as nothing being able to — only a run that entered them tells those two apart.",
                    signal.scope, missing.Count, layer, Join(missing)));
            }
        }

        /// <summary>
        /// Transitions never caught running. The via row names the transition a layer is
        /// blending through, and it can only name one WHILE it blends — so this finding is
        /// bounded twice over, and says so rather than overstating itself:
        ///
        /// a transition the row cannot tell from another (two routes between one pair of states,
        /// a sub-machine and an Exit out of one state) is not in the row's labels at all, so it
        /// is neither accused nor cleared here; and a transition that finishes inside the frame
        /// it starts on — a duration of zero, which is most VRChat toggles — is never seen even
        /// though it fired.
        ///
        /// The second one would make this finding actively wrong, so a layer that MOVED without
        /// the row naming what moved it is left alone entirely. A layer where every change of
        /// state was preceded by a named blend is one the row kept up with, and only there is
        /// "never fired" a claim this run can make. The cost is that a layer with one instant
        /// transition in it reports nothing about the rest — deliberate, because the alternative
        /// is naming a transition that ran as one that never did.
        /// </summary>
        static void TransitionsNeverSeen(SignalTrace trace, List<string> findings)
        {
            foreach (var signal in trace.Signals)
            {
                string layer = LayerOf(signal, ViaRow);
                if (layer == null) continue;
                var state = trace.Find(signal.scope, layer + StateRow);
                if (state == null || !NamedEveryMove(state, signal)) continue;
                var missing = Unseen(signal);
                if (missing.Count == 0) continue;
                findings.Add(L.Tr(
                    "{0} is never seen in {1} of layer {2}'s transitions ({3}). Read off the via row, which catches a transition only while it is blending — a route this run cannot name is neither counted here nor cleared.",
                    signal.scope, missing.Count, layer, Join(missing)));
            }
        }

        /// <summary>Whether the via row named something for every move the layer made. The
        /// state row changes on the frame AFTER a blend finishes, so the frame before the change
        /// is where the name was — a move with neither is one nothing was watching.</summary>
        static bool NamedEveryMove(SignalTrace.Signal state, SignalTrace.Signal via)
        {
            for (int frame = 1; frame < state.Frames; frame++)
                if (state.ChangedAt(frame) && via.At(frame) < 0f && via.At(frame - 1) < 0f)
                    return false;
            return true;
        }

        /// <summary>
        /// The wire's defining failure, counted. It samples the wearer's values whole once a
        /// period, so a value that changed and was back where it started by the next reading was
        /// never sent — no loss, no delay, simply nothing to receive. It is the reason a decoder
        /// that fires on a value CHANGING can miss a step, and it is invisible on a waveform
        /// unless somebody happens to line the edges up against the sample row.
        ///
        /// Between two samples only. Before the first one nothing has crossed at all, and a
        /// change there was not missed by a sample — it was made before there was a sample to
        /// miss it, and the first one hands over whatever is current anyway.
        ///
        /// Read off the recorded rows, which are written at the END of a frame while the wire
        /// read its values just before that frame ran. The two differ by a frame at the edges,
        /// so a change that lives for exactly one frame either side of a sample can be counted
        /// or not; anything worth calling a lost change is many frames wide at 60 fps.
        /// </summary>
        static void ChangesThatDiedInsideOnePeriod(SignalTrace trace, SyncWire wire,
            List<string> findings)
        {
            var samples = SampleFrames(trace);
            if (samples.Count < 2) return;

            var counted = new List<string>();
            int total = 0;
            foreach (var signal in Synced(trace, wire))
            {
                int lost = 0;
                for (int i = 1; i < samples.Count; i++)
                {
                    int from = samples[i - 1], to = samples[i];
                    if (!Mathf.Approximately(signal.At(from), signal.At(to))) continue;
                    for (int frame = from + 1; frame < to; frame++)
                        if (!Mathf.Approximately(signal.At(frame), signal.At(from)))
                        {
                            lost++;
                            break;
                        }
                }
                if (lost == 0) continue;
                total += lost;
                counted.Add(signal.name + " ×" + lost);
            }
            if (counted.Count == 0) return;
            findings.Add(L.Tr(
                "{0} change(s) came and went inside one sync period and never left the wearer ({1}). The wire reads the whole set once a period, so a value back where it started by the next reading was never sent at all.",
                total, Join(counted)));
        }

        /// <summary>
        /// Values the sample cannot hold. A Float crosses as 8 bits over -1..1 and an Int as a
        /// byte over 0..255, and a value outside those does not arrive late — it arrives
        /// different, which is the single most surprising thing about a synced Float and the one
        /// a waveform hides completely: both rows look settled, at two different heights.
        ///
        /// Nothing to say with quantize off. That run is deliberately not modelling the wire's
        /// arithmetic, and reporting rounding it was told to skip would be reporting the
        /// settings back.
        ///
        /// One entry per parameter, at the first sample that could not carry it: a radial
        /// dragged out of range holds a hundred frames of the same finding, and a list of them
        /// says nothing the first does not.
        /// </summary>
        static void ValuesTheWireCouldNotCarry(SignalTrace trace, SyncWire wire,
            List<string> findings)
        {
            if (!wire.quantize) return;
            var samples = SampleFrames(trace);
            if (samples.Count == 0) return;

            var changed = new List<string>();
            foreach (var signal in Synced(trace, wire))
            {
                foreach (int frame in samples)
                {
                    float value = signal.At(frame);
                    if (!OutOfRange(signal.kind, value)) continue;
                    changed.Add(signal.name + " " + Number(signal.kind, value) + " → "
                        + Number(signal.kind, wire.Compress(value, TypeOf(signal.kind))));
                    break;
                }
            }
            if (changed.Count == 0) return;
            findings.Add(L.Tr(
                "{0} synced value(s) arrive changed rather than late ({1}). A Float crosses as 8 bits over -1..1 and an Int as a byte over 0..255, so a value outside that is a different number when it lands.",
                changed.Count, Join(changed)));
        }

        /// <summary>
        /// Inputs that go nowhere. Pressing something on the wearer that is not on the wire is
        /// the commonest way a run looks right and an avatar does not: the wearer's copy does
        /// everything asked of it, and nobody else ever sees any of it.
        ///
        /// Built-ins are not a mistake here however they are pressed — VRChat carries those on
        /// its own channels, which is exactly what makes them absent from an expression
        /// parameter store. A name the controller does not have is left alone too: it did
        /// nothing at all, which is a different complaint and not one of these five.
        /// </summary>
        static void InputsThatNeverLeave(SignalTrace trace, Stimulus stimulus, SyncWire wire,
            List<string> findings)
        {
            if (stimulus == null) return;
            var stranded = new List<string>();
            // The active tracks only: a muted one is an input the experiment left out,
            // and a warning about something that is not going to happen is noise.
            foreach (var entry in stimulus.Active)
            {
                if (entry == null || string.IsNullOrEmpty(entry.parameter)) continue;
                // Empty is the wearer, who is the one pressing things. An input aimed at
                // somebody else's copy is a run asking what that copy does with it, and whether
                // the wire would have carried it is not the question.
                if (!string.IsNullOrEmpty(entry.scope) && entry.scope != Simulation.LocalScope)
                    continue;
                string name = entry.parameter;
                if (stranded.Contains(name) || VrcParameters.IsBuiltIn(name)) continue;
                if (wire.parameters.Contains(name)) continue;
                if (trace.Find(Simulation.LocalScope, name) == null) continue;
                stranded.Add(name);
            }
            if (stranded.Count == 0) return;
            findings.Add(L.Tr(
                "{0} parameter(s) are pressed here and never leave the wearer ({1}). They are not on the wire and VRChat does not carry them by itself, so whatever they do, they do to one person.",
                stranded.Count, Join(stranded)));
        }

        // ---- reading the trace ----------------------------------------------

        /// <summary>The layer this row belongs to, or null if it is not that kind of row. A
        /// State row with labels is the test rather than the name alone: a parameter may be
        /// called anything, including something ending in "/state".
        ///
        /// Any avatar's rows, not only a simulated client's — a recording taken off Play mode
        /// has states nobody entered exactly the way a run does, and the two findings that read
        /// these rows say nothing about the wire and so nothing about how the rows were got.</summary>
        static string LayerOf(SignalTrace.Signal signal, string suffix)
        {
            if (signal.kind != SignalKind.State || signal.labels == null
                || signal.labels.Length == 0 || !Simulation.IsAvatar(signal.scope))
                return null;
            string name = signal.name ?? string.Empty;
            return name.Length > suffix.Length
                && name.EndsWith(suffix, System.StringComparison.Ordinal)
                ? name.Substring(0, name.Length - suffix.Length) : null;
        }

        /// <summary>The labels of a State row that no frame of it ever held. -1 — nothing this
        /// run can name — indexes nothing and is left out of both directions.</summary>
        static List<string> Unseen(SignalTrace.Signal signal)
        {
            var seen = new bool[signal.labels.Length];
            for (int frame = 0; frame < signal.Frames; frame++)
            {
                int at = Mathf.RoundToInt(signal.At(frame));
                if (at >= 0 && at < seen.Length) seen[at] = true;
            }
            var missing = new List<string>();
            for (int i = 0; i < seen.Length; i++)
                if (!seen[i]) missing.Add(signal.labels[i]);
            return missing;
        }

        static List<int> SampleFrames(SignalTrace trace)
        {
            var frames = new List<int>();
            var sample = trace.Find(Simulation.WireScope, SampleRow);
            if (sample == null) return frames;
            for (int frame = 0; frame < sample.Frames; frame++)
                if (sample.At(frame) != 0f) frames.Add(frame);
            return frames;
        }

        /// <summary>The wearer's rows for what the sample actually carries. A built-in named in
        /// the list is skipped the way <see cref="Simulation.Carry"/> skips it — it travels by
        /// VRChat's arrangement, so nothing about this wire applies to it.</summary>
        static IEnumerable<SignalTrace.Signal> Synced(SignalTrace trace, SyncWire wire)
        {
            foreach (var name in wire.parameters)
            {
                if (VrcParameters.IsBuiltIn(name)) continue;
                var signal = trace.Find(Simulation.LocalScope, name);
                if (signal != null) yield return signal;
            }
        }

        static bool OutOfRange(SignalKind kind, float value)
        {
            switch (kind)
            {
                case SignalKind.Float:
                    return value < -1f || value > 1f;
                case SignalKind.Int:
                    return value < 0f || value > 255f;
                default:
                    return false;
            }
        }

        static AnimatorControllerParameterType TypeOf(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.Bool:
                    return AnimatorControllerParameterType.Bool;
                case SignalKind.Trigger:
                    return AnimatorControllerParameterType.Trigger;
                case SignalKind.Int:
                    return AnimatorControllerParameterType.Int;
                default:
                    return AnimatorControllerParameterType.Float;
            }
        }

        /// <summary>A value as the row prints it, so a finding and the waveform under it say
        /// the same number.</summary>
        static string Number(SignalKind kind, float value) =>
            kind == SignalKind.Int
                ? Mathf.RoundToInt(value).ToString() : value.ToString("0.###");

        /// <summary>Names, with a tail rather than a wall of them. A copy of SimNotes' — the
        /// two lists want the same shape and neither wants the other's internals public for the
        /// sake of five lines.</summary>
        static string Join(List<string> names)
        {
            const int shown = 3;
            if (names.Count <= shown) return string.Join(", ", names.ToArray());
            var head = names.GetRange(0, shown);
            return string.Join(", ", head.ToArray())
                + L.Tr(" and {0} more", names.Count - shown);
        }
    }
}
