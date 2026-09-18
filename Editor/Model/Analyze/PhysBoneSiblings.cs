using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// VRC PhysBones and Contacts expose families of auto-generated parameters sharing one
    /// prefix ("Tail_IsGrabbed", "Tail_Angle", …). When the user renames one member, the
    /// others usually need the same prefix change — this finds them so the rename UI can
    /// offer a batch rename; when they add one, MissingFamily lists what completes the set.
    /// </summary>
    static class PhysBoneSiblings
    {
        /// <summary>The whole family: each auto-generated suffix with the type the PhysBone
        /// system writes through it. The types are VRChat's contract, not a user choice.</summary>
        public static readonly (string suffix, AnimatorControllerParameterType type)[] Family =
        {
            ("_IsGrabbed", AnimatorControllerParameterType.Bool),
            ("_IsPosed", AnimatorControllerParameterType.Bool),
            ("_Angle", AnimatorControllerParameterType.Float),
            ("_Stretch", AnimatorControllerParameterType.Float),
            ("_Squish", AnimatorControllerParameterType.Float),
        };

        /// <summary>Auto-generated suffixes of VRC PhysBone / Contact parameter families.</summary>
        public static readonly string[] KnownSuffixes =
            System.Array.ConvertAll(Family, member => member.suffix);

        /// <summary>The family prefix of <paramref name="parameterName"/>, or null when the
        /// name carries no known suffix.</summary>
        public static string PrefixOf(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName)) return null;
            foreach (var suffix in KnownSuffixes)
                if (parameterName.EndsWith(suffix) && parameterName.Length > suffix.Length)
                    return parameterName.Substring(0, parameterName.Length - suffix.Length);
            return null;
        }

        /// <summary>Other parameters of the same family present on the controller
        /// (same prefix, any known suffix, excluding the renamed one itself).</summary>
        public static List<string> Siblings(AnimatorController controller, string parameterName)
        {
            var siblings = new List<string>();
            var prefix = PrefixOf(parameterName);
            if (controller == null || prefix == null) return siblings;
            foreach (var parameter in controller.parameters)
            {
                if (parameter.name == parameterName) continue;
                var otherPrefix = PrefixOf(parameter.name);
                if (otherPrefix == prefix)
                    siblings.Add(parameter.name);
            }
            return siblings;
        }

        /// <summary>Family members the controller doesn't have yet, seeded from any member's
        /// name — or, for a name with no known suffix, from the name itself as the prefix.
        /// That second reading is what lets "make one parameter, complete the family from its
        /// row menu" work without a prefix prompt of its own.</summary>
        public static List<(string name, AnimatorControllerParameterType type)> MissingFamily(
            AnimatorController controller, string parameterName)
        {
            var missing = new List<(string, AnimatorControllerParameterType)>();
            if (controller == null || string.IsNullOrEmpty(parameterName)) return missing;
            var prefix = PrefixOf(parameterName) ?? parameterName;
            var existing = new HashSet<string>();
            foreach (var parameter in controller.parameters) existing.Add(parameter.name);
            foreach (var (suffix, type) in Family)
                if (!existing.Contains(prefix + suffix))
                    missing.Add((prefix + suffix, type));
            return missing;
        }

        /// <summary>The sibling's name after the family prefix changed with the rename
        /// old → new, or null when the rename didn't change the family prefix.</summary>
        public static string RenamedSibling(string sibling, string oldName, string newName)
        {
            var oldPrefix = PrefixOf(oldName);
            var newPrefix = PrefixOf(newName);
            if (oldPrefix == null || newPrefix == null || oldPrefix == newPrefix) return null;
            var siblingPrefix = PrefixOf(sibling);
            if (siblingPrefix != oldPrefix) return null;
            return newPrefix + sibling.Substring(oldPrefix.Length);
        }
    }
}
