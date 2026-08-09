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
        bool _addToStore = true;
        bool _assignEmptyClip = true;
        string _search = string.Empty;
        Vector2 _pickScroll;
        bool _timelineOpen = true;
        /// <summary>The cycle spelled out step by step, as target names. Empty means the pass
        /// is derived from the rates, so this list doubles as the manual/automatic mode.</summary>
        readonly List<string> _schedule = new List<string>();
        /// <summary>Set by anything that can reshape the slots the schedule refers to. The
        /// repair runs once per such edit rather than every repaint: it is stable on a valid
        /// cycle, but running it unprompted still invites steps to move on their own.</summary>
        bool _scheduleStale;

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

        public void LoadConfig(GraphFrameData.AsyncSyncConfig config)
        {
            _baseName = config.baseName;
            _encoding = (AsyncSyncBuilder.IndexEncoding)config.encoding;
            _stepSeconds = config.stepSeconds;
            _floatChannels = Mathf.Clamp(config.FloatChannelsOrDefault, 1, 8);
            _boolChannels = Mathf.Clamp(config.BoolChannelsOrDefault, 1, 8);
            _schedule.Clear();
            if (config.schedule != null) _schedule.AddRange(config.schedule);
            _scheduleStale = true;

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
            // targets are in and before the override goes on. A cycle it cannot settle comes
            // back empty, which is exactly how this form spells "use the rates".
            if (_scheduleStale && _schedule.Count > 0)
            {
                var repaired = AsyncSyncBuilder.RepairScheduleOverride(request, _schedule);
                _schedule.Clear();
                _schedule.AddRange(repaired);
            }
            _scheduleStale = false;
            request.scheduleOverride.AddRange(_schedule);
            return request;
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
            if (EditorGUI.EndChangeCheck()) _scheduleStale = true;
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
            _scheduleStale = true;
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
                    ? L.Tr("Top to bottom is the cycle order. The timing itself is set by hand in the timeline below.")
                    : L.Tr("Top to bottom is the cycle order. ×N syncs a parameter N times per pass; everything else shares the steps in between."),
                EditorStyles.miniLabel);

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            var visits = Manual ? VisitCounts(request) : null;
            // Batching is why two rows can move as one, and until now nothing said so. The
            // slot number is shown whenever channels could group anything — a condition that
            // cannot change mid-draw, unlike "is anything actually grouped", which the Split
            // buttons below would flip and take the layout's control count with it.
            var slots = AsyncSyncBuilder.BuildSlots(request);
            bool grouping = _floatChannels > 1 || _boolChannels > 1;
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
                    if (split != row.split)
                    {
                        row.split = split;
                        _scheduleStale = true;
                    }
                    EditorGUI.EndDisabledGroup();
                }

                if (Manual)
                {
                    // A count, not a control: under a hand-written cycle the rate no longer
                    // decides how often a slot comes round, so showing it as an input would
                    // be a lie. (It still decides which targets batch together, which is why
                    // changing it means leaving manual timing first.)
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
                _scheduleStale = true;
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

        /// <summary>True while the cycle is spelled out step by step rather than derived from
        /// the rates — the two are the same switch, an explicit cycle being what overrides.</summary>
        bool Manual => _schedule.Count > 0;

        /// <summary>How many steps of the pass send each target, by name.</summary>
        Dictionary<string, int> VisitCounts(AsyncSyncBuilder.Request request)
        {
            var counts = new Dictionary<string, int>();
            var slots = AsyncSyncBuilder.BuildSlots(request);
            var schedule = CurrentSchedule(request, slots, out _);
            var visits = new int[slots.Count];
            foreach (var step in schedule) visits[step]++;
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    counts[name] = visits[i];
            return counts;
        }

        /// <summary>
        /// The pass as slot indices, plus the name→slot map the rows are drawn against. A
        /// hand-written cycle is read straight from <see cref="_schedule"/> rather than through
        /// <see cref="AsyncSyncBuilder.EffectiveSchedule"/>: that one falls back to the rates
        /// the moment the cycle stops being runnable, and an editor that redrew someone else's
        /// pass the instant an edit went wrong would be unusable.
        /// </summary>
        List<int> CurrentSchedule(AsyncSyncBuilder.Request request,
            List<AsyncSyncBuilder.Slot> slots, out Dictionary<string, int> slotOf)
        {
            slotOf = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOf[name] = i;

            var schedule = new List<int>();
            if (!Manual) return AsyncSyncBuilder.EffectiveSchedule(request, slots);
            foreach (var name in _schedule)
                if (slotOf.TryGetValue(name, out int slot))
                    schedule.Add(slot);
            return schedule;
        }

        /// <summary>
        /// One pass as a small timeline: a row per parameter, a mark wherever the cycle sends
        /// it. Rates are a statement about spacing — that a ×2 slot comes round near the
        /// opposite ends of the pass — and spacing is the one thing a line of names can't
        /// show. Names stay in a readable column on the left rather than inside the marks,
        /// so the view survives long parameter names and 60-step passes alike.
        /// </summary>
        void DrawCycleTimeline(AsyncSyncBuilder.Request request)
        {
            var slots = AsyncSyncBuilder.BuildSlots(request);
            var schedule = CurrentSchedule(request, slots, out var slotOf);
            if (schedule.Count < 2)
            {
                // Too little left to draw, and in manual mode that would take the way back to
                // the rates down with it. Ask for a repair instead: one that finds nothing
                // schedulable returns empty, which is the way back.
                if (Manual) _scheduleStale = true;
                return;
            }

            _timelineOpen = EditorGUILayout.Foldout(_timelineOpen,
                L.Tr("Cycle Timeline ({0} steps, {1:0.#} s)",
                    schedule.Count, schedule.Count * request.stepSeconds),
                true);
            if (!_timelineOpen) return;

            DrawTimingMode(slots, schedule);

            // A step beside itself, and a parameter the pass never sends, are both refused by
            // Validate — which says so under the form. Colouring the cells says WHERE, which
            // is the part a sentence cannot carry.
            var clashing = new bool[schedule.Count];
            for (int k = 0; k < schedule.Count; k++)
                if (schedule[k] == schedule[(k + 1) % schedule.Count])
                    clashing[k] = clashing[(k + 1) % schedule.Count] = true;

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            float rowHeight = Manual ? ManualRowHeight : TimelineRowHeight;
            var mark = EditorGUIUtility.isProSkin
                ? new Color(0.35f, 0.65f, 0.95f)
                : new Color(0.20f, 0.45f, 0.80f);
            var clash = EditorGUIUtility.isProSkin
                ? new Color(0.85f, 0.35f, 0.30f)
                : new Color(0.75f, 0.20f, 0.15f);
            var track = EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.06f)
                : new Color(0f, 0f, 0f, 0.06f);

            foreach (var row in _order)
            {
                if (!slotOf.TryGetValue(row.name, out int slot)) continue;
                // One control per row whatever the width — the layout and repaint passes
                // must agree on how many there are.
                var line = EditorGUILayout.GetControlRect(false, rowHeight);
                var lane = new Rect(line.x + TimelineLabelWidth + TimelineLabelGap, line.y,
                    Mathf.Max(1f, line.width - TimelineLabelWidth - TimelineLabelGap), line.height);

                bool sent = schedule.Contains(slot);
                GUI.Label(new Rect(line.x, line.y, TimelineLabelWidth, line.height),
                    new GUIContent(row.name, RowTooltip(row, intervals, sent)),
                    TimelineLabelStyle(sent ? (Color?)null : clash));
                EditorGUI.DrawRect(lane, track);

                float cell = lane.width / schedule.Count;
                for (int k = 0; k < schedule.Count; k++)
                {
                    if (schedule[k] != slot) continue;
                    EditorGUI.DrawRect(
                        new Rect(lane.x + k * cell, lane.y + 1f,
                            Mathf.Max(2f, cell - 1f), lane.height - 2f),
                        clashing[k] ? clash : mark);
                }

                if (Manual) HandleLaneClick(lane, cell, schedule.Count, row);
            }

            DrawTimelineAxis(schedule.Count * request.stepSeconds);
            if (Manual)
                EditorGUILayout.LabelField(
                    L.Tr("Click a row to make that step send it. Every step sends exactly one slot, so a click moves the step rather than adding one."),
                    WrappedMiniLabel());
        }

        /// <summary>
        /// Assigning a step to a row, by click or by dragging across several. Nothing here
        /// enforces the two rules a cycle has to keep — a repair that moved steps out from
        /// under the pointer would be worse than the red cells that say what is wrong.
        /// </summary>
        void HandleLaneClick(Rect lane, float cell, int steps, Row row)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown && e.type != EventType.MouseDrag) return;
            if (e.button != 0 || !lane.Contains(e.mousePosition)) return;

            int step = Mathf.Clamp((int)((e.mousePosition.x - lane.x) / Mathf.Max(1f, cell)),
                0, Mathf.Min(steps, _schedule.Count) - 1);
            if (_schedule[step] != row.name)
            {
                _schedule[step] = row.name;
                GUI.changed = true;
            }
            e.Use();
        }

        /// <summary>The switch between a pass worked out from the rates and one written out
        /// step by step, plus the length of the hand-written one.</summary>
        void DrawTimingMode(List<AsyncSyncBuilder.Slot> slots, List<int> schedule)
        {
            EditorGUILayout.BeginHorizontal();
            if (!Manual)
            {
                if (GUILayout.Button(new GUIContent(L.Tr("Set Timing By Hand"),
                        L.Tr("Write the pass out step by step instead of deriving it from the rates. It starts as the pass shown here, so nothing changes until you move something.")),
                        EditorStyles.miniButton, GUILayout.Width(150)))
                {
                    foreach (var step in schedule) _schedule.Add(slots[step].targets[0]);
                    _scheduleStale = false;
                    // Switching modes changes which controls the rest of this pass draws, and
                    // IMGUI counts those across the layout and repaint passes both.
                    GUIUtility.ExitGUI();
                }
            }
            else
            {
                int max = Mathf.Max(MaxManualSteps, _schedule.Count);
                int steps = EditorGUILayout.IntSlider(L.Tr("Steps"), _schedule.Count,
                    Mathf.Max(2, slots.Count), max);
                if (steps != _schedule.Count) SetStepCount(steps, slots);
                if (GUILayout.Button(new GUIContent(L.Tr("Back To Rates"),
                        L.Tr("Discard the hand-written pass and let the ×N rates lay the cycle out again.")),
                        EditorStyles.miniButton, GUILayout.Width(110)))
                {
                    _schedule.Clear();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Lengthens or shortens the hand-written pass. Growing hands each new step
        /// to the slot that has waited longest; shrinking takes them off the end and leaves
        /// the repair to put back any slot that lost its last visit.</summary>
        void SetStepCount(int steps, List<AsyncSyncBuilder.Slot> slots)
        {
            while (_schedule.Count > steps && _schedule.Count > 2)
                _schedule.RemoveAt(_schedule.Count - 1);

            var slotOf = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOf[name] = i;

            while (_schedule.Count < steps)
            {
                var indices = new List<int>();
                foreach (var name in _schedule)
                    if (slotOf.TryGetValue(name, out int slot)) indices.Add(slot);

                // Two slots can only alternate, so there is no slot free of both ends and the
                // pass can only grow in pairs; NextStepSlot gives up the far end for that case
                // and the repair settles the wrap afterwards.
                int picked = AsyncSyncBuilder.NextStepSlot(indices, slots.Count);
                if (picked < 0) break;
                _schedule.Add(slots[picked].targets[0]);
            }
            _scheduleStale = true;
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

            int requests = AsyncSyncBuilder.RequestableTargets(request).Count;
            if (requests > 0)
                EditorGUILayout.LabelField(
                    L.Tr("Sync requests: {0} local Bool flag(s), nothing synced.", requests),
                    EditorStyles.miniLabel);

            int steps = AsyncSyncBuilder.BuildSchedule(slots).Count;
            EditorGUILayout.LabelField(
                L.Tr("One full pass: {0:0.#} s ({1} steps × {2:0.##} s)",
                    AsyncSyncBuilder.CycleSeconds(request), steps, _stepSeconds),
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
