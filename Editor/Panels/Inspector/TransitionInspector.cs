using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- transition ------------------------------------------------------

    /// <summary>
    /// The transition list of the selected edge(s) plus the editor for whatever is selected in
    /// it: this class draws the single-transition form and hands two or more selected rows to
    /// the multi-transition editor.
    /// </summary>
    class TransitionInspector
    {
        readonly DaerDContext _context;
        readonly GraphSync _sync;
        readonly List<AnimatorTransitionBase> _selectedTransitions;
        readonly MultiTransitionInspector _multiTransition;

        int _rangeAnchor = -1;

        public TransitionInspector(DaerDContext context, GraphSync sync,
            List<AnimatorTransitionBase> selectedTransitions, MultiTransitionInspector multiTransition)
        {
            _context = context;
            _sync = sync;
            _selectedTransitions = selectedTransitions;
            _multiTransition = multiTransition;
        }

        /// <summary>Forgets where a Shift-range selection would start from.</summary>
        public void ResetAnchor() => _rangeAnchor = -1;

        public void DrawTransitionContext()
        {
            var controller = _context.Controller;
            var pool = GatherTransitionPool();
            if (pool.Count == 0)
            {
                if (_context.Selection is TransitionEdge edge && edge.IsDefaultEdge)
                    EditorGUILayout.HelpBox(L.Tr("Default-state link. Set a different default state from a state's context menu."),
                        MessageType.Info);
                else
                    EditorGUILayout.LabelField(L.Tr("No transitions to edit."));
                return;
            }

            PruneSelection(pool);
            HandleCopyPasteShortcuts();
            DrawTransitionList(pool);
            PanelGui.HorizontalLine();

            if (_selectedTransitions.Count >= 2)
                _multiTransition.DrawMultiTransitionEditor(controller);
            else
                DrawSingleTransition(_selectedTransitions[0], controller);
        }

        /// <summary>All transitions of every currently selected (non-default) edge. The graph
        /// answers which edges those are — including the fallback for a selection the graph is not
        /// highlighting — and hands over the transitions, never the edges themselves.</summary>
        List<AnimatorTransitionBase> GatherTransitionPool()
        {
            var pool = new List<AnimatorTransitionBase>();
            foreach (var group in _context.GetSelectedTransitionGroups())
            {
                if (group.isDefault) continue;
                foreach (var t in group.transitions)
                    if (t != null && !pool.Contains(t)) pool.Add(t);
            }
            return pool;
        }

        void PruneSelection(List<AnimatorTransitionBase> pool)
        {
            _selectedTransitions.RemoveAll(t => t == null || !pool.Contains(t));
            if (_selectedTransitions.Count == 0)
                _selectedTransitions.Add(pool[0]);
        }

        /// <summary>Unity-style vertical transition list with Solo / Mute columns and multi-select.</summary>
        void DrawTransitionList(List<AnimatorTransitionBase> pool)
        {
            EditorGUILayout.LabelField(L.Tr("Transitions") + " (" + pool.Count + ")", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Solo"), EditorStyles.miniLabel, GUILayout.Width(34));
            EditorGUILayout.LabelField(L.Tr("Mute"), EditorStyles.miniLabel, GUILayout.Width(36));
            EditorGUILayout.LabelField(L.Tr("Transition"), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < pool.Count; i++)
            {
                var t = pool[i];
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginChangeCheck();
                bool solo = EditorGUILayout.Toggle(t.solo, GUILayout.Width(34));
                bool mute = EditorGUILayout.Toggle(t.mute, GUILayout.Width(36));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RegisterCompleteObjectUndo(t, "Edit Transition");
                    t.solo = solo;
                    t.mute = mute;
                    EditorUtility.SetDirty(t);
                    _multiTransition.RefreshEdges();
                }

                bool selected = _selectedTransitions.Contains(t);
                var prevBackground = GUI.backgroundColor;
                if (selected) GUI.backgroundColor = PanelGui.SelectionTint;
                if (GUILayout.Button((i + 1) + ".  " + ParameterConverter.DescribeTransition(t), EditorStyles.miniButton))
                    HandleRowClick(pool, i);
                GUI.backgroundColor = prevBackground;

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(24)))
                {
                    DeleteTransitionRow(t, pool);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.Tr("+ Add Transition")))
            {
                AddTransitionToAnchorEdge(pool);
                GUIUtility.ExitGUI();
            }
            if (pool.Count > 1 && GUILayout.Button(L.Tr("Select All"), GUILayout.Width(80)))
            {
                _selectedTransitions.Clear();
                _selectedTransitions.AddRange(pool);
            }
            EditorGUILayout.EndHorizontal();
        }

        void HandleRowClick(List<AnimatorTransitionBase> pool, int index)
        {
            var t = pool[index];
            var e = Event.current;
            bool additive = e != null && (e.control || e.command);
            bool range = e != null && e.shift;

            var previous = new List<AnimatorTransitionBase>(_selectedTransitions);

            if (range && _rangeAnchor >= 0 && _rangeAnchor < pool.Count)
            {
                _selectedTransitions.Clear();
                int lo = Mathf.Min(_rangeAnchor, index);
                int hi = Mathf.Max(_rangeAnchor, index);
                for (int i = lo; i <= hi; i++)
                    _selectedTransitions.Add(pool[i]);
            }
            else if (additive)
            {
                if (_selectedTransitions.Contains(t)) _selectedTransitions.Remove(t);
                else _selectedTransitions.Add(t);
                _rangeAnchor = index;
            }
            else
            {
                _selectedTransitions.Clear();
                _selectedTransitions.Add(t);
                _rangeAnchor = index;
            }

            // If the selection actually changed, force the in-flight condition input (FloatField,
            // delayed text field, popup) to end before its value gets attributed to the newly
            // selected transition. Without this, a value typed for transition X leaks into Y.
            if (!SameSet(previous, _selectedTransitions))
                EndConditionInput();
        }

        static bool SameSet(List<AnimatorTransitionBase> a, List<AnimatorTransitionBase> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>
        /// Drops keyboard focus and resets the editor's internal hot/keyboard control so any
        /// FloatField / DelayedFloatField currently being typed in stops being the active
        /// control — the next layout pass redraws it fresh for the new selected transition.
        /// </summary>
        public static void EndConditionInput()
        {
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
        }

        void DeleteTransitionRow(AnimatorTransitionBase transition, List<AnimatorTransitionBase> pool)
        {
            // Deleting needs the edge object itself (it is where the transition's source node
            // lives), so this one stays a GraphSync command rather than a context notification.
            var edge = _sync.FindEdge(transition);
            if (edge == null) return;

            AnimatorTransitionBase remaining = null;
            foreach (var t in pool)
                if (!ReferenceEquals(t, transition)) { remaining = t; break; }

            _selectedTransitions.Remove(transition);
            _sync.DeleteTransition(edge, transition);
            _context.Select(remaining);
        }

        void AddTransitionToAnchorEdge(List<AnimatorTransitionBase> pool)
        {
            var anchor = _selectedTransitions.Count > 0 ? _selectedTransitions[0] : pool[0];
            // The new transition runs between the anchor edge's two endpoint nodes, so this needs
            // the edge object; like the delete row, it stays a GraphSync command.
            var edge = _sync.FindEdge(anchor);
            if (edge == null) return;
            var created = _sync.CreateTransition(
                edge.output?.node as GraphNodeBase, edge.input?.node as GraphNodeBase);
            _sync.Rebuild();
            if (created != null) _context.Select(created);
        }

        /// <summary>
        /// Ctrl+C copies the selected transition(s); Ctrl+V pastes the copy onto every selected one;
        /// Ctrl+Shift+V pastes it as a new transition alongside each selected one.
        /// </summary>
        void HandleCopyPasteShortcuts()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || !(e.control || e.command))
                return;

            if (e.keyCode == KeyCode.C && _selectedTransitions.Count >= 1)
            {
                TransitionClipboard.Copy(_selectedTransitions);
                e.Use();
            }
            else if (e.keyCode == KeyCode.V && TransitionClipboard.HasData && _selectedTransitions.Count >= 1)
            {
                if (e.shift) _multiTransition.PasteSelectedAsNew();
                else _multiTransition.PasteOntoSelected();
                e.Use();
                GUIUtility.ExitGUI();
            }
        }

        // ---- single transition ----------------------------------------------

        void DrawSingleTransition(AnimatorTransitionBase transition, AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("Transition") + "  " + ParameterConverter.DescribeTransition(transition),
                EditorStyles.boldLabel);
            DrawTransitionSettings(transition);

            PanelGui.HorizontalLine();
            DrawConditions(transition, controller);
        }

        void DrawTransitionSettings(AnimatorTransitionBase transition)
        {
            var stateTransition = transition as AnimatorStateTransition;
            if (stateTransition == null)
            {
                EditorGUILayout.LabelField(L.Tr("(Entry / state-machine transition — no timing settings.)"),
                    EditorStyles.miniLabel);
                return;
            }

            EditorGUI.BeginChangeCheck();
            bool hasExitTime = EditorGUILayout.Toggle(L.Tr("Has Exit Time"), stateTransition.hasExitTime);
            float exitTime;
            using (new EditorGUI.DisabledScope(!hasExitTime))
                exitTime = EditorGUILayout.FloatField(L.Tr("Exit Time"), stateTransition.exitTime);
            bool fixedDuration = EditorGUILayout.Toggle(L.Tr("Fixed Duration"), stateTransition.hasFixedDuration);
            float duration = EditorGUILayout.FloatField(L.Tr("Duration"), stateTransition.duration);
            float offset = EditorGUILayout.FloatField(L.Tr("Offset"), stateTransition.offset);
            var interruption = (TransitionInterruptionSource)EditorGUILayout.EnumPopup(L.Tr("Interruption"), stateTransition.interruptionSource);
            bool ordered = EditorGUILayout.Toggle(L.Tr("Ordered Interruption"), stateTransition.orderedInterruption);
            bool toSelf = EditorGUILayout.Toggle(L.Tr("Can Transition To Self"), stateTransition.canTransitionToSelf);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(stateTransition, "Edit Transition");
                stateTransition.hasExitTime = hasExitTime;
                stateTransition.exitTime = exitTime;
                stateTransition.hasFixedDuration = fixedDuration;
                stateTransition.duration = duration;
                stateTransition.offset = offset;
                stateTransition.interruptionSource = interruption;
                stateTransition.orderedInterruption = ordered;
                stateTransition.canTransitionToSelf = toSelf;
                EditorUtility.SetDirty(stateTransition);
            }
        }

        void DrawConditions(AnimatorTransitionBase transition, AnimatorController controller)
        {
            EditorGUILayout.LabelField(L.Tr("Conditions"), EditorStyles.boldLabel);

            var paramNames = PanelGui.AllParameterNames(controller);
            if (paramNames.Length == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("Add parameters before building conditions."), MessageType.Info);
                return;
            }
            var typeByName = PanelGui.ParameterTypeMap(controller);

            var working = ConditionGui.ToDataList(transition);
            bool changed = false;
            int removeIndex = -1;
            EditorGUI.BeginChangeCheck();

            for (int i = 0; i < working.Count; i++)
            {
                var condition = working[i];
                EditorGUILayout.BeginHorizontal();

                int paramIndex = Mathf.Max(0, Array.IndexOf(paramNames, condition.parameter));
                paramIndex = EditorGUILayout.Popup(paramIndex, paramNames);
                condition.parameter = paramNames[paramIndex];

                var type = typeByName.TryGetValue(condition.parameter, out var t) ? t : AnimatorControllerParameterType.Float;
                ConditionGui.DrawConditionValue(condition, type);

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22)))
                    removeIndex = i;

                EditorGUILayout.EndHorizontal();
            }

            if (EditorGUI.EndChangeCheck())
                changed = true;
            if (removeIndex >= 0)
            {
                working.RemoveAt(removeIndex);
                changed = true;
            }
            if (GUILayout.Button(L.Tr("+ Add Condition")))
            {
                var type = typeByName.TryGetValue(paramNames[0], out var t) ? t : AnimatorControllerParameterType.Float;
                working.Add(new TransitionClipboard.ConditionData { parameter = paramNames[0], mode = PanelGui.ModesFor(type)[0] });
                changed = true;
            }

            if (changed)
            {
                Undo.RegisterCompleteObjectUndo(transition, "Edit Conditions");
                TransitionClipboard.SetConditions(transition, working);
                EditorUtility.SetDirty(transition);
            }
        }
    }
}
