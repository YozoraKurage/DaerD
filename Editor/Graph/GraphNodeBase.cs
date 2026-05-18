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

        protected Port AddInputPort()
        {
            Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
            Input.portName = string.Empty;
            inputContainer.Add(Input);
            return Input;
        }

        protected Port AddOutputPort()
        {
            Output = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
            Output.portName = string.Empty;
            outputContainer.Add(Output);
            return Output;
        }
    }
}
