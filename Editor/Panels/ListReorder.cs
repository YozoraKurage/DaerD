using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Drag-to-reorder support for IMGUI list rows. Each row draws a grip handle at its
    /// left edge (<see cref="DrawHandle"/>); grabbing a handle and dragging past other
    /// rows reorders the list. One instance lives per list and carries the in-progress
    /// drag across the IMGUI event passes.
    ///
    /// Usage, once per OnGUI:
    /// <code>
    /// reorder.Begin();
    /// foreach (row)
    /// {
    ///     var rowRect = EditorGUILayout.BeginHorizontal();
    ///     reorder.DrawHandle();          // first control inside the row
    ///     /* draw the rest of the row */
    ///     EditorGUILayout.EndHorizontal();
    ///     reorder.Row(rowRect);
    /// }
    /// reorder.End((from, to) => Move(from, to));
    /// </code>
    /// </summary>
    class ListReorder
    {
        public const float HandleWidth = 18f;

        readonly List<Rect> _rows = new List<Rect>();
        int _controlId;
        int _dragIndex = -1;   // row being dragged, -1 when idle
        float _pointerY;       // latest pointer Y, in list-content space

        /// <summary>Call once before the rows are drawn.</summary>
        public void Begin()
        {
            _rows.Clear();
            _controlId = GUIUtility.GetControlID(FocusType.Passive);
        }

        /// <summary>
        /// Draws the grip handle for the next row. Call as the first control inside the
        /// row's horizontal group, before <see cref="Row"/> records that row.
        /// </summary>
        public void DrawHandle()
        {
            int index = _rows.Count;
            // ExpandHeight(false): the handle is one line tall and must NOT stretch — an
            // expanding handle would eat all the panel's spare height when rows are few.
            var rect = GUILayoutUtility.GetRect(HandleWidth, EditorGUIUtility.singleLineHeight,
                GUILayout.Width(HandleWidth), GUILayout.ExpandHeight(false));

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Pan);
            DrawGrip(rect, index == _dragIndex);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                _dragIndex = index;
                _pointerY = e.mousePosition.y;
                GUIUtility.hotControl = _controlId;
                e.Use();
            }
        }

        /// <summary>Records a drawn row's rect; call once per row, in order, after EndHorizontal.</summary>
        public void Row(Rect rowRect) => _rows.Add(rowRect);

        /// <summary>
        /// Processes the in-progress drag and, when it ends on a different slot, invokes
        /// <paramref name="onMove"/> with the source and destination row indices.
        /// </summary>
        public void End(Action<int, int> onMove)
        {
            if (_dragIndex < 0) return;

            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDrag:
                    _pointerY = e.mousePosition.y;
                    e.Use();
                    break;

                case EventType.MouseUp:
                    _pointerY = e.mousePosition.y;
                    Finish(onMove);
                    e.Use();
                    break;

                // The MouseUp never reached the window because the button was released
                // while the pointer was outside it. A MouseMove (movement with no button
                // held) is the first proof the drag is over: resolve it at the last
                // tracked position rather than leaving the drag stuck on screen.
                case EventType.MouseMove:
                    Finish(onMove);
                    e.Use();
                    break;

                case EventType.KeyDown:
                    if (e.keyCode == KeyCode.Escape)
                    {
                        EndDrag();
                        e.Use();
                    }
                    break;

                case EventType.Repaint:
                    DrawFeedback();
                    break;
            }
        }

        /// <summary>Resolves the drag, applying the move when it lands on a different slot.</summary>
        void Finish(Action<int, int> onMove)
        {
            int from = _dragIndex;
            int to = ResolveTarget(from);
            EndDrag();
            if (from >= 0 && from < _rows.Count && to >= 0 && to != from)
            {
                onMove(from, to);
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>Clears the drag state and releases the captured control.</summary>
        void EndDrag()
        {
            _dragIndex = -1;
            if (GUIUtility.hotControl == _controlId) GUIUtility.hotControl = 0;
        }

        /// <summary>Destination row index for a drag that started at <paramref name="from"/>.</summary>
        int ResolveTarget(int from)
        {
            if (_rows.Count == 0) return -1;
            int gap = InsertGap();
            int to = gap > from ? gap - 1 : gap;
            return Mathf.Clamp(to, 0, _rows.Count - 1);
        }

        /// <summary>Insertion slot in [0, rowCount] derived from the pointer position.</summary>
        int InsertGap()
        {
            int gap = 0;
            for (int i = 0; i < _rows.Count; i++)
                if (_pointerY > _rows[i].center.y) gap = i + 1;
            return gap;
        }

        void DrawFeedback()
        {
            if (_rows.Count == 0) return;

            if (_dragIndex >= 0 && _dragIndex < _rows.Count)
                EditorGUI.DrawRect(_rows[_dragIndex], DaerDColors.DragRow);

            int gap = InsertGap();
            float y = gap < _rows.Count ? _rows[gap].y : _rows[_rows.Count - 1].yMax;
            var bounds = _rows[0];
            EditorGUI.DrawRect(new Rect(bounds.x, y - 1f, bounds.width, 2f), DaerDColors.Selected);
        }

        static void DrawGrip(Rect rect, bool active)
        {
            var e = Event.current;
            if (e.type != EventType.Repaint) return;

            var color = active ? DaerDColors.Selected
                : rect.Contains(e.mousePosition) ? DaerDColors.GripHover : DaerDColors.Grip;
            const float barW = 9f, barH = 1.5f, gap = 3f;
            float x = rect.x + (rect.width - barW) * 0.5f;
            float y = rect.center.y - (barH * 3f + gap * 2f) * 0.5f;
            for (int i = 0; i < 3; i++)
                EditorGUI.DrawRect(new Rect(x, Mathf.Round(y + i * (barH + gap)), barW, barH), color);
        }
    }
}
