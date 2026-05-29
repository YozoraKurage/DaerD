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

        bool _highlighted;
        bool _dropTarget;

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

            RefreshLabels();
            RefreshExpandedState();
            RefreshPorts();
        }

        public void RefreshLabels()
        {
            title = State.name;
            _nameLabel.text = State.name;
            _motionLabel.text = DescribeMotion(State.motion);
            tooltip = State.motion is BlendTree
                ? "Double-click to open the blend tree view"
                : string.Empty;
        }

        public void SetIsDefault(bool isDefault)
        {
            _nameLabel.style.backgroundColor =
                isDefault ? DefaultStateColor : (StyleColor)StyleKeyword.Null;
        }

        /// <summary>Highlights the node when it is the live state during play mode.</summary>
        public void SetIsCurrent(bool isCurrent)
        {
            style.backgroundColor = isCurrent ? CurrentStateColor : (StyleColor)StyleKeyword.Null;
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
