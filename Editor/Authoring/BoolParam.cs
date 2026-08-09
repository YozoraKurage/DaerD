using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    public sealed class BoolParam : ParamHandle
    {
        internal BoolParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsTrue() => Make(AnimatorConditionMode.If, 0f, "IsTrue()");

        public Condition IsFalse() => Make(AnimatorConditionMode.IfNot, 0f, "IsFalse()");

        public Condition IsEqualTo(bool value) =>
            Make(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f,
                $"IsEqualTo({RecipeScript.B(value)})");
    }
}
