using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// VRC PhysBones and Contacts expose families of auto-generated parameters sharing one
    /// prefix ("Tail_IsGrabbed", "Tail_Angle", …). When the user renames one member, the
    /// others usually need the same prefix change — this finds them so the rename UI can
    /// offer a batch rename.
    /// </summary>
    static class PhysBoneSiblings
    {
        /// <summary>Auto-generated suffixes of VRC PhysBone / Contact parameter families.</summary>
        public static readonly string[] KnownSuffixes =
        {
            "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish",
        };

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
