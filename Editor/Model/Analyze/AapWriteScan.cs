using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// Which of a controller's own parameters are written by animation instead of by the
    /// avatar: the AAP idiom (animator animated parameter), where a clip animates a Float
    /// on the Animator itself and a Write-Defaults-ON Direct blend tree plays that clip
    /// every frame. Every DBT gadget output is one of these, and so is any hand-authored
    /// AAP clip — the scan looks at the clips, so it finds both.
    ///
    /// This matters because a VRC Parameter Driver cannot see an AAP value: the animation
    /// system holds it, so a driver Copy reads whatever the animator's own field happens to
    /// be and a driver write is overwritten by the tree on the same frame. DaerD's async
    /// sync sends through driver Copies, which is why a multiplexed AAP never reaches
    /// remotes — <see cref="ControllerAnalyzer"/> and the sync wizard both warn from here.
    ///
    /// The walk only visits states the layer can actually enter
    /// (<see cref="ControllerReachability"/>). A clip parked on a state nothing leads to is
    /// not something animation writes, and counting it produced warnings with no way to
    /// silence them. What is still counted without proof is the layer's weight: nothing here
    /// asks whether a weight-0 layer is ever raised, because a Layer Control behaviour in
    /// another controller can raise it and this scan would never see that.
    /// </summary>
    static class AapWriteScan
    {
        /// <summary>The AAPs one layer writes. Layers are kept apart because they do not add
        /// up the way a Direct tree's children do — see
        /// <see cref="ControllerAnalyzer"/>'s layer-conflict check.</summary>
        internal class LayerWrites
        {
            public int layerIndex;
            public string layerName;
            public AnimatorLayerBlendingMode blendingMode;
            public float defaultWeight;
            public readonly HashSet<string> parameters = new HashSet<string>();
        }

        /// <summary>Declared parameters of <paramref name="controller"/> that some clip on a
        /// reachable state animates on the Animator itself.</summary>
        public static HashSet<string> CollectWrittenParameters(AnimatorController controller)
        {
            var written = new HashSet<string>();
            foreach (var layer in CollectByLayer(controller))
                written.UnionWith(layer.parameters);
            return written;
        }

        /// <summary>The same scan, kept split by layer. Layers that write nothing are left
        /// out; the entries come back in layer order.</summary>
        public static List<LayerWrites> CollectByLayer(AnimatorController controller)
        {
            var result = new List<LayerWrites>();
            if (controller == null) return result;

            // Restricted to names the controller declares. Humanoid muscle curves bind to
            // typeof(Animator) with an empty path exactly like an AAP does, so without this
            // filter every locomotion clip would look like it wrote dozens of parameters.
            var declared = new HashSet<string>();
            foreach (var parameter in controller.parameters) declared.Add(parameter.name);
            if (declared.Count == 0) return result;

            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var root = ControllerReachability.PlayedMachine(controller, i);
                if (root == null) continue;
                bool synced = layers[i].syncedLayerIndex >= 0;
                var reachable = ControllerReachability.ReachableStates(root);

                var writes = new LayerWrites
                {
                    layerIndex = i,
                    layerName = layers[i].name,
                    blendingMode = layers[i].blendingMode,
                    defaultWeight = layers[i].defaultWeight,
                };
                var visited = new HashSet<Motion>();
                foreach (var sm in root.SelfAndDescendants())
                    foreach (var cs in sm.states)
                    {
                        var state = cs.state;
                        if (state == null) continue;
                        if (!reachable.Contains(state)) continue;
                        // A synced layer replays the source layer's states; each may carry an
                        // override motion, and falls back to the source's when it does not.
                        var motion = synced ? layers[i].GetOverrideMotion(state) ?? state.motion : state.motion;
                        Collect(motion, declared, writes.parameters, visited);
                    }
                if (writes.parameters.Count > 0) result.Add(writes);
            }
            return result;
        }

        static void Collect(Motion motion, HashSet<string> declared, HashSet<string> into,
            HashSet<Motion> visited)
        {
            if (motion == null || !visited.Add(motion)) return;
            if (motion is BlendTree tree)
            {
                foreach (var child in tree.children) Collect(child.motion, declared, into, visited);
                return;
            }
            if (!(motion is AnimationClip clip)) return;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                if (IsAapBinding(binding) && declared.Contains(binding.propertyName))
                    into.Add(binding.propertyName);
        }

        /// <summary>The shape DaerD itself writes an AAP with (see
        /// <see cref="DbtBuilder.ParameterClip"/>): the Animator component at the animated
        /// root, addressed by the parameter's own name.</summary>
        static bool IsAapBinding(EditorCurveBinding binding) =>
            binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path);
    }
}
