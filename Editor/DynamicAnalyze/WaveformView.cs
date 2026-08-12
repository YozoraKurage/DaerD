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
    sealed class WaveformView
    {
        public const float RowHeight = 18f;
        const float RulerHeight = 18f;
        const float NameWidth = 210f;
        const float ValueWidth = 74f;
        const float ScrollbarHeight = 14f;

        public SignalTrace trace;
        /// <summary>Where the reader is. Every value shown beside a name is this frame's.</summary>
        public int cursorFrame;
        /// <summary>Horizontal zoom. Frames narrower than a pixel are still drawn — the
        /// column takes the last of them, which is what a viewer at this scale can say.</summary>
        public float pixelsPerFrame = 4f;
        public int firstFrame;
        public string filter = string.Empty;

        Vector2 _rowScroll;
        readonly Dictionary<SignalTrace.Signal, Vector2> _ranges =
            new Dictionary<SignalTrace.Signal, Vector2>();
        SignalTrace _rangedFor;
        // The filtered row list, kept rather than rebuilt: OnGUI runs at least twice a frame
        // and this is walked by both of them.
        readonly List<SignalTrace.Signal> _visible = new List<SignalTrace.Signal>();
        SignalTrace _visibleFor;
        string _visibleFilter;

        public int Frames => trace != null ? trace.Frames : 0;

        /// <summary>Fits the whole run across this width — what a fresh run opens at.</summary>
        public void Fit(float width)
        {
            float area = Mathf.Max(32f, width - NameWidth - ValueWidth);
            pixelsPerFrame = Frames > 0 ? Mathf.Clamp(area / Frames, 0.02f, 40f) : 4f;
            firstFrame = 0;
        }

        public void Draw(Rect rect)
        {
            if (trace == null || trace.Frames == 0)
            {
                EditorGUI.LabelField(rect, L.Tr("No run yet — press Run."),
                    EditorStyles.centeredGreyMiniLabel);
                return;
            }
            if (_rangedFor != trace) MeasureRanges();

            var visible = Visible();
            var plot = new Rect(rect.x + NameWidth + ValueWidth, rect.y + RulerHeight,
                Mathf.Max(1f, rect.width - NameWidth - ValueWidth),
                Mathf.Max(1f, rect.height - RulerHeight - ScrollbarHeight));
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
                var row = new Rect(0f, i * RowHeight, rect.width - 16f, RowHeight);
                if (i % 2 == 1) EditorGUI.DrawRect(row, WaveformColors.RowTint);
                DrawSignal(visible[i], row);
            }
            GUI.EndScrollView();

            DrawCursor(plot);
            DrawScrollbar(new Rect(plot.x, plot.yMax, plot.width, ScrollbarHeight), plot.width);
            HandleInput(plot);
        }

        // ---- rows -----------------------------------------------------------

        void DrawSignal(SignalTrace.Signal signal, Rect row)
        {
            var name = new Rect(row.x + 4f, row.y, NameWidth - 8f, row.height);
            EditorGUI.LabelField(name, signal.Path, EditorStyles.miniLabel);
            var value = new Rect(row.x + NameWidth, row.y, ValueWidth - 6f, row.height);
            EditorGUI.LabelField(value, signal.TextAt(cursorFrame), EditorStyles.miniLabel);

            // The waveform is pixels and nothing else — no control, no layout — so the Layout
            // pass has no business computing it.
            if (Event.current.type != EventType.Repaint) return;
            var plot = new Rect(row.x + NameWidth + ValueWidth, row.y + 2f,
                Mathf.Max(1f, row.width - NameWidth - ValueWidth), row.height - 4f);
            if (signal.kind == SignalKind.State) DrawBands(signal, plot);
            else DrawTrace(signal, plot);
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
                EditorGUI.DrawRect(block, WaveformColors.StateBand);
                string label = signal.TextAt(start);
                if (block.width > 26f)
                    EditorGUI.LabelField(new Rect(block.x + 3f, block.y - 1f,
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
        void DrawTrace(SignalTrace.Signal signal, Rect plot)
        {
            var range = _ranges.TryGetValue(signal, out var r) ? r : new Vector2(0f, 1f);
            var ink = signal.kind == SignalKind.Bool ? WaveformColors.BoolInk
                : signal.kind == SignalKind.Int ? WaveformColors.IntInk : WaveformColors.FloatInk;
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
                EditorGUI.LabelField(new Rect(x + 2f, rect.y - 1f, 90f, rect.height),
                    trace.TimeAt(frame).ToString("0.###") + "s", EditorStyles.miniLabel);
            }
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
                cursorFrame = Mathf.Clamp(FrameAt(plot, e.mousePosition.x), 0, Frames - 1);
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

        static float Y(Rect plot, float value, Vector2 range)
        {
            float span = range.y - range.x;
            float at = Mathf.Approximately(span, 0f) ? 0.5f : (value - range.x) / span;
            return plot.yMax - at * plot.height;
        }

        void ClampScroll(float width)
        {
            int span = Mathf.Max(1, Mathf.CeilToInt(width / Mathf.Max(0.02f, pixelsPerFrame)));
            firstFrame = Mathf.Clamp(firstFrame, 0, Mathf.Max(0, Frames - span));
            cursorFrame = Mathf.Clamp(cursorFrame, 0, Mathf.Max(0, Frames - 1));
        }

        /// <summary>Each signal's own vertical range, over the WHOLE run rather than the part
        /// on screen — a line that rescaled itself as it was scrolled would make two moments
        /// impossible to compare by eye.</summary>
        void MeasureRanges()
        {
            _ranges.Clear();
            _rangedFor = trace;
            foreach (var signal in trace.Signals)
            {
                if (signal.kind == SignalKind.State) continue;
                float low = float.MaxValue, high = float.MinValue;
                for (int frame = 0; frame < signal.Frames; frame++)
                {
                    float value = signal.At(frame);
                    low = Mathf.Min(low, value);
                    high = Mathf.Max(high, value);
                }
                if (signal.kind == SignalKind.Bool) { low = 0f; high = 1f; }
                if (low > high) { low = 0f; high = 1f; }
                if (Mathf.Approximately(low, high)) { low -= 0.5f; high += 0.5f; }
                _ranges[signal] = new Vector2(low, high);
            }
        }

        public List<SignalTrace.Signal> Visible()
        {
            if (_visibleFor == trace && _visibleFilter == filter
                && (trace == null || _visible.Count <= trace.Signals.Count))
                return _visible;
            _visibleFor = trace;
            _visibleFilter = filter;
            _visible.Clear();
            if (trace == null) return _visible;
            foreach (var signal in trace.Signals)
                if (string.IsNullOrEmpty(filter)
                    || signal.Path.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _visible.Add(signal);
            return _visible;
        }

        /// <summary>Drops the cached row list — for a trace that grew since it was built, which
        /// is every frame of a live session.</summary>
        public void Invalidate() => _visibleFor = null;
    }
}
