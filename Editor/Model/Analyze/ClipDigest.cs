using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// A deterministic, few-line summary of what an AnimationClip does — for reading, not
    /// editing. Controllers round-trip through C# recipes, but a clip is a value
    /// time-series, not a procedure (the same premise as the .ddrun format), so the
    /// reverse-engineering story for .anim is "reference + digest": state the facts —
    /// what is bound, constant or animated, to which values — in a handful of lines
    /// instead of thousands of Unity YAML ones. Everything is read straight off the
    /// curves; nothing is guessed, and what the digest drops (curve shapes, tangents,
    /// key timing) is named by the caller (<see cref="ClipDigestEntry"/>).
    /// </summary>
    static class ClipDigest
    {
        /// <summary>One float binding, reduced to the facts a reader needs: where it
        /// points and either the held value (constant) or the key count and value range
        /// (animated). Ranges come from key values only — interpolation can overshoot
        /// between keys, which the digest's footer note owns up to.</summary>
        internal class CurveFact
        {
            public string path;
            public string type;
            public string property;
            public int keys;
            public bool constant;
            public float value;   // when constant
            public float min;     // when not
            public float max;
        }

        /// <summary>An object-reference (PPtr) binding: material swaps and the like.
        /// Values are object names, not references — the digest is text for a reader.</summary>
        internal class ObjectRefFact
        {
            public string path;
            public string type;
            public string property;
            public string valueType;
            public readonly List<(float time, string value)> keys = new List<(float, string)>();
        }

        /// <summary>Humanoid muscle / root-motion / IK-goal curves, kept as a tally per
        /// body region. A pose clip binds dozens of muscles; listing them one per line
        /// would defeat the point of a digest, and "which regions, how many, do they
        /// move" is what tells a pose from a dance.</summary>
        internal class MuscleFact
        {
            public int total;
            public int animated;
            public readonly SortedDictionary<string, int> regions = new SortedDictionary<string, int>();
        }

        internal class Facts
        {
            public string name;
            public float length;
            public float frameRate;
            public bool loop;
            public bool animated;
            public readonly List<CurveFact> constants = new List<CurveFact>();
            public readonly List<CurveFact> motion = new List<CurveFact>();
            /// <summary>Writes to the Animator's own parameters — the AAP idiom
            /// (<see cref="AapWriteScan"/> is the controller-wide view of the same thing).</summary>
            public readonly List<CurveFact> parameters = new List<CurveFact>();
            public readonly List<ObjectRefFact> objectRefs = new List<ObjectRefFact>();
            public MuscleFact muscles;   // null when the clip has no humanoid curves
        }

        public static Facts Collect(AnimationClip clip)
        {
            var facts = new Facts
            {
                name = clip.name,
                length = clip.length,
                frameRate = clip.frameRate,
                loop = AnimationUtility.GetAnimationClipSettings(clip).loopTime,
            };

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                bool constant = true;
                float first = curve.keys[0].value, min = first, max = first;
                foreach (var key in curve.keys)
                {
                    if (key.value != first) constant = false;
                    if (key.value < min) min = key.value;
                    if (key.value > max) max = key.value;
                }

                if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)
                    && IsHumanoid(binding.propertyName))
                {
                    var muscles = facts.muscles ?? (facts.muscles = new MuscleFact());
                    muscles.total++;
                    if (!constant) muscles.animated++;
                    var region = Region(binding.propertyName);
                    muscles.regions.TryGetValue(region, out var count);
                    muscles.regions[region] = count + 1;
                    continue;
                }

                var fact = new CurveFact
                {
                    path = binding.path,
                    type = binding.type != null ? binding.type.Name : "?",
                    property = binding.propertyName,
                    keys = curve.length,
                    constant = constant,
                    value = first,
                    min = min,
                    max = max,
                };
                if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path))
                    facts.parameters.Add(fact);
                else if (constant)
                    facts.constants.Add(fact);
                else
                    facts.motion.Add(fact);
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (keys == null || keys.Length == 0) continue;
                var fact = new ObjectRefFact
                {
                    path = binding.path,
                    type = binding.type != null ? binding.type.Name : "?",
                    property = Regex.Replace(binding.propertyName, @"\.Array\.data\[", "["),
                };
                foreach (var key in keys)
                {
                    fact.keys.Add((key.time, key.value != null ? key.value.name : null));
                    if (fact.valueType == null && key.value != null)
                        fact.valueType = key.value.GetType().Name;
                }
                facts.objectRefs.Add(fact);
            }

            facts.animated = facts.motion.Count > 0
                || (facts.muscles != null && facts.muscles.animated > 0)
                || facts.parameters.Exists(p => !p.constant)
                || facts.objectRefs.Exists(r => r.keys.Count > 1);
            return facts;
        }

        public static string Format(Facts facts)
        {
            var sb = new StringBuilder();
            sb.Append("clip \"").Append(facts.name).Append("\" — ")
              .Append(F(facts.length)).Append("s @ ").Append(F(facts.frameRate)).Append("fps")
              .Append(facts.loop ? ", loop on" : ", loop off")
              .Append(facts.animated ? ", animated" : ", static").Append('\n');

            if (facts.muscles == null && facts.parameters.Count == 0 && facts.constants.Count == 0
                && facts.motion.Count == 0 && facts.objectRefs.Count == 0)
            {
                sb.Append("  (empty — no curves)\n");
                return sb.ToString();
            }

            if (facts.muscles != null)
            {
                sb.Append("  humanoid: ").Append(facts.muscles.total).Append(" muscle/root curves");
                sb.Append(facts.muscles.animated == 0
                    ? " (all constant)"
                    : facts.muscles.animated == facts.muscles.total
                        ? " (all animated)"
                        : " (" + facts.muscles.animated + " animated)");
                sb.Append(": ");
                bool firstRegion = true;
                foreach (var region in facts.muscles.regions)
                {
                    if (!firstRegion) sb.Append(", ");
                    firstRegion = false;
                    sb.Append(region.Key).Append('(').Append(region.Value).Append(')');
                }
                sb.Append('\n');
            }

            AppendCurveSection(sb, "animator params (AAP):", facts.parameters, nameOnly: true);
            AppendCurveSection(sb, "constants:", facts.constants, nameOnly: false);
            AppendCurveSection(sb, "motion:", facts.motion, nameOnly: false);

            if (facts.objectRefs.Count > 0)
            {
                sb.Append("  object refs:\n");
                foreach (var fact in facts.objectRefs)
                {
                    sb.Append("    ").Append(Site(fact.path, fact.type)).Append(' ')
                      .Append(fact.property)
                      .Append(fact.valueType != null ? " <" + fact.valueType + ">" : "")
                      .Append(':');
                    foreach (var (time, value) in fact.keys)
                        sb.Append(" t=").Append(F(time)).Append(' ')
                          .Append(value != null ? "\"" + value + "\"" : "(none)");
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        static void AppendCurveSection(StringBuilder sb, string title, List<CurveFact> facts,
            bool nameOnly)
        {
            if (facts.Count == 0) return;
            sb.Append("  ").Append(title).Append('\n');
            foreach (var fact in facts)
            {
                sb.Append("    ");
                if (!nameOnly) sb.Append(Site(fact.path, fact.type)).Append(' ');
                sb.Append(fact.property);
                if (fact.constant)
                {
                    sb.Append(" = ").Append(F(fact.value));
                    if (fact.keys > 1) sb.Append(" (").Append(fact.keys).Append(" keys)");
                }
                else
                {
                    sb.Append(" — ").Append(fact.keys).Append(" keys in [")
                      .Append(F(fact.min)).Append(", ").Append(F(fact.max)).Append(']');
                }
                sb.Append('\n');
            }
        }

        static string Site(string path, string type) =>
            (string.IsNullOrEmpty(path) ? "(root)" : path) + " <" + type + ">";

        static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>The clips a controller plays and where: state motions, blend-tree
        /// children (any depth), synced-layer overrides. Unlike <see cref="AapWriteScan"/>
        /// this does not filter by reachability — a reverse-engineering reader wants to see
        /// parked clips too, precisely because they are suspicious.</summary>
        internal class ClipUse
        {
            public AnimationClip clip;
            public readonly List<string> sites = new List<string>();
        }

        public static List<ClipUse> CollectFromController(AnimatorController controller)
        {
            var result = new List<ClipUse>();
            if (controller == null) return result;
            var byClip = new Dictionary<AnimationClip, ClipUse>();
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var root = ControllerReachability.PlayedMachine(controller, i);
                if (root == null) continue;
                bool synced = layers[i].syncedLayerIndex >= 0;
                foreach (var sm in root.SelfAndDescendants())
                    foreach (var childState in sm.states)
                    {
                        var state = childState.state;
                        if (state == null) continue;
                        var motion = synced
                            ? layers[i].GetOverrideMotion(state) ?? state.motion
                            : state.motion;
                        Add(motion, layers[i].name + "/" + state.name, byClip, result,
                            new HashSet<Motion>());
                    }
            }
            return result;
        }

        static void Add(Motion motion, string site, Dictionary<AnimationClip, ClipUse> byClip,
            List<ClipUse> result, HashSet<Motion> visited)
        {
            if (motion == null || !visited.Add(motion)) return;
            if (motion is BlendTree tree)
            {
                foreach (var child in tree.children)
                    Add(child.motion, site + " (tree \"" + tree.name + "\")", byClip, result, visited);
                return;
            }
            if (!(motion is AnimationClip clip)) return;
            if (!byClip.TryGetValue(clip, out var use))
            {
                use = new ClipUse { clip = clip };
                byClip.Add(clip, use);
                result.Add(use);
            }
            use.sites.Add(site);
        }

        /// <summary>Blend-tree structure, one indented block per tree-playing state. This is
        /// the half of a gadget the clip digests cannot show: a Direct tree's per-child
        /// weight parameters are the arithmetic, a 1D tree's thresholds are the LUT — with
        /// only the child clips visible, a DBT gadget reads as "a pile of AAP clips".</summary>
        public static string FormatTrees(AnimatorController controller)
        {
            var sb = new StringBuilder();
            if (controller == null) return "";
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var root = ControllerReachability.PlayedMachine(controller, i);
                if (root == null) continue;
                bool synced = layers[i].syncedLayerIndex >= 0;
                foreach (var sm in root.SelfAndDescendants())
                    foreach (var childState in sm.states)
                    {
                        var state = childState.state;
                        if (state == null) continue;
                        var motion = synced
                            ? layers[i].GetOverrideMotion(state) ?? state.motion
                            : state.motion;
                        if (!(motion is BlendTree tree)) continue;
                        sb.Append("  ").Append(layers[i].name).Append('/').Append(state.name)
                          .Append(":\n");
                        AppendTree(sb, tree, 2, new HashSet<Motion>());
                    }
            }
            return sb.Length == 0 ? "" : "trees:\n" + sb;
        }

        static void AppendTree(StringBuilder sb, BlendTree tree, int depth, HashSet<Motion> visited)
        {
            sb.Append(new string(' ', depth * 2)).Append("tree \"").Append(tree.name)
              .Append("\" ").Append(TreeKind(tree)).Append(":\n");
            if (!visited.Add(tree))
            {
                sb.Append(new string(' ', depth * 2 + 2)).Append("(already shown)\n");
                return;
            }
            foreach (var child in tree.children)
            {
                if (child.motion is BlendTree nested)
                {
                    // The child's own axis label still matters, so print it above the block.
                    var label = ChildLabel(tree, child);
                    if (label.Length > 0)
                        sb.Append(new string(' ', depth * 2 + 2)).Append(label).Append(":\n");
                    AppendTree(sb, nested, depth + 2, visited);
                    continue;
                }
                sb.Append(new string(' ', depth * 2 + 2));
                sb.Append(child.motion is AnimationClip clip ? "\"" + clip.name + "\"" : "(none)");
                var axis = ChildLabel(tree, child);
                if (axis.Length > 0) sb.Append(' ').Append(axis);
                if (child.timeScale != 1f) sb.Append(" speed ").Append(F(child.timeScale));
                if (child.mirror) sb.Append(" mirrored");
                sb.Append('\n');
            }
        }

        static string TreeKind(BlendTree tree)
        {
            switch (tree.blendType)
            {
                case BlendTreeType.Simple1D:
                    return "1D(" + tree.blendParameter + ")";
                case BlendTreeType.SimpleDirectional2D:
                    return "2D SimpleDirectional(" + tree.blendParameter + ", " + tree.blendParameterY + ")";
                case BlendTreeType.FreeformDirectional2D:
                    return "2D FreeformDirectional(" + tree.blendParameter + ", " + tree.blendParameterY + ")";
                case BlendTreeType.FreeformCartesian2D:
                    return "2D FreeformCartesian(" + tree.blendParameter + ", " + tree.blendParameterY + ")";
                case BlendTreeType.Direct:
                    return "Direct";
                default:
                    return tree.blendType.ToString();
            }
        }

        static string ChildLabel(BlendTree parent, ChildMotion child)
        {
            switch (parent.blendType)
            {
                case BlendTreeType.Simple1D:
                    return "@ " + F(child.threshold);
                case BlendTreeType.Direct:
                    return "x " + child.directBlendParameter;
                default:
                    return "@ (" + F(child.position.x) + ", " + F(child.position.y) + ")";
            }
        }

        // Humanoid curves bind to typeof(Animator) with an empty path, exactly like an AAP
        // — only the name tells them apart (same trap AapWriteScan documents). Muscle names
        // come from HumanTrait; finger and root/IK bindings use spellings HumanTrait does
        // not list, hence the two patterns.
        static readonly HashSet<string> MuscleNames = new HashSet<string>(HumanTrait.MuscleName);
        static readonly Regex RootIkPattern = new Regex(
            @"^(Root|Motion|LeftFoot|RightFoot|LeftHand|RightHand)[TQ]\.[xyzw]$");
        static readonly Regex FingerPattern = new Regex(
            @"^(Left|Right)Hand\.(Thumb|Index|Middle|Ring|Little)\.([123] Stretched|Spread)$");

        static bool IsHumanoid(string property) =>
            MuscleNames.Contains(property)
            || RootIkPattern.IsMatch(property)
            || FingerPattern.IsMatch(property);

        static string Region(string property)
        {
            if (property.StartsWith("LeftHand.")) return "LeftFingers";
            if (property.StartsWith("RightHand.")) return "RightFingers";
            if (RootIkPattern.IsMatch(property))
                return property.StartsWith("Root") || property.StartsWith("Motion") ? "Root" : "IK";
            if (property.Contains("Eye") || property.Contains("Jaw")
                || property.Contains("Neck") || property.Contains("Head")) return "Head";
            bool left = property.StartsWith("Left"), right = property.StartsWith("Right");
            if (property.Contains("Shoulder") || property.Contains("Arm")
                || property.Contains("Forearm") || property.Contains("Hand"))
                return left ? "LeftArm" : right ? "RightArm" : "Body";
            if (property.Contains("Leg") || property.Contains("Foot") || property.Contains("Toes"))
                return left ? "LeftLeg" : right ? "RightLeg" : "Body";
            return "Body";
        }
    }
}
