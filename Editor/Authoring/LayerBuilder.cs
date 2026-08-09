using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    public sealed class LayerBuilder : MachineScope
    {
        readonly ControllerIR.Layer _layer;

        internal LayerBuilder(ControllerBuilder root, ControllerIR.Layer layer)
            : base(root, layer.machine, string.Empty)
        {
            _layer = layer;
        }

        public LayerBuilder WithWeight(float weight)
        {
            _layer.defaultWeight = weight;
            Root.Script?.Call(this, $"WithWeight({RecipeScript.F(weight)})");
            return this;
        }

        public LayerBuilder Additive()
        {
            _layer.blending = AnimatorLayerBlendingMode.Additive;
            Root.Script?.Call(this, "Additive()");
            return this;
        }

        public LayerBuilder WithIkPass(bool on = true)
        {
            _layer.ikPass = on;
            Root.Script?.Call(this, on ? "WithIkPass()" : "WithIkPass(false)");
            return this;
        }

        public LayerBuilder WithAvatarMask(AvatarMask mask)
        {
            _layer.mask = mask;
            Root.Script?.Call(this, $"WithAvatarMask({Root.Script.AssetRef(mask)})");
            return this;
        }
    }
}
