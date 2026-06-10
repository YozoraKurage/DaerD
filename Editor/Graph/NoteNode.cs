using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A free-floating memo (sticky note) drawn among the graph nodes. Dragging anywhere on
    /// the note moves it, the bottom-right handle resizes it, and double-click (or F2) edits
    /// the text in place.
    /// </summary>
    class NoteNode : GraphElement
    {
        public GraphFrameData.Note Note { get; }

        readonly Label _textLabel;
        readonly Action<string> _onTextCommitted;
        TextField _editField;

        public NoteNode(GraphFrameData.Note note, Action onGeometryChanged, Action<string> onTextCommitted)
        {
            Note = note;
            _onTextCommitted = onTextCommitted;
            AddToClassList("dd-note");
            style.position = Position.Absolute;
            // Above frames (-10) but still behind the regular nodes and edges.
            layer = -5;
            tooltip = "Double-click or F2 to edit";

            capabilities = Capabilities.Selectable | Capabilities.Movable | Capabilities.Deletable
                         | Capabilities.Resizable;

            _textLabel = new Label { pickingMode = PickingMode.Ignore };
            _textLabel.AddToClassList("dd-note__text");
            Add(_textLabel);

            Add(new Resizer());
            // Resizes bypass graphViewChanged; sizes are persisted from geometry events while
            // moves are persisted by GraphSync's moved-elements path on drop.
            RegisterCallback<GeometryChangedEvent>(_ => onGeometryChanged?.Invoke());
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2 && evt.button == 0)
                {
                    BeginEdit();
                    evt.StopPropagation();
                }
            });

            RefreshVisuals();
        }

        public void RefreshVisuals()
        {
            _textLabel.text = Note.text ?? string.Empty;
            _textLabel.style.fontSize = Note.fontSize;
            var c = Note.color;
            style.backgroundColor = new Color(c.r, c.g, c.b, 0.95f);
            ApplyBorder();
        }

        void ApplyBorder()
        {
            var c = Note.color;
            var borderColor = selected
                ? new Color(0.40f, 0.70f, 1.00f)
                : new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f);
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

        /// <summary>
        /// Swaps the text for a multiline field. Enter inserts a newline; focus-out commits,
        /// Escape cancels.
        /// </summary>
        public void BeginEdit()
        {
            if (_editField != null) return;

            var field = new TextField { value = Note.text ?? string.Empty, multiline = true };
            field.AddToClassList("dd-note__edit");
            field.style.fontSize = Note.fontSize;
            _editField = field;
            _textLabel.style.display = DisplayStyle.None;
            Insert(IndexOf(_textLabel) + 1, field);

            bool finished = false;
            void Finish(bool commit)
            {
                if (finished) return;
                finished = true;
                string value = field.value;
                _editField = null;
                field.RemoveFromHierarchy();
                _textLabel.style.display = DisplayStyle.Flex;
                if (!commit || value == Note.text) return;
                _onTextCommitted?.Invoke(value);
                RefreshVisuals();
            }

            field.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape)
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
    }
}
