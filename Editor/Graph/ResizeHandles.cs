using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Eight square resize handles — four corners plus the midpoint of every edge — overlaid
    /// on a GraphElement while it is selected. Dragging a handle resizes the element from that
    /// side or corner; persistence rides on the element's own geometry-change handling.
    /// </summary>
    class ResizeHandles : VisualElement
    {
        public ResizeHandles(GraphElement target, Vector2 minSize)
        {
            pickingMode = PickingMode.Ignore;
            style.position = Position.Absolute;
            style.left = 0;
            style.top = 0;
            style.right = 0;
            style.bottom = 0;

            Add(new Handle(target, minSize, "nw", left: true, top: true));
            Add(new Handle(target, minSize, "n", top: true));
            Add(new Handle(target, minSize, "ne", right: true, top: true));
            Add(new Handle(target, minSize, "w", left: true));
            Add(new Handle(target, minSize, "e", right: true));
            Add(new Handle(target, minSize, "sw", left: true, bottom: true));
            Add(new Handle(target, minSize, "s", bottom: true));
            Add(new Handle(target, minSize, "se", right: true, bottom: true));
        }

        public void SetVisible(bool visible) =>
            style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        sealed class Handle : VisualElement
        {
            readonly GraphElement _target;
            readonly Vector2 _minSize;
            readonly bool _left, _right, _top, _bottom;
            Vector2 _startMouse;
            Rect _startRect;
            bool _dragging;

            public Handle(GraphElement target, Vector2 minSize, string anchor,
                bool left = false, bool right = false, bool top = false, bool bottom = false)
            {
                _target = target;
                _minSize = minSize;
                _left = left;
                _right = right;
                _top = top;
                _bottom = bottom;
                AddToClassList("dd-resize-handle");
                AddToClassList("dd-resize-handle--" + anchor);

                RegisterCallback<MouseDownEvent>(OnMouseDown);
                RegisterCallback<MouseMoveEvent>(OnMouseMove);
                RegisterCallback<MouseUpEvent>(OnMouseUp);
            }

            // Work in the target's parent space so the drag tracks correctly at any zoom.
            Vector2 MouseInParentSpace(Vector2 worldPosition) =>
                _target.parent != null ? _target.parent.WorldToLocal(worldPosition) : worldPosition;

            void OnMouseDown(MouseDownEvent evt)
            {
                if (evt.button != 0) return;
                _dragging = true;
                _startRect = _target.GetPosition();
                _startMouse = MouseInParentSpace(evt.mousePosition);
                this.CaptureMouse();
                evt.StopPropagation();
            }

            void OnMouseMove(MouseMoveEvent evt)
            {
                if (!_dragging) return;
                var delta = MouseInParentSpace(evt.mousePosition) - _startMouse;

                float x = _startRect.x, y = _startRect.y;
                float w = _startRect.width, h = _startRect.height;
                if (_left) { x += delta.x; w -= delta.x; }
                if (_right) w += delta.x;
                if (_top) { y += delta.y; h -= delta.y; }
                if (_bottom) h += delta.y;

                if (w < _minSize.x)
                {
                    if (_left) x = _startRect.xMax - _minSize.x;
                    w = _minSize.x;
                }
                if (h < _minSize.y)
                {
                    if (_top) y = _startRect.yMax - _minSize.y;
                    h = _minSize.y;
                }

                _target.SetPosition(new Rect(x, y, w, h));
                evt.StopPropagation();
            }

            void OnMouseUp(MouseUpEvent evt)
            {
                if (!_dragging) return;
                _dragging = false;
                this.ReleaseMouse();
                evt.StopPropagation();
            }
        }
    }
}
