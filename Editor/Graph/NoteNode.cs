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
        readonly ResizeHandles _resizeHandles;
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

            // Snappable lets the stock GraphView snap-to-borders pick the note up during
            // drag, the same as States / Sub-State Machines.
            capabilities = Capabilities.Selectable | Capabilities.Movable | Capabilities.Deletable
                         | Capabilities.Snappable;

            _textLabel = new Label { pickingMode = PickingMode.Ignore };
            _textLabel.AddToClassList("dd-note__text");
            Add(_textLabel);

            // Square handles on every edge and corner, shown while the note is selected.
            // They call SetPosition directly (bypassing graphViewChanged); sizes are persisted
            // from geometry events while moves persist via GraphSync's moved-elements path.
            _resizeHandles = new ResizeHandles(this, new Vector2(80f, 40f));
            _resizeHandles.SetVisible(false);
            Add(_resizeHandles);

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
            // The colour's alpha is honoured as-is, so notes can be made semi-transparent
            // (context menu Opacity presets or the inspector colour field).
            style.backgroundColor = Note.color;
            ApplyBorder();
        }

        void ApplyBorder()
        {
            var c = Note.color;
            var borderColor = selected
                ? DaerDColors.Selected
                : new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f, Mathf.Clamp01(c.a + 0.25f));
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
            _resizeHandles.SetVisible(true);
            ApplyBorder();
        }

        public override void OnUnselected()
        {
            base.OnUnselected();
            _resizeHandles.SetVisible(false);
            ApplyBorder();
        }

        /// <summary>
        /// Swaps the text for a multiline field. Enter inserts a newline; focus-out commits,
        /// Escape also commits — losing typed text on a stray Escape is a worse UX than the
        /// (undoable via Ctrl+Z) commit it replaces.
        /// </summary>
        public void BeginEdit()
        {
            if (_editField != null) return;

            var field = new TextField { value = Note.text ?? string.Empty, multiline = true };
            field.AddToClassList("dd-note__edit");
            field.style.fontSize = Note.fontSize;
            // Make the inner text-input element wrap long lines instead of clipping them, and
            // grow to fill the field. TextField doesn't propagate white-space to the inner
            // element by default in 2022.3, so we set it here.
            var input = field.Q<VisualElement>("unity-text-input");
            if (input != null)
            {
                input.style.whiteSpace = WhiteSpace.Normal;
                input.style.flexGrow = 1;
            }
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
                    // Escape exits the edit mode but keeps what was typed — same outcome as
                    // clicking outside the note. The previous "discard on Escape" behaviour
                    // ate user input on a stray keypress and is gone.
                    Finish(true);
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
