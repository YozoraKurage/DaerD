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
    /// cycle order, mark a row Req to accept sync requests, and read the resulting refresh
    /// interval next to each row, with the whole cycle previewed underneath. The form itself
    /// lives in <see cref="AsyncSyncForm"/>
    /// (shared with the sync layer's dedicated panel); the wizard adds the layer choice —
    /// create a new layer, or regenerate a saved setup's layer in place.
    /// </summary>
    class AsyncSyncWindow : EditorWindow
    {
        AnimatorController _controller;
        Action<int> _onApplied;

        readonly AsyncSyncForm _form = new AsyncSyncForm();
        // Saved setups (persisted in GraphFrameData): picking one prefills the wizard and
        // regenerates that layer in place — same idea as the DBT gadget's layer choice.
        readonly List<GraphFrameData.AsyncSyncConfig> _configs =
            new List<GraphFrameData.AsyncSyncConfig>();
        /// <summary>0 = create a new layer; 1.. = _configs[index - 1].</summary>
        int _layerChoice;
        Vector2 _windowScroll;

        /// <summary>onApplied receives the index of the generated layer.</summary>
        public static void Open(AnimatorController controller, Action<int> onApplied)
        {
            var window = CreateInstance<AsyncSyncWindow>();
            window.titleContent = new GUIContent(L.Tr("Async Sync"));
            window.minSize = new Vector2(500, 560);
            window._controller = controller;
            window._onApplied = onApplied;
            window._configs.AddRange(GraphFrameData.GetAsyncSyncs(controller));
            window._form.SetController(controller);
            // A suggestion for a fresh setup; a saved one overwrites it right below.
            window._form.SetBaseName(AsyncSyncBuilder.DefaultBaseName(controller));
            if (window._configs.Count > 0)
            {
                window._layerChoice = 1;
                window._form.LoadConfig(window._configs[0]);
            }
            window.ShowUtility();
        }

        /// <summary>The AAP set the warnings read is cached across repaints, and this window
        /// has no controller-change events to drop it on. Regaining focus is the moment an
        /// edit made in another window can have landed, and it is free.</summary>
        void OnFocus() => _form.InvalidateAnimatedParameters();

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
            _form.DrawGeneratedSection();

            EditorGUILayout.Space(4);
            _form.DrawPickList();

            var request = _form.BuildRequest(_layerChoice > 0 && _layerChoice - 1 < _configs.Count
                ? AsyncSyncBuilder.LayerIndexOf(_controller, _configs[_layerChoice - 1]) : -1);
            EditorGUILayout.Space(4);
            _form.DrawOrderSection(request);

            _form.DrawPreview(request);
            _form.DrawBlockingProblem(request);
            foreach (var warning in AsyncSyncBuilder.Warnings(request, _form.AnimatedParameters()))
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            // A split builds every ring itself, so there is nothing left for this window to
            // create — it reports the layer it kept and gets out of the way.
            if (_form.DrawSplitProposal(request))
            {
                _onApplied?.Invoke(request.layerIndex >= 0
                    ? request.layerIndex : _controller.layers.Length - 1);
                Close();
                GUIUtility.ExitGUI();
            }
            _form.DrawStoreFix(request);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            if (GUILayout.Button(L.Tr("Create"), GUILayout.Width(DaerDLayout.DialogButton)))
                TryApply(request);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
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
                    _form.LoadConfig(_configs[picked - 1]);
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
