using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="AsyncSyncBuilder"/>: tick the parameters to multiplex (with a
    /// search filter), then arrange them in the Sync Order section — drag rows to set the
    /// cycle order, give a row a ×N rate to sync it N times per pass, and read the resulting
    /// refresh interval next to each row, with the whole cycle previewed underneath. Shows
    /// the synced-bit cost against syncing each parameter directly. Parameters that belong
    /// to sync machinery (IsLocal, another setup's generated parameters) are kept out of
    /// the list entirely.
    /// </summary>
    class AsyncSyncWindow : EditorWindow
    {
        class Row
        {
            public string name;
            public AnimatorControllerParameterType type;
            public bool selected;
            public int rate = 1;
        }

        AnimatorController _controller;
        Action<int> _onApplied;

        readonly List<Row> _rows = new List<Row>();
        /// <summary>Selected rows in multiplex order — this list IS the cycle order.
        /// Ticking appends, unticking removes, dragging rearranges.</summary>
        readonly List<Row> _order = new List<Row>();
        readonly ListReorder _reorder = new ListReorder();
        // Saved setups (persisted in GraphFrameData): picking one prefills the wizard and
        // regenerates that layer in place — same idea as the DBT gadget's layer choice.
        readonly List<GraphFrameData.AsyncSyncConfig> _configs =
            new List<GraphFrameData.AsyncSyncConfig>();
        /// <summary>0 = create a new layer; 1.. = _configs[index - 1].</summary>
        int _layerChoice;
        string _baseName = "Async";
        AsyncSyncBuilder.IndexEncoding _encoding = AsyncSyncBuilder.IndexEncoding.Auto;
        float _stepSeconds = 0.3f;
        int _floatChannels = 1;
        bool _addToStore = true;
        bool _assignEmptyClip = true;
        string _search = string.Empty;
        Vector2 _windowScroll;
        Vector2 _pickScroll;

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

        /// <summary>onApplied receives the index of the generated layer.</summary>
        public static void Open(AnimatorController controller, Action<int> onApplied)
        {
            var window = CreateInstance<AsyncSyncWindow>();
            window.titleContent = new GUIContent(L.Tr("Async Sync"));
            window.minSize = new Vector2(500, 560);
            window._controller = controller;
            window._onApplied = onApplied;
            window._configs.AddRange(GraphFrameData.GetAsyncSyncs(controller));
            window.BuildRows();
            if (window._configs.Count > 0)
            {
                window._layerChoice = 1;
                window.LoadConfig(window._configs[0]);
            }
            window.ShowUtility();
        }

        void BuildRows()
        {
            _rows.Clear();
            _order.Clear();
            foreach (var parameter in _controller.parameters)
            {
                if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                // The machinery must not multiplex itself: IsLocal and the parameters an
                // async-sync setup generated never appear as candidates.
                if (AsyncSyncBuilder.IsReservedName(_controller, parameter.name)) continue;
                _rows.Add(new Row { name = parameter.name, type = parameter.type });
            }
        }

        void OnGUI()
        {
            // Utility windows outlive domain reloads but their fields don't; bail out.
            if (_controller == null)
            {
                Close();
                return;
            }

            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);

            EditorGUILayout.LabelField(L.Tr("Async Sync"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Time-multiplexes the ticked parameters over a few synced parameters (an index plus value channels): a local cycle copies each slot into the channels in turn, and remote clients decode it back. The targets themselves stay unsynced — values update round-robin, one slot per step."),
                MessageType.Info);

            DrawLayerChoice();

            EditorGUILayout.Space(4);
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

            var store = ParameterStore.Of(_controller);
            if (store != null)
                _addToStore = EditorGUILayout.Toggle(
                    new GUIContent(L.Tr("Add Synced Params To Store"),
                        L.Tr("Add the generated index and channel parameters to the associated parameter store as synced.")),
                    _addToStore);

            EditorGUILayout.Space(4);
            DrawPickList();

            var request = BuildRequest(store);
            EditorGUILayout.Space(4);
            DrawOrderSection(request);

            DrawPreview(request);
            foreach (var warning in AsyncSyncBuilder.Warnings(request))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(100)))
                Close();
            if (GUILayout.Button(L.Tr("Create"), GUILayout.Width(100)))
                TryApply(request);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>The tick list with its search filter. Ticking appends the parameter to
        /// the cycle order below; unticking removes it.</summary>
        void DrawPickList()
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
            }
        }

        /// <summary>
        /// The cycle editor: selected parameters in multiplex order. Drag the handle to
        /// reorder; the ×N popup syncs a row N times per pass; the label on the right is
        /// the refresh interval the current schedule actually delivers.
        /// </summary>
        void DrawOrderSection(AsyncSyncBuilder.Request request)
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

            DrawCyclePreview(request);
        }

        /// <summary>One line spelling out the pass: "F → B → F → I". Long cycles truncate —
        /// the point is to see the interleaving, not to read all 60 steps.</summary>
        void DrawCyclePreview(AsyncSyncBuilder.Request request)
        {
            var slots = AsyncSyncBuilder.BuildSlots(request);
            var schedule = AsyncSyncBuilder.BuildSchedule(slots);
            if (schedule.Count < 2) return;

            const int maxShown = 24;
            var labels = new List<string>();
            for (int i = 0; i < schedule.Count && i < maxShown; i++)
            {
                var slot = slots[schedule[i]];
                labels.Add(slot.targets.Count > 1
                    ? slot.targets[0] + "+" + (slot.targets.Count - 1)
                    : slot.targets[0]);
            }
            string text = string.Join(" → ", labels);
            if (schedule.Count > maxShown)
                text += " → …(" + schedule.Count + ")";
            var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
            EditorGUILayout.LabelField(L.Tr("Cycle:") + "  " + text + "  ⟳", style);
        }

        void DrawPreview(AsyncSyncBuilder.Request request)
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

            int steps = AsyncSyncBuilder.BuildSchedule(slots).Count;
            EditorGUILayout.LabelField(
                L.Tr("One full pass: {0:0.#} s ({1} steps × {2:0.##} s)",
                    AsyncSyncBuilder.CycleSeconds(request), steps, _stepSeconds),
                EditorStyles.miniLabel);
        }

        /// <summary>Saved setups double as the layer choice: "create new" or regenerate an
        /// existing async-sync layer in place with the (editable) saved inputs.</summary>
        void DrawLayerChoice()
        {
            var labels = new string[_configs.Count + 1];
            labels[0] = L.Tr("Create new layer");
            for (int i = 0; i < _configs.Count; i++)
                labels[i + 1] = LayerNameOf(_configs[i]);
            int picked = EditorGUILayout.Popup(L.Tr("Target Layer"),
                Mathf.Clamp(_layerChoice, 0, labels.Length - 1), labels);
            if (picked != _layerChoice)
            {
                _layerChoice = picked;
                if (picked > 0)
                    LoadConfig(_configs[picked - 1]);
            }
            if (_layerChoice > 0)
                EditorGUILayout.HelpBox(
                    L.Tr("Applying regenerates the selected layer in place (its states are rebuilt)."),
                    MessageType.None);
        }

        string LayerNameOf(GraphFrameData.AsyncSyncConfig config)
        {
            var layers = _controller.layers;
            foreach (var layer in layers)
                if (layer.stateMachine == config.layer)
                    return layer.name;
            return config.layer != null ? config.layer.name : "?";
        }

        int LayerIndexOf(GraphFrameData.AsyncSyncConfig config)
        {
            var layers = _controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == config.layer)
                    return i;
            return -1;
        }

        void LoadConfig(GraphFrameData.AsyncSyncConfig config)
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
                        _order.Add(row);
                        break;
                    }
        }

        AsyncSyncBuilder.Request BuildRequest(ParameterStore store)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = _controller,
                baseName = _baseName != null ? _baseName.Trim() : string.Empty,
                encoding = _encoding,
                stepSeconds = _stepSeconds,
                floatChannels = _floatChannels,
                store = store,
                addToStore = _addToStore,
                assignEmptyClip = _assignEmptyClip,
                emptyClip = GraphFrameData.GetEmptyClip(_controller),
                layerIndex = _layerChoice > 0 && _layerChoice - 1 < _configs.Count
                    ? LayerIndexOf(_configs[_layerChoice - 1]) : -1,
            };
            foreach (var row in _order)
            {
                request.targets.Add(row.name);
                if (row.rate > 1) request.rates[row.name] = row.rate;
            }
            return request;
        }

        void TryApply(AsyncSyncBuilder.Request request)
        {
            var error = AsyncSyncBuilder.Validate(request);
            if (error != null)
            {
                EditorUtility.DisplayDialog(L.Tr("Async Sync"), error, "OK");
                return;
            }
            AsyncSyncBuilder.Apply(request);
            _onApplied?.Invoke(request.layerIndex >= 0
                ? request.layerIndex : _controller.layers.Length - 1);
            Close();
        }
    }
}
