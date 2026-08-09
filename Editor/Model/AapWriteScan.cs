using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
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
    /// </summary>
    static class AapWriteScan
    {
        /// <summary>Declared parameters of <paramref name="controller"/> that some reachable
        /// clip animates on the Animator itself.</summary>
        public static HashSet<string> CollectWrittenParameters(AnimatorController controller)
        {
            var written = new HashSet<string>();
            if (controller == null) return written;

            // Restricted to names the controller declares. Humanoid muscle curves bind to
            // typeof(Animator) with an empty path exactly like an AAP does, so without this
            // filter every locomotion clip would look like it wrote dozens of parameters.
            var declared = new HashSet<string>();
            foreach (var parameter in controller.parameters) declared.Add(parameter.name);
            if (declared.Count == 0) return written;

            foreach (var clip in ClipRepather.ClipsOf(controller))
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (IsAapBinding(binding) && declared.Contains(binding.propertyName))
                        written.Add(binding.propertyName);
            }
            return written;
        }

        /// <summary>The shape DaerD itself writes an AAP with (see
        /// <see cref="DbtBuilder.ParameterClip"/>): the Animator component at the animated
        /// root, addressed by the parameter's own name.</summary>
        static bool IsAapBinding(EditorCurveBinding binding) =>
            binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path);
    }
}
