using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// Reverse-engineering entry the dev-environment test daemon invokes by reflection
    /// (exec-method.sh). Deliberately not product surface: no UI, not public, no menu —
    /// it exists so an AI session with editor access can ask "what do these clips do"
    /// without reading Unity YAML. The argument is a path to a text file listing one
    /// project-relative asset path per line (.anim, or .controller to digest every clip
    /// the controller plays); the return value is the digest text.
    /// </summary>
    static class ClipDigestEntry
    {
        internal static string Run(string argFile)
        {
            var sb = new StringBuilder();
            foreach (var raw in File.ReadAllLines(argFile))
            {
                var path = raw.Trim();
                if (path.Length == 0 || path.StartsWith("#")) continue;
                if (path.EndsWith(".anim"))
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                    if (clip == null) { NotFound(sb, path); continue; }
                    sb.Append(ClipDigest.Format(ClipDigest.Collect(clip))).Append('\n');
                }
                else if (path.EndsWith(".controller"))
                {
                    var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                    if (controller == null) { NotFound(sb, path); continue; }
                    AppendController(sb, controller, path);
                }
                else
                {
                    sb.Append("unsupported (expected .anim or .controller): ").Append(path)
                      .Append("\n\n");
                }
            }
            sb.Append("note: values/ranges are read from curve keys (interpolation can ")
              .Append("overshoot between keys); curve shapes, tangents and key timing are ")
              .Append("not shown.\n");
            return sb.ToString();
        }

        static void AppendController(StringBuilder sb, AnimatorController controller, string path)
        {
            var uses = ClipDigest.CollectFromController(controller);
            sb.Append("controller \"").Append(controller.name).Append("\" (").Append(path)
              .Append("): ").Append(controller.layers.Length).Append(" layers, ")
              .Append(uses.Count).Append(" distinct clips\n\n");
            var trees = ClipDigest.FormatTrees(controller);
            if (trees.Length > 0) sb.Append(trees).Append('\n');
            foreach (var use in uses)
            {
                sb.Append("used by: ").Append(string.Join("; ", use.sites)).Append('\n');
                sb.Append(ClipDigest.Format(ClipDigest.Collect(use.clip))).Append('\n');
            }
        }

        static void NotFound(StringBuilder sb, string path)
        {
            sb.Append("not found (is it inside the project and imported?): ").Append(path)
              .Append("\n\n");
        }
    }
}
