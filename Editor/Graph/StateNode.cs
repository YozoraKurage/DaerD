using System;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    class StateNode : GraphNodeBase
    {
        public AnimatorState State { get; }
        public override object Model => State;

        readonly Label _nameLabel;
        readonly Label _motionLabel;
        readonly Action<AnimatorState> _onOpenBlendTree;
        static readonly Color DefaultStateColor = new Color(0.78f, 0.45f, 0.13f);
        static readonly Color CurrentStateColor = new Color(0.20f, 0.55f, 0.25f);
        static readonly Color HighlightBorderColor = new Color(0.96f, 0.84f, 0.22f);
        static readonly Color DropTargetColor = new Color(0.42f, 0.82f, 0.46f);
        // Opaque #393939. The stock node-border / port columns are translucent, so the node body
        // showed the graph background through it and overlapping states bled through. Painting the
        // rounded node-border (not the square root) plus the port columns opaque fixes both.
        static readonly Color BodyColor = new Color(0.224f, 0.224f, 0.224f);

        bool _highlighted;
        bool _dropTarget;
        TextField _renameField;
        readonly VisualElement _nodeBorder;
        readonly Label _wdBadge;
        readonly Label _behaviourBadge;

        public StateNode(AnimatorState state, Action<AnimatorState> onOpenBlendTree = null)
        {
            State = state;
            _onOpenBlendTree = onOpenBlendTree;
            AddToClassList("state-node");
            AddToClassList("compact-node");
            AddInputPort();
            AddOutputPort();

            // The title bar is dropped (see DaerD.uss); the name and motion show in a
            // text column between the input port (left) and output port (right). Every
            // state node uses the same fixed width.
            Input.tooltip = "Incoming transitions";
            Output.tooltip = "Drag from here to create a transition";

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("compact-node__text");

            _nameLabel = new Label { pickingMode = PickingMode.Ignore };
            _nameLabel.AddToClassList("compact-node__name");
            _motionLabel = new Label { pickingMode = PickingMode.Ignore };
            _motionLabel.AddToClassList("compact-node__motion");
            text.Add(_nameLabel);
            text.Add(_motionLabel);
            topContainer.Insert(1, text);

            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Deletable
                          | Capabilities.Copiable | Capabilities.Snappable;

            // Double-click on a state whose motion is a BlendTree drills into the tree view,
            // matching the affordance the sub-state machine node already offers.
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2 && evt.button == 0 && State?.motion is BlendTree)
                {
                    _onOpenBlendTree?.Invoke(State);
                    evt.StopPropagation();
                }
            });

            _nodeBorder = this.Q("node-border");
            ApplyBodyColor(BodyColor);

            // Corner badges: WD (Write Defaults on) and B (has StateMachineBehaviours).
            var badges = new VisualElement { pickingMode = PickingMode.Ignore };
            badges.AddToClassList("state-node__badges");
            _wdBadge = new Label("WD") { pickingMode = PickingMode.Ignore, tooltip = "Write Defaults is ON" };
            _wdBadge.AddToClassList("state-node__badge");
            _wdBadge.AddToClassList("state-node__badge--wd");
            _behaviourBadge = new Label("B") { pickingMode = PickingMode.Ignore, tooltip = "Has StateMachineBehaviours" };
            _behaviourBadge.AddToClassList("state-node__badge");
            _behaviourBadge.AddToClassList("state-node__badge--b");
            badges.Add(_wdBadge);
            badges.Add(_behaviourBadge);
            Add(badges);

            RefreshLabels();
            RefreshExpandedState();
            RefreshPorts();
        }

        /// <summary>Paints the rounded body and the left/right port columns one opaque colour.</summary>
        void ApplyBodyColor(Color color)
        {
            if (_nodeBorder != null) _nodeBorder.style.backgroundColor = color;
            inputContainer.style.backgroundColor = color;
            outputContainer.style.backgroundColor = color;
        }

        // Suppress the stock node menu (its "Disconnect all" lands at the top, easy to mis-click);
        // AnimatorGraphView builds the full context menu and re-adds Disconnect at the bottom.
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) { }

        public void RefreshLabels()
        {
            title = State.name;
            _nameLabel.text = State.name;
            _motionLabel.text = DescribeMotion(State.motion);
            tooltip = State.motion is BlendTree
                ? "Double-click to open the blend tree view"
                : string.Empty;

            bool showBadges = DaerDSettings.ShowStateBadges;
            var behaviours = State.behaviours;
            _wdBadge.style.display = showBadges && State.writeDefaultValues
                ? DisplayStyle.Flex : DisplayStyle.None;
            _behaviourBadge.style.display = showBadges && behaviours != null && behaviours.Length > 0
                ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Swaps the name (or motion) label for a one-shot text field so it can be renamed in place.
        /// Commits on Enter or focus-out, cancels on Escape. <paramref name="onCommit"/> receives the
        /// trimmed text and is responsible for the actual rename + undo.
        /// </summary>
        public void BeginInlineEdit(string initial, Action<string> onCommit, bool motionLabel)
        {
            if (_renameField != null) return;   // already editing
            var label = motionLabel ? _motionLabel : _nameLabel;
            var parent = label.parent;
            if (parent == null) return;

            var field = new TextField { value = initial ?? string.Empty };
            field.AddToClassList("compact-node__rename");
            _renameField = field;

            int index = parent.IndexOf(label);
            label.style.display = DisplayStyle.None;
            parent.Insert(index, field);

            bool finished = false;
            void Finish(bool commit)
            {
                if (finished) return;
                finished = true;
                string value = field.value;
                _renameField = null;
                field.RemoveFromHierarchy();
                label.style.display = DisplayStyle.Flex;
                if (commit) onCommit?.Invoke(value?.Trim());
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
            // Focus-out commits, EXCEPT when the field was detached by a graph rebuild (panel == null):
            // that is a teardown, not a user confirmation, so cancel instead of writing a stray value.
            field.RegisterCallback<FocusOutEvent>(_ => Finish(field.panel != null));
            // Keep clicks inside the field from reaching the node (e.g. its double-click handler).
            field.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());

            // Focus once the field is laid out in the panel.
            field.schedule.Execute(() =>
            {
                field.Focus();
                field.SelectAll();
            }).ExecuteLater(0);
        }

        public void SetIsDefault(bool isDefault)
        {
            _nameLabel.style.backgroundColor =
                isDefault ? DefaultStateColor : (StyleColor)StyleKeyword.Null;
        }

        /// <summary>Highlights the node when it is the live state during play mode.</summary>
        public void SetIsCurrent(bool isCurrent)
        {
            ApplyBodyColor(isCurrent ? CurrentStateColor : BodyColor);
        }

        /// <summary>Outlines the node when a find-usages query matches it.</summary>
        public void SetHighlight(bool on)
        {
            _highlighted = on;
            ApplyBorder();
        }

        /// <summary>Outlines the node while an AnimationClip is being dragged over it.</summary>
        public void SetDropTarget(bool on)
        {
            _dropTarget = on;
            ApplyBorder();
        }

        void ApplyBorder()
        {
            StyleColor color;
            StyleFloat width;
            if (_dropTarget)
            {
                color = DropTargetColor;
                width = 2.5f;
            }
            else if (_highlighted)
            {
                color = HighlightBorderColor;
                width = 2f;
            }
            else
            {
                color = StyleKeyword.Null;
                width = StyleKeyword.Null;
            }
            style.borderTopColor = color;
            style.borderBottomColor = color;
            style.borderLeftColor = color;
            style.borderRightColor = color;
            style.borderTopWidth = width;
            style.borderBottomWidth = width;
            style.borderLeftWidth = width;
            style.borderRightWidth = width;
        }

        static string DescribeMotion(Motion motion)
        {
            if (motion == null) return "(no motion)";
            if (motion is BlendTree bt) return "Blend Tree: " + bt.name;
            return motion.name;
        }
    }
}
