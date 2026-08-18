using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Authoring
{
    public sealed class MachineBuilder : MachineScope
    {
        readonly ControllerIR.ChildMachine _child;

        internal MachineBuilder(ControllerBuilder root, ControllerIR.ChildMachine child, string prefix)
            : base(root, child.machine, prefix)
        {
            _child = child;
        }

        public MachineBuilder At(float x, float y)
        {
            _child.position = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"At({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        /// <summary>Transition drawn from this machine's node in the parent view.</summary>
        public TransitionBuilder TransitionsTo(StateBuilder destination) =>
            Source("TransitionsTo", destination.Path, ControllerIR.Transition.Target.State, destination);

        public TransitionBuilder TransitionsTo(MachineBuilder destination) =>
            Source("TransitionsTo", destination.Prefix, ControllerIR.Transition.Target.Machine, destination);

        public TransitionBuilder Exits() =>
            Source("Exits", string.Empty, ControllerIR.Transition.Target.Exit, null);

        TransitionBuilder Source(string method, string path, ControllerIR.Transition.Target target,
            object destination)
        {
            var transition = NewTransition(target, path, isStateTransition: false);
            _child.transitions.Add(transition);
            var builder = new TransitionBuilder(Root, transition);
            Root.Script?.Declare(builder, "t", this, destination == null
                ? $"{method}()"
                : $"{method}({Root.Script.NameArg(destination)})");
            return builder;
        }
    }
}
