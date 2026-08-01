using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="NetworkSyncBuilder"/>: pick the layer, the synced parameter and
    /// the wiring style, and generate the local-driver + remote-mirror structure that makes a
    /// local-only layer visible to other VRChat players. The sync parameter name follows the
    /// picked layer until the user edits it by hand.
    /// </summary>
    class NetworkSyncWindow : EditorWindow
    {
        AnimatorController _controller;
        Action<int> _onApplied;

        int _layerIndex;
        string _syncParameter = string.Empty;
        NetworkSyncBuilder.Encoding _encoding = NetworkSyncBuilder.Encoding.Int;
        NetworkSyncBuilder.RemoteWiring _wiring = NetworkSyncBuilder.RemoteWiring.AnyState;
        bool _preserveTransitionProperties;
        string _remotePrefix = "[Net] ";
        bool _stripBehaviours = true;
        bool _pack = true;
        bool _ownDriver = true;

        static readonly string[] EncodingLabels = { "Int (1 parameter, 8 bit)", "Bool × n (1 bit each)" };
        static readonly string[] WiringLabels = { "Any State", "All-to-All" };

        /// <summary>onApplied receives the index of the synced layer.</summary>
        public static void Open(AnimatorController controller, int layerIndex, Action<int> onApplied)
        {
            var window = CreateInstance<NetworkSyncWindow>();
            window.titleContent = new GUIContent(L.Tr("Network Sync"));
            window.minSize = new Vector2(460, 400);
            window._controller = controller;
            window._layerIndex = Mathf.Clamp(layerIndex, 0, Mathf.Max(0, controller.layers.Length - 1));
            window._onApplied = onApplied;
            window._syncParameter = window.DefaultParameterName();
            window.ShowUtility();
        }

        string DefaultParameterName()
        {
            var layers = _controller != null ? _controller.layers : null;
            return layers != null && _layerIndex >= 0 && _layerIndex < layers.Length
                ? layers[_layerIndex].name + "/Sync"
                : "Sync";
        }

        int StateCount()
        {
            var layers = _controller.layers;
            if (_layerIndex < 0 || _layerIndex >= layers.Length) return 0;
            var stateMachine = layers[_layerIndex].stateMachine;
            if (stateMachine == null) return 0;
            int count = 0;
            foreach (var child in stateMachine.states)
                if (child.state != null) count++;
            return count;
        }

        void OnGUI()
        {
            // Utility windows outlive domain reloads but their fields don't; bail out.
            if (_controller == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Network Sync (Beta)"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Beta: the generated structure may still change between versions — review the result before shipping. For syncing parameter VALUES (rather than mirroring a whole layer), consider Round-Robin Sync instead."),
                MessageType.Warning);
            EditorGUILayout.HelpBox(
                L.Tr("Makes a layer driven by local-only parameters visible to remote players: each state writes its index into a synced parameter via a Parameter Driver, and generated remote states mirror the layer for everyone else. IsLocal separates the two halves."),
                MessageType.Info);

            DrawLayerChoice();

            int count = StateCount();
            _encoding = (NetworkSyncBuilder.Encoding)EditorGUILayout.Popup(
                L.Tr("Encoding"), (int)_encoding, EncodingLabels);
            if (_encoding == NetworkSyncBuilder.Encoding.Bool && count > 0)
            {
                int bits = NetworkSyncBuilder.BitsRequired(count);
                EditorGUILayout.LabelField(" ",
                    L.Tr("{0} bit(s): {1} … {2}", bits,
                        NetworkSyncBuilder.BitParameterName(_syncParameter, 0),
                        NetworkSyncBuilder.BitParameterName(_syncParameter, bits - 1)),
                    EditorStyles.miniLabel);
            }
            _syncParameter = EditorGUILayout.TextField(L.Tr("Sync Parameter"), _syncParameter);
            _wiring = (NetworkSyncBuilder.RemoteWiring)EditorGUILayout.Popup(
                new GUIContent(L.Tr("Remote Wiring"),
                    L.Tr("Any State: N transitions from the Any State node. All-to-All: N×(N-1) transitions between the mirror states.")),
                (int)_wiring, WiringLabels);
            _preserveTransitionProperties = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Preserve Transition Timing"),
                    L.Tr("Copy blend duration and interruption settings from each state's first outgoing transition (exit time stays off). Off generates instant transitions.")),
                _preserveTransitionProperties);
            _remotePrefix = EditorGUILayout.TextField(L.Tr("Remote State Prefix"), _remotePrefix);
            _stripBehaviours = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Strip Behaviours On Mirrors"),
                    L.Tr("Remote copies drop their StateMachineBehaviours so drivers and audio don't fire twice.")),
                _stripBehaviours);
            _pack = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Pack Into Sub-State Machine"),
                    L.Tr("Group the generated remote states into a 'Network' sub-state machine to keep the layer readable.")),
                _pack);
            _ownDriver = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Own Driver Instance"),
                    L.Tr("Write the sync values through a dedicated Parameter Driver named 'Network' instead of appending rows to a driver already on the state.")),
                _ownDriver);

            foreach (var warning in NetworkSyncBuilder.Warnings(BuildRequest()))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(100)))
                Close();
            if (GUILayout.Button(L.Tr("Create"), GUILayout.Width(100)))
                TryApply();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The sync parameter name follows the picked layer until edited by hand.</summary>
        void DrawLayerChoice()
        {
            var layers = _controller.layers;
            var labels = new string[layers.Length];
            for (int i = 0; i < layers.Length; i++)
                labels[i] = layers[i].name;
            int picked = EditorGUILayout.Popup(L.Tr("Target Layer"),
                Mathf.Clamp(_layerIndex, 0, labels.Length - 1), labels);
            if (picked != _layerIndex)
            {
                bool followed = _syncParameter == DefaultParameterName();
                _layerIndex = picked;
                if (followed)
                    _syncParameter = DefaultParameterName();
            }
        }

        NetworkSyncBuilder.Request BuildRequest() => new NetworkSyncBuilder.Request
        {
            controller = _controller,
            layerIndex = _layerIndex,
            syncParameter = _syncParameter != null ? _syncParameter.Trim() : string.Empty,
            encoding = _encoding,
            wiring = _wiring,
            preserveTransitionProperties = _preserveTransitionProperties,
            remotePrefix = _remotePrefix ?? string.Empty,
            stripBehaviours = _stripBehaviours,
            packIntoSubMachine = _pack,
            ownDriverInstance = _ownDriver,
        };

        void TryApply()
        {
            var request = BuildRequest();
            var error = NetworkSyncBuilder.Validate(request);
            if (error != null)
            {
                EditorUtility.DisplayDialog(L.Tr("Network Sync"), error, "OK");
                return;
            }
            NetworkSyncBuilder.Apply(request);
            _onApplied?.Invoke(request.layerIndex);
            Close();
        }
    }
}
