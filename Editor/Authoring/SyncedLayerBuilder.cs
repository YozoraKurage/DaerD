using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>Synced layer: mirrors a source layer's states, overriding motions only.</summary>
    public sealed class SyncedLayerBuilder
    {
        readonly ControllerBuilder _root;
        readonly ControllerIR.Layer _layer;

        internal SyncedLayerBuilder(ControllerBuilder root, ControllerIR.Layer layer, string sourceLayer)
        {
            _root = root;
            _layer = layer;
            // Resolved lazily so declaration order doesn't matter; a bad name surfaces as -1
            // plus a note at bake time.
            root.PostBakeSyncs.Add(() =>
            {
                int index = root.IndexOfLayer(sourceLayer);
                if (index < 0)
                    root.Notes.Add(L.Tr("Synced layer '{0}': source layer '{1}' is not declared in this recipe.",
                        layer.name, sourceLayer));
                layer.syncedLayerIndex = index;
            });
        }

        public SyncedLayerBuilder WithWeight(float weight)
        {
            _layer.defaultWeight = weight;
            _root.Script?.Call(this, $"WithWeight({RecipeScript.F(weight)})");
            return this;
        }

        public SyncedLayerBuilder Additive()
        {
            _layer.blending = AnimatorLayerBlendingMode.Additive;
            _root.Script?.Call(this, "Additive()");
            return this;
        }

        public SyncedLayerBuilder WithIkPass(bool on = true)
        {
            _layer.ikPass = on;
            _root.Script?.Call(this, on ? "WithIkPass()" : "WithIkPass(false)");
            return this;
        }

        public SyncedLayerBuilder WithAvatarMask(AvatarMask mask)
        {
            _layer.mask = mask;
            _root.Script?.Call(this, $"WithAvatarMask({_root.Script.AssetRef(mask)})");
            return this;
        }

        public SyncedLayerBuilder AffectsTiming(bool on = true)
        {
            _layer.syncedLayerAffectsTiming = on;
            _root.Script?.Call(this, on ? "AffectsTiming()" : "AffectsTiming(false)");
            return this;
        }

        /// <summary>Overrides the motion of a source-layer state ("Sub/State" path form).</summary>
        public SyncedLayerBuilder Override(string statePath, Motion motion)
        {
            _layer.syncedMotions.Add(new ControllerIR.MotionOverride
            { statePath = statePath, motion = motion });
            _root.Script?.Call(this,
                $"Override({RecipeScript.S(statePath)}, {_root.Script.AssetRef(motion)})");
            return this;
        }

        /// <summary>
        /// Adds a behaviour this layer runs on a source-layer state instead of the source's
        /// own — the behaviour half of a synced layer's overrides. Call once per behaviour;
        /// they stack on that state in call order.
        /// </summary>
        public SyncedLayerBuilder OverrideBehaviourJson(string statePath, string typeName,
            string json, string instanceName = null)
        {
            var entry = _layer.syncedBehaviours.Find(o => o.statePath == statePath);
            if (entry == null)
            {
                entry = new ControllerIR.BehaviourOverride { statePath = statePath };
                _layer.syncedBehaviours.Add(entry);
            }
            entry.behaviours.Add(new ControllerIR.Behaviour
            {
                typeName = typeName,
                json = json,
                instanceName = instanceName ?? string.Empty,
            });
            _root.Script?.Call(this, instanceName == null
                ? $"OverrideBehaviourJson({RecipeScript.S(statePath)}, {RecipeScript.S(typeName)}, {RecipeScript.S(json)})"
                : $"OverrideBehaviourJson({RecipeScript.S(statePath)}, {RecipeScript.S(typeName)}, {RecipeScript.S(json)}, {RecipeScript.S(instanceName)})");
            return this;
        }
    }
}
