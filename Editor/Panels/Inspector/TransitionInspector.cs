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
        readonly TransitionListGui _list = new TransitionListGui();

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
            var groups = _context.GetSelectedTransitionGroups();
            var pool = GatherTransitionPool(groups);
            if (pool.Count == 0)
            {
                if (_context.Selection is TransitionEdge edge && edge.IsDefaultEdge)
                    EditorGUILayout.HelpBox(L.Tr("Default-state link. Set a different default state from a state's context menu."),
                        MessageType.Info);
                else
                    EditorGUILayout.LabelField(L.Tr("No transitions to edit."));
                return;
            }

            var rows = BuildRows(groups, pool, out var reorderSource);
            PruneSelection(rows, pool);
            HandleCopyPasteShortcuts();
            DrawTransitionList(rows, pool, reorderSource);
            PanelGui.HorizontalLine();

            if (_selectedTransitions.Count >= 2)
                _multiTransition.DrawMultiTransitionEditor(controller);
            else
                DrawSingleTransition(_selectedTransitions[0], controller);
        }

        /// <summary>
        /// The transition list for a source that is not an edge — the Entry and Any State nodes,
        /// which carry transitions but are selected as a node rather than as an edge. Everything
        /// below the list belongs to a selected transition, so it only appears once one is picked.
        /// </summary>
        public void DrawSourceContext(TransitionEnd source)
        {
            var rows = TransitionListGui.RowsOf(source, _context.CurrentStateMachine);
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("{0} node. Drag from its port to create transitions.", source.Label), MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(source.Label, EditorStyles.boldLabel);
            _selectedTransitions.RemoveAll(t => t == null || !Contains(rows, t));
            HandleCopyPasteShortcuts();
            DrawTransitionList(rows, null, source);

            if (_selectedTransitions.Count == 0) return;
            PanelGui.HorizontalLine();
            if (_selectedTransitions.Count >= 2)
                _multiTransition.DrawMultiTransitionEditor(_context.Controller);
            else
                DrawSingleTransition(_selectedTransitions[0], _context.Controller);
        }

        /// <summary>All transitions of every currently selected (non-default) edge. The graph
        /// answers which edges those are — including the fallback for a selection the graph is not
        /// highlighting — and hands over the transitions, never the edges themselves.</summary>
        static List<AnimatorTransitionBase> GatherTransitionPool(List<TransitionGroup> groups)
        {
            var pool = new List<AnimatorTransitionBase>();
            foreach (var group in groups)
            {
                if (group.IsDefault) continue;
                foreach (var t in group.Transitions)
                    if (t != null && !pool.Contains(t)) pool.Add(t);
            }
            return pool;
        }

        /// <summary>
        /// What the list shows. When every selected edge leaves the same node, it is that node's
        /// complete transition list in evaluation order — the form that can be reordered, and the
        /// only one where the neighbours a drag would move past are all on screen. A selection
        /// spanning several sources falls back to just the selected transitions, each still
        /// numbered by where it really sits.
        /// </summary>
        List<TransitionRow> BuildRows(List<TransitionGroup> groups, List<AnimatorTransitionBase> pool,
            out TransitionEnd? reorderSource)
        {
            var sm = _context.CurrentStateMachine;
            reorderSource = SharedSource(groups);
            return reorderSource.HasValue
                ? TransitionListGui.RowsOf(reorderSource.Value, sm)
                : TransitionListGui.RowsFor(pool, groups, sm);
        }

        /// <summary>The one node every selected edge leaves, or null when they disagree.</summary>
        static TransitionEnd? SharedSource(List<TransitionGroup> groups)
        {
            TransitionEnd? shared = null;
            foreach (var group in groups)
            {
                if (group.IsDefault) continue;
                if (group.Source.Kind == TransitionEndKind.None) return null;
                if (shared.HasValue)
                {
                    if (!shared.Value.SameAs(group.Source)) return null;
                }
                else
                {
                    shared = group.Source;
                }
            }
            return shared;
        }

        /// <summary>
        /// Drops rows that are gone and makes sure something is selected. The fallback is the
        /// selected edge's own first transition, not the list's — with a whole source on screen,
        /// row one usually belongs to a different edge than the one the user clicked.
        /// </summary>
        void PruneSelection(List<TransitionRow> rows, List<AnimatorTransitionBase> pool)
        {
            _selectedTransitions.RemoveAll(t => t == null || !Contains(rows, t));
            if (_selectedTransitions.Count == 0)
                _selectedTransitions.Add(pool.Count > 0 ? pool[0] : rows[0].Transition);
        }

        static bool Contains(List<TransitionRow> rows, AnimatorTransitionBase transition)
        {
            foreach (var row in rows)
                if (ReferenceEquals(row.Transition, transition)) return true;
            return false;
        }

        /// <summary>Unity-style vertical transition list with Solo / Mute columns and multi-select.</summary>
        void DrawTransitionList(List<TransitionRow> rows, List<AnimatorTransitionBase> pool,
            TransitionEnd? reorderSource)
        {
            EditorGUILayout.LabelField(L.Tr("Transitions") + " (" + rows.Count + ")", EditorStyles.boldLabel);

            Action<int, int> onMove = null;
            if (reorderSource.HasValue && rows.Count > 1)
            {
                var source = reorderSource.Value;
                onMove = (from, to) =>
                {
                    if (EdgeCommands.Reorder(source, _context.CurrentStateMachine, from, to))
                        _context.NotifyGraphStructureChanged();
                };
            }

            var result = _list.Draw(rows, _selectedTransitions.Contains, _multiTransition.RefreshEdges, onMove);
            _sync.SetHoveredTransition(result.hovered);

            if (result.clicked >= 0)
                HandleRowClick(rows, result.clicked);
            if (result.deleted != null)
            {
                DeleteTransitionRow(result.deleted, rows);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.BeginHorizontal();
            if (pool != null && GUILayout.Button(L.Tr("+ Add Transition")))
            {
                AddTransitionToAnchorEdge(pool);
                GUIUtility.ExitGUI();
            }
            if (rows.Count > 1 && GUILayout.Button(L.Tr("Select All"), GUILayout.Width(80)))
            {
                _selectedTransitions.Clear();
                foreach (var row in rows)
                    if (row.Transition != null) _selectedTransitions.Add(row.Transition);
            }
            EditorGUILayout.EndHorizontal();
        }

        void HandleRowClick(List<TransitionRow> rows, int index)
        {
            var t = rows[index].Transition;
            if (t == null) return;
            var e = Event.current;
            bool additive = e != null && (e.control || e.command);
            bool range = e != null && e.shift;

            var previous = new List<AnimatorTransitionBase>(_selectedTransitions);

            if (range && _rangeAnchor >= 0 && _rangeAnchor < rows.Count)
            {
                _selectedTransitions.Clear();
                int lo = Mathf.Min(_rangeAnchor, index);
                int hi = Mathf.Max(_rangeAnchor, index);
                for (int i = lo; i <= hi; i++)
                    if (rows[i].Transition != null) _selectedTransitions.Add(rows[i].Transition);
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

            // A plain click on a row belonging to some other edge has to move the graph
            // selection too, or the highlighted edge and the form below would be about
            // different transitions. Ctrl / Shift clicks are building a set inside this list
            // and must not collapse it to one.
            if (!additive && !range && _sync.FindEdge(t) != null && !InSelectedEdges(t))
                _context.Select(t);
        }

        /// <summary>True when the transition belongs to one of the edges the graph has selected.</summary>
        bool InSelectedEdges(AnimatorTransitionBase transition)
        {
            foreach (var group in _context.GetSelectedTransitionGroups())
                if (group.Transitions.Contains(transition)) return true;
            return false;
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

        void DeleteTransitionRow(AnimatorTransitionBase transition, List<TransitionRow> rows)
        {
            // Deleting needs the edge object itself (it is where the transition's source node
            // lives), so this one stays a GraphSync command rather than a context notification.
            var edge = _sync.FindEdge(transition);
            if (edge == null) return;

            AnimatorTransitionBase remaining = null;
            foreach (var row in rows)
                if (row.Transition != null && !ReferenceEquals(row.Transition, transition))
                { remaining = row.Transition; break; }

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
                exitTime = EditorGUILayout.FloatField(new GUIContent(L.Tr("Exit Time"),
                        L.Tr("A fraction of the source state's length: 1 is its end, 0.5 is halfway, "
                             + "2 is after two loops.")),
                    stateTransition.exitTime);
            bool fixedDuration = EditorGUILayout.Toggle(L.Tr("Fixed Duration"), stateTransition.hasFixedDuration);
            float duration = EditorGUILayout.FloatField(DurationLabel(fixedDuration), stateTransition.duration);
            float offset = EditorGUILayout.FloatField(new GUIContent(L.Tr("Offset"),
                    L.Tr("Where the destination state starts, as a fraction of its own length.")),
                stateTransition.offset);
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

        /// <summary>
        /// The duration field changes unit with the Fixed Duration toggle above it, and the
        /// label never said which one was in force — the same 0.25 means a quarter of a second
        /// or a quarter of the source clip. Named the way Unity's own inspector names them, so
        /// the two editors agree; the tooltip carries what "(%)" actually means, since the
        /// field holds a fraction rather than a number out of a hundred.
        /// </summary>
        static GUIContent DurationLabel(bool fixedDuration) => fixedDuration
            ? new GUIContent(L.Tr("Duration (s)"), L.Tr("Crossfade length in seconds."))
            : new GUIContent(L.Tr("Duration (%)"),
                L.Tr("Crossfade length as a fraction of the source state's length — 0.25 is a quarter of it."));

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

                DrawParameterPicker(condition, paramNames);

                var type = typeByName.TryGetValue(condition.parameter, out var t) ? t : AnimatorControllerParameterType.Float;
                ConditionGui.DrawConditionValue(condition, type);

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
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
                working.Add(NextCondition(working, paramNames, typeByName));
                changed = true;
            }

            if (changed)
            {
                Undo.RegisterCompleteObjectUndo(transition, "Edit Conditions");
                TransitionClipboard.SetConditions(transition, working);
                EditorUtility.SetDirty(transition);
                // Nothing was told before this. The edge in the graph draws a summary of
                // these conditions and the parameter list marks the ones nothing reads — both
                // were left showing the old answer until some unrelated edit came past. Neither
                // needs the graph rebuilt: one edge changed how it reads, and the set of
                // parameters something references changed. Saying more than that would tear
                // down and rebuild every node in the layer to relabel one line.
                _context.NotifyGraphVisualsChanged(transition);
                _context.NotifyParametersChanged();
            }
        }
        /// <summary>
        /// The parameter dropdown for one condition row. A condition naming a parameter the
        /// controller no longer declares keeps that name — as an extra, red entry at the end of
        /// the list — instead of reading as the first parameter. It used to silently become the
        /// first one: nothing was written while the row was merely drawn, but editing any other
        /// row on the same transition saved the whole working list, so the broken condition
        /// quietly turned into a working one pointing somewhere else, and the analyzer's
        /// complaint about it disappeared with it.
        /// </summary>
        static void DrawParameterPicker(TransitionClipboard.ConditionData condition, string[] paramNames)
        {
            int index = Array.IndexOf(paramNames, condition.parameter);
            bool missing = index < 0;

            var options = paramNames;
            if (missing)
            {
                options = new string[paramNames.Length + 1];
                Array.Copy(paramNames, options, paramNames.Length);
                options[paramNames.Length] = MissingLabel(condition.parameter);
                index = paramNames.Length;
            }

            var previous = GUI.color;
            if (missing) GUI.color = DaerDColors.Warning;
            int picked = EditorGUILayout.Popup(index, options);
            GUI.color = previous;

            // Picking the missing entry itself is a no-op; picking a real one is the repair.
            if (picked < paramNames.Length) condition.parameter = paramNames[picked];
        }

        static string MissingLabel(string parameter) =>
            (string.IsNullOrEmpty(parameter) ? L.Tr("(no parameter)") : parameter) + "  " + L.Tr("(missing)");

        /// <summary>
        /// What "+ Add Condition" starts from. A second condition on a numeric parameter is
        /// almost always the other half of a range, so it inherits the last row's parameter and
        /// takes the opposite comparison — Greater then Less. Bool and Trigger have no range to
        /// build, and a second condition on the same one only ever contradicts the first, so
        /// they fall back to the first parameter in the list.
        /// </summary>
        public static TransitionClipboard.ConditionData NextCondition(List<TransitionClipboard.ConditionData> working,
            string[] paramNames, Dictionary<string, AnimatorControllerParameterType> typeByName)
        {
            string parameter = paramNames[0];
            AnimatorConditionMode? opposite = null;

            if (working.Count > 0)
            {
                var last = working[working.Count - 1];
                if (Array.IndexOf(paramNames, last.parameter) >= 0
                    && typeByName.TryGetValue(last.parameter, out var lastType)
                    && (lastType == AnimatorControllerParameterType.Float
                        || lastType == AnimatorControllerParameterType.Int))
                {
                    parameter = last.parameter;
                    if (last.mode == AnimatorConditionMode.Greater) opposite = AnimatorConditionMode.Less;
                    else if (last.mode == AnimatorConditionMode.Less) opposite = AnimatorConditionMode.Greater;
                }
            }

            var type = typeByName.TryGetValue(parameter, out var t) ? t : AnimatorControllerParameterType.Float;
            return new TransitionClipboard.ConditionData
            {
                parameter = parameter,
                mode = opposite ?? PanelGui.ModesFor(type)[0],
            };
        }

    }
}
