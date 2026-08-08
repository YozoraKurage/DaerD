using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Generates the "AAP exponential smoothing" gadget for a float parameter. A Direct
    /// blend tree layer (one Write-Defaults-ON state sitting at Entry) evaluates, every
    /// frame,
    ///     output = lerp(source, output, smoothing)
    /// via a 1D tree that cross-fades between a source-following tree (at smoothing 0) and
    /// a feedback tree driven by the output itself (at smoothing 1). Both leaves are AAP
    /// clips — one-key AnimationClips that animate the output parameter on the Animator.
    /// Everything created (clips and trees) is stored as sub-assets of the controller.
    /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
    /// </summary>
    static class AapSmoothing
    {
        public class Request
        {
            public AnimatorController controller;
            /// <summary>Float parameter to smooth.</summary>
            public string source;
            /// <summary>Smoothed copy written by the gadget, e.g. "X/Smoothed". Must be new.</summary>
            public string output;
            /// <summary>Smoothing-amount parameter (0 = follow instantly, →1 = slower).
            /// May already exist as a Float so several gadgets can share it.</summary>
            public string smoothing;
            public float smoothingDefault = 0.9f;
            /// <summary>Value range the parameter moves in; the AAP clips pin its ends.</summary>
            public float rangeMin = -1f;
            public float rangeMax = 1f;
            /// <summary>Existing DBT (or empty) layer to add the gadget to, or -1 to create one.</summary>
            public int layerIndex = -1;
            public string newLayerName = "DBT";
        }

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");

            var sourceParam = DbtBuilder.FindParameter(controller, r.source);
            if (sourceParam == null || sourceParam.type != AnimatorControllerParameterType.Float)
                return L.Tr("The source must be an existing Float parameter.");

            if (string.IsNullOrEmpty(r.output) || r.output == r.source)
                return L.Tr("The output parameter needs a name different from the source.");
            if (DbtBuilder.FindParameter(controller, r.output) != null)
                return L.Tr("A parameter named '{0}' already exists.", r.output);

            if (string.IsNullOrEmpty(r.smoothing) || r.smoothing == r.source || r.smoothing == r.output)
                return L.Tr("The smoothing parameter needs its own name.");
            var smoothing = DbtBuilder.FindParameter(controller, r.smoothing);
            if (smoothing != null && smoothing.type != AnimatorControllerParameterType.Float)
                return L.Tr("Parameter '{0}' exists but is not a Float.", r.smoothing);

            if (!(r.rangeMin < r.rangeMax))
                return L.Tr("Range Min must be smaller than Range Max.");

            return ValidateLayerChoice(controller, r.layerIndex, r.newLayerName);
        }

        /// <summary>Shared layer-choice check for all DBT gadget requests.</summary>
        internal static string ValidateLayerChoice(AnimatorController controller, int layerIndex, string newLayerName)
        {
            if (layerIndex >= 0)
            {
                if (layerIndex >= controller.layers.Length)
                    return L.Tr("The target layer no longer exists.");
                var layer = controller.layers[layerIndex];
                if (!DbtBuilder.IsLayerEmpty(layer) && !ControllerAnalyzer.IsDirectBlendTreeOnlyLayer(layer))
                    return L.Tr("The target layer must be empty or contain only Direct blend tree states.");
            }
            else if (string.IsNullOrEmpty(newLayerName))
            {
                return L.Tr("The new layer needs a name.");
            }
            return null;
        }

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.
        /// <paramref name="commitSubAssets"/> off leaves the flush to the caller — see
        /// <see cref="AapGadgets.Apply"/>.</summary>
        public static bool Apply(Request r, bool commitSubAssets = true)
        {
            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("AAP Smoothing"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "AAP Smoothing");

                string weightParam = DbtBuilder.EnsureConstantOneParameter(controller);
                DbtBuilder.EnsureFloatParameter(controller, r.output,
                    DbtBuilder.FindParameter(controller, r.source).defaultFloat);
                DbtBuilder.EnsureFloatParameter(controller, r.smoothing, Mathf.Clamp01(r.smoothingDefault));

                var root = DbtBuilder.EnsureDirectBlendTreeLayer(controller, r.layerIndex, r.newLayerName);

                // The two AAP leaves, shared by the input and the feedback tree.
                var clipMin = DbtBuilder.ParameterClip(controller, r.output, r.rangeMin);
                var clipMax = DbtBuilder.ParameterClip(controller, r.output, r.rangeMax);

                var inputTree = DbtBuilder.Tree1D(controller, DbtBuilder.Sanitize(r.source) + " (Input)", r.source);
                inputTree.AddChild(clipMin, r.rangeMin);
                inputTree.AddChild(clipMax, r.rangeMax);

                var feedbackTree = DbtBuilder.Tree1D(controller, DbtBuilder.Sanitize(r.output) + " (Feedback)", r.output);
                feedbackTree.AddChild(clipMin, r.rangeMin);
                feedbackTree.AddChild(clipMax, r.rangeMax);

                var smoothTree = DbtBuilder.Tree1D(controller, "Smooth " + DbtBuilder.Sanitize(r.source), r.smoothing);
                smoothTree.AddChild(inputTree, 0f);
                smoothTree.AddChild(feedbackTree, 1f);

                DbtBuilder.AddDirectChild(root, smoothTree, weightParam);
                EditorUtility.SetDirty(controller);
            }
            // The clips and trees above are sub-assets; one flush shows the whole batch.
            if (commitSubAssets) DbtBuilder.CommitSubAssets(controller);
            return true;
        }
    }
}
