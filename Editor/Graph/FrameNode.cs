using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A comment/group box drawn behind the graph nodes. Only the title bar and a thin border
    /// margin are clickable, so rubber-band selection and node interaction keep working inside
    /// the frame; resizing uses the standard GraphView resizer in the bottom-right corner.
    /// Dragging the title bar carries the nodes lying fully inside the frame along in real
    /// time; dragging the border moves the frame alone. A locked frame cannot be moved,
    /// resized, renamed or deleted.
    /// </summary>
    class FrameNode : GraphElement
    {
        public GraphFrameData.Frame Frame { get; }

        const float TitleHeight = 24f;
        const float BorderPick = 6f;

        readonly Label _titleLabel;
        readonly VisualElement _titleBar;
        readonly VisualElement _body;
        readonly Image _lockButton;
        readonly ResizeHandles _resizeHandles;
        readonly Func<Rect, List<GraphElement>> _contentsResolver;
        readonly Action<string> _onRenameCommitted;
        readonly Action _onLockToggled;
        TextField _renameField;

        // Captured on mouse-down: the nodes inside the frame and where they started, so they
        // can be moved in lockstep with the frame during the drag.
        List<(GraphElement node, Vector2 startPosition)> _draggedContents;
        Rect _dragStartBounds;

        public FrameNode(GraphFrameData.Frame frame, Action onGeometryChanged,
            Func<Rect, List<GraphElement>> contentsResolver, Action<string> onRenameCommitted,
            Action onLockToggled)
        {
            Frame = frame;
            _contentsResolver = contentsResolver;
            _onRenameCommitted = onRenameCommitted;
            _onLockToggled = onLockToggled;
            AddToClassList("dd-frame");
            style.position = Position.Absolute;
            // Positive layer renders frames above the default-layer transition edges and nodes,
            // so the frame's title / border / faint body tint sit on top of the transitions
            // crossing them. The body is alpha-0.12 and the title bar leaves the node area
            // free, so states inside stay clearly visible — and ContainsPoint below ignores
            // the body interior, so clicks still fall through to the states they hit.
            layer = 5;

            tooltip = "Drag the border to move the frame alone";

            _titleBar = new VisualElement
            {
                tooltip = "Drag to move the frame and the nodes inside it. Double-click or F2 to rename.",
            };
            _titleBar.AddToClassList("dd-frame__title");
            _titleBar.style.height = TitleHeight;
            _titleLabel = new Label { pickingMode = PickingMode.Ignore };
            _titleLabel.AddToClassList("dd-frame__title-label");
            _titleBar.Add(_titleLabel);

            // Inspector-style lock toggle in the frame's top-right corner.
            _lockButton = new Image { tooltip = "Lock / unlock this frame" };
            _lockButton.AddToClassList("dd-frame__lock");
            _lockButton.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                _onLockToggled?.Invoke();
                evt.StopPropagation();
            });
            _titleBar.Add(_lockButton);
            Add(_titleBar);

            _body = new VisualElement { pickingMode = PickingMode.Ignore };
            _body.AddToClassList("dd-frame__body");
            _body.style.flexGrow = 1;
            Add(_body);

            // Square handles on every edge and corner, shown while the frame is selected.
            // They call SetPosition directly (bypassing graphViewChanged), so geometry events
            // below persist the size; position-only changes are persisted by GraphSync's
            // moved-elements path when a drag is dropped.
            _resizeHandles = new ResizeHandles(this, new Vector2(120f, 60f));
            _resizeHandles.SetVisible(false);
            Add(_resizeHandles);

            RegisterCallback<GeometryChangedEvent>(_ =>
            {
                FollowDraggedContents();
                onGeometryChanged?.Invoke();
            });
            RegisterCallback<MouseDownEvent>(OnMouseDownCaptureContents, TrickleDown.TrickleDown);
            _titleBar.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2 && evt.button == 0)
                {
                    BeginRename();
                    evt.StopPropagation();
                }
            });

            RefreshVisuals();
        }

        /// <summary>
        /// Snapshot the contained nodes when a title-bar drag might start. Only nodes lying
        /// entirely inside the frame count — a node merely touching the outline visibly pokes
        /// out, so carrying it along would contradict what the user sees. Border drags (and
        /// resizes) move the frame alone, so they capture nothing.
        /// </summary>
        void OnMouseDownCaptureContents(MouseDownEvent evt)
        {
            _draggedContents = null;
            if (evt.button != 0 || Frame.locked || !Frame.moveNodesWithFrame) return;
            if (!IsInTitleBar(evt.target as VisualElement)) return;

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

        bool IsInTitleBar(VisualElement element)
        {
            for (var e = element; e != null; e = e.parent)
                if (e == _titleBar) return true;
            return false;
        }

        /// <summary>Swaps the title for a one-shot text field; Enter / focus-out commits, Escape cancels.</summary>
        public void BeginRename()
        {
            if (_renameField != null || Frame.locked) return;

            var field = new TextField { value = Frame.title };
            field.AddToClassList("dd-frame__rename");
            _renameField = field;
            _titleLabel.style.display = DisplayStyle.None;
            _titleBar.Add(field);

            bool finished = false;
            void Finish(bool commit)
            {
                if (finished) return;
                finished = true;
                string value = field.value;
                _renameField = null;
                field.RemoveFromHierarchy();
                _titleLabel.style.display = DisplayStyle.Flex;
                if (!commit) return;
                value = value?.Trim();
                if (string.IsNullOrEmpty(value) || value == Frame.title) return;
                _onRenameCommitted?.Invoke(value);
                RefreshVisuals();
            }

            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Finish(true);
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape)
                {
                    Finish(false);
                    evt.StopPropagation();
                }
            });
            // Focus-out commits, except when the field was detached by a graph rebuild
            // (panel == null): that's a teardown, not a confirmation.
            field.RegisterCallback<FocusOutEvent>(_ => Finish(field.panel != null));
            field.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());

            field.schedule.Execute(() =>
            {
                field.Focus();
                field.SelectAll();
            }).ExecuteLater(0);
        }

        public void RefreshVisuals()
        {
            _titleLabel.text = string.IsNullOrEmpty(Frame.title) ? "Frame" : Frame.title;
            var c = Frame.color;
            _titleBar.style.backgroundColor = DaerDColors.Fade(c, Frame.locked ? 0.55f : 0.85f);
            _body.style.backgroundColor = DaerDColors.Fade(c, 0.12f);

            var lockIcon = EditorGUIUtility.IconContent(Frame.locked ? "LockIcon-On" : "LockIcon");
            _lockButton.image = lockIcon?.image;

            // A locked frame stays selectable (to inspect / unlock) but loses every
            // geometry-changing capability; the resize handles disappear with it.
            // Snappable lets the stock GraphView snap-to-borders pick the frame up
            // during drag, the same as States / Sub-State Machines.
            capabilities = Frame.locked
                ? Capabilities.Selectable
                : Capabilities.Selectable | Capabilities.Movable | Capabilities.Deletable | Capabilities.Snappable;
            _resizeHandles.SetVisible(selected && !Frame.locked);

            ApplyBorder();
        }

        void ApplyBorder()
        {
            var c = Frame.color;
            var borderColor = selected ? DaerDColors.Selected : DaerDColors.Fade(c, 0.9f);
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
            _resizeHandles.SetVisible(!Frame.locked);
            ApplyBorder();
        }

        public override void OnUnselected()
        {
            base.OnUnselected();
            _resizeHandles.SetVisible(false);
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
