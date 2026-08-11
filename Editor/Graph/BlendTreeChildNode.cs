using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>Visual node representing an AnimationClip (or other Motion) child of a BlendTree.</summary>
    class BlendTreeChildNode : GraphNodeBase
    {
        public ChildMotion Child { get; }
        public BlendTree Parent { get; }
        public override object Model => Child.motion;


        public BlendTreeChildNode(BlendTree parent, ChildMotion child, System.Action onClick, System.Action onDoubleClick)
        {
            Parent = parent;
            Child = child;
            AddToClassList("blendtree-child-node");
            AddInputPort();
            // No output port: leaf motions don't have further children.
            Input.pickingMode = PickingMode.Ignore;

            capabilities = Capabilities.Selectable;

            var motion = child.motion;
            title = motion != null ? motion.name : "(empty)";
            titleContainer.style.backgroundColor = motion != null ? DaerDColors.BlendTreeChildHeader : DaerDColors.BlendTreeEmptyHeader;

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 4;
            body.style.paddingBottom = 4;

            body.Add(new Label("Type: " + DescribeType(motion)) { pickingMode = PickingMode.Ignore });
            body.Add(new Label("Slot: " + DescribeSlot(parent, child)) { pickingMode = PickingMode.Ignore });
            body.Add(new Label("Time Scale: " + child.timeScale.ToString("0.###")) { pickingMode = PickingMode.Ignore });

            extensionContainer.Add(body);
            RefreshExpandedState();
            RefreshPorts();

            tooltip = motion is AnimationClip
                ? "Click to ping in Project · Double-click to open the clip"
                : "Click to ping in Project";

            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                if (evt.clickCount == 2)
                {
                    onDoubleClick?.Invoke();
                    evt.StopPropagation();
                }
                else if (evt.clickCount == 1)
                {
                    onClick?.Invoke();
                }
            });
        }

        static string DescribeType(Motion motion)
        {
            if (motion == null) return "(empty)";
            if (motion is BlendTree) return "Blend Tree";
            if (motion is AnimationClip) return "Animation Clip";
            return motion.GetType().Name;
        }

        static string DescribeSlot(BlendTree parent, ChildMotion child)
        {
            if (parent == null) return string.Empty;
            switch (parent.blendType)
            {
                case BlendTreeType.Simple1D:
                    return "threshold " + child.threshold.ToString("0.###");
                case BlendTreeType.Direct:
                    return "param " + (string.IsNullOrEmpty(child.directBlendParameter) ? "(none)" : child.directBlendParameter);
                default:
                    return "pos (" + child.position.x.ToString("0.##") + ", " + child.position.y.ToString("0.##") + ")";
            }
        }
    }
}
