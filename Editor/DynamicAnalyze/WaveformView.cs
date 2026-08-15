using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.DynamicAnalyze
{
    /// <summary>
    /// The trace, drawn: names down the left, values at the cursor beside them, and the run
    /// itself across the right. A waveform viewer rather than a table because the question a
    /// run answers is nearly always about WHEN — when did this go up, what was that when this
    /// changed, how long between the two — and a table makes every one of those into counting.
    ///
    /// Draws from the trace and holds nothing but where the reader is looking (cursor, zoom,
    /// scroll, filter). Re-running replaces the trace and leaves the view where it was, which
    /// is what makes "change one setting and look at the same moment again" possible.
    /// </summary>
    /// <summary>
    /// Each signal's own vertical range, over the WHOLE run rather than the part on screen — a
    /// line that rescaled itself as it was scrolled would make two moments impossible to
    /// compare by eye.
    ///
    /// Held apart from the drawing because of how a live session grows. A batch run hands the
    /// viewer a brand new trace every time, so "have I measured this one" used to be answered
    /// by comparing the reference — but LIVE keeps appending to the SAME trace, and by that
    /// test it was measured once, at the first repaint, and never again. Every later frame was
    /// then drawn against a range from before it existed: Lag climbs for the whole session, so
    /// a live run walked out of its own scale within seconds and drew over the row above.
    ///
    /// So the reading is kept and EXTENDED rather than rebuilt. Only the frames recorded since
    /// the last look are read, which is what keeps a session that has been running for an hour
    /// costing the same per repaint as one that just started.
    /// </summary>
    sealed class SignalRanges
    {
        // The measured extremes, raw. What to draw against is derived from them rather than
        // stored, so extending a range gives exactly the numbers a measurement from scratch
        // would — the padding a flat signal gets must not compound every time it grows.
        readonly Dictionary<SignalTrace.Signal, Vector2> _seen =
            new Dictionary<SignalTrace.Signal, Vector2>();
        SignalTrace _trace;
        int _recorded;

        /// <summary>What this signal is drawn against. A signal nothing has measured yet gets
        /// 0..1, so a row that arrived this frame draws flat rather than not at all.</summary>
        public Vector2 Of(SignalTrace.Signal signal) =>
            signal != null && _seen.TryGetValue(signal, out var raw)
                ? Shown(signal, raw) : new Vector2(0f, 1f);

        /// <summary>Takes in whatever has been recorded since the last call. Cheap enough to
        /// call every repaint, which is the point of it.</summary>
        public void Update(SignalTrace trace)
        {
            if (!ReferenceEquals(_trace, trace))
            {
                _seen.Clear();
                _trace = trace;
                _recorded = 0;
            }
            if (trace == null) return;

            // Counted off the trace's own total rather than off its length: a live session
            // trims its oldest frames away, so the length stops growing long before the run
            // does. Trimming only ever drops from the front, which is what makes the frames
            // still to be read the LAST few of every signal.
            int fresh = Mathf.Max(0, trace.Recorded - _recorded);
            _recorded = trace.Recorded;
            foreach (var signal in trace.Signals)
            {
                if (signal.kind == SignalKind.State) continue;
                bool known = _seen.TryGetValue(signal, out var raw);
                // A signal declared since the last look has all of its samples to be read.
                int from = known ? Mathf.Max(0, signal.Frames - fresh) : 0;
                if (known && from >= signal.Frames) continue;

                float low = known ? raw.x : float.MaxValue;
                float high = known ? raw.y : float.MinValue;
                for (int frame = from; frame < signal.Frames; frame++)
                {
                    float value = signal.At(frame);
                    low = Mathf.Min(low, value);
                    high = Mathf.Max(high, value);
                }
                _seen[signal] = new Vector2(low, high);
            }
        }

        /// <summary>The extremes turned into something to draw against: a Bool is 0..1 whatever
        /// it happened to do, a signal that never moved gets a band around its one value rather
        /// than a zero-height one, and a signal with no samples at all gets 0..1.</summary>
        static Vector2 Shown(SignalTrace.Signal signal, Vector2 raw)
        {
            float low = raw.x, high = raw.y;
            if (signal.kind == SignalKind.Bool || signal.kind == SignalKind.Trigger)
            { low = 0f; high = 1f; }
            if (low > high) { low = 0f; high = 1f; }
            if (Mathf.Approximately(low, high)) { low -= 0.5f; high += 0.5f; }
            return new Vector2(low, high);
        }
    }

    /// <summary>
    /// The worst any Lag row ever reached, and whose it was. A run's Lag scope is one row per
    /// parameter and the question asked of the whole scope is nearly always the same one — how
    /// far behind did the other person ever get, and on what — so the scope's own header
    /// answers it rather than making a reader open every row and look for the tallest peak.
    ///
    /// Kept and extended for the same reason <see cref="SignalRanges"/> is: a live session
    /// hands the viewer the SAME trace over and over as it grows, and a summary rebuilt from
    /// scratch every repaint would cost the length of the session on every frame of it. Only
    /// the frames recorded since the last look are read, so an hour-old session costs what a
    /// fresh one does.
    /// </summary>
    sealed class LagSummary
    {
        /// <summary>One person's worst moment. Kept per scope because a run can have several
        /// people in it, and the header of "Lag 2" answering with how far behind Remote 1 once
        /// got would be a number about somebody else.</summary>
        sealed class Peak
        {
            public float worst;
            public string parameter;
        }

        readonly Dictionary<string, Peak> _peaks = new Dictionary<string, Peak>();
        SignalTrace _trace;
        int _recorded;
        bool _measured;

        /// <summary>The largest lag any parameter of anybody reached, in seconds. Zero when
        /// nothing has been measured — which is also what a run with no remote reads as.</summary>
        public float Worst { get; private set; }

        /// <summary>Whose it was. Null until something has been behind.</summary>
        public string Parameter { get; private set; }

        /// <summary>Whether there is anything to say. A run where the remote never fell behind
        /// has a worst of zero, and "worst 0 s" is worth saying — it is the good answer.</summary>
        public bool Known => Parameter != null;

        /// <summary>The same, for one person's rows.</summary>
        public bool KnownIn(string scope) => _peaks.ContainsKey(scope);

        public float WorstIn(string scope) =>
            _peaks.TryGetValue(scope, out var peak) ? peak.worst : 0f;

        public string ParameterIn(string scope) =>
            _peaks.TryGetValue(scope, out var peak) ? peak.parameter : null;

        /// <summary>Takes in whatever has been recorded since the last call. Cheap enough to
        /// call every repaint, which is the point of it.</summary>
        public void Update(SignalTrace trace)
        {
            if (!ReferenceEquals(_trace, trace))
            {
                _trace = trace;
                _recorded = 0;
                _measured = false;
                Worst = 0f;
                Parameter = null;
                _peaks.Clear();
            }
            if (trace == null) return;

            // Counted off the trace's own total rather than off its length, because a live
            // session trims its oldest frames away — the same reason SignalRanges does.
            int fresh = Mathf.Max(0, trace.Recorded - _recorded);
            bool first = !_measured;
            _measured = true;
            _recorded = trace.Recorded;
            foreach (var signal in trace.Signals)
            {
                if (!Simulation.IsLag(signal.scope)) continue;
                if (!_peaks.TryGetValue(signal.scope, out var peak))
                    _peaks[signal.scope] = peak = new Peak { parameter = signal.name };
                int from = first ? 0 : Mathf.Max(0, signal.Frames - fresh);
                for (int frame = from; frame < signal.Frames; frame++)
                {
                    float lag = signal.At(frame);
                    // The first sample seen is the worst so far whatever it is, so a run where
                    // nobody ever fell behind still has an answer, and the answer is zero.
                    if (lag > peak.worst)
                    {
                        peak.worst = lag;
                        peak.parameter = signal.name;
                    }
                    if (Parameter != null && lag <= Worst) continue;
                    Worst = lag;
                    Parameter = signal.name;
                }
            }
        }
    }

    /// <summary>
    /// Walks a second run alongside the one on screen, BY TIME.
    ///
    /// Two runs of the same length do not share a frame numbering: jitter makes every frame its
    /// own length, so frame 300 of one run and frame 300 of the other are different moments, and
    /// laying them over each other by number would draw a comparison nobody asked for. What the
    /// reader means by "the same moment" is the same second.
    ///
    /// <see cref="SignalTrace.FrameAt"/> answers that, but by walking the run from the start —
    /// once per pixel that is the length of the run times the width of the window, which is
    /// exactly the cost the drawing was rewritten to avoid. A row is drawn left to right and
    /// time only ever goes forward, so this keeps where it got to and carries on from there:
    /// one pass over the ghost per row, however many pixels the row is asked about.
    /// </summary>
    struct GhostCursor
    {
        readonly SignalTrace _ghost;
        int _at;

        public GhostCursor(SignalTrace ghost)
        {
            _ghost = ghost;
            _at = 0;
        }

        /// <summary>The ghost's frame at this moment — the first whose end is at or after it,
        /// which is what a cursor over a waveform means and what FrameAt would say. Only ever
        /// moves forward, so asking about an earlier moment than the last one gives the frame
        /// it is already on rather than a walk back.</summary>
        public int At(float seconds)
        {
            if (_ghost == null || _ghost.Frames == 0) return -1;
            while (_at + 1 < _ghost.Frames && _ghost.TimeAt(_at) < seconds) _at++;
            return _at;
        }
    }

    sealed class WaveformView
    {
        public const float RowHeight = 18f;
        const float RulerHeight = 18f;
        const float NameWidth = 200f;
        const float ValueWidth = 88f;
        const float ScrollbarHeight = 14f;

        public SignalTrace trace;

        /// <summary>
        /// Another run, drawn under this one. A saved run opened to compare against: the answer
        /// to "it used to work" and to "did that setting change anything", which is a question
        /// about two runs and cannot be asked of one.
        ///
        /// It adds no rows — the list is the run in hand, and a ghost signal is drawn on the row
        /// whose scope and name it matches, or not at all. A run being compared against is a
        /// second reading of the same thing, not a second set of things.
        /// </summary>
        public SignalTrace ghost;

        /// <summary>Hide signals that never moved. On by default: a real avatar declares
        /// hundreds of parameters and a given run touches a dozen of them, and a list where
        /// the dozen are lost among the rest is a list nobody reads. A row that can be poked
        /// is exempt — see <see cref="Visible"/> for why that is not a preference.</summary>
        public bool movedOnly = true;

        /// <summary>Whether this row's value can be typed into — true in a live session for a
        /// parameter of a running client. The row's own scope says whose value it is, which is
        /// why poking does not need a control of its own anywhere else.</summary>
        public System.Func<SignalTrace.Signal, bool> editable;

        /// <summary>Where a typed value goes.</summary>
        public System.Action<SignalTrace.Signal, float> write;
        /// <summary>Where the reader is. Every value shown beside a name is this frame's.</summary>
        public int cursorFrame;

        /// <summary>
        /// The other end of a measurement, or -1 for none. Shift+click puts it down and
        /// Shift+click on it again picks it up.
        ///
        /// Nearly every question a run is opened to answer is a duration — how long after the
        /// toggle did the layer move, how long was the remote behind, how long did that state
        /// hold — and one cursor can only ever say what time it is. Two say how far apart.
        /// </summary>
        public int markFrame = -1;
        /// <summary>Horizontal zoom. Frames narrower than a pixel are still drawn — the
        /// column takes the last of them, which is what a viewer at this scale can say.</summary>
        public float pixelsPerFrame = 4f;
        public int firstFrame;
        public string filter = string.Empty;

        Vector2 _rowScroll;
        /// <summary>Where the row list is on screen, as the last draw put it. Kept because the
        /// list decides whether it may change shape by asking where the pointer is — see
        /// <see cref="MayReshape"/>.</summary>
        Rect _rowArea;
        /// <summary>The last geometry a real pass was drawn with. The Layout pass is handed a
        /// dummy rect, and a pass that lays the rows out differently from the pass the click
        /// arrives in hands out different control ids.</summary>
        Rect _lastRect;
        readonly SignalRanges _ranges = new SignalRanges();
        // The ghost's own measurements. A second reading rather than more entries in the first:
        // the two runs are replaced independently, and a range keyed by signal has no way to
        // tell which run's signals it should forget.
        readonly SignalRanges _ghostRanges = new SignalRanges();
        readonly LagSummary _lag = new LagSummary();
        GUIStyle _rangeStyle;
        // The filtered row list, kept rather than rebuilt: OnGUI runs at least twice a frame
        // and this is walked by both of them.
        readonly List<Row> _visible = new List<Row>();
        SignalTrace _visibleFor;
        string _visibleFilter;
        bool _visibleMovedOnly;
        bool _visiblePokes;
        int _visibleSignals;
        readonly HashSet<string> _collapsed = new HashSet<string>();

        /// <summary>A line of the list: a scope's header, or one signal under it.</summary>
        public struct Row
        {
            public string scope;
            public SignalTrace.Signal signal;
            public int count;
            /// <summary>How many of them are actually listed — what survived the moved-only
            /// rule. The pair is the header's answer to "not shown" versus "not there".</summary>
            public int shown;
            public bool IsHeader => signal == null;
        }

        public int Frames => trace != null ? trace.Frames : 0;

        /// <summary>Whether there is a second cursor to measure from.</summary>
        public bool HasMark => markFrame >= 0 && markFrame < Frames;

        /// <summary>
        /// Seconds between the two cursors, and zero when there is only one. Absolute on
        /// purpose: a reader marks whichever end they noticed first, and a duration that came
        /// out negative because they happened to notice the later one would be a puzzle rather
        /// than an answer.
        /// </summary>
        public float Span() =>
            HasMark ? Mathf.Abs(trace.TimeAt(cursorFrame) - trace.TimeAt(markFrame)) : 0f;

        /// <summary>Puts the mark down, moves it, or — on the frame it is already on — picks it
        /// up. One gesture for all three, because "measure from here" and "stop measuring" are
        /// the same thought a moment apart.</summary>
        public void Mark(int frame)
        {
            if (Frames == 0) { markFrame = -1; return; }
            frame = Mathf.Clamp(frame, 0, Frames - 1);
            markFrame = frame == markFrame ? -1 : frame;
        }

        /// <summary>
        /// Puts both cursors back inside the run. Called whenever the trace may have been
        /// replaced: the frame numbers are the reader's place in the window and survive a
        /// re-run on purpose, but a shorter run has nowhere to put them — and a mark left
        /// pointing past the end would measure to a moment that does not exist.
        /// </summary>
        public void ClampCursors()
        {
            if (Frames == 0)
            {
                cursorFrame = 0;
                markFrame = -1;
                return;
            }
            cursorFrame = Mathf.Clamp(cursorFrame, 0, Frames - 1);
            if (markFrame >= 0) markFrame = Mathf.Clamp(markFrame, 0, Frames - 1);
        }

        /// <summary>Fits the whole run across this width — what a fresh run opens at.</summary>
        public void Fit(float width)
        {
            float area = Mathf.Max(32f, width - NameWidth - ValueWidth);
            pixelsPerFrame = Frames > 0 ? Mathf.Clamp(area / Frames, 0.02f, 40f) : 4f;
            firstFrame = 0;
        }

        public void Draw(Rect rect)
        {
            // GUILayoutUtility hands the Layout pass a dummy rect, so laying the rows out from
            // it would put a different number of them on screen than the pass the click arrives
            // in — and IMGUI hands out control ids in draw order, so the cell that receives a
            // value would not be the one that was clicked. The last real geometry is the honest
            // answer to "where are the rows" during a pass that is not being told.
            if (Event.current != null && Event.current.type == EventType.Layout)
            {
                if (_lastRect.width > 1f) rect = _lastRect;
            }
            else if (rect.width > 1f) _lastRect = rect;

            ClampCursors();
            if (trace == null || trace.Frames == 0)
            {
                GUI.Label(rect, L.Tr("No run yet — press Run."),
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }
            Measure();

            var plot = new Rect(rect.x + NameWidth + ValueWidth, rect.y + RulerHeight,
                Mathf.Max(1f, rect.width - NameWidth - ValueWidth),
                Mathf.Max(1f, rect.height - RulerHeight - ScrollbarHeight));
            // Before the list is asked for: whether it may change shape depends on where the
            // pointer is, and this is where the rows are.
            _rowArea = new Rect(rect.x, plot.y, rect.width, plot.height);
            var visible = Visible();
            ClampScroll(plot.width);

            DrawRuler(new Rect(plot.x, rect.y, plot.width, RulerHeight), plot.width);
            EditorGUI.DrawRect(plot, WaveformColors.Backdrop);

            float content = visible.Count * RowHeight;
            var view = new Rect(rect.x, plot.y, rect.width, plot.height);
            _rowScroll = GUI.BeginScrollView(view,
                _rowScroll, new Rect(0f, 0f, rect.width - 16f, content), false, content > plot.height);
            // Only the rows on screen. A scroll view clips the rest, but clipping happens after
            // the drawing has already been asked for, which is the expensive half.
            int firstRow = Mathf.Max(0, Mathf.FloorToInt(_rowScroll.y / RowHeight));
            int lastRow = Mathf.Min(visible.Count - 1,
                Mathf.CeilToInt((_rowScroll.y + plot.height) / RowHeight));
            for (int i = firstRow; i <= lastRow; i++)
            {
                var area = new Rect(0f, i * RowHeight, rect.width - 16f, RowHeight);
                if (visible[i].IsHeader) DrawHeader(visible[i], area);
                else
                {
                    if (i % 2 == 1) EditorGUI.DrawRect(area, WaveformColors.RowTint);
                    DrawSignal(visible[i].signal, area);
                }
            }
            GUI.EndScrollView();

            DrawMark(plot);
            DrawCursor(plot);
            DrawScrollbar(new Rect(plot.x, plot.yMax, plot.width, ScrollbarHeight), plot.width);
            HandleInput(plot);
        }

        // ---- rows -----------------------------------------------------------

        /// <summary>A scope's line: how many of its rows there are, and whether they show.</summary>
        void DrawHeader(Row row, Rect area)
        {
            EditorGUI.DrawRect(area, WaveformColors.Header);
            bool open = !_collapsed.Contains(row.scope);
            var fold = new Rect(area.x + 4f, area.y, NameWidth + ValueWidth - 8f, area.height);
            // "Local  (2 / 14)" when the moved-only rule is holding rows back: a header that
            // says only "(14)" over two rows reads as twelve signals missing, not twelve quiet.
            string label = row.shown < row.count
                ? row.scope + "  (" + row.shown + " / " + row.count + ")"
                : row.scope + "  (" + row.count + ")";
            // The Lag scope is a row per parameter and one question about all of them: how far
            // behind did the other person ever get, and on what. Saying it on the header answers
            // it without opening the scope at all — and it stays true with the scope folded,
            // which is where a reader who has already seen the answer leaves it.
            if (Simulation.IsLag(row.scope) && _lag.KnownIn(row.scope))
                label += "   " + L.Tr("worst {0:0.###} s: {1}",
                    _lag.WorstIn(row.scope), _lag.ParameterIn(row.scope));
            bool wanted = EditorGUI.Foldout(fold, open, label, true);
            if (wanted == open) return;
            if (wanted) _collapsed.Remove(row.scope);
            else _collapsed.Add(row.scope);
            Invalidate();
        }

        void DrawSignal(SignalTrace.Signal signal, Rect row)
        {
            var name = new Rect(row.x + 14f, row.y, NameWidth - 18f, row.height);
            GUI.Label(name, signal.name, EditorStyles.miniLabel);

            // The value at the cursor, and in a live session the way to change it. Beside its
            // own waveform rather than in a panel of its own, so pushing on something and
            // watching what it does are the same glance.
            // The whole height of the row, not the box's own. A field takes clicks over exactly
            // the rect it is given, so insetting it left a two-pixel band between every pair of
            // cells that belonged to neither — and a click that lands in one does nothing at
            // all, which reads as the window having missed it. The number field draws at its
            // own height inside this whatever it is given, so nothing looks different.
            var value = new Rect(row.x + NameWidth, row.y, ValueWidth - 8f, row.height);
            if (editable != null && editable(signal)) DrawEditor(signal, value);
            else GUI.Label(value, signal.TextAt(cursorFrame), EditorStyles.miniLabel);

            // The waveform is pixels and nothing else — no control, no layout — so the Layout
            // pass has no business computing it.
            //
            // Which is why everything past here draws with GUI.Label and never with
            // EditorGUI.LabelField. A label reads as a thing with no behaviour, but
            // EditorGUI's takes a control id all the same, and this branch runs on the repaint
            // and on nothing else. Every id after it would then be one number here and another
            // in the pass that carries the click — and IMGUI decides which field is being
            // typed into by id, so the caret would appear in a row further up, or in a label,
            // where it looks like the window ignored the click. GUI.Label takes no id.
            if (Event.current.type != EventType.Repaint) return;
            var plot = new Rect(row.x + NameWidth + ValueWidth, row.y + 2f,
                Mathf.Max(1f, row.width - NameWidth - ValueWidth), row.height - 4f);
            if (signal.kind == SignalKind.State)
            {
                // A ghost is a line and nothing else. Two bands over each other is two names in
                // one strip of pixels and neither of them readable, so a state row compares by
                // being looked at twice rather than by being drawn twice.
                DrawBands(signal, plot);
                return;
            }

            var band = RangeOf(signal);
            var other = Ghost(signal);
            if (other != null) DrawGhost(other, plot, band);
            DrawTrace(signal, plot, band);
            DrawRangeLabels(signal, new Rect(row.x + NameWidth + ValueWidth, row.y,
                plot.width, row.height), band);
        }

        /// <summary>Takes in whatever the runs have recorded since the last look — the scales
        /// the rows are drawn against and the Lag scope's own summary. Incremental, so a live
        /// session costs the same per repaint however long it has been going.</summary>
        public void Measure()
        {
            _ranges.Update(trace);
            _ghostRanges.Update(ghost);
            _lag.Update(trace);
        }

        /// <summary>The ghost's copy of this row's signal — matched by scope and name, since
        /// that is what makes it the same signal in another run. Frame numbers are not: two
        /// runs number their frames independently.</summary>
        SignalTrace.Signal Ghost(SignalTrace.Signal signal) =>
            ghost != null ? ghost.Find(signal.scope, signal.name) : null;

        /// <summary>
        /// What this row is drawn against. With a ghost it is the two runs' ranges together:
        /// the same row at two scales is two pictures side by side, and every difference in
        /// height between them would be an artefact of the drawing rather than of the runs.
        /// </summary>
        public Vector2 RangeOf(SignalTrace.Signal signal)
        {
            var band = _ranges.Of(signal);
            var other = Ghost(signal);
            if (other == null) return band;
            var theirs = _ghostRanges.Of(other);
            return new Vector2(Mathf.Min(band.x, theirs.x), Mathf.Max(band.y, theirs.y));
        }

        /// <summary>A bus, the way a waveform viewer draws one: a block per run of equal
        /// values, named inside if the block is wide enough to hold the name.</summary>
        void DrawBands(SignalTrace.Signal signal, Rect plot)
        {
            int last = LastFrame(plot.width);
            int start = firstFrame;
            for (int frame = firstFrame; frame <= last; frame++)
            {
                bool edge = frame == last || !Mathf.Approximately(signal.At(frame), signal.At(frame + 1));
                if (!edge) continue;
                float x0 = X(plot, start), x1 = X(plot, frame + 1);
                var block = new Rect(x0, plot.y, Mathf.Max(1f, x1 - x0), plot.height);
                string label = signal.TextAt(start);
                // A colour per state name rather than one for every band. Zoomed out far enough
                // that no name fits, the colours are the only thing left saying whether a layer
                // is going somewhere new or bouncing between the same two states — which is the
                // question a whole run is usually opened to answer.
                EditorGUI.DrawRect(block, WaveformColors.BandFor(label));
                if (block.width > 26f)
                    GUI.Label(new Rect(block.x + 3f, block.y - 1f,
                        block.width - 5f, block.height + 2f), label, EditorStyles.miniLabel);
                EditorGUI.DrawRect(new Rect(x1 - 1f, plot.y, 1f, plot.height), WaveformColors.Grid);
                start = frame + 1;
            }
        }

        /// <summary>
        /// A line: one horizontal per run of frames that share a pixel row, and one vertical
        /// where the level changes. Not one rect per frame — a flat signal is one rect however
        /// long the run is, which is the difference between a window that scrolls and one that
        /// does not. Most rows in most runs are flat for most of their length.
        ///
        /// Zoomed out past a pixel per frame it steps by whole columns, so the work is bounded
        /// by the width of the window rather than by the length of the run.
        /// </summary>
        void DrawTrace(SignalTrace.Signal signal, Rect plot, Vector2 range)
        {
            var ink = signal.kind == SignalKind.Bool ? WaveformColors.BoolInk
                : signal.kind == SignalKind.Int ? WaveformColors.IntInk : WaveformColors.FloatInk;
            // A trigger is an instant, not a level — the same thing the wire's own rows are, and
            // drawn in the same ink so a one-frame spike reads as one.
            if (signal.kind == SignalKind.Trigger) ink = WaveformColors.EventInk;
            if (signal.scope == Simulation.WireScope) ink = WaveformColors.EventInk;

            float perFrame = Mathf.Max(0.02f, pixelsPerFrame);
            int stride = perFrame >= 1f ? 1 : Mathf.Max(1, Mathf.FloorToInt(1f / perFrame));
            int last = LastFrame(plot.width);
            int runStart = firstFrame;
            float runY = Y(plot, signal.At(firstFrame), range);

            for (int frame = firstFrame + stride; frame <= last; frame += stride)
            {
                float y = Y(plot, signal.At(frame), range);
                if (Mathf.Abs(y - runY) < 0.5f) continue;      // still the same pixel row
                Hold(plot, ink, runStart, frame, runY);
                float top = Mathf.Min(runY, y), bottom = Mathf.Max(runY, y);
                EditorGUI.DrawRect(new Rect(X(plot, frame), top,
                    Mathf.Max(1f, perFrame), bottom - top + 1f), ink);
                runStart = frame;
                runY = y;
            }
            Hold(plot, ink, runStart, last + 1, runY);
        }

        /// <summary>
        /// The same signal from the run being compared against, laid under this row.
        ///
        /// The x axis belongs to the run in hand — its frames are the columns — so every column
        /// asks the ghost what it was doing at THAT MOMENT rather than at that frame number. The
        /// two runs number their frames independently and jitter pulls them apart within the
        /// first second, so by number the comparison would drift for the whole run.
        ///
        /// Shaped like <see cref="DrawTrace"/>, and bounded the same way: one rect per run of
        /// columns that share a pixel row, stepping by whole columns once a frame is thinner
        /// than a pixel, and one forward-only pass along the ghost for the row.
        /// </summary>
        void DrawGhost(SignalTrace.Signal signal, Rect plot, Vector2 range)
        {
            if (ghost == null || ghost.Frames == 0) return;
            var ink = WaveformColors.GhostInk;
            float perFrame = Mathf.Max(0.02f, pixelsPerFrame);
            int stride = perFrame >= 1f ? 1 : Mathf.Max(1, Mathf.FloorToInt(1f / perFrame));
            int last = LastFrame(plot.width);
            var cursor = new GhostCursor(ghost);

            int runStart = firstFrame;
            float runY = Y(plot, signal.At(cursor.At(trace.TimeAt(firstFrame))), range);
            for (int frame = firstFrame + stride; frame <= last; frame += stride)
            {
                float y = Y(plot, signal.At(cursor.At(trace.TimeAt(frame))), range);
                if (Mathf.Abs(y - runY) < 0.5f) continue;
                Hold(plot, ink, runStart, frame, runY);
                float top = Mathf.Min(runY, y), bottom = Mathf.Max(runY, y);
                EditorGUI.DrawRect(new Rect(X(plot, frame), top,
                    Mathf.Max(1f, perFrame), bottom - top + 1f), ink);
                runStart = frame;
                runY = y;
            }
            Hold(plot, ink, runStart, last + 1, runY);
        }

        /// <summary>
        /// The numbers this row's height means, at the height they mean it — the top of the band
        /// written at the top and the bottom at the bottom.
        ///
        /// Without them a waveform says only the shape of a change and never its size: a row
        /// that swings the full height of itself looks the same whether it went 0 to 1 or 0 to
        /// 5000, and every row in the window is drawn to its own scale. Only Float and Int rows
        /// get them — a Bool is 0..1 by construction and saying so would be noise on every other
        /// line, and a State row has its names written in it already.
        ///
        /// Always on rather than on the row under the pointer: hovering would mean repainting
        /// the window on every mouse move, and this one already repaints itself under a live
        /// session. The moved-only rule keeps the list to the rows that did something, which is
        /// what makes a number on each of them a scale rather than clutter.
        /// </summary>
        void DrawRangeLabels(SignalTrace.Signal signal, Rect row, Vector2 range)
        {
            if (signal.kind != SignalKind.Float && signal.kind != SignalKind.Int) return;
            // Too narrow to hold a number without covering the run it is annotating.
            if (row.width < 64f) return;
            if (_rangeStyle == null)
                _rangeStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    fontSize = 8,
                    padding = new RectOffset(0, 0, 0, 0),
                    // Two labels in eighteen pixels: they only fit because each is pinned to its
                    // own end of the row rather than centred in half of it.
                    alignment = TextAnchor.UpperLeft,
                };
            _rangeStyle.normal.textColor = WaveformColors.RangeLabel;
            GUI.Label(new Rect(row.x + 2f, row.y - 1f, 56f, 9f),
                Number(signal, range.y), _rangeStyle);
            GUI.Label(new Rect(row.x + 2f, row.yMax - 9f, 56f, 9f),
                Number(signal, range.x), _rangeStyle);
        }

        /// <summary>A range's end, printed the way this signal's values are — an Int row whose
        /// band runs 0..5 says 5, not 5.001.</summary>
        static string Number(SignalTrace.Signal signal, float value) =>
            signal.kind == SignalKind.Int
                ? Mathf.RoundToInt(value).ToString()
                : value.ToString("0.###");

        void DrawEditor(SignalTrace.Signal signal, Rect rect)
        {
            float current = signal.At(signal.Frames - 1);
            float next = current;
            switch (signal.kind)
            {
                // A press, because that is what a trigger is: a checkbox would offer to hold it
                // down, and nothing can — Mecanim takes it back the moment a transition reads
                // it, so the box would tick itself off again and read as a bug. The row's own
                // waveform is where the answer is: one frame up, and then whatever the
                // controller did about it.
                case SignalKind.Trigger:
                    if (GUI.Button(rect, new GUIContent(L.Tr("Fire"),
                            L.Tr("Set this trigger on the client this row belongs to")),
                            EditorStyles.miniButton) && write != null)
                        write(signal, 1f);
                    return;
                case SignalKind.Bool:
                    next = EditorGUI.Toggle(rect, current != 0f) ? 1f : 0f;
                    break;
                case SignalKind.Int:
                    next = EditorGUI.IntField(rect, Mathf.RoundToInt(current));
                    break;
                default:
                    next = EditorGUI.FloatField(rect, current);
                    break;
            }
            if (!Mathf.Approximately(next, current) && write != null) write(signal, next);
        }

        void Hold(Rect plot, Color ink, int from, int to, float y)
        {
            float x0 = X(plot, from), x1 = X(plot, to);
            if (x1 <= x0) return;
            EditorGUI.DrawRect(new Rect(x0, y, x1 - x0, 1f), ink);
        }

        // ---- chrome ---------------------------------------------------------

        void DrawRuler(Rect rect, float width)
        {
            EditorGUI.DrawRect(rect, WaveformColors.Ruler);
            int last = LastFrame(width);
            // A mark about every 80 px, rounded to a whole number of frames so the labels do
            // not crawl as the zoom changes.
            int stride = Mathf.Max(1, Mathf.RoundToInt(80f / Mathf.Max(0.02f, pixelsPerFrame)));
            for (int frame = firstFrame - firstFrame % stride; frame <= last; frame += stride)
            {
                if (frame < 0) continue;
                float x = X(rect, frame);
                EditorGUI.DrawRect(new Rect(x, rect.y + rect.height - 5f, 1f, 5f), WaveformColors.Grid);
                GUI.Label(new Rect(x + 2f, rect.y - 1f, 90f, rect.height),
                    trace.TimeAt(frame).ToString("0.###") + "s", EditorStyles.miniLabel);
            }
        }

        /// <summary>The other end of the measurement, under the cursor's own line so that a
        /// mark and a cursor on the same frame still read as two.</summary>
        void DrawMark(Rect plot)
        {
            if (!HasMark) return;
            if (markFrame < firstFrame || markFrame > LastFrame(plot.width)) return;
            EditorGUI.DrawRect(new Rect(X(plot, markFrame), plot.y - RulerHeight, 1f,
                plot.height + RulerHeight), WaveformColors.Mark);
        }

        void DrawCursor(Rect plot)
        {
            if (cursorFrame < firstFrame || cursorFrame > LastFrame(plot.width)) return;
            float x = X(plot, cursorFrame);
            EditorGUI.DrawRect(new Rect(x, plot.y - RulerHeight, 1f, plot.height + RulerHeight),
                WaveformColors.Cursor);
        }

        void DrawScrollbar(Rect rect, float width)
        {
            int span = Mathf.Max(1, Mathf.CeilToInt(width / Mathf.Max(0.02f, pixelsPerFrame)));
            if (span >= Frames) return;
            firstFrame = Mathf.RoundToInt(GUI.HorizontalScrollbar(rect, firstFrame,
                span, 0f, Frames));
        }

        void HandleInput(Rect plot)
        {
            var e = Event.current;
            if (!plot.Contains(e.mousePosition)) return;
            if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
            {
                int frame = Mathf.Clamp(FrameAt(plot, e.mousePosition.x), 0, Frames - 1);
                // Shift moves the mark instead of the cursor. Held while dragging it keeps
                // moving the mark rather than picking it up on every frame it crosses — the
                // toggle is a click, which is a thing done once and on purpose.
                if (e.shift && e.type == EventType.MouseDrag) markFrame = frame;
                else if (e.shift) Mark(frame);
                else cursorFrame = frame;
                e.Use();
                GUI.changed = true;
            }
            else if (e.type == EventType.ScrollWheel)
            {
                // Zoom about the pointer, so the thing being looked at stays under it.
                int anchor = FrameAt(plot, e.mousePosition.x);
                pixelsPerFrame = Mathf.Clamp(pixelsPerFrame * (e.delta.y > 0f ? 0.85f : 1.18f),
                    0.02f, 40f);
                firstFrame = Mathf.Max(0,
                    anchor - Mathf.RoundToInt((e.mousePosition.x - plot.x) / pixelsPerFrame));
                e.Use();
            }
        }

        // ---- geometry -------------------------------------------------------

        float X(Rect plot, int frame) => plot.x + (frame - firstFrame) * pixelsPerFrame;

        int FrameAt(Rect plot, float x) =>
            firstFrame + Mathf.FloorToInt((x - plot.x) / Mathf.Max(0.02f, pixelsPerFrame));

        int LastFrame(float width) =>
            Mathf.Min(Frames - 1, firstFrame + Mathf.CeilToInt(width / Mathf.Max(0.02f, pixelsPerFrame)));

        /// <summary>Where a value sits in its row's band. Clamped, so a value the range has not
        /// caught up with yet stays on its own row instead of being drawn over the one above:
        /// the range follows a growing trace now, but a row is one signal's and nothing that
        /// happens to it should be readable as another's.</summary>
        static float Y(Rect plot, float value, Vector2 range)
        {
            float span = range.y - range.x;
            float at = Mathf.Approximately(span, 0f) ? 0.5f : Mathf.Clamp01((value - range.x) / span);
            return plot.yMax - at * plot.height;
        }

        void ClampScroll(float width)
        {
            int span = Mathf.Max(1, Mathf.CeilToInt(width / Mathf.Max(0.02f, pixelsPerFrame)));
            firstFrame = Mathf.Clamp(firstFrame, 0, Mathf.Max(0, Frames - span));
            cursorFrame = Mathf.Clamp(cursorFrame, 0, Mathf.Max(0, Frames - 1));
        }

        /// <summary>
        /// The list as drawn: a header per scope, and under it the signals that survived the
        /// filter, the moved-only rule and the fold. Rebuilt only when one of those changed —
        /// it is walked twice a frame, and a live run answers "did anything new move" by the
        /// signal count rather than by looking again.
        /// </summary>
        public List<Row> Visible()
        {
            var e = Event.current;
            return Visible(MayReshape(e != null, GUIUtility.hotControl,
                EditorGUIUtility.editingTextField, _rowArea,
                e != null ? e.mousePosition : Vector2.zero));
        }

        /// <summary>
        /// Whether the row list may change shape at this moment.
        ///
        /// A live session appends to the SAME trace and the list is rebuilt as it grows, so a
        /// row that has just started moving is inserted — in the middle of the list, under the
        /// pointer, between the frame the reader saw and the frame their click is processed.
        /// The click then lands on whatever row took that place, and because IMGUI hands out
        /// control ids in draw order, a value being typed into a cell continues into a
        /// different signal's cell. Neither is visible: the value simply appears somewhere else.
        ///
        /// So the list is held still while it is being used, and the change lands the moment it
        /// is let go. Pure, and told the editor's state rather than reading it, so the rule can
        /// be checked without a window.
        /// </summary>
        internal static bool MayReshape(bool hasEvent, int hotControl, bool editingText,
            Rect rows, Vector2 pointer) =>
            !hasEvent
            || (hotControl == 0 && !editingText && !rows.Contains(pointer));

        internal List<Row> Visible(bool mayReshape)
        {
            int signals = trace != null ? trace.Signals.Count : 0;
            bool pokes = editable != null;
            // What the reader asked for, as against what the run did on its own.
            bool asked = _visibleFor != trace || _visibleFilter != filter
                || _visibleMovedOnly != movedOnly || _visiblePokes != pokes;
            if (!asked && _visibleSignals == signals && !_dirty) return _visible;
            // Held still while the list is being used — see MayReshape. Only against the run's
            // own doing: somebody typing in the filter is asking for a different list and is
            // answered, even though typing is exactly the state the hold watches for.
            if (!asked && !mayReshape && _visible.Count > 0) return _visible;
            _visibleFor = trace;
            _visibleFilter = filter;
            _visibleMovedOnly = movedOnly;
            _visibleSignals = signals;
            _visiblePokes = pokes;
            _dirty = false;
            _visible.Clear();
            if (trace == null) return _visible;

            string scope = null;
            int headerAt = -1;
            foreach (var signal in trace.Signals)
            {
                if (signal.scope != scope)
                {
                    scope = signal.scope;
                    headerAt = _visible.Count;
                    _visible.Add(new Row { scope = scope, count = 0 });
                }
                if (!string.IsNullOrEmpty(filter)
                    && signal.Path.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                // A row that never moved is still counted — the header saying "3 of 214" is
                // how a reader knows the rest are there and quiet rather than missing.
                //
                // And a row that can be poked is never hidden, quiet or not. Since the value
                // cell became the only way to write a value, hiding a quiet row hid the control
                // too: a fresh live session, where nothing has moved yet, offered nothing to
                // push on and no way to make anything move. The moved-only rule is about
                // reading; an editable row is there for writing.
                var header = _visible[headerAt];
                header.count++;
                bool shown = !movedOnly || signal.Moved
                    || (editable != null && editable(signal));
                if (shown) header.shown++;
                _visible[headerAt] = header;
                if (!shown) continue;
                if (_collapsed.Contains(scope)) continue;
                _visible.Add(new Row { scope = scope, signal = signal });
            }
            // A scope whose every row was filtered out has nothing left to head.
            for (int i = _visible.Count - 1; i >= 0; i--)
                if (_visible[i].IsHeader && _visible[i].count == 0)
                    _visible.RemoveAt(i);
            return _visible;
        }

        bool _dirty;

        /// <summary>Drops the cached row list — after a fold, or when a live run has moved
        /// something that was quiet until now.</summary>
        public void Invalidate() => _dirty = true;
    }
}
