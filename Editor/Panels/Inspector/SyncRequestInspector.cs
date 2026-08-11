using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- sync requests ---------------------------------------------------

    /// <summary>The per-state Sync Request component, drawn under the state form.</summary>
    class SyncRequestInspector
    {
        readonly DaerDContext _context;

        public SyncRequestInspector(DaerDContext context)
        {
            _context = context;
        }

        /// <summary>Setups the user opened with "+ Add Sync Request" but hasn't ticked a
        /// target in yet — nothing is stored until the first tick, so the open box only
        /// exists here. Keyed by state instance ID; drafts of other states are dropped on
        /// draw, so a selection change closes them.</summary>
        readonly List<(int stateId, string baseName)> _syncRequestDrafts =
            new List<(int stateId, string baseName)>();

        /// <summary>
        /// The per-state Sync Request "component": while the avatar sits in this state, the
        /// ticked parameters are requested from an async sync setup out of turn (see
        /// <see cref="SyncRequestBuilder"/>). Backed by a DaerD-managed Parameter Driver —
        /// visible under Behaviours below, rewritten wholesale on every edit here — plus a
        /// record in GraphFrameData.
        /// </summary>
        public void DrawSyncRequests(AnimatorState state)
        {
            var controller = _context.Controller;
            var configs = GraphFrameData.GetAsyncSyncs(controller);
            var entries = GraphFrameData.GetSyncRequests(controller, state);
            _syncRequestDrafts.RemoveAll(draft => draft.stateId != state.GetInstanceID());
            // No setup and nothing stored: most states in most controllers — draw nothing.
            if (configs.Count == 0 && entries.Count == 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L.Tr("Sync Request"), EditorStyles.boldLabel);

            if (!VrcParameterDriver.SdkAvailable)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("VRChat SDK not found — the Parameter Driver behaviour is required."),
                    MessageType.Warning);
                return;
            }

            var open = new List<GraphFrameData.AsyncSyncConfig>();
            var addable = new List<GraphFrameData.AsyncSyncConfig>();
            foreach (var config in configs)
            {
                bool shown = _syncRequestDrafts.Contains((state.GetInstanceID(), config.baseName));
                foreach (var entry in entries)
                    if (entry.baseName == config.baseName)
                        shown = true;
                (shown ? open : addable).Add(config);
            }

            foreach (var config in open)
                DrawSyncRequestBox(state, config, FindSyncRequest(entries, config.baseName));

            // Records whose setup is gone (renamed base, deleted layer): the driver on the
            // state still fires into nothing — surface it instead of silently keeping it.
            foreach (var entry in entries)
            {
                if (FindConfig(configs, entry.baseName) != null) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.HelpBox(
                    L.Tr("This state requests from async sync '{0}', which no longer exists.",
                        entry.baseName),
                    MessageType.Warning);
                if (GUILayout.Button(L.Tr("Remove"), EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    SyncRequestBuilder.Remove(controller, state, entry.baseName);
                    _context.NotifyGraphVisualsChanged(state);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndVertical();
            }

            if (addable.Count > 0
                && GUILayout.Button(L.Tr("+ Add Sync Request"), EditorStyles.miniButton))
            {
                if (addable.Count == 1)
                {
                    _syncRequestDrafts.Add((state.GetInstanceID(), addable[0].baseName));
                }
                else
                {
                    var menu = new GenericMenu();
                    int stateId = state.GetInstanceID();
                    foreach (var config in addable)
                    {
                        string baseName = config.baseName;
                        menu.AddItem(new GUIContent(baseName), false,
                            () => _syncRequestDrafts.Add((stateId, baseName)));
                    }
                    menu.ShowAsContext();
                }
            }
        }

        static GraphFrameData.SyncRequest FindSyncRequest(
            List<GraphFrameData.SyncRequest> entries, string baseName)
        {
            foreach (var entry in entries)
                if (entry.baseName == baseName)
                    return entry;
            return null;
        }

        static GraphFrameData.AsyncSyncConfig FindConfig(
            List<GraphFrameData.AsyncSyncConfig> configs, string baseName)
        {
            foreach (var config in configs)
                if (config.baseName == baseName)
                    return config;
            return null;
        }

        /// <summary>One setup's box: target ticks applied immediately — the first tick
        /// creates the driver and the record, unticking the last one removes both.</summary>
        void DrawSyncRequestBox(AnimatorState state, GraphFrameData.AsyncSyncConfig config,
            GraphFrameData.SyncRequest entry)
        {
            var controller = _context.Controller;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Async Sync '{0}'", config.baseName),
                EditorStyles.miniBoldLabel);
            if (GUILayout.Button(L.Tr("Remove"), EditorStyles.miniButton, GUILayout.Width(70)))
            {
                _syncRequestDrafts.Remove((state.GetInstanceID(), config.baseName));
                SyncRequestBuilder.Remove(controller, state, config.baseName);
                _context.NotifyGraphVisualsChanged(state);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                L.Tr("Ticked parameters are synced out of turn while this state plays."),
                EditorStyles.miniLabel);

            var selected = new List<string>();
            if (entry != null) selected.AddRange(entry.targets);

            EditorGUI.BeginChangeCheck();
            foreach (var target in config.targets)
            {
                bool was = selected.Contains(target);
                bool now = EditorGUILayout.ToggleLeft(target, was);
                if (now && !was) selected.Add(target);
                else if (!now && was) selected.Remove(target);
            }
            if (EditorGUI.EndChangeCheck())
            {
                if (selected.Count == 0)
                {
                    // Keep the box open as a draft so the user can tick something else.
                    if (!_syncRequestDrafts.Contains((state.GetInstanceID(), config.baseName)))
                        _syncRequestDrafts.Add((state.GetInstanceID(), config.baseName));
                    if (entry != null)
                        SyncRequestBuilder.Remove(controller, state, config.baseName);
                }
                else if (!SyncRequestBuilder.Apply(controller, config, state, selected))
                {
                    // Enabling a request rebuilds the sync layer, so anything that now stops
                    // that setup from being applied stops this too. Saying nothing would look
                    // like the tick simply didn't take.
                    EditorUtility.DisplayDialog(L.Tr("Sync Request"),
                        L.Tr("'{0}' could not be rebuilt, so the request was not added. Open its sync layer and apply it there to see what is wrong.",
                            config.baseName), "OK");
                }
                _context.NotifyGraphVisualsChanged(state);
            }

            // A recipe regenerates its layers by destroy-and-recreate, on both sides of this
            // feature: a request on a recipe-built state dies with the state, and a
            // recipe-built sync layer is rebuilt from the recipe's own Requestable list.
            var codeOwned = GraphFrameData.GetCodeOwned(controller);
            var currentRoot = _context.CurrentLayer?.stateMachine;
            if (currentRoot != null && codeOwned.ContainsKey(currentRoot))
                EditorGUILayout.HelpBox(
                    L.Tr("This layer is generated by a recipe — the next Generate rebuilds its states and this request is lost. Add the request in the recipe instead."),
                    MessageType.Warning);
            else if (config.layer != null && codeOwned.ContainsKey(config.layer))
                EditorGUILayout.HelpBox(
                    L.Tr("Async sync '{0}' is generated by a recipe — mark the targets with .Requestable(...) there, or the next Generate drops the request routes.",
                        config.baseName),
                    MessageType.Info);

            EditorGUILayout.EndVertical();
        }
    }
}
