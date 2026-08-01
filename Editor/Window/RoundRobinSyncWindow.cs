using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="RoundRobinSyncBuilder"/>: tick the parameters to multiplex,
    /// pick the index encoding and step interval, and generate the send-cycle / decoder
    /// layer. Shows the synced-bit cost against syncing each parameter directly.
    /// </summary>
    class RoundRobinSyncWindow : EditorWindow
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
        string _baseName = "RRSync";
        RoundRobinSyncBuilder.IndexEncoding _encoding = RoundRobinSyncBuilder.IndexEncoding.Int;
        float _stepSeconds = 0.3f;
        bool _addToStore = true;
        Vector2 _scroll;

        static readonly string[] EncodingLabels = { "Int (8 bit)", "Bool × n (1 bit each)" };

        /// <summary>onApplied receives the index of the generated layer.</summary>
        public static void Open(AnimatorController controller, Action<int> onApplied)
        {
            var window = CreateInstance<RoundRobinSyncWindow>();
            window.titleContent = new GUIContent(L.Tr("Round-Robin Sync"));
            window.minSize = new Vector2(460, 420);
            window._controller = controller;
            window._onApplied = onApplied;
            window.BuildRows();
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

            EditorGUILayout.LabelField(L.Tr("Round-Robin Sync"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Time-multiplexes the ticked parameters over a few synced parameters (an index plus one value channel per type): a local cycle copies each parameter into its channel in turn, and remote clients decode it back. The targets themselves stay unsynced — values update round-robin, one slot per step."),
                MessageType.Info);

            _baseName = EditorGUILayout.TextField(L.Tr("Base Name"), _baseName);
            _encoding = (RoundRobinSyncBuilder.IndexEncoding)EditorGUILayout.Popup(
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
                int direct = RoundRobinSyncBuilder.DirectBits(request);
                int compressed = RoundRobinSyncBuilder.CompressedBits(request);
                EditorGUILayout.LabelField(
                    L.Tr("Synced cost: {0} bit (direct sync would be {1} bit)", compressed, direct),
                    EditorStyles.miniLabel);
            }
            foreach (var warning in RoundRobinSyncBuilder.Warnings(request))
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

        RoundRobinSyncBuilder.Request BuildRequest(ParameterStore store)
        {
            var request = new RoundRobinSyncBuilder.Request
            {
                controller = _controller,
                baseName = _baseName != null ? _baseName.Trim() : string.Empty,
                encoding = _encoding,
                stepSeconds = _stepSeconds,
                store = store,
                addToStore = _addToStore,
            };
            foreach (var row in _rows)
                if (row.selected)
                    request.targets.Add(row.name);
            return request;
        }

        void TryApply(RoundRobinSyncBuilder.Request request)
        {
            var error = RoundRobinSyncBuilder.Validate(request);
            if (error != null)
            {
                EditorUtility.DisplayDialog(L.Tr("Round-Robin Sync"), error, "OK");
                return;
            }
            RoundRobinSyncBuilder.Apply(request);
            _onApplied?.Invoke(_controller.layers.Length - 1);
            Close();
        }
    }
}
