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
        bool _addToStore = true;
        bool _assignEmptyClip = true;
        string _search = string.Empty;
        Vector2 _pickScroll;
        /// <summary>Width of the schedule strip as of the last repaint. The cell grid is laid
        /// out by hand, and deriving the row count from a rect measured in the current pass
        /// would change it between layout and repaint — IMGUI counts controls across both.</summary>
        float _stripWidth = 400f;

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

            var rates = config.RateMap();
            foreach (var row in _rows)
            {
                row.selected = false;
                row.rate = 1;
                row.request = false;
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
            }
            return request;
        }

        // ---- sections --------------------------------------------------------

        public void DrawGeneratedSection()
        {
            EditorGUILayout.LabelField(L.Tr("Generated Sync"), EditorStyles.boldLabel);
            _baseName = EditorGUILayout.TextField(L.Tr("Base Name"), _baseName);
            _encoding = (AsyncSyncBuilder.IndexEncoding)EditorGUILayout.Popup(
                L.Tr("Index Encoding"), (int)_encoding, EncodingLabels());
            _floatChannels = EditorGUILayout.IntSlider(
                new GUIContent(L.Tr("Float Channels"),
                    L.Tr("Synced Float channels. Each step carries up to this many Float parameters at once — fewer slots and a faster cycle, at 8 synced bits per extra channel.")),
                _floatChannels, 1, 8);
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
            }
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
                L.Tr("Top to bottom is the cycle order. ×N syncs a parameter N times per pass; everything else shares the steps in between."),
                EditorStyles.miniLabel);

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);

            _reorder.Begin();
            foreach (var row in _order)
            {
                var rowRect = EditorGUILayout.BeginHorizontal();
                _reorder.DrawHandle();

                EditorGUILayout.LabelField(row.name + "  (" + row.type + ")");

                int rateIndex = Mathf.Max(0, Array.IndexOf(RateValues, row.rate));
                rateIndex = EditorGUILayout.Popup(rateIndex, RateLabels, GUILayout.Width(48));
                row.rate = RateValues[rateIndex];

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
            });

            DrawScheduleStrip(request);
        }

        // ---- schedule strip --------------------------------------------------

        const float MinCellWidth = 46f;
        const float CellHeight = 20f;
        const float CellGap = 2f;
        /// <summary>Cells drawn before the strip gives up and says how many are left. The
        /// point is to see the interleaving, not to read all 120 steps of a long pass.</summary>
        const int MaxStripCells = 64;

        /// <summary>
        /// One pass drawn left to right, one cell per step, coloured by slot: the ×N rates
        /// are meant to be read as spacing, and a line of names can't show spacing. Wraps to
        /// as many rows as the pane's width needs.
        /// </summary>
        void DrawScheduleStrip(AsyncSyncBuilder.Request request)
        {
            var slots = AsyncSyncBuilder.BuildSlots(request);
            var schedule = AsyncSyncBuilder.EffectiveSchedule(request, slots);
            if (schedule.Count < 2) return;

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            var requestable = AsyncSyncBuilder.RequestableTargets(request);
            int shown = Mathf.Min(schedule.Count, MaxStripCells);
            int perRow = Mathf.Max(1, Mathf.FloorToInt(_stripWidth / MinCellWidth));
            int rows = Mathf.CeilToInt(shown / (float)perRow);

            var area = GUILayoutUtility.GetRect(MinCellWidth, rows * (CellHeight + CellGap) - CellGap,
                GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) _stripWidth = area.width;

            float cellWidth = (area.width - (perRow - 1) * CellGap) / perRow;
            var label = CellStyle();
            for (int i = 0; i < shown; i++)
            {
                var slot = slots[schedule[i]];
                var cell = new Rect(
                    area.x + i % perRow * (cellWidth + CellGap),
                    area.y + i / perRow * (CellHeight + CellGap),
                    cellWidth, CellHeight);

                EditorGUI.DrawRect(cell, SlotColor(schedule[i]));
                // A glyph would be at the mercy of the editor font; a bar under the cell
                // reads the same everywhere.
                if (SlotIsRequestable(slot, requestable))
                    EditorGUI.DrawRect(new Rect(cell.x, cell.yMax - 2f, cell.width, 2f),
                        EditorGUIUtility.isProSkin ? Color.white : Color.black);
                GUI.Label(cell, new GUIContent(CellLabel(slot), CellTooltip(slot, intervals, requestable)),
                    label);
            }

            if (schedule.Count > shown)
                EditorGUILayout.LabelField(
                    L.Tr("…and {0} more step(s) in the pass.", schedule.Count - shown),
                    EditorStyles.miniLabel);
        }

        /// <summary>Hue stepped by the golden ratio: neighbouring slots land far apart on the
        /// wheel however many there are, so adjacent cells never read as one block.</summary>
        static Color SlotColor(int slot)
        {
            float hue = slot * 0.618034f % 1f;
            return EditorGUIUtility.isProSkin
                ? Color.HSVToRGB(hue, 0.42f, 0.52f)
                : Color.HSVToRGB(hue, 0.30f, 0.94f);
        }

        static GUIStyle CellStyle() => new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
            normal = { textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black },
        };

        static bool SlotIsRequestable(AsyncSyncBuilder.Slot slot, List<string> requestable)
        {
            foreach (var name in slot.targets)
                if (requestable.Contains(name)) return true;
            return false;
        }

        /// <summary>"+n" for a batch, matching the generated state names.</summary>
        static string CellLabel(AsyncSyncBuilder.Slot slot) =>
            slot.targets.Count > 1
                ? slot.targets[0] + "+" + (slot.targets.Count - 1)
                : slot.targets[0];

        static string CellTooltip(AsyncSyncBuilder.Slot slot, Dictionary<string, float> intervals,
            List<string> requestable)
        {
            var lines = new List<string>();
            foreach (var name in slot.targets)
            {
                string line = intervals.TryGetValue(name, out float seconds)
                    ? L.Tr("{0} — every {1:0.##} s", name, seconds)
                    : name;
                if (requestable.Contains(name)) line += "  " + L.Tr("(requestable)");
                lines.Add(line);
            }
            return string.Join("\n", lines);
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
