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

        bool _playing;
        bool _next;

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
                if (evt.clickCount == 2 && evt.button == 0)
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

        /// <summary>Lit when the state actually playing is one this machine contains — from out
        /// here that state has no node of its own, and the box standing in for it is the only
        /// thing that can say the layer is in there.</summary>
        public override void SetPlayback(bool playing, bool next, float progress)
        {
            // Every tick while playing; only touch the style when the answer changed.
            if (playing == _playing && next == _next) return;
            _playing = playing;
            _next = next;
            titleContainer.style.backgroundColor =
                playing ? PlayingColor : next ? PlayingNextColor : HeaderColor;
        }

        public void RefreshLabels() => title = StateMachine.name + "  ▸";
    }
}
