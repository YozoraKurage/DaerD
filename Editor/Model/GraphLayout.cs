using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Repositions the nodes of one state machine. Positions live on the asset.</summary>
    static class GraphLayout
    {
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

        /// <summary>
        /// Spaces the selected states evenly between the outermost two, along X for Row and
        /// along Y for Column, keeping the other coordinate. Needs at least three states —
        /// with two there is nothing between the ends to space out.
        /// </summary>
        public static void Distribute(AnimatorStateMachine sm, ICollection<AnimatorState> selected, AlignAxis axis)
        {
            if (sm == null || selected == null || selected.Count < 3) return;
            var targets = selected as HashSet<AnimatorState> ?? new HashSet<AnimatorState>(selected);

            var states = sm.states;
            var picked = new List<int>();
            for (int i = 0; i < states.Length; i++)
                if (states[i].state != null && targets.Contains(states[i].state))
                    picked.Add(i);
            if (picked.Count < 3) return;

            picked.Sort((a, b) =>
                (axis == AlignAxis.Row ? states[a].position.x : states[a].position.y)
                .CompareTo(axis == AlignAxis.Row ? states[b].position.x : states[b].position.y));

            float first = axis == AlignAxis.Row
                ? states[picked[0]].position.x : states[picked[0]].position.y;
            float last = axis == AlignAxis.Row
                ? states[picked[picked.Count - 1]].position.x : states[picked[picked.Count - 1]].position.y;
            float step = (last - first) / (picked.Count - 1);

            string label = axis == AlignAxis.Row ? "Distribute States (Row)" : "Distribute States (Column)";
            using (new UndoScope(label))
            {
                Undo.RegisterCompleteObjectUndo(sm, label);
                for (int n = 0; n < picked.Count; n++)
                {
                    var cs = states[picked[n]];
                    var p = cs.position;
                    if (axis == AlignAxis.Row) p.x = first + step * n;
                    else p.y = first + step * n;
                    cs.position = p;
                    states[picked[n]] = cs;
                }
                sm.states = states;
                EditorUtility.SetDirty(sm);
            }
        }
    }
}
