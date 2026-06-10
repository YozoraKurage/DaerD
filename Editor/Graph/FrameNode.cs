using System;
using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A comment/group box drawn behind the graph nodes. Only the title bar and a thin border
    /// margin are clickable, so rubber-band selection and node interaction keep working inside
    /// the frame; resizing uses the standard GraphView resizer in the bottom-right corner.
    /// Dragging the frame carries the nodes lying fully inside it along in real time
    /// (Alt-drag moves the frame alone).
    /// </summary>
    class FrameNode : GraphElement
    {
        public GraphFrameData.Frame Frame { get; }

        const float TitleHeight = 24f;
        const float BorderPick = 6f;

        readonly Label _titleLabel;
        readonly VisualElement _titleBar;
        readonly VisualElement _body;
        readonly Func<Rect, List<GraphElement>> _contentsResolver;

        // Captured on mouse-down: the nodes inside the frame and where they started, so they
        // can be moved in lockstep with the frame during the drag.
        List<(GraphElement node, Vector2 startPosition)> _draggedContents;
        Rect _dragStartBounds;

        public FrameNode(GraphFrameData.Frame frame, Action onGeometryChanged,
            Func<Rect, List<GraphElement>> contentsResolver)
        {
            Frame = frame;
            _contentsResolver = contentsResolver;
            AddToClassList("dd-frame");
            style.position = Position.Absolute;
            // Negative layer renders frames behind nodes and edges.
            layer = -10;

            capabilities = Capabilities.Selectable | Capabilities.Movable | Capabilities.Deletable
                         | Capabilities.Resizable;

            _titleBar = new VisualElement
            {
                tooltip = "Drag to move the frame and the nodes inside it (Alt-drag: frame only)",
            };
            _titleBar.AddToClassList("dd-frame__title");
            _titleBar.style.height = TitleHeight;
            _titleLabel = new Label { pickingMode = PickingMode.Ignore };
            _titleLabel.AddToClassList("dd-frame__title-label");
            _titleBar.Add(_titleLabel);
            Add(_titleBar);

            _body = new VisualElement { pickingMode = PickingMode.Ignore };
            _body.AddToClassList("dd-frame__body");
            _body.style.flexGrow = 1;
            Add(_body);

            Add(new Resizer());
            // The resizer manipulates layout directly (it bypasses graphViewChanged), so size
            // changes are persisted from geometry events; position-only changes are persisted
            // by GraphSync's moved-elements path when the drag is dropped.
            RegisterCallback<GeometryChangedEvent>(_ =>
            {
                FollowDraggedContents();
                onGeometryChanged?.Invoke();
            });
            RegisterCallback<MouseDownEvent>(OnMouseDownCaptureContents, TrickleDown.TrickleDown);

            RefreshVisuals();
        }

        /// <summary>
        /// Snapshot the contained nodes when a drag might start. Only nodes lying entirely
        /// inside the frame count — a node merely touching the outline visibly pokes out, so
        /// carrying it along would contradict what the user sees.
        /// </summary>
        void OnMouseDownCaptureContents(MouseDownEvent evt)
        {
            _draggedContents = null;
            if (evt.button != 0) return;
            if (IsInResizer(evt.target as VisualElement)) return;   // resizing never drags contents
            if (evt.altKey || !Frame.moveNodesWithFrame) return;

            var contents = _contentsResolver?.Invoke(GetPosition());
            if (contents == null || contents.Count == 0) return;
            _dragStartBounds = GetPosition();
            _draggedContents = new List<(GraphElement, Vector2)>(contents.Count);
            foreach (var node in contents)
                _draggedContents.Add((node, node.GetPosition().position));
        }

        /// <summary>Moves the captured nodes in lockstep with the frame while it is dragged.</summary>
        void FollowDraggedContents()
        {
            if (_draggedContents == null) return;
            var delta = GetPosition().position - _dragStartBounds.position;
            foreach (var (node, startPosition) in _draggedContents)
            {
                var rect = node.GetPosition();
                node.SetPosition(new Rect(startPosition + delta, rect.size));
            }
        }

        /// <summary>
        /// The nodes the current drag carried along (null when none), clearing the capture.
        /// GraphSync calls this on drop to persist their new positions.
        /// </summary>
        public List<GraphElement> TakeDraggedContents()
        {
            var captured = _draggedContents;
            _draggedContents = null;
            if (captured == null) return null;
            var nodes = new List<GraphElement>(captured.Count);
            foreach (var (node, _) in captured)
                nodes.Add(node);
            return nodes;
        }

        static bool IsInResizer(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
                if (e is Resizer) return true;
            return false;
        }

        public void RefreshVisuals()
        {
            _titleLabel.text = string.IsNullOrEmpty(Frame.title) ? "Frame" : Frame.title;
            var c = Frame.color;
            _titleBar.style.backgroundColor = new Color(c.r, c.g, c.b, 0.85f);
            _body.style.backgroundColor = new Color(c.r, c.g, c.b, 0.12f);
            ApplyBorder();
        }

        void ApplyBorder()
        {
            var c = Frame.color;
            var borderColor = selected ? new Color(0.40f, 0.70f, 1.00f) : new Color(c.r, c.g, c.b, 0.9f);
            float width = selected ? 2f : 1f;
            style.borderTopColor = borderColor;
            style.borderBottomColor = borderColor;
            style.borderLeftColor = borderColor;
            style.borderRightColor = borderColor;
            style.borderTopWidth = width;
            style.borderBottomWidth = width;
            style.borderLeftWidth = width;
            style.borderRightWidth = width;
        }

        public override void OnSelected()
        {
            base.OnSelected();
            ApplyBorder();
        }

        public override void OnUnselected()
        {
            base.OnUnselected();
            ApplyBorder();
        }

        // Clicks land on the frame only via its title bar or a thin border margin; the interior
        // falls through to the graph so nodes inside stay clickable and rubber-band still works.
        public override bool ContainsPoint(Vector2 localPoint)
        {
            var rect = new Rect(0f, 0f, layout.width, layout.height);
            if (!rect.Contains(localPoint)) return false;
            if (localPoint.y <= TitleHeight) return true;
            return localPoint.x <= BorderPick || localPoint.x >= rect.width - BorderPick
                || localPoint.y >= rect.height - BorderPick;
        }

        // Rubber-band selection only picks the frame up when it sweeps over the title bar,
        // not when selecting nodes inside the frame body.
        public override bool Overlaps(Rect rectangle)
        {
            var titleRect = new Rect(0f, 0f, layout.width, TitleHeight);
            return titleRect.Overlaps(rectangle, true);
        }
    }
}
