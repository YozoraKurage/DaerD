using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    public sealed class FloatParam : ParamHandle
    {
        internal FloatParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsGreaterThan(float value) =>
            Make(AnimatorConditionMode.Greater, value, $"IsGreaterThan({RecipeScript.F(value)})");

        public Condition IsLessThan(float value) =>
            Make(AnimatorConditionMode.Less, value, $"IsLessThan({RecipeScript.F(value)})");
    }
}
