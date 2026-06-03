using System;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    class SubStateMachineNode : GraphNodeBase
    {
        public AnimatorStateMachine StateMachine { get; }
        public override object Model => StateMachine;

        static readonly Color HeaderColor = new Color(0.20f, 0.34f, 0.46f);

        public SubStateMachineNode(AnimatorStateMachine stateMachine, Action onOpen)
        {
            StateMachine = stateMachine;
            AddToClassList("ssm-node");
            AddInputPort();
            AddOutputPort();

            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Deletable
                          | Capabilities.Copiable | Capabilities.Snappable;
            titleContainer.style.backgroundColor = HeaderColor;

            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2)
                {
                    onOpen?.Invoke();
                    evt.StopPropagation();
                }
            });

            RefreshLabels();
            RefreshExpandedState();
            RefreshPorts();
        }

        // Suppress the stock node menu; AnimatorGraphView builds the full context menu itself.
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) { }

        public void RefreshLabels() => title = StateMachine.name + "  ▸";
    }
}
