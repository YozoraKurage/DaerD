using System;
using UnityEditor.Experimental.GraphView;
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
