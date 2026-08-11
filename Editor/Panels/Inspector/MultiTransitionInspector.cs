using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- multi-transition editing ----------------------------------------

    /// <summary>
    /// Bulk editor for the transitions selected in the transition list: common settings, the
    /// conditions they share and a row that adds one condition to all of them. Reads and writes
    /// the same selection list the single-transition editor works from.
    /// </summary>
    class MultiTransitionInspector
    {
        readonly DaerDContext _context;
        readonly GraphSync _sync;
        readonly List<AnimatorTransitionBase> _selectedTransitions;
        readonly TransitionClipboard.ConditionData _newCondition =
            new TransitionClipboard.ConditionData { mode = AnimatorConditionMode.If, parameter = string.Empty };

        public MultiTransitionInspector(DaerDContext context, GraphSync sync,
            List<AnimatorTransitionBase> selectedTransitions)
        {
            _context = context;
            _sync = sync;
            _selectedTransitions = selectedTransitions;
        }

        public void DrawMultiTransitionEditor(AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("{0} transitions selected", _selectedTransitions.Count), EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!TransitionClipboard.HasData))
            {
                if (GUILayout.Button(L.Tr("Paste Copied Transition Onto All {0} Selected", _selectedTransitions.Count)))
                    PasteOntoSelected();
            }

            DrawMultiSettings();
            PanelGui.HorizontalLine();
            DrawSharedConditions(controller);
            EditorGUILayout.Space(4);
            DrawAddConditionToAll(controller);
        }

        void DrawMultiSettings()
        {
            EditorGUILayout.LabelField(L.Tr("Common Settings (applied to all selected)"), EditorStyles.boldLabel);

            MultiEditGui.Bool(L.Tr("Mute"), _selectedTransitions, x => x.mute, (x, v) => x.mute = v, afterApply: RefreshEdges);
            MultiEditGui.Bool(L.Tr("Solo"), _selectedTransitions, x => x.solo, (x, v) => x.solo = v, afterApply: RefreshEdges);

            var stateTransitions = new List<AnimatorStateTransition>();
            foreach (var t in _selectedTransitions)
                if (t is AnimatorStateTransition st) stateTransitions.Add(st);
            if (stateTransitions.Count == 0) return;

            MultiEditGui.Bool(L.Tr("Has Exit Time"), stateTransitions, x => x.hasExitTime, (x, v) => x.hasExitTime = v);
            MultiEditGui.Float(L.Tr("Exit Time"), stateTransitions, x => x.exitTime, (x, v) => x.exitTime = v);
            MultiEditGui.Bool(L.Tr("Fixed Duration"), stateTransitions, x => x.hasFixedDuration, (x, v) => x.hasFixedDuration = v);
            MultiEditGui.Float(L.Tr("Duration"), stateTransitions, x => x.duration, (x, v) => x.duration = v);
            MultiEditGui.Float(L.Tr("Offset"), stateTransitions, x => x.offset, (x, v) => x.offset = v);
            MultiEditGui.Interruption(stateTransitions);
            MultiEditGui.Bool(L.Tr("Ordered Interruption"), stateTransitions, x => x.orderedInterruption, (x, v) => x.orderedInterruption = v);
            MultiEditGui.Bool(L.Tr("Can Transition To Self"), stateTransitions, x => x.canTransitionToSelf, (x, v) => x.canTransitionToSelf = v);
        }

        void DrawSharedConditions(AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("Shared Conditions"), EditorStyles.boldLabel);

            int total = _selectedTransitions.Count;
            var shared = ConditionGui.SharedConditions(_selectedTransitions);
            if (shared.Count == 0)
            {
                EditorGUILayout.LabelField(L.Tr("(the selected transitions have no conditions)"), EditorStyles.miniLabel);
                return;
            }

            var paramNames = PanelGui.AllParameterNames(controller);
            if (paramNames.Length == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("Add parameters before editing conditions."), MessageType.Info);
                return;
            }
            var typeByName = PanelGui.ParameterTypeMap(controller);

            foreach (var entry in shared)
            {
                var original = entry.data;
                var working = new TransitionClipboard.ConditionData
                {
                    mode = original.mode,
                    parameter = original.parameter,
                    threshold = original.threshold,
                };
                bool sharedByAll = entry.count == total;

                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                if (!sharedByAll) GUI.color = DaerDColors.Partial;   // amber marks partial coverage
                EditorGUILayout.LabelField(entry.count + "/" + total, EditorStyles.miniLabel, GUILayout.Width(32));
                GUI.color = prevColor;

                EditorGUI.BeginChangeCheck();
                int paramIndex = Mathf.Max(0, Array.IndexOf(paramNames, working.parameter));
                paramIndex = EditorGUILayout.Popup(paramIndex, paramNames);
                working.parameter = paramNames[paramIndex];
                var type = typeByName.TryGetValue(working.parameter, out var ty) ? ty : AnimatorControllerParameterType.Float;
                ConditionGui.DrawConditionValue(working, type, delayed: true);
                bool edited = EditorGUI.EndChangeCheck();

                bool remove = GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton));
                EditorGUILayout.EndHorizontal();

                if (remove)
                {
                    RemoveCommonCondition(original);
                    GUIUtility.ExitGUI();   // a row disappears, so restart layout
                }
                else if (edited)
                {
                    ReplaceCommonCondition(original, working);
                    // Restart layout: changing the parameter can change the value control's shape,
                    // and a recompute may merge/reorder rows. The threshold uses a delayed field, so
                    // this only fires on commit (Enter / focus-out) — typing stays smooth.
                    GUIUtility.ExitGUI();
                }
            }
        }

        void DrawAddConditionToAll(AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("Add The Same Condition To Every Selected Transition"), EditorStyles.boldLabel);

            var paramNames = PanelGui.AllParameterNames(controller);
            if (paramNames.Length == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("Add parameters before building conditions."), MessageType.Info);
                return;
            }
            var typeByName = PanelGui.ParameterTypeMap(controller);

            EditorGUILayout.BeginHorizontal();
            int paramIndex = Mathf.Max(0, Array.IndexOf(paramNames, _newCondition.parameter));
            paramIndex = EditorGUILayout.Popup(paramIndex, paramNames);
            _newCondition.parameter = paramNames[paramIndex];
            var type = typeByName.TryGetValue(_newCondition.parameter, out var ty) ? ty : AnimatorControllerParameterType.Float;
            ConditionGui.DrawConditionValue(_newCondition, type);
            if (GUILayout.Button(L.Tr("Add"), EditorStyles.miniButton, GUILayout.Width(DaerDLayout.RowAction)))
            {
                AddConditionToAll(_newCondition);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        void ReplaceCommonCondition(TransitionClipboard.ConditionData oldData, TransitionClipboard.ConditionData newData)
        {
            using (new UndoScope("Edit Common Condition"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var list = ConditionGui.ToDataList(transition);
                    bool changed = false;
                    foreach (var data in list)
                        if (ConditionGui.Same(data, oldData))
                        {
                            data.mode = newData.mode;
                            data.parameter = newData.parameter;
                            data.threshold = newData.threshold;
                            changed = true;
                        }
                    if (changed)
                    {
                        Undo.RegisterCompleteObjectUndo(transition, "Edit Common Condition");
                        TransitionClipboard.SetConditions(transition, list);
                        EditorUtility.SetDirty(transition);
                    }
                }
            }
        }

        void RemoveCommonCondition(TransitionClipboard.ConditionData data)
        {
            using (new UndoScope("Remove Common Condition"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var list = ConditionGui.ToDataList(transition);
                    if (list.RemoveAll(d => ConditionGui.Same(d, data)) > 0)
                    {
                        Undo.RegisterCompleteObjectUndo(transition, "Remove Common Condition");
                        TransitionClipboard.SetConditions(transition, list);
                        EditorUtility.SetDirty(transition);
                    }
                }
            }
        }

        void AddConditionToAll(TransitionClipboard.ConditionData data)
        {
            using (new UndoScope("Add Condition To All"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var list = ConditionGui.ToDataList(transition);
                    list.Add(new TransitionClipboard.ConditionData
                    {
                        mode = data.mode,
                        parameter = data.parameter,
                        threshold = data.threshold,
                    });
                    Undo.RegisterCompleteObjectUndo(transition, "Add Condition To All");
                    TransitionClipboard.SetConditions(transition, list);
                    EditorUtility.SetDirty(transition);
                }
            }
        }

        public void PasteOntoSelected()
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            using (new UndoScope("Paste Transition"))
            {
                foreach (var transition in _selectedTransitions)
                    TransitionClipboard.Apply(transition, snapshot);
            }
            RefreshEdges();
        }

        /// <summary>Adds a new transition (with the copied settings) alongside each selected one.</summary>
        public void PasteSelectedAsNew()
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            AnimatorTransitionBase last = null;
            using (new UndoScope("Paste Transition As New"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var edge = _sync.FindEdge(transition);
                    if (edge == null) continue;
                    var created = _sync.CreateTransition(
                        edge.output?.node as GraphNodeBase, edge.input?.node as GraphNodeBase);
                    if (created != null) { TransitionClipboard.Apply(created, snapshot); last = created; }
                }
            }
            _sync.Rebuild();
            if (last != null) _context.Select(last);
        }

        /// <summary>Repaints every edge: mute / solo and the condition text are drawn on the edge
        /// labels, and an edit here can touch transitions spread over several of them.</summary>
        public void RefreshEdges() =>
            _context.NotifyGraphVisualsChanged(DaerDContext.GraphVisuals.AllEdges);
    }
}
