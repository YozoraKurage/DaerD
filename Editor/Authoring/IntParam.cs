using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    public sealed class IntParam : ParamHandle
    {
        internal IntParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsGreaterThan(int value) =>
            Make(AnimatorConditionMode.Greater, value, $"IsGreaterThan({value})");

        public Condition IsLessThan(int value) =>
            Make(AnimatorConditionMode.Less, value, $"IsLessThan({value})");

        public Condition IsEqualTo(int value) =>
            Make(AnimatorConditionMode.Equals, value, $"IsEqualTo({value})");

        public Condition IsNotEqualTo(int value) =>
            Make(AnimatorConditionMode.NotEqual, value, $"IsNotEqualTo({value})");
    }
}
