using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A comment/group box drawn behind the graph nodes. Only the title bar and a thin border
    /// margin are clickable, so rubber-band selection and node interaction keep working inside
    /// the frame; resizing uses the standard GraphView resizer in the bottom-right corner.
    /// </summary>
    class FrameNode : GraphElement
    {
        public GraphFrameData.Frame Frame { get; }

        const float TitleHeight = 24f;
        const float BorderPick = 6f;

        readonly Label _titleLabel;
        readonly VisualElement _titleBar;
        readonly VisualElement _body;

        public FrameNode(GraphFrameData.Frame frame, Action onGeometryChanged)
        {
            Frame = frame;
            AddToClassList("dd-frame");
            style.position = Position.Absolute;
            // Negative layer renders frames behind nodes and edges.
            layer = -10;

            capabilities = Capabilities.Selectable | Capabilities.Movable | Capabilities.Deletable
                         | Capabilities.Resizable;

            _titleBar = new VisualElement();
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
            // changes are persisted from geometry events; GraphSync ignores pure position
            // changes here because moves are persisted via the regular moved-elements path.
            RegisterCallback<GeometryChangedEvent>(_ => onGeometryChanged?.Invoke());

            RefreshVisuals();
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
