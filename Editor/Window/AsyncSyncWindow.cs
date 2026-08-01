using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="AsyncSyncBuilder"/>: tick the parameters to multiplex,
    /// pick the index encoding and step interval, and generate the send-cycle / decoder
    /// layer. Shows the synced-bit cost against syncing each parameter directly.
    /// </summary>
    class AsyncSyncWindow : EditorWindow
    {
        class Row
        {
            public string name;
            public AnimatorControllerParameterType type;
            public bool selected;
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
        AsyncSyncBuilder.IndexEncoding _encoding = AsyncSyncBuilder.IndexEncoding.Int;
        float _stepSeconds = 0.3f;
        bool _addToStore = true;
        Vector2 _scroll;

        static readonly string[] EncodingLabels = { "Int (8 bit)", "Bool × n (1 bit each)" };

        /// <summary>onApplied receives the index of the generated layer.</summary>
        public static void Open(AnimatorController controller, Action<int> onApplied)
        {
            var window = CreateInstance<AsyncSyncWindow>();
            window.titleContent = new GUIContent(L.Tr("Async Sync"));
            window.minSize = new Vector2(460, 420);
            window._controller = controller;
            window._onApplied = onApplied;
            window.BuildRows();
            window._configs.AddRange(GraphFrameData.GetAsyncSyncs(controller));
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
                if (parameter.type != AnimatorControllerParameterType.Trigger)
                    _rows.Add(new Row { name = parameter.name, type = parameter.type });
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
                L.Tr("Time-multiplexes the ticked parameters over a few synced parameters (an index plus one value channel per type): a local cycle copies each parameter into its channel in turn, and remote clients decode it back. The targets themselves stay unsynced — values update round-robin, one slot per step."),
                MessageType.Info);

            DrawLayerChoice();
            _baseName = EditorGUILayout.TextField(L.Tr("Base Name"), _baseName);
            _encoding = (AsyncSyncBuilder.IndexEncoding)EditorGUILayout.Popup(
                L.Tr("Index Encoding"), (int)_encoding, EncodingLabels);
            _stepSeconds = EditorGUILayout.FloatField(
                new GUIContent(L.Tr("Step Interval (s)"),
                    L.Tr("Dwell per slot. VRChat syncs roughly every 0.3 s — shorter steps risk remotes skipping slots.")),
                _stepSeconds);

            var store = ParameterStore.Of(_controller);
            if (store != null)
                _addToStore = EditorGUILayout.Toggle(
                    new GUIContent(L.Tr("Add Synced Params To Store"),
                        L.Tr("Add the generated index and channel parameters to the associated parameter store as synced.")),
                    _addToStore);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L.Tr("Parameters To Multiplex"), EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120));
            foreach (var row in _rows)
                row.selected = EditorGUILayout.ToggleLeft(
                    row.name + "  (" + row.type + ")", row.selected);
            if (_rows.Count == 0)
                EditorGUILayout.HelpBox(L.Tr("This controller has no Float / Int / Bool parameters."),
                    MessageType.Info);
            EditorGUILayout.EndScrollView();

            var request = BuildRequest(store);
            if (request.targets.Count >= 2)
            {
                int direct = AsyncSyncBuilder.DirectBits(request);
                int compressed = AsyncSyncBuilder.CompressedBits(request);
                EditorGUILayout.LabelField(
                    L.Tr("Synced cost: {0} bit (direct sync would be {1} bit)", compressed, direct),
                    EditorStyles.miniLabel);
            }
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
            foreach (var row in _rows)
                row.selected = config.targets.Contains(row.name);
        }

        AsyncSyncBuilder.Request BuildRequest(ParameterStore store)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = _controller,
                baseName = _baseName != null ? _baseName.Trim() : string.Empty,
                encoding = _encoding,
                stepSeconds = _stepSeconds,
                store = store,
                addToStore = _addToStore,
                layerIndex = _layerChoice > 0 && _layerChoice - 1 < _configs.Count
                    ? LayerIndexOf(_configs[_layerChoice - 1]) : -1,
            };
            foreach (var row in _rows)
                if (row.selected)
                    request.targets.Add(row.name);
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
