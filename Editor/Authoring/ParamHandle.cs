using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    // ---- parameters and conditions -----------------------------------------------

    /// <summary>A declared (or referenced) controller parameter. Handles are how recipes
    /// name parameters everywhere — conditions, drivers, per-state parameter slots.</summary>
    public abstract class ParamHandle
    {
        internal readonly ControllerBuilder Root;
        public string Name { get; }

        internal ParamHandle(ControllerBuilder root, string name)
        {
            Root = root;
            Name = name ?? string.Empty;
        }

        internal Condition Make(AnimatorConditionMode mode, float threshold, string call) =>
            new Condition
            {
                Mode = mode,
                Parameter = Name,
                Threshold = threshold,
                Source = Root.Script != null ? Root.Script.NameArg(this) + "." + call : null,
            };
    }
}
