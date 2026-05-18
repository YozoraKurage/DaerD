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

        readonly Label _motionLabel;
        static readonly Color DefaultStateColor = new Color(0.78f, 0.45f, 0.13f);
        static readonly Color CurrentStateColor = new Color(0.20f, 0.55f, 0.25f);

        public StateNode(AnimatorState state)
        {
            State = state;
            AddToClassList("state-node");
            AddInputPort();
            AddOutputPort();

            _motionLabel = new Label { pickingMode = PickingMode.Ignore };
            _motionLabel.AddToClassList("state-node__motion");
            mainContainer.Add(_motionLabel);

            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Deletable
                          | Capabilities.Copiable | Capabilities.Snappable;

            RefreshLabels();
            RefreshExpandedState();
            RefreshPorts();
        }

        public void RefreshLabels()
        {
            title = State.name;
            _motionLabel.text = DescribeMotion(State.motion);
        }

        public void SetIsDefault(bool isDefault)
        {
            if (isDefault)
                titleContainer.style.backgroundColor = DefaultStateColor;
            else
                titleContainer.style.backgroundColor = StyleKeyword.Null;
        }

        /// <summary>Highlights the node when it is the live state during play mode.</summary>
        public void SetIsCurrent(bool isCurrent)
        {
            style.backgroundColor = isCurrent ? CurrentStateColor : (StyleColor)StyleKeyword.Null;
        }

        /// <summary>Outlines the node when a find-usages query matches it.</summary>
        public void SetHighlight(bool on)
        {
            StyleColor color = on ? new Color(0.96f, 0.84f, 0.22f) : (StyleColor)StyleKeyword.Null;
            StyleFloat width = on ? 2f : (StyleFloat)StyleKeyword.Null;
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
