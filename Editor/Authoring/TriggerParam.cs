using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    public sealed class TriggerParam : ParamHandle
    {
        internal TriggerParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsSet() => Make(AnimatorConditionMode.If, 0f, "IsSet()");
    }
}
