using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>Base for every node drawn in the animator graph.</summary>
    abstract class GraphNodeBase : Node
    {
        public Port Input { get; private set; }
        public Port Output { get; private set; }

        /// <summary>The underlying animator object (AnimatorState, AnimatorStateMachine, ...).</summary>
        public abstract object Model { get; }

        /// <summary>Play-mode colours, shared so that a state and the sub-state machine standing
        /// in for one of its states light up the same way.</summary>
        protected static readonly Color PlayingColor = new Color(0.20f, 0.55f, 0.25f);

        /// <summary>Where a running transition is headed — the same green, held back, so the two
        /// ends of a crossfade read as one thing happening rather than two states at once.</summary>
        protected static readonly Color PlayingNextColor = new Color(0.16f, 0.36f, 0.20f);

        /// <summary>What this node is doing in the running Animator. Nodes that have nothing to
        /// show for it (Entry, Exit, Any State) keep the default no-op.</summary>
        public virtual void SetPlayback(bool playing, bool next, float progress) { }

        /// <summary>
        /// The transition end a graph node stands for — the model behind the node, as the
        /// transition commands see it. A null or unrecognised node becomes
        /// <see cref="TransitionEnd.None"/>, which nothing may connect to. This is the single
        /// node-to-end conversion; GraphSync and the drag connect rule both go through it.
        /// </summary>
        public static TransitionEnd EndOf(GraphNodeBase node)
        {
            switch (node)
            {
                case StateNode sn:
                    return TransitionEnd.Of(sn.State);
                case SubStateMachineNode mn:
                    return TransitionEnd.Of(mn.StateMachine);
                case SpecialNode spn:
                    switch (spn.Kind)
                    {
                        case SpecialNodeKind.Entry: return TransitionEnd.Entry;
                        case SpecialNodeKind.Exit: return TransitionEnd.Exit;
                        default: return TransitionEnd.AnyState;
                    }
                default:
                    return TransitionEnd.None;
            }
        }

        // One shared listener for every port. It reproduces Unity's stock port-to-port drop
        // behaviour and additionally completes a drop that lands anywhere on a destination
        // node's body — not only on its small input port. See NodeBodyEdgeConnectorListener.
        static readonly IEdgeConnectorListener s_connectorListener = new NodeBodyEdgeConnectorListener();

        protected Port AddInputPort()
        {
            Input = CreatePort(Direction.Input);
            Input.portName = string.Empty;
            inputContainer.Add(Input);
            return Input;
        }

        protected Port AddOutputPort()
        {
            Output = CreatePort(Direction.Output);
            Output.portName = string.Empty;
            outputContainer.Add(Output);
            return Output;
        }

        // Mirrors Port.Create&lt;Edge&gt;, but attaches our own connector listener instead of the
        // internal default one. Port's constructor is protected, so we reach it through a tiny
        // subclass. The drag candidate stays a plain Edge (the rebuild turns the committed
        // transition into a TransitionEdge afterwards), exactly as before.
        static Port CreatePort(Direction direction)
        {
            var port = new DaerDPort(Orientation.Horizontal, direction, Port.Capacity.Multi, typeof(bool));
            port.AddManipulator(new EdgeConnector<Edge>(s_connectorListener));
            return port;
        }

        sealed class DaerDPort : Port
        {
            public DaerDPort(Orientation orientation, Direction direction, Capacity capacity, Type type)
                : base(orientation, direction, capacity, type) { }
        }
    }
}
