using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>One transition condition, produced by a parameter handle
    /// (go.IsTrue(), blend.IsGreaterThan(0.5f)) and consumed by When / And.</summary>
    public sealed class Condition
    {
        internal AnimatorConditionMode Mode;
        internal string Parameter;
        internal float Threshold;
        internal string Source;
    }
}
