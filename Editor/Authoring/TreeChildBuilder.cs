using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>Slot options of one blend-tree child.</summary>
    public sealed class TreeChildBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.TreeChild Child;

        internal TreeChildBuilder(ControllerBuilder root, ControllerIR.TreeChild child)
        {
            _root = root;
            Child = child;
        }

        public TreeChildBuilder Threshold(float threshold)
        {
            Child.threshold = threshold;
            _root.Script?.Call(this, $"Threshold({RecipeScript.F(threshold)})");
            return this;
        }

        public TreeChildBuilder Position(float x, float y)
        {
            Child.position = new Vector2(x, y);
            _root.Script?.Call(this, $"Position({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        public TreeChildBuilder TimeScale(float scale)
        {
            Child.timeScale = scale;
            _root.Script?.Call(this, $"TimeScale({RecipeScript.F(scale)})");
            return this;
        }

        public TreeChildBuilder CycleOffset(float offset)
        {
            Child.cycleOffset = offset;
            _root.Script?.Call(this, $"CycleOffset({RecipeScript.F(offset)})");
            return this;
        }

        public TreeChildBuilder Mirror(bool on = true)
        {
            Child.mirror = on;
            _root.Script?.Call(this, on ? "Mirror()" : "Mirror(false)");
            return this;
        }
    }
}
