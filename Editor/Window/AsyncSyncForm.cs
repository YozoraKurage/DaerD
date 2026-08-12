using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The async-sync setup form, shared between the wizard (<see cref="AsyncSyncWindow"/>)
    /// and the sync layer's dedicated panel (AsyncSyncPanel): the generated-sync fields, the
    /// tick list, the drag-to-order cycle editor with per-row ×N rates and Req(uestable)
    /// marks, and the cost/cycle preview. The host owns the layer choice and the apply
    /// button; the form owns every input that ends up in the
    /// <see cref="AsyncSyncBuilder.Request"/>.
    ///
    /// "Set Timing By Hand" turns the cycle preview into a grid of toggles, one column per
    /// step, and that is the opt-in: nothing about the rate-derived pass changes until it is
    /// pressed, and someone who only wants ordinary multiplexing never meets a cell.
    /// </summary>
    class AsyncSyncForm
    {
        class Row
        {
            public string name;
            public AnimatorControllerParameterType type;
            public bool selected;
            public int rate = 1;
            /// <summary>Accept on-demand sync requests for this target.</summary>
            public bool request;
            /// <summary>Start a slot rather than share channels with the row above.</summary>
            public bool split;
        }

        AnimatorController _controller;

        readonly List<Row> _rows = new List<Row>();
        /// <summary>Selected rows in multiplex order — this list IS the cycle order.
        /// Ticking appends, unticking removes, dragging rearranges.</summary>
        readonly List<Row> _order = new List<Row>();
        readonly ListReorder _reorder = new ListReorder();
        string _baseName = "Async";
        AsyncSyncBuilder.IndexEncoding _encoding = AsyncSyncBuilder.IndexEncoding.Auto;
        float _stepSeconds = 0.3f;
        int _floatChannels = 1;
        int _boolChannels = 1;
        /// <summary>Let a step send what the step before it sent, paid for with a clock
        /// phase in the index (<see cref="AsyncSyncBuilder.Request.allowRepeatSteps"/>).</summary>
        bool _allowRepeatSteps;
        /// <summary>Generate the remote-initialized flag
        /// (<see cref="AsyncSyncBuilder.Request.ready"/>).</summary>
        bool _ready;
        bool _addToStore = true;
        bool _assignEmptyClip = true;
        string _search = string.Empty;
        Vector2 _pickScroll;
        bool _timelineOpen = true;
        /// <summary>The pass written out as a grid: one entry per step, naming what that step
        /// sends. Empty means the pass is derived from the rates, so this list doubles as the
        /// manual/automatic mode.</summary>
        readonly List<GraphFrameData.AsyncSyncConfig.StepSpec> _steps =
            new List<GraphFrameData.AsyncSyncConfig.StepSpec>();
        /// <summary>Set by anything that can reshape what the grid refers to. The repair runs
        /// once per such edit rather than every repaint: it is stable on a valid grid, but
        /// running it unprompted still invites steps to move on their own. Toggling a cell is
        /// deliberately NOT such an edit — see <see cref="PaintCell"/>.</summary>
        bool _stepsStale;
        /// <summary>
        /// An explicit cycle the setup already carries: target names, one per step — the
        /// vocabulary <c>c.AsyncSync().Schedule(…)</c> writes. Carried through rather than
        /// edited, because the grid replaced the wizard's own cycle editor and there is no
        /// control here for one. A form that simply dropped what it cannot draw would rebuild
        /// the layer on the rates and then save THAT over its author's pass, which is a silent
        /// way to lose work. Empty for every setup that never had one, and ignored while
        /// <see cref="_steps"/> is present — a grid answers the same question with more of the
        /// picture.
        /// </summary>
        readonly List<string> _schedule = new List<string>();

        // Parameters animation writes (AAP). The scan walks every state, every blend tree and
        // every clip's curve bindings — far too much for the per-event redraw the warnings live
        // in, where a drag across the timing grid would pay for it dozens of times a second.
        // Cached per controller and dropped the way ParametersPanel drops its own copy: on the
        // structural edits that can change the answer.
        HashSet<string> _animated;
        AnimatorController _animatedController;
        /// <summary>The row a drag is painting and what it is painting (in, or out), so a
        /// stroke across a half-filled row fills it rather than inverting it.</summary>
        string _paintTarget;
        bool _paintAdd;
        /// <summary>The step whose last click was refused for want of a channel, or -1. Held
        /// only to colour it: a click that does nothing and says nothing reads as a dead UI.</summary>
        int _fullStep = -1;

        // ×1 is "no rate" — the popup only offers meaningful multipliers beyond it.
        static readonly int[] RateValues = { 1, 2, 3, 4 };
        static readonly string[] RateLabels = { "×1", "×2", "×3", "×4" };

        // Rebuilt per draw so a language switch is picked up. Order matches the enum.
        static string[] EncodingLabels() => new[]
        {
            L.Tr("Int (8 bit)"),
            L.Tr("Bool × n (1 bit each)"),
            L.Tr("Auto (fewest synced bits)"),
        };

        public bool HasController => _controller != null;
        public string BaseName => _baseName;

        /// <summary>(Re)binds the form to a controller and rebuilds the candidate rows;
        /// selections are reset (load a config afterwards to restore one).</summary>
        public void SetController(AnimatorController controller)
        {
            _controller = controller;
            _rows.Clear();
            _order.Clear();
            _steps.Clear();
            _schedule.Clear();
            if (controller == null) return;
            foreach (var parameter in controller.parameters)
            {
                if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                // The machinery must not multiplex itself: IsLocal and the parameters an
                // async-sync setup generated never appear as candidates.
                if (AsyncSyncBuilder.IsReservedName(controller, parameter.name)) continue;
                _rows.Add(new Row { name = parameter.name, type = parameter.type });
            }
        }

        /// <summary>Prefills the base name for the generated parameters. Null / empty is
        /// ignored, so a caller can offer a suggestion without having to check it first; a
        /// <see cref="LoadConfig"/> afterwards still wins, the saved setup's own name being
        /// the more specific answer.</summary>
        public void SetBaseName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _baseName = name;
        }

        /// <summary>
        /// The AAP set for the bound controller, worked out at most once per invalidation.
        /// Hosts hand this to <see cref="AsyncSyncBuilder.Warnings"/> so the draw path never
        /// pays for the scan — see <see cref="InvalidateAnimatedParameters"/> for what makes
        /// it stale.
        /// </summary>
        public HashSet<string> AnimatedParameters()
        {
            if (_animated != null && _animatedController == _controller) return _animated;
            _animatedController = _controller;
            return _animated = AapWriteScan.CollectWrittenParameters(_controller);
        }

        /// <summary>Drops the cached AAP set, so the next draw scans again. Hosts call this on
        /// the structural changes that can add or remove an animated write; a clip edited in
        /// another window shows up on the next such change or when the host regains focus,
        /// which is the right trade for a warning banner against a per-frame walk of the whole
        /// controller.</summary>
        public void InvalidateAnimatedParameters() => _animated = null;

        public void LoadConfig(GraphFrameData.AsyncSyncConfig config)
        {
            _baseName = config.baseName;
            _encoding = (AsyncSyncBuilder.IndexEncoding)config.encoding;
            _stepSeconds = config.stepSeconds;
            _floatChannels = Mathf.Clamp(config.FloatChannelsOrDefault, 1, 8);
            _boolChannels = Mathf.Clamp(config.BoolChannelsOrDefault, 1, 8);
            _allowRepeatSteps = config.allowRepeatSteps;
            _ready = config.ready;
            _steps.Clear();
            if (config.steps != null)
                foreach (var step in config.steps)
                    _steps.Add(StepOf(step?.targets));
            // Restored beside the grid rather than converted into one: the two are different
            // vocabularies, and a recipe that said Schedule(…) must still export as Schedule(…)
            // after a round trip through this form.
            _schedule.Clear();
            if (config.schedule != null) _schedule.AddRange(config.schedule);
            _stepsStale = true;
            _fullStep = -1;

            var rates = config.RateMap();
            foreach (var row in _rows)
            {
                row.selected = false;
                row.rate = 1;
                row.request = false;
                row.split = false;
            }
            _order.Clear();
            // The saved target list is ordered — restoring it restores the cycle order.
            foreach (var name in config.targets)
                foreach (var row in _rows)
                    if (row.name == name)
                    {
                        row.selected = true;
                        if (rates.TryGetValue(name, out int rate))
                            row.rate = Mathf.Clamp(rate, 1, RateValues[RateValues.Length - 1]);
                        row.request = config.requests != null && config.requests.Contains(name);
                        row.split = config.slotBreaks != null && config.slotBreaks.Contains(name);
                        _order.Add(row);
                        break;
                    }
        }

        public AsyncSyncBuilder.Request BuildRequest(int layerIndex)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = _controller,
                baseName = _baseName != null ? _baseName.Trim() : string.Empty,
                encoding = _encoding,
                stepSeconds = _stepSeconds,
                floatChannels = _floatChannels,
                boolChannels = _boolChannels,
                allowRepeatSteps = _allowRepeatSteps,
                ready = _ready,
                store = ParameterStore.Of(_controller),
                addToStore = _addToStore,
                assignEmptyClip = _assignEmptyClip,
                emptyClip = GraphFrameData.GetEmptyClip(_controller),
                layerIndex = layerIndex,
            };
            foreach (var row in _order)
            {
                request.targets.Add(row.name);
                if (row.rate > 1) request.rates[row.name] = row.rate;
                if (row.request) request.requestTargets.Add(row.name);
                if (row.split) request.slotBreaks.Add(row.name);
            }

            // The repair needs the slots, which need the request — hence here, once the
            // targets are in and before the grid goes on. A grid it cannot settle comes back
            // empty, which is exactly how this form spells "use the rates".
            if (_stepsStale && _steps.Count > 0)
            {
                var repaired = AsyncSyncBuilder.RepairSteps(request, _steps);
                _steps.Clear();
                _steps.AddRange(repaired);
                _fullStep = -1;
            }
            _stepsStale = false;
            ApplyScheduleOverride(request);
            Snapshot(request);
            return request;
        }

        /// <summary>
        /// Puts the carried explicit cycle onto the request, so a setup that has one is
        /// rebuilt on it instead of on the rates. A grid outranks it and is left to win on its
        /// own — <see cref="AsyncSyncSchedule.EffectiveSchedule"/> reads the two in that order.
        ///
        /// A cycle the current slots can still run is passed through EXACTLY as written: a
        /// panel opened and applied without an edit has to give back the setup it was handed,
        /// and a repair that renamed a batched step's spokesman would show up as drift in the
        /// author's C#. Only one the slots have outgrown — a target unticked, channels widened
        /// until two steps merged — is repaired, and one the repair cannot settle comes back
        /// empty, which is how this form spells "use the rates".
        /// </summary>
        void ApplyScheduleOverride(AsyncSyncBuilder.Request request)
        {
            request.scheduleOverride.Clear();
            if (_steps.Count > 0 || _schedule.Count == 0) return;

            request.scheduleOverride.AddRange(_schedule);
            var slots = AsyncSyncBuilder.BuildSlots(request);
            if (AsyncSyncBuilder.ResolveScheduleOverride(request, slots, null) != null) return;

            var repaired = AsyncSyncBuilder.RepairScheduleOverride(request, _schedule);
            _schedule.Clear();
            _schedule.AddRange(repaired);
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(_schedule);
        }

        /// <summary>Copies the grid into the request the rest of the pass reads. Copied rather
        /// than shared: the request is handed to the model, which is entitled to assume it
        /// describes one moment, while this form goes on rewriting the grid as the mouse moves.</summary>
        void Snapshot(AsyncSyncBuilder.Request request)
        {
            request.steps.Clear();
            foreach (var step in _steps) request.steps.Add(StepOf(step.targets));
        }

        static GraphFrameData.AsyncSyncConfig.StepSpec StepOf(List<string> targets)
        {
            var step = new GraphFrameData.AsyncSyncConfig.StepSpec();
            if (targets != null) step.targets.AddRange(targets);
            return step;
        }

        // ---- sections --------------------------------------------------------

        public void DrawGeneratedSection()
        {
            EditorGUILayout.LabelField(L.Tr("Generated Sync"), EditorStyles.boldLabel);
            _baseName = EditorGUILayout.TextField(L.Tr("Base Name"), _baseName);
            _encoding = (AsyncSyncBuilder.IndexEncoding)EditorGUILayout.Popup(
                L.Tr("Index Encoding"), (int)_encoding, EncodingLabels());
            DrawChannelCounts();
            _stepSeconds = EditorGUILayout.FloatField(
                new GUIContent(L.Tr("Step Interval (s)"),
                    L.Tr("Dwell per slot. VRChat syncs roughly every 0.3 s — shorter steps risk remotes skipping slots.")),
                _stepSeconds);

            // Turning it off has to bring the pass back into line: a grid drawn with repeats
            // in it is not a pass the decoder can run once the phase stops paying for them.
            EditorGUI.BeginChangeCheck();
            _allowRepeatSteps = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Allow Repeated Steps"),
                    L.Tr("Let a step send what the step before it sent. A clock phase folded into the index tells the two apart, at one more decoder state per parameter set that actually repeats — and, under a Bool index, sometimes one more synced bit.")),
                _allowRepeatSteps);
            if (EditorGUI.EndChangeCheck()) _stepsStale = true;

            _ready = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Remote Initialized Flag"),
                    L.Tr("Generate a local Bool that turns on once this client has decoded every slot at least once — what a remote has instead of a way to ask. The wearer reads it as on from the start, so a remote that has finished initializing is Ready && !IsLocal. Costs one local Bool per slot and a second layer; nothing synced.")),
                _ready);

            // The generated states are machinery, but Unity (and the analyzer) still want a
            // motion on them; the controller's Empty clip is exactly what that is for. Offered
            // even with no clip designated — applying creates one inside the controller.
            var emptyClip = GraphFrameData.GetEmptyClip(_controller);
            _assignEmptyClip = EditorGUILayout.Toggle(
                new GUIContent(
                    emptyClip != null
                        ? L.Tr("Fill States With '{0}'", emptyClip.name)
                        : L.Tr("Fill States With The Empty Clip"),
                    L.Tr("Fill the generated states with this controller's Empty clip. If none is set yet, a 1-second no-op clip is created inside the controller and registered as its Empty clip.")),
                _assignEmptyClip);

            if (ParameterStore.Of(_controller) != null)
                _addToStore = EditorGUILayout.Toggle(
                    new GUIContent(L.Tr("Add Synced Params To Store"),
                        L.Tr("Add the generated index and channel parameters to the associated parameter store as synced.")),
                    _addToStore);
        }

        /// <summary>
        /// Channel counts for the types actually being multiplexed. Only Floats and Bools
        /// batch, so a Bool-only setup has no business being asked about Float channels —
        /// and with only one of them on screen the count needs no type in its name.
        /// </summary>
        void DrawChannelCounts()
        {
            bool hasFloat = false, hasBool = false;
            foreach (var row in _order)
            {
                if (row.type == AnimatorControllerParameterType.Float) hasFloat = true;
                else if (row.type == AnimatorControllerParameterType.Bool) hasBool = true;
            }

            // Channel counts regroup the slots a hand-timed cycle refers to.
            EditorGUI.BeginChangeCheck();
            if (hasFloat)
                _floatChannels = EditorGUILayout.IntSlider(
                    new GUIContent(hasBool ? L.Tr("Float Channels") : L.Tr("Channels"),
                        L.Tr("Synced Float channels. Each step carries up to this many Float parameters at once — fewer slots and a faster cycle, at 8 synced bits per extra channel.")),
                    _floatChannels, 1, 8);
            if (hasBool)
                _boolChannels = EditorGUILayout.IntSlider(
                    new GUIContent(hasFloat ? L.Tr("Bool Channels") : L.Tr("Channels"),
                        L.Tr("Synced Bool channels. Each step carries up to this many Bool parameters at once — fewer slots and a faster cycle, at 1 synced bit per extra channel.")),
                    _boolChannels, 1, 8);
            if (EditorGUI.EndChangeCheck()) _stepsStale = true;
        }

        /// <summary>The tick list with its search filter. Ticking appends the parameter to
        /// the cycle order below; unticking removes it.</summary>
        public void DrawPickList()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                L.Tr("Parameters To Multiplex") + " (" + _order.Count + "/" + _rows.Count + ")",
                EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(80), GUILayout.MaxWidth(180));
            EditorGUILayout.EndHorizontal();

            _pickScroll = EditorGUILayout.BeginScrollView(_pickScroll, GUILayout.MinHeight(110),
                GUILayout.MaxHeight(160));
            int visible = 0;
            foreach (var row in _rows)
            {
                if (!string.IsNullOrEmpty(_search)
                    && row.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                visible++;
                bool selected = EditorGUILayout.ToggleLeft(
                    row.name + "  (" + row.type + ")", row.selected);
                if (selected != row.selected)
                    SetSelected(row, selected);
            }
            if (_rows.Count == 0)
                EditorGUILayout.HelpBox(L.Tr("This controller has no Float / Int / Bool parameters."),
                    MessageType.Info);
            else if (visible == 0)
                EditorGUILayout.LabelField(L.Tr("No parameter matches the search."),
                    EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        void SetSelected(Row row, bool selected)
        {
            row.selected = selected;
            if (selected)
            {
                if (!_order.Contains(row)) _order.Add(row);
            }
            else
            {
                _order.Remove(row);
                row.rate = 1;
                row.request = false;
                row.split = false;
            }
            _stepsStale = true;
        }

        /// <summary>
        /// The cycle editor: selected parameters in multiplex order. Drag the handle to
        /// reorder; the ×N popup syncs a row N times per pass; Req marks the row as
        /// requestable (states can ask for it out of turn); the label on the right is the
        /// refresh interval the current schedule actually delivers.
        /// </summary>
        public void DrawOrderSection(AsyncSyncBuilder.Request request)
        {
            EditorGUILayout.LabelField(L.Tr("Sync Order & Rates"), EditorStyles.boldLabel);
            if (_order.Count == 0)
            {
                EditorGUILayout.LabelField(
                    L.Tr("Tick parameters above — drag them here into the order the cycle should visit them."),
                    EditorStyles.miniLabel);
                return;
            }
            EditorGUILayout.LabelField(
                Manual
                    ? L.Tr("Top to bottom is only the listing order here — what each step sends is set cell by cell in the grid below.")
                    : _schedule.Count > 0
                        // Rates are not inert under a carried cycle, only demoted: the pass is
                        // the cycle's, but BuildSlots still groups by (type, rate), so the ×N
                        // popup goes on deciding which parameters share a step.
                        ? L.Tr("This setup carries an explicit cycle written in C#, so top to bottom is only the listing order and ×N only decides which parameters share a step. The pass itself is the timeline below.")
                        : L.Tr("Top to bottom is the cycle order. ×N syncs a parameter N times per pass; everything else shares the steps in between."),
                EditorStyles.miniLabel);

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            var visits = Manual ? VisitCounts(ColumnSets(request)) : null;
            // Batching is why two rows can move as one, and until now nothing said so. The
            // slot number is shown whenever channels could group anything — a condition that
            // cannot change mid-draw, unlike "is anything actually grouped", which the Split
            // buttons below would flip and take the layout's control count with it. A grid
            // shows the grouping cell by cell, so neither has anything left to say there.
            var slots = AsyncSyncBuilder.BuildSlots(request);
            bool grouping = !Manual && (_floatChannels > 1 || _boolChannels > 1);
            var slotOfRow = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOfRow[name] = i;

            _reorder.Begin();
            foreach (var row in _order)
            {
                var rowRect = EditorGUILayout.BeginHorizontal();
                _reorder.DrawHandle();

                EditorGUILayout.LabelField(row.name + "  (" + row.type + ")");

                if (grouping)
                {
                    slotOfRow.TryGetValue(row.name, out int slot);
                    EditorGUILayout.LabelField(
                        new GUIContent("#" + (slot + 1),
                            L.Tr("The step this parameter rides in. Rows sharing a number share a step, are copied by one driver, and so are always sent together.")),
                        EditorStyles.miniLabel, GUILayout.Width(26));

                    bool splittable = row.type == AnimatorControllerParameterType.Float
                        ? _floatChannels > 1 : row.type == AnimatorControllerParameterType.Bool
                            && _boolChannels > 1;
                    EditorGUI.BeginDisabledGroup(!splittable);
                    bool split = GUILayout.Toggle(row.split && splittable,
                        new GUIContent(L.Tr("Split"),
                            L.Tr("Give this parameter a step of its own instead of sharing one with the row above. Parameters sharing a step are sent together and cannot be timed apart.")),
                        EditorStyles.miniButton, GUILayout.Width(40));
                    // Only a toggle the user could actually reach writes back. A disabled one
                    // hands back the value it was drawn with — the greyed-out false — and
                    // taking that for an edit would clear a mark the channel count is merely
                    // too narrow for at this moment, silently and with no click involved.
                    if (splittable && split != row.split)
                    {
                        row.split = split;
                        _stepsStale = true;
                    }
                    EditorGUI.EndDisabledGroup();
                }

                if (Manual)
                {
                    // A count, not a control: under a hand-written grid neither the rate nor
                    // the batching is the model's to decide any more, so showing the rate as
                    // an input would be a lie.
                    visits.TryGetValue(row.name, out int times);
                    EditorGUILayout.LabelField(
                        new GUIContent("×" + times,
                            L.Tr("Steps in the pass that send this parameter. Set it by clicking the timeline; go back to rates to have it worked out for you.")),
                        EditorStyles.miniLabel, GUILayout.Width(48));
                }
                else
                {
                    int rateIndex = Mathf.Max(0, Array.IndexOf(RateValues, row.rate));
                    rateIndex = EditorGUILayout.Popup(rateIndex, RateLabels, GUILayout.Width(48));
                    row.rate = RateValues[rateIndex];
                }

                row.request = GUILayout.Toggle(row.request,
                    new GUIContent("Req",
                        L.Tr("Accept sync requests: a state's Sync Request (or anything setting the '{0}' flag) makes the cycle send this parameter at the next step instead of waiting a full pass. Costs no synced bits.",
                            AsyncSyncBuilder.RequestParameter(request.baseName, row.name))),
                    EditorStyles.miniButton, GUILayout.Width(36));

                string interval = intervals.TryGetValue(row.name, out float seconds)
                    ? L.Tr("every {0:0.##} s", seconds)
                    : "—";
                EditorGUILayout.LabelField(interval, EditorStyles.miniLabel, GUILayout.Width(90));

                EditorGUILayout.EndHorizontal();
                _reorder.Row(rowRect);
            }
            _reorder.End((from, to) =>
            {
                var moved = _order[from];
                _order.RemoveAt(from);
                _order.Insert(to, moved);
                // Batches fill in listed order, so reordering can regroup the slots.
                _stepsStale = true;
            });

            DrawCycleTimeline(request);
        }

        // ---- cycle timeline --------------------------------------------------

        const float TimelineRowHeight = 13f;
        /// <summary>Rows are click targets once the timing is set by hand, and 13 px is a
        /// thin thing to hit.</summary>
        const float ManualRowHeight = 17f;
        const float TimelineLabelWidth = 116f;
        const float TimelineLabelGap = 4f;
        const int MaxManualSteps = 64;

        /// <summary>True while the pass is written out as a grid rather than derived from the
        /// rates — the two are the same switch, a grid being what overrides.</summary>
        bool Manual => _steps.Count > 0;

        /// <summary>How many steps of the pass send each target, by name.</summary>
        static Dictionary<string, int> VisitCounts(List<List<string>> columns)
        {
            var counts = new Dictionary<string, int>();
            foreach (var column in columns)
                foreach (var name in column)
                {
                    counts.TryGetValue(name, out int times);
                    counts[name] = times + 1;
                }
            return counts;
        }

        /// <summary>
        /// What each step of the pass sends, in order — the columns of the view below. A grid
        /// is read straight from <see cref="_steps"/> rather than through the slots: nothing
        /// else can draw a step that is momentarily empty or overfull, and an editor that
        /// redrew someone else's pass the instant an edit went wrong would be unusable.
        /// </summary>
        List<List<string>> ColumnSets(AsyncSyncBuilder.Request request)
        {
            var columns = new List<List<string>>();
            if (Manual)
            {
                foreach (var step in _steps)
                    columns.Add(AsyncSyncBuilder.NormalizeStep(request, step));
                return columns;
            }
            var slots = AsyncSyncBuilder.BuildSlots(request);
            foreach (var step in AsyncSyncBuilder.EffectiveSchedule(request, slots))
                columns.Add(slots[step].targets);
            return columns;
        }

        /// <summary>
        /// One pass as a small timeline: a row per parameter, a mark wherever the cycle sends
        /// it. Rates are a statement about spacing — that a ×2 slot comes round near the
        /// opposite ends of the pass — and spacing is the one thing a line of names can't
        /// show. Names stay in a readable column on the left rather than inside the marks,
        /// so the view survives long parameter names and 60-step passes alike.
        ///
        /// Under a grid the same picture is the editor: every cell is a toggle, so what a step
        /// sends is set where it is read. Nothing is corrected on click — a step that moved
        /// out from under the pointer would be worse than a red cell saying what is wrong.
        /// </summary>
        void DrawCycleTimeline(AsyncSyncBuilder.Request request)
        {
            var columns = ColumnSets(request);
            if (columns.Count < 2)
            {
                // Too little left to draw, and in manual mode that would take the way back to
                // the rates down with it. Ask for a repair instead: one that finds nothing
                // schedulable returns empty, which is the way back.
                if (Manual) _stepsStale = true;
                return;
            }

            _timelineOpen = EditorGUILayout.Foldout(_timelineOpen,
                L.Tr("Cycle Timeline ({0} steps, {1:0.#} s)",
                    columns.Count, columns.Count * request.stepSeconds),
                true);
            if (!_timelineOpen) return;

            DrawTimingMode(request, columns);
            // The step count above may have just rewritten the grid.
            columns = ColumnSets(request);
            if (columns.Count < 2) return;

            var flagged = Violations(request, columns);
            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            float rowHeight = Manual ? ManualRowHeight : TimelineRowHeight;
            var mark = DaerDColors.SyncMark;
            var clash = DaerDColors.SyncClash;
            var flag = DaerDColors.Fade(clash, 0.22f);
            var track = DaerDColors.SyncTrack;

            // Any press or release outside a lane ends the stroke — the row that owns the
            // press claims it again below. Rows only see the events that land on them, so a
            // stroke that began or ended off the grid would otherwise still be held by the
            // row it last touched, and the next drag would paint that one.
            if (Event.current.type == EventType.MouseUp
                || Event.current.type == EventType.MouseDown)
                _paintTarget = null;

            foreach (var row in _order)
            {
                // One control per row whatever the width — the layout and repaint passes
                // must agree on how many there are.
                var line = EditorGUILayout.GetControlRect(false, rowHeight);
                var lane = new Rect(line.x + TimelineLabelWidth + TimelineLabelGap, line.y,
                    Mathf.Max(1f, line.width - TimelineLabelWidth - TimelineLabelGap), line.height);

                bool sent = false;
                foreach (var column in columns)
                    if (column.Contains(row.name)) { sent = true; break; }
                GUI.Label(new Rect(line.x, line.y, TimelineLabelWidth, line.height),
                    new GUIContent(row.name, RowTooltip(row, intervals, sent)),
                    TimelineLabelStyle(sent ? (Color?)null : clash));
                EditorGUI.DrawRect(lane, track);

                float cell = lane.width / columns.Count;
                for (int k = 0; k < columns.Count; k++)
                {
                    var box = new Rect(lane.x + k * cell, lane.y + 1f,
                        Mathf.Max(2f, cell - 1f), lane.height - 2f);
                    // The whole column is tinted, not just its marks: an empty step and two
                    // steps sending the same set are both invisible in the marks alone.
                    if (flagged[k]) EditorGUI.DrawRect(box, flag);
                    if (columns[k].Contains(row.name))
                        EditorGUI.DrawRect(box, flagged[k] ? clash : mark);
                }

                if (Manual) HandleGridCell(request, lane, cell, columns.Count, row);
            }

            DrawTimelineAxis(columns.Count * request.stepSeconds);
            if (Manual)
                EditorGUILayout.LabelField(
                    _fullStep >= 0
                        ? L.Tr("That step already carries as many parameters of this type as its channels allow. Raise the channel count, or use another step.")
                        : L.Tr("Click a cell to add or remove that parameter from the step; drag along a row to paint several. Everything in one column is sent together, in one go."),
                    WrappedMiniLabel());
        }

        /// <summary>
        /// The steps to draw in red: one that sends nothing, one that sends more of a type
        /// than the channels carry, one that repeats its neighbour (the wrap included) while
        /// no clock is paying for it, and the one whose last click was refused. All but the
        /// last are refused by Validate, which says so in words under the form; colouring says
        /// WHERE, which is the part a sentence cannot carry.
        /// </summary>
        bool[] Violations(AsyncSyncBuilder.Request request, List<List<string>> columns)
        {
            var flagged = new bool[columns.Count];
            var byName = DbtBuilder.ParametersByName(_controller);
            for (int k = 0; k < columns.Count; k++)
            {
                int next = (k + 1) % columns.Count;
                if (columns[k].Count == 0) flagged[k] = true;
                if (!request.allowRepeatSteps && columns.Count > 1
                    && SameSet(columns[k], columns[next]))
                    flagged[k] = flagged[next] = true;
                foreach (var name in columns[k])
                {
                    var parameter = byName.Find(name);
                    // Room "for one more" is measured against the rest of the step, so this
                    // reads as "does this member fit at all".
                    if (parameter == null || Fits(request, columns[k], name, parameter.type))
                        continue;
                    flagged[k] = true;
                    break;
                }
            }
            if (_fullStep >= 0 && _fullStep < flagged.Length) flagged[_fullStep] = true;
            return flagged;
        }

        static bool Fits(AsyncSyncBuilder.Request request, List<string> column, string name,
            AnimatorControllerParameterType type)
        {
            var others = new List<string>(column);
            others.Remove(name);
            return AsyncSyncBuilder.StepHasRoom(request, others, type);
        }

        static bool SameSet(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var name in a)
                if (!b.Contains(name)) return false;
            return true;
        }

        /// <summary>
        /// Toggling a cell, by click or by dragging along the row. The stroke remembers which
        /// way it started so dragging across a half-filled row fills it rather than inverting
        /// it, and it stays on the row it began on — a stroke that flipped every row it passed
        /// over would be unusable at 13 px a row.
        /// </summary>
        void HandleGridCell(AsyncSyncBuilder.Request request, Rect lane, float cell, int columns,
            Row row)
        {
            var e = Event.current;
            if (e.button != 0) return;
            if (e.type == EventType.MouseDown && lane.Contains(e.mousePosition))
            {
                int step = StepAt(lane, cell, columns, e.mousePosition.x);
                _paintTarget = row.name;
                _paintAdd = step < _steps.Count && !_steps[step].targets.Contains(row.name);
                PaintCell(request, step, row);
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _paintTarget == row.name
                     && e.mousePosition.x >= lane.x && e.mousePosition.x <= lane.xMax)
            {
                PaintCell(request, StepAt(lane, cell, columns, e.mousePosition.x), row);
                e.Use();
            }
        }

        static int StepAt(Rect lane, float cell, int columns, float x) =>
            Mathf.Clamp((int)((x - lane.x) / Mathf.Max(1f, cell)), 0, columns - 1);

        /// <summary>
        /// Puts the row into the step or takes it out. Capacity is the one rule enforced here
        /// rather than shown: a step with no channel left for the type cannot carry the target
        /// at all, so the click is refused — and the step is marked, since a click that does
        /// nothing and says nothing reads as a dead UI. The grid is NOT marked stale: a cell
        /// cannot change which slots exist, only whether the pass over them is legal, and that
        /// is what the red cells and the blocking problem are for.
        /// </summary>
        void PaintCell(AsyncSyncBuilder.Request request, int step, Row row)
        {
            if (step < 0 || step >= _steps.Count) return;
            var targets = _steps[step].targets;
            if (_paintAdd == targets.Contains(row.name)) return;
            if (_paintAdd)
            {
                if (!AsyncSyncBuilder.StepHasRoom(request, targets, row.type))
                {
                    _fullStep = step;
                    return;
                }
                targets.Add(row.name);
            }
            else
            {
                targets.Remove(row.name);
            }
            _fullStep = -1;
            Snapshot(request);
            GUI.changed = true;
        }

        /// <summary>The switch between a pass worked out from the rates and one written out as
        /// a grid, plus the length of the grid.</summary>
        void DrawTimingMode(AsyncSyncBuilder.Request request, List<List<string>> columns)
        {
            EditorGUILayout.BeginHorizontal();
            if (!Manual)
            {
                if (GUILayout.Button(new GUIContent(L.Tr("Set Timing By Hand"),
                        L.Tr("Write the pass out step by step instead of deriving it from the rates. It starts as the pass shown here, so nothing changes until you move something.")),
                        EditorStyles.miniButton, GUILayout.Width(150)))
                {
                    foreach (var column in columns) _steps.Add(StepOf(column));
                    // The grid starts as whatever pass was showing, a carried cycle included,
                    // and says everything that cycle said plus which targets share each step.
                    // Keeping both would leave the weaker one as dead data in the saved setup.
                    _schedule.Clear();
                    _stepsStale = false;
                    _fullStep = -1;
                    Snapshot(request);
                    // Switching modes changes which controls the rest of this pass draws, and
                    // IMGUI counts those across the layout and repaint passes both.
                    GUIUtility.ExitGUI();
                }
                // The wizard has no editor for a cycle written in C#, so the one thing it can
                // offer is the way out of it — without which the rate controls above would sit
                // there overridden by something nothing on screen can reach.
                if (_schedule.Count > 0 && GUILayout.Button(new GUIContent(L.Tr("Back To Rates"),
                        L.Tr("Discard the explicit cycle this setup carries and let the ×N rates lay the pass out again.")),
                        EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    // Nothing to snapshot: the cycle reaches the request through
                    // ApplyScheduleOverride, and this frame's request is abandoned below.
                    _schedule.Clear();
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                int max = Mathf.Max(MaxManualSteps, _steps.Count);
                // Two is the floor for any grid — the index has to change — and no longer the
                // slot count: a grid's steps carry sets, so one step can cover several slots'
                // worth of targets at once.
                int steps = EditorGUILayout.IntSlider(L.Tr("Steps"), _steps.Count, 2, max);
                if (steps != _steps.Count) SetStepCount(request, steps);
                if (GUILayout.Button(new GUIContent(L.Tr("Back To Rates"),
                        L.Tr("Discard the hand-written pass and let the ×N rates lay the cycle out again.")),
                        EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    _steps.Clear();
                    _fullStep = -1;
                    Snapshot(request);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Lengthens or shortens the grid. Growing repeats the set that has waited
        /// longest and clashes with neither end — the slot <see cref="AsyncSyncSchedule.NextStepSlot"/>
        /// would have picked, read back as the targets it stands for; shrinking takes steps off
        /// the end and leaves the repair to give a target that lost its last step a new one.</summary>
        void SetStepCount(AsyncSyncBuilder.Request request, int count)
        {
            while (_steps.Count > count && _steps.Count > 2)
                _steps.RemoveAt(_steps.Count - 1);

            while (_steps.Count < count)
            {
                // The picks read the grid through the request, so it has to keep up.
                Snapshot(request);
                var slots = AsyncSyncBuilder.BuildSlots(request);
                // Two sets can only alternate, so there is none free of both ends and the pass
                // can only grow in pairs; NextStepSlot gives up the far end for that case and
                // the repair settles the wrap afterwards.
                int picked = AsyncSyncBuilder.NextStepSlot(
                    AsyncSyncBuilder.EffectiveSchedule(request, slots), slots.Count);
                if (picked < 0) break;
                _steps.Add(StepOf(slots[picked].targets));
            }
            Snapshot(request);
            _fullStep = -1;
            _stepsStale = true;
        }

        /// <summary>Both ends of the pass, so the marks have a scale to read against.</summary>
        static void DrawTimelineAxis(float cycleSeconds)
        {
            var line = EditorGUILayout.GetControlRect(false, 12f);
            var lane = new Rect(line.x + TimelineLabelWidth + TimelineLabelGap, line.y,
                Mathf.Max(1f, line.width - TimelineLabelWidth - TimelineLabelGap), line.height);
            GUI.Label(lane, "0 s", EditorStyles.miniLabel);
            var end = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperRight };
            GUI.Label(lane, L.Tr("{0:0.#} s ⟳", cycleSeconds), end);
        }

        static GUIStyle TimelineLabelStyle(Color? textColor = null)
        {
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip,
            };
            if (textColor.HasValue) style.normal.textColor = textColor.Value;
            return style;
        }

        static GUIStyle WrappedMiniLabel() =>
            new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

        static string RowTooltip(Row row, Dictionary<string, float> intervals, bool sent)
        {
            if (!sent)
                return L.Tr("'{0}' is never sent — no step of the pass carries it.", row.name);
            string tooltip = row.name;
            if (intervals.TryGetValue(row.name, out float seconds))
                tooltip += " — " + L.Tr("every {0:0.##} s", seconds);
            if (row.request) tooltip += "  " + L.Tr("(requestable)");
            return tooltip;
        }

        /// <summary>
        /// The reason applying would be refused, shown while the setup is being edited rather
        /// than only in a dialog once the button is pressed: a rejected target — a Trigger, a
        /// parameter animation writes — is something to see at the moment it is ticked.
        /// Silent until there is a cycle to speak of, so an empty form doesn't open in red.
        /// </summary>
        public void DrawBlockingProblem(AsyncSyncBuilder.Request request)
        {
            if (request == null || request.targets.Count < 2) return;
            var problem = AsyncSyncBuilder.Validate(request);
            if (problem != null)
                EditorGUILayout.HelpBox(problem, MessageType.Error);
        }

        public void DrawPreview(AsyncSyncBuilder.Request request)
        {
            if (request.targets.Count < 2) return;

            int direct = AsyncSyncBuilder.DirectBits(request);
            int compressed = AsyncSyncBuilder.CompressedBits(request);
            EditorGUILayout.LabelField(
                L.Tr("Synced cost: {0} bit (direct sync would be {1} bit)", compressed, direct),
                EditorStyles.miniLabel);

            var slots = AsyncSyncBuilder.BuildSlots(request);
            if (_encoding == AsyncSyncBuilder.IndexEncoding.Auto)
                EditorGUILayout.LabelField(
                    L.Tr("Auto picked the {0} index for {1} slots.",
                        AsyncSyncBuilder.ResolveEncoding(request), slots.Count),
                    EditorStyles.miniLabel);

            // The Bool index only grows at powers of two, so the tail of a range is free —
            // worth saying out loud, since it changes how many parameters to put in.
            int free = AsyncSyncBuilder.FreeSlots(request);
            if (free > 0)
                EditorGUILayout.LabelField(
                    L.Tr("{0} more slot(s) fit in the current index at no extra synced cost.", free),
                    EditorStyles.miniLabel);

            // What the clock is costing right now. It charges only for the slots that actually
            // repeat, so the number moves as the pass is edited — and reading 0 is the answer
            // to "is this option doing anything yet".
            if (request.allowRepeatSteps)
            {
                var clock = AsyncSyncBuilder.BuildClock(request, slots,
                    AsyncSyncBuilder.EffectiveSchedule(request, slots));
                int phased = 0;
                foreach (var phases in clock.slotPhases)
                    if (phases > 1) phased++;
                EditorGUILayout.LabelField(
                    L.Tr("Repeated steps: {0} of {1} slot(s) need a second decoder state.",
                        phased, slots.Count),
                    EditorStyles.miniLabel);
            }

            int requests = AsyncSyncBuilder.RequestableTargets(request).Count;
            if (requests > 0)
                EditorGUILayout.LabelField(
                    L.Tr("Sync requests: {0} local Bool flag(s) and one Int, nothing synced.", requests),
                    EditorStyles.miniLabel);

            if (request.ready)
                EditorGUILayout.LabelField(
                    L.Tr("Remote initialized flag: {0} local Bool(s) and one layer, nothing synced.",
                        slots.Count + 1),
                    EditorStyles.miniLabel);

            // The pass actually being built, not the one the rates would lay out — the seconds
            // beside it come from the same place, and the two disagreeing read as a bug.
            int steps = AsyncSyncBuilder.EffectiveSchedule(request, slots).Count;
            EditorGUILayout.LabelField(
                L.Tr("One full pass: {0:0.#} s ({1} steps × {2:0.##} s)",
                    AsyncSyncBuilder.CycleSeconds(request), steps, _stepSeconds),
                EditorStyles.miniLabel);

            // What requests cost, as a number rather than as advice to use them sparingly. A
            // detour spends a step and hands the ring its place back, and detours can't chain,
            // so this is the ceiling however often the flags are raised.
            if (requests > 0)
                EditorGUILayout.LabelField(
                    L.Tr("With requests running: up to {0:0.#} s per pass.",
                        AsyncSyncBuilder.WorstCycleSeconds(request)),
                    EditorStyles.miniLabel);
        }

        /// <summary>
        /// The fix for the "targets are still synced in the store" warning: multiplexing only
        /// saves bits once the targets stop syncing directly, and that lives in the store, not
        /// in the controller. Drawn right under the warnings, and only while there is
        /// something to fix — no store, no store writes, or nothing synced draws nothing.
        /// </summary>
        public void DrawStoreFix(AsyncSyncBuilder.Request request)
        {
            if (request == null || request.store == null || !request.addToStore
                || request.targets == null)
                return;
            // Runs every repaint, so it stops at the first hit — same reading as the warning.
            bool anySynced = false;
            foreach (var name in request.targets)
            {
                var entry = request.store.Find(name);
                if (entry == null || !entry.synced) continue;
                anySynced = true;
                break;
            }
            if (!anySynced) return;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent(L.Tr("Unsync Targets In Store"),
                    L.Tr("Clear the synced flag on the multiplexed targets in the parameter store. Their values travel through the generated channels instead, which is the whole point of the setup.")),
                    EditorStyles.miniButton, GUILayout.Width(190)))
            {
                using (new UndoScope("Unsync Async Sync Targets"))
                    request.store.SetSynced(request.targets, false);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
