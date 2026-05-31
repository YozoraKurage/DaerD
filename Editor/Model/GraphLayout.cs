using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Repositions the nodes of one state machine. Positions live on the asset.</summary>
    static class GraphLayout
    {
        const float ColumnSpacing = 280f;
        const float RowSpacing = 110f;
        static readonly Vector3 Origin = new Vector3(260f, 60f, 0f);

        /// <summary>How a multi-selection is straightened by <see cref="Align"/>.</summary>
        public enum AlignAxis
        {
            /// <summary>Equalize Y so the selected states sit in a single horizontal row.</summary>
            Row,
            /// <summary>Equalize X so the selected states sit in a single vertical column.</summary>
            Column,
        }

        /// <summary>
        /// Straightens the selected states onto a shared axis without disturbing the rest of the
        /// graph. Row alignment equalizes Y (a horizontal row); Column alignment equalizes X (a
        /// vertical column). The shared coordinate is the average of the selection, so the line
        /// lands where the states already cluster. Needs at least two selected states.
        /// </summary>
        public static void Align(AnimatorStateMachine sm, ICollection<AnimatorState> selected, AlignAxis axis)
        {
            if (sm == null || selected == null || selected.Count < 2) return;
            var targets = selected as HashSet<AnimatorState> ?? new HashSet<AnimatorState>(selected);

            var states = sm.states;
            float sum = 0f;
            int count = 0;
            for (int i = 0; i < states.Length; i++)
                if (states[i].state != null && targets.Contains(states[i].state))
                {
                    sum += axis == AlignAxis.Row ? states[i].position.y : states[i].position.x;
                    count++;
                }
            if (count < 2) return;
            float shared = sum / count;

            string label = axis == AlignAxis.Row ? "Align States (Row)" : "Align States (Column)";
            using (new UndoScope(label))
            {
                Undo.RegisterCompleteObjectUndo(sm, label);
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].state == null || !targets.Contains(states[i].state)) continue;
                    var cs = states[i];
                    var p = cs.position;
                    if (axis == AlignAxis.Row) p.y = shared; else p.x = shared;
                    cs.position = p;
                    states[i] = cs;
                }
                sm.states = states;
                EditorUtility.SetDirty(sm);
            }
        }

        public static void Grid(AnimatorStateMachine sm)
        {
            if (sm == null) return;
            var states = sm.states;
            var machines = sm.stateMachines;
            int total = states.Length + machines.Length;
            if (total == 0) return;
            int columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(total)));

            using (new UndoScope("Auto Layout (Grid)"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Auto Layout (Grid)");
                int index = 0;
                for (int i = 0; i < states.Length; i++, index++)
                {
                    var cs = states[i];
                    cs.position = Cell(index, columns);
                    states[i] = cs;
                }
                for (int i = 0; i < machines.Length; i++, index++)
                {
                    var cm = machines[i];
                    cm.position = Cell(index, columns);
                    machines[i] = cm;
                }
                sm.states = states;
                sm.stateMachines = machines;
                EditorUtility.SetDirty(sm);
            }
        }

        public static void Hierarchical(AnimatorStateMachine sm)
        {
            if (sm == null) return;

            using (new UndoScope("Auto Layout (Hierarchical)"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Auto Layout (Hierarchical)");

                var depth = new Dictionary<AnimatorState, int>();
                var queue = new Queue<AnimatorState>();
                if (sm.defaultState != null) { depth[sm.defaultState] = 0; queue.Enqueue(sm.defaultState); }
                foreach (var et in sm.entryTransitions)
                    if (et.destinationState != null && !depth.ContainsKey(et.destinationState))
                    {
                        depth[et.destinationState] = 0;
                        queue.Enqueue(et.destinationState);
                    }

                while (queue.Count > 0)
                {
                    var s = queue.Dequeue();
                    int d = depth[s];
                    foreach (var tr in s.transitions)
                    {
                        var dest = tr.destinationState;
                        if (dest != null && !depth.ContainsKey(dest))
                        {
                            depth[dest] = d + 1;
                            queue.Enqueue(dest);
                        }
                    }
                }

                int maxDepth = 0;
                int orphanColumn = 1;
                foreach (var cs in sm.states)
                {
                    if (cs.state == null) continue;
                    if (!depth.ContainsKey(cs.state)) depth[cs.state] = -1;
                    else maxDepth = Mathf.Max(maxDepth, depth[cs.state]);
                }
                // place orphans (no incoming path) in their own trailing column
                var states0 = sm.states;
                for (int i = 0; i < states0.Length; i++)
                    if (states0[i].state != null && depth[states0[i].state] < 0)
                        depth[states0[i].state] = maxDepth + orphanColumn;

                var rowCursor = new Dictionary<int, int>();
                var states = sm.states;
                for (int i = 0; i < states.Length; i++)
                {
                    if (states[i].state == null) continue;
                    int d = depth[states[i].state];
                    rowCursor.TryGetValue(d, out int row);
                    rowCursor[d] = row + 1;
                    var cs = states[i];
                    cs.position = Origin + new Vector3(d * ColumnSpacing, row * RowSpacing, 0f);
                    states[i] = cs;
                }
                sm.states = states;

                var machines = sm.stateMachines;
                int machineColumn = maxDepth + orphanColumn + 1;
                for (int i = 0; i < machines.Length; i++)
                {
                    var cm = machines[i];
                    cm.position = Origin + new Vector3(machineColumn * ColumnSpacing, i * RowSpacing, 0f);
                    machines[i] = cm;
                }
                sm.stateMachines = machines;

                EditorUtility.SetDirty(sm);
            }
        }

        static Vector3 Cell(int index, int columns) =>
            Origin + new Vector3((index % columns) * ColumnSpacing, (index / columns) * RowSpacing, 0f);
    }
}
