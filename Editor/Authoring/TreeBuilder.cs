using System;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Authoring
{
    // ---- blend trees ------------------------------------------------------------------

    /// <summary>An embedded blend tree (create with <see cref="ControllerBuilder.NewBlendTree"/>,
    /// attach with WithAnimation) — AAC's NewBlendTree flow, including chained children.</summary>
    public sealed class TreeBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.Tree Tree;
        TreeChildBuilder _lastChild;

        /// <summary>Slot options of the most recently added child (time scale, mirror…),
        /// for the rare settings the WithAnimation signatures don't carry.</summary>
        public TreeChildBuilder LastChild => _lastChild;

        internal TreeBuilder(ControllerBuilder root, ControllerIR.Tree tree)
        {
            _root = root;
            Tree = tree;
        }

        public TreeBuilder Simple1D(FloatParam parameter)
        {
            Tree.type = BlendTreeType.Simple1D;
            Tree.blendParameter = parameter.Name;
            _root.Script?.Call(this, $"Simple1D({_root.Script.NameArg(parameter)})");
            return this;
        }

        public TreeBuilder SimpleDirectional2D(FloatParam parameterX, FloatParam parameterY) =>
            TwoD(BlendTreeType.SimpleDirectional2D, "SimpleDirectional2D", parameterX, parameterY);

        public TreeBuilder FreeformDirectional2D(FloatParam parameterX, FloatParam parameterY) =>
            TwoD(BlendTreeType.FreeformDirectional2D, "FreeformDirectional2D", parameterX, parameterY);

        public TreeBuilder FreeformCartesian2D(FloatParam parameterX, FloatParam parameterY) =>
            TwoD(BlendTreeType.FreeformCartesian2D, "FreeformCartesian2D", parameterX, parameterY);

        TreeBuilder TwoD(BlendTreeType type, string method, FloatParam x, FloatParam y)
        {
            Tree.type = type;
            Tree.blendParameter = x.Name;
            Tree.blendParameterY = y.Name;
            _root.Script?.Call(this,
                $"{method}({_root.Script.NameArg(x)}, {_root.Script.NameArg(y)})");
            return this;
        }

        public TreeBuilder Direct()
        {
            Tree.type = BlendTreeType.Direct;
            _root.Script?.Call(this, "Direct()");
            return this;
        }

        public TreeBuilder AutoThresholds(bool on)
        {
            Tree.useAutomaticThresholds = on;
            _root.Script?.Call(this, $"AutoThresholds({RecipeScript.B(on)})");
            return this;
        }

        public TreeBuilder ThresholdRange(float min, float max)
        {
            Tree.minThreshold = min;
            Tree.maxThreshold = max;
            _root.Script?.Call(this, $"ThresholdRange({RecipeScript.F(min)}, {RecipeScript.F(max)})");
            return this;
        }

        public TreeBuilder NormalizedBlendValues(bool on = true)
        {
            Tree.normalizedBlendValues = on;
            _root.Script?.Call(this, on ? "NormalizedBlendValues()" : "NormalizedBlendValues(false)");
            return this;
        }

        // ---- children (AAC WithAnimation overloads) -----------------------------------

        public TreeBuilder WithAnimation(Motion motion) =>
            Child(motion, null, $"WithAnimation({_root.Script?.AssetRef(motion)})", null);

        /// <summary>1D child at an explicit threshold.</summary>
        public TreeBuilder WithAnimation(Motion motion, float threshold) =>
            Child(motion, null,
                $"WithAnimation({_root.Script?.AssetRef(motion)}, {RecipeScript.F(threshold)})",
                child => child.threshold = threshold);

        /// <summary>2D child at a blend-space position.</summary>
        public TreeBuilder WithAnimation(Motion motion, float x, float y) =>
            Child(motion, null,
                $"WithAnimation({_root.Script?.AssetRef(motion)}, {RecipeScript.F(x)}, {RecipeScript.F(y)})",
                child => child.position = new Vector2(x, y));

        /// <summary>Direct child weighted by a Float parameter.</summary>
        public TreeBuilder WithAnimation(Motion motion, FloatParam directParameter) =>
            Child(motion, null,
                $"WithAnimation({_root.Script?.AssetRef(motion)}, {_root.Script?.NameArg(directParameter)})",
                child => child.directParameter = directParameter.Name);

        public TreeBuilder WithAnimation(TreeBuilder blendTree) =>
            Child(null, blendTree.Tree, $"WithAnimation({_root.Script?.NameArg(blendTree)})", null);

        public TreeBuilder WithAnimation(TreeBuilder blendTree, float threshold) =>
            Child(null, blendTree.Tree,
                $"WithAnimation({_root.Script?.NameArg(blendTree)}, {RecipeScript.F(threshold)})",
                child => child.threshold = threshold);

        public TreeBuilder WithAnimation(TreeBuilder blendTree, float x, float y) =>
            Child(null, blendTree.Tree,
                $"WithAnimation({_root.Script?.NameArg(blendTree)}, {RecipeScript.F(x)}, {RecipeScript.F(y)})",
                child => child.position = new Vector2(x, y));

        public TreeBuilder WithAnimation(TreeBuilder blendTree, FloatParam directParameter) =>
            Child(null, blendTree.Tree,
                $"WithAnimation({_root.Script?.NameArg(blendTree)}, {_root.Script?.NameArg(directParameter)})",
                child => child.directParameter = directParameter.Name);

        TreeBuilder Child(Motion motion, ControllerIR.Tree nested, string call,
            Action<ControllerIR.TreeChild> configure)
        {
            var child = new ControllerIR.TreeChild { motionAsset = motion, tree = nested };
            configure?.Invoke(child);
            Tree.children.Add(child);
            _lastChild = new TreeChildBuilder(_root, child);
            if (_root.Script != null)
            {
                _root.Script.Call(this, call);
                _root.Script.RegisterAlias(_lastChild, _root.Script.NameArg(this) + ".LastChild");
            }
            return this;
        }
    }
}
