using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="AsyncSyncBuilder"/>: tick the parameters to multiplex (with a
    /// search filter and an optional priority mark per row), pick the Float channel count and
    /// step interval, and generate the send-cycle / decoder layer. Shows the synced-bit cost
    /// against syncing each parameter directly. Parameters that belong to sync machinery
    /// (IsLocal, another setup's generated parameters) are kept out of the list entirely.
    /// </summary>
    class AsyncSyncWindow : EditorWindow
    {
        class Row
        {
            public string name;
            public AnimatorControllerParameterType type;
            public bool selected;
            public bool priority;
        }

        AnimatorController _controller;
        Action<int> _onApplied;

        readonly List<Row> _rows = new List<Row>();
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
        Vector2 _scroll;

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
            window.minSize = new Vector2(480, 480);
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
            // motion on them; the controller's Empty clip is exactly what that is for.
            var emptyClip = GraphFrameData.GetEmptyClip(_controller);
            using (new EditorGUI.DisabledScope(emptyClip == null))
                _assignEmptyClip = EditorGUILayout.Toggle(
                    new GUIContent(
                        emptyClip != null
                            ? L.Tr("Fill States With '{0}'", emptyClip.name)
                            : L.Tr("Fill States With The Empty Clip"),
                        L.Tr("Assign this controller's Empty clip to the generated states, so they aren't motion-less. Set the clip in the controller overview.")),
                    emptyClip != null && _assignEmptyClip);

            var store = ParameterStore.Of(_controller);
            if (store != null)
                _addToStore = EditorGUILayout.Toggle(
                    new GUIContent(L.Tr("Add Synced Params To Store"),
                        L.Tr("Add the generated index and channel parameters to the associated parameter store as synced.")),
                    _addToStore);

            EditorGUILayout.Space(4);
            DrawTargetList();

            var request = BuildRequest(store);
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
        }

        /// <summary>The tick list with its search filter and the per-row priority mark.</summary>
        void DrawTargetList()
        {
            int selectedCount = 0;
            foreach (var row in _rows)
                if (row.selected) selectedCount++;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                L.Tr("Parameters To Multiplex") + " (" + selectedCount + "/" + _rows.Count + ")",
                EditorStyles.boldLabel);
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(80), GUILayout.MaxWidth(180));
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(140));
            int visible = 0;
            foreach (var row in _rows)
            {
                if (!string.IsNullOrEmpty(_search)
                    && row.name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                visible++;

                EditorGUILayout.BeginHorizontal();
                row.selected = EditorGUILayout.ToggleLeft(
                    row.name + "  (" + row.type + ")", row.selected);
                if (row.selected)
                    row.priority = GUILayout.Toggle(row.priority,
                        new GUIContent(L.Tr("Priority"),
                            L.Tr("Refresh this parameter every other step; the parameters without the mark share the steps in between.")),
                        EditorStyles.miniButton, GUILayout.Width(64));
                else
                    row.priority = false;
                EditorGUILayout.EndHorizontal();
            }
            if (_rows.Count == 0)
                EditorGUILayout.HelpBox(L.Tr("This controller has no Float / Int / Bool parameters."),
                    MessageType.Info);
            else if (visible == 0)
                EditorGUILayout.LabelField(L.Tr("No parameter matches the search."),
                    EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
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

            float priorityInterval = AsyncSyncBuilder.PriorityIntervalSeconds(request);
            if (priorityInterval > 0f)
                EditorGUILayout.LabelField(
                    L.Tr("Priority parameters refresh every {0:0.#} s; the rest wait for the full pass.",
                        priorityInterval),
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
            foreach (var row in _rows)
            {
                row.selected = config.targets.Contains(row.name);
                row.priority = row.selected && config.priorities != null
                    && config.priorities.Contains(row.name);
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
            foreach (var row in _rows)
            {
                if (!row.selected) continue;
                request.targets.Add(row.name);
                if (row.priority) request.priorities.Add(row.name);
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
