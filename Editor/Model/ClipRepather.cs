using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Fixes AnimationClip bindings whose hierarchy paths no longer exist under the avatar
    /// (objects renamed or moved): scans for broken paths and rewrites a path prefix across
    /// clips, moving each curve to the new binding. Inspired by hfcRed's Animation-Repathing
    /// workflow; implemented independently.
    /// </summary>
    static class ClipRepather
    {
        public class BrokenBinding
        {
            public AnimationClip clip;
            public EditorCurveBinding binding;
        }

        /// <summary>Every distinct clip the controller references (states and blend trees).</summary>
        public static List<AnimationClip> ClipsOf(AnimatorController controller)
        {
            var clips = new List<AnimationClip>();
            if (controller == null) return clips;
            foreach (var entry in ControllerCleanup.CollectClipUsages(controller))
                if (entry.clip != null)
                    clips.Add(entry.clip);
            return clips;
        }

        /// <summary>Bindings whose path resolves to nothing under <paramref name="root"/>.
        /// The empty path (the root itself) always resolves.</summary>
        public static List<BrokenBinding> ScanBroken(IEnumerable<AnimationClip> clips, GameObject root)
        {
            var broken = new List<BrokenBinding>();
            if (root == null) return broken;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    if (!Resolves(root, binding.path))
                        broken.Add(new BrokenBinding { clip = clip, binding = binding });
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    if (!Resolves(root, binding.path))
                        broken.Add(new BrokenBinding { clip = clip, binding = binding });
            }
            return broken;
        }

        static bool Resolves(GameObject root, string path) =>
            string.IsNullOrEmpty(path) || root.transform.Find(path) != null;

        /// <summary>Distinct broken paths, most frequent first — the scan UI shows the top
        /// few as one-click "From" fillers.</summary>
        public static List<string> DistinctBrokenPaths(List<BrokenBinding> broken)
        {
            var counts = new Dictionary<string, int>();
            foreach (var entry in broken)
            {
                counts.TryGetValue(entry.binding.path, out int count);
                counts[entry.binding.path] = count + 1;
            }
            var paths = new List<string>(counts.Keys);
            paths.Sort((a, b) => counts[b].CompareTo(counts[a]));
            return paths;
        }

        /// <summary>
        /// Rewrites every binding whose path is <paramref name="from"/> (or lies under it) so
        /// it starts with <paramref name="to"/> instead, moving the curve data. Returns the
        /// number of bindings rewritten. Undoable per clip.
        /// </summary>
        public static int Repath(IEnumerable<AnimationClip> clips, string from, string to)
        {
            if (string.IsNullOrEmpty(from) || to == null || from == to) return 0;
            int rewritten = 0;
            foreach (var clip in clips)
            {
                if (clip == null) continue;
                bool touched = false;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (!TryMapPath(binding.path, from, to, out var newPath)) continue;
                    if (!touched)
                    {
                        Undo.RegisterCompleteObjectUndo(clip, "Repath Animation");
                        touched = true;
                    }
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    var moved = binding;
                    moved.path = newPath;
                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    AnimationUtility.SetEditorCurve(clip, moved, curve);
                    rewritten++;
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    if (!TryMapPath(binding.path, from, to, out var newPath)) continue;
                    if (!touched)
                    {
                        Undo.RegisterCompleteObjectUndo(clip, "Repath Animation");
                        touched = true;
                    }
                    var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    var moved = binding;
                    moved.path = newPath;
                    AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                    AnimationUtility.SetObjectReferenceCurve(clip, moved, curve);
                    rewritten++;
                }
                if (touched)
                    EditorUtility.SetDirty(clip);
            }
            return rewritten;
        }

        /// <summary>Exact match or child of the "from" path → its path under "to".</summary>
        public static bool TryMapPath(string path, string from, string to, out string mapped)
        {
            if (path == from)
            {
                mapped = to;
                return true;
            }
            if (path != null && path.StartsWith(from + "/"))
            {
                mapped = to + path.Substring(from.Length);
                return true;
            }
            mapped = null;
            return false;
        }

        /// <summary>Analyzer hook: broken-binding warnings when a scene Animator runs the
        /// controller (headless analysis is unaffected).</summary>
        public static void Analyze(AnimatorController controller,
            List<ControllerAnalyzer.Issue> issues)
        {
            var root = FindAnimatorRoot(controller);
            if (root == null) return;
            var broken = ScanBroken(ClipsOf(controller), root);
            var perClip = new Dictionary<AnimationClip, int>();
            foreach (var entry in broken)
            {
                perClip.TryGetValue(entry.clip, out int count);
                perClip[entry.clip] = count + 1;
            }
            foreach (var pair in perClip)
                issues.Add(new ControllerAnalyzer.Issue
                {
                    kind = ControllerAnalyzer.Kind.ClipBindings,
                    severity = ControllerAnalyzer.Severity.Warning,
                    message = L.Tr("Clip '{0}' has {1} binding(s) whose path no longer exists under '{2}'.",
                        pair.Key.name, pair.Value, root.name),
                    context = pair.Key,
                });
        }

        public static GameObject FindAnimatorRoot(AnimatorController controller)
        {
            if (controller == null) return null;
            foreach (var animator in Object.FindObjectsOfType<Animator>(true))
                if (animator.runtimeAnimatorController == controller)
                    return animator.gameObject;
            return null;
        }
    }
}
