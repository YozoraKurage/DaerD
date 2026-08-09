using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The clip index itself, shared between the standalone window
    /// (<see cref="ClipsWindow"/>) and the home screen's inline card: every AnimationClip the
    /// controller references, with the states that use it (Jump), a bulk replace slot per clip
    /// and a name filter. Same split as <see cref="AsyncSyncForm"/> — the host owns the window
    /// chrome and where a Jump lands, the form owns the list and everything typed into it.
    /// </summary>
    class ClipsForm
    {
        AnimatorController _controller;
        List<ControllerCleanup.ClipEntry> _entries;
        readonly HashSet<AnimationClip> _expanded = new HashSet<AnimationClip>();
        string _filter = string.Empty;
        // Jump / Replace are recorded during the row draw and executed after the layout pass
        // ends — both change focus or rebuild the list under the current IMGUI layout otherwise.
        ControllerCleanup.ClipUsage _pendingJump;
        AnimationClip _pendingReplaceFrom;
        AnimationClip _pendingReplaceTo;

        /// <summary>Where a Jump goes. The window opens the main window on the usage; the home
        /// screen is already inside one and navigates its own context instead.</summary>
        public Action<ControllerCleanup.ClipUsage> JumpRequested;

        /// <summary>Raised after a bulk replace rewrote the controller, for the host to tell
        /// whoever is showing that controller (node labels carry motion names).</summary>
        public Action ControllerModified;

        public AnimatorController Controller => _controller;

        public void SetController(AnimatorController controller)
        {
            _controller = controller;
            RefreshEntries();
        }

        public void RefreshEntries()
        {
            _entries = _controller != null ? ControllerCleanup.CollectClipUsages(_controller) : null;
        }

        /// <summary>
        /// The controller slot, Refresh and Remap, plus the name filter. Separate from
        /// <see cref="DrawList"/> because a host with its own scroll view keeps this above it.
        ///
        /// <paramref name="withControllerSlot"/> is off for a host that already has a controller
        /// of its own: the home screen follows the one on screen and rebinds the form to it every
        /// repaint, so a slot there would snap back the moment it was changed.
        /// </summary>
        public void DrawHeader(bool withControllerSlot)
        {
            EditorGUILayout.BeginHorizontal();
            if (withControllerSlot)
            {
                var picked = (AnimatorController)EditorGUILayout.ObjectField(
                    _controller, typeof(AnimatorController), false);
                if (picked != _controller)
                    SetController(picked);
            }
            using (new EditorGUI.DisabledScope(_controller == null))
            {
                if (GUILayout.Button(new GUIContent(L.Tr("Refresh"),
                        L.Tr("List every AnimationClip this controller references and the states that use it.")),
                        GUILayout.Width(80)))
                    RefreshEntries();
                if (GUILayout.Button(new GUIContent(L.Tr("Remap"),
                        L.Tr("Fix clip bindings whose hierarchy paths broke (renames / moves).")),
                        GUILayout.Width(80)))
                {
                    ClipRemapperWindow.Open(_controller);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
            _filter = EditorGUILayout.TextField(_filter, EditorStyles.toolbarSearchField);
        }

        /// <summary>The list, and the deferred actions its rows queued.</summary>
        public void DrawList()
        {
            if (_controller == null)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("Assign an Animator Controller to list its animation clips."), MessageType.Info);
                return;
            }
            if (_entries == null) return;

            if (_entries.Count == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("No clips are referenced by this controller."), MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(L.Tr("{0} clip(s) referenced.", _entries.Count), EditorStyles.miniLabel);

            bool anyShown = false;
            foreach (var entry in _entries)
            {
                if (entry.clip == null) continue;   // deleted since the list was built
                if (!MatchesFilter(entry.clip.name)) continue;
                anyShown = true;
                DrawClipEntry(entry);
            }
            if (!anyShown)
                EditorGUILayout.HelpBox(L.Tr("No matches."), MessageType.None);

            RunPendingActions();
        }

        bool MatchesFilter(string clipName) =>
            string.IsNullOrEmpty(_filter)
            || clipName.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;

        void DrawClipEntry(ControllerCleanup.ClipEntry entry)
        {
            EditorGUILayout.BeginHorizontal();
            bool expanded = _expanded.Contains(entry.clip);
            string title = entry.clip.name + " (" + entry.usages.Count + ")"
                + (entry.embedded ? " " + L.Tr("(embedded)") : string.Empty);
            bool now = EditorGUILayout.Foldout(expanded, title, true);
            if (now != expanded)
            {
                if (now) _expanded.Add(entry.clip);
                else _expanded.Remove(entry.clip);
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Ping"), L.Tr("Highlight this object in the Project / graph")),
                    EditorStyles.miniButton, GUILayout.Width(46)))
                EditorGUIUtility.PingObject(entry.clip);
            EditorGUILayout.EndHorizontal();

            if (!now) return;
            EditorGUI.indentLevel++;

            // Dropping / picking a clip here retargets every use of this clip at once.
            // The field always displays None — it is an action slot, not a stored value.
            var replacement = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent(L.Tr("Replace With"),
                    L.Tr("Swap every use of this clip in this controller for the picked clip (undoable)")),
                null, typeof(AnimationClip), false);
            if (replacement != null && replacement != entry.clip)
            {
                _pendingReplaceFrom = entry.clip;
                _pendingReplaceTo = replacement;
            }

            foreach (var usage in entry.usages)
            {
                if (usage.state == null) continue;   // stale after a structure edit
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(new GUIContent(usage.label, usage.label), EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent(L.Tr("Jump"),
                        L.Tr("Open the layer and select the state that uses this clip")),
                        EditorStyles.miniButton, GUILayout.Width(46)))
                    _pendingJump = usage;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }

        void RunPendingActions()
        {
            if (_pendingJump != null)
            {
                var usage = _pendingJump;
                _pendingJump = null;
                JumpRequested?.Invoke(usage);
            }
            if (_pendingReplaceFrom != null)
            {
                var from = _pendingReplaceFrom;
                var to = _pendingReplaceTo;
                _pendingReplaceFrom = null;
                _pendingReplaceTo = null;
                ReplaceClip(from, to);
            }
        }

        void ReplaceClip(AnimationClip from, AnimationClip to)
        {
            int replaced = ControllerCleanup.ReplaceClip(_controller, from, to);
            if (replaced > 0)
            {
                // Keep this entry open under its new clip so the result is visible.
                _expanded.Remove(from);
                _expanded.Add(to);
                ControllerModified?.Invoke();
            }
            RefreshEntries();
        }
    }
}
