using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD
{
    /// <summary>One drawn transition row: what it is, where it starts, and where it sits in the
    /// order its source is evaluated in.</summary>
    readonly struct TransitionRow
    {
        public readonly AnimatorTransitionBase Transition;
        public readonly TransitionEnd Source;
        /// <summary>1-based position in the source's transition list; 0 when it is not known.</summary>
        public readonly int Priority;

        public TransitionRow(AnimatorTransitionBase transition, TransitionEnd source, int priority)
        {
            Transition = transition;
            Source = source;
            Priority = priority;
        }
    }

    /// <summary>
    /// The Unity-style transition list: a Solo and a Mute column, the transition itself as
    /// "<c>source → destination</c>" behind its priority number, and a delete button. Shared by
    /// the state form and the transition editor so both read the same and both can reorder.
    /// </summary>
    /// <remarks>
    /// The number is the position the Animator evaluates the transition at, not the row index —
    /// a list showing only some of a source's transitions still numbers them by where they
    /// really are. Reordering is offered only when the rows are one source's complete list,
    /// because dragging within a partial list cannot say what happens to the rows it hides.
    /// </remarks>
    class TransitionListGui
    {
        readonly ListReorder _reorder = new ListReorder();

        internal struct Result
        {
            /// <summary>The transition the pointer is over, or null. Only ever answered on the
            /// repaint pass, where the row rectangles are real; a caller pushing it to the graph
            /// should ask on that pass too, or it will clear the highlight and set it again on
            /// every event the panel receives.</summary>
            public AnimatorTransitionBase hovered;
            /// <summary>True on the pass that could see the pointer at all.</summary>
            public bool hoverKnown;
            /// <summary>Index of the row that was clicked this event, or -1.</summary>
            public int clicked;
            /// <summary>The transition whose delete button was pressed, or null.</summary>
            public AnimatorTransitionBase deleted;
        }

        /// <summary>
        /// Draws the header and every row. <paramref name="onMove"/> being null makes the list
        /// read-only in order and drops the grip column with it.
        /// </summary>
        public Result Draw(IList<TransitionRow> rows, Func<AnimatorTransitionBase, bool> isSelected,
            Action onSoloMuteChanged, Action<int, int> onMove)
        {
            var result = new Result { clicked = -1, hoverKnown = Event.current.type == EventType.Repaint };

            EditorGUILayout.BeginHorizontal();
            if (onMove != null) GUILayout.Space(ListReorder.HandleWidth);
            EditorGUILayout.LabelField(L.Tr("Solo"), EditorStyles.miniLabel, GUILayout.Width(34));
            EditorGUILayout.LabelField(L.Tr("Mute"), EditorStyles.miniLabel, GUILayout.Width(36));
            EditorGUILayout.LabelField(L.Tr("Transition"), EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            if (onMove != null) _reorder.Begin();

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var t = row.Transition;
                if (t == null) continue;

                var rowRect = EditorGUILayout.BeginHorizontal();
                if (onMove != null) _reorder.DrawHandle();

                EditorGUI.BeginChangeCheck();
                bool solo = EditorGUILayout.Toggle(t.solo, GUILayout.Width(34));
                bool mute = EditorGUILayout.Toggle(t.mute, GUILayout.Width(36));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RegisterCompleteObjectUndo(t, "Edit Transition");
                    t.solo = solo;
                    t.mute = mute;
                    EditorUtility.SetDirty(t);
                    onSoloMuteChanged?.Invoke();
                }

                var previousBackground = GUI.backgroundColor;
                if (isSelected != null && isSelected(t)) GUI.backgroundColor = DaerDColors.SelectedRow;
                if (GUILayout.Button(Label(row), EditorStyles.miniButton))
                    result.clicked = i;
                GUI.backgroundColor = previousBackground;

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    result.deleted = t;

                EditorGUILayout.EndHorizontal();

                // Only the repaint pass has real rectangles; during layout every row would
                // claim the pointer.
                if (Event.current.type == EventType.Repaint && rowRect.Contains(Event.current.mousePosition))
                    result.hovered = t;

                if (onMove != null) _reorder.Row(rowRect);
            }

            if (onMove != null) _reorder.End(onMove);
            return result;
        }

        /// <summary>
        /// "3.  Idle → Wave    Gesture = 1" — the priority, both ends, then what it is waiting
        /// for. Naming the source on every row is what keeps a list that mixes sources readable;
        /// naming the condition is what tells apart the rows of one arrow, which otherwise say
        /// the same two state names as each other and differ only by number.
        /// </summary>
        static string Label(TransitionRow row)
        {
            string number = row.Priority > 0 ? row.Priority + ".  " : string.Empty;
            string summary = ParameterConverter.SummarizeConditions(row.Transition);
            return number + row.Source.Label + " " + ParameterConverter.DescribeTransition(row.Transition)
                + (string.IsNullOrEmpty(summary) ? string.Empty : "    " + summary);
        }

        /// <summary>
        /// One source's complete transition list as rows, in evaluation order. This is the form
        /// that can be reordered.
        /// </summary>
        public static List<TransitionRow> RowsOf(TransitionEnd source, AnimatorStateMachine sm)
        {
            var transitions = EdgeCommands.TransitionsFrom(source, sm);
            var rows = new List<TransitionRow>(transitions.Length);
            for (int i = 0; i < transitions.Length; i++)
                rows.Add(new TransitionRow(transitions[i], source, i + 1));
            return rows;
        }

        /// <summary>
        /// Rows for a handful of transitions that do not make up any one source's whole list —
        /// each keeps the priority it really has, so the numbers stay honest even though the
        /// rows in between are missing.
        /// </summary>
        public static List<TransitionRow> RowsFor(IList<AnimatorTransitionBase> transitions,
            IList<TransitionGroup> groups, AnimatorStateMachine sm)
        {
            var priorities = new Dictionary<AnimatorTransitionBase, TransitionRow>();
            foreach (var group in groups)
                foreach (var row in RowsOf(group.Source, sm))
                    if (row.Transition != null) priorities[row.Transition] = row;

            var rows = new List<TransitionRow>(transitions.Count);
            foreach (var t in transitions)
            {
                if (t == null) continue;
                rows.Add(priorities.TryGetValue(t, out var known)
                    ? known
                    : new TransitionRow(t, TransitionEnd.None, 0));
            }
            return rows;
        }
    }
}
