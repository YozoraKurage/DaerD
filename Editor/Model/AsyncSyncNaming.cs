using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Every name an async-sync setup generates, derived from its base name: the index (or
    /// its bits), the value channels, the local request flags — and the base name itself for
    /// a setup that doesn't have one yet. One place, because the send cycle, the decoder, the
    /// wizard and the per-state sync request all have to spell them the same way.
    /// </summary>
    static class AsyncSyncNaming
    {
        public static string IndexParameter(string baseName) => baseName + "/Index";

        public static string BitParameter(string baseName, int bit) =>
            baseName + "/Index/b" + bit;

        public static string ChannelParameter(string baseName, AnimatorControllerParameterType type) =>
            baseName + "/" + type;

        /// <summary>Channel 0 keeps the legacy "/Float" name; extras are "/Float2", "/Float3"…</summary>
        public static string FloatChannelParameter(string baseName, int channel) =>
            channel == 0
                ? baseName + "/" + AnimatorControllerParameterType.Float
                : baseName + "/" + AnimatorControllerParameterType.Float + (channel + 1);

        /// <summary>Channel 0 keeps the plain "/Bool" name — the one every setup built before
        /// Bool batching existed already syncs — and extras are "/Bool2", "/Bool3"… Renaming
        /// channel 0 would leave that parameter behind in the store, still synced, while the
        /// regenerated layer talked to a new one.</summary>
        public static string BoolChannelParameter(string baseName, int channel) =>
            channel == 0
                ? baseName + "/" + AnimatorControllerParameterType.Bool
                : baseName + "/" + AnimatorControllerParameterType.Bool + (channel + 1);

        /// <summary>The local request flag for one target. Lives under the base namespace, so
        /// <see cref="AsyncSyncBuilder.IsReservedName"/> keeps it out of the multiplexed set automatically.</summary>
        public static string RequestParameter(string baseName, string target) =>
            baseName + "/Req/" + target;

        /// <summary>
        /// The base name a new setup on this controller starts from: "DD" plus the first six
        /// hex digits of the controller's asset GUID. A fixed default collides as soon as two
        /// distributions that each bring a cycle meet on one avatar — both would own
        /// "Async/Index" — while the GUID is unique per controller and never moves, so a
        /// recipe regenerating its layer resolves the same name on every Generate. An
        /// in-memory controller has no GUID and keeps the historical "Async".
        /// </summary>
        public static string DefaultBaseName(AnimatorController controller)
        {
            string path = AssetDatabase.GetAssetPath(controller);
            var taken = new List<string>();
            foreach (var config in GraphFrameData.GetAsyncSyncs(controller))
                if (!string.IsNullOrEmpty(config.baseName))
                    taken.Add(config.baseName);
            return DefaultBaseName(
                string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path), taken);
        }

        /// <summary>Core of <see cref="DefaultBaseName(AnimatorController)"/>: one controller can
        /// host several setups, and the second one can't answer to the first one's name — hence
        /// the "_2", "_3"… suffixes.</summary>
        internal static string DefaultBaseName(string guid, ICollection<string> taken)
        {
            if (string.IsNullOrEmpty(guid)) return "Async";
            string stem = "DD" + guid.Substring(0, Mathf.Min(6, guid.Length));
            string name = stem;
            // Terminates: the taken set is finite and every candidate name is distinct.
            for (int n = 2; taken != null && taken.Contains(name); n++)
                name = stem + "_" + n;
            return name;
        }
    }
}
