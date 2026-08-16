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
        /// Where the ring resumes after a request has been served: the step the detour left
        /// from, written by every send state and read by the request states on their way back.
        /// Local and never synced, like the request flags beside it.
        /// </summary>
        public static string ReturnParameter(string baseName) => baseName + "/Return";

        /// <summary>
        /// The remote-initialized flag: 0 until this client has decoded every slot at least
        /// once, and 1 from then on. Local and never synced — a remote has no way to tell the
        /// wearer anything, so this is each client's own reading of what it has received.
        /// </summary>
        public static string ReadyParameter(string baseName) => baseName + "/Ready";

        /// <summary>
        /// One slot's "this client has decoded it at least once" bit, set by that slot's Recv
        /// driver and never cleared. Named after the slot the way its states are, so a layer
        /// and the parameter list read as the same setup.
        /// </summary>
        public static string SeenParameter(string baseName, string slotName) =>
            baseName + "/Seen/" + slotName;

        /// <summary>The layer the Ready watcher gets, beside the cycle's own. Its own layer
        /// because it has to be evaluated while the sync layer is busy being a ring.</summary>
        public static string ReadyLayerName(string layerName) => layerName + " Ready";

        /// <summary>
        /// The drift-suspicion flag: 1 when the lap that just closed did not bring every slot,
        /// 0 when it did. Local and never synced, and unlike <see cref="ReadyParameter"/> it
        /// falls again — the question it answers is about the last lap, not about ever.
        /// </summary>
        public static string StaleParameter(string baseName) => baseName + "/Stale";

        /// <summary>One slot's "arrived during this lap" bit: set by that slot's Recv driver
        /// and cleared once a lap by the watcher that reads them.</summary>
        public static string FreshParameter(string baseName, string slotName) =>
            baseName + "/Fresh/" + slotName;

        /// <summary>The layer the Stale watcher gets. Its own for the same reason Ready's
        /// is — and its own rather than shared with Ready's, because this one is a cycle of
        /// states and that one is a latch.</summary>
        public static string StaleLayerName(string layerName) => layerName + " Stale";

        /// <summary>
        /// What a grouped target is SENT from, on the wearer's side: the step that opens the
        /// group's lap copies every member's current value here in one driver, and every step
        /// that carries a member puts this on the wire rather than the parameter itself. One
        /// moment's reading of the whole group, so the values a lap sends belong together
        /// however far apart the pass sends them. Same type as the target, local, never synced.
        /// </summary>
        public static string LatchParameter(string baseName, string target) =>
            baseName + "/Latch/" + target;

        /// <summary>
        /// Where a grouped target waits: the decoder writes here instead of into the parameter
        /// itself, and the group's commit copies the whole set across at once. Same type as
        /// the target, local, and never synced.
        /// </summary>
        public static string HoldParameter(string baseName, string target) =>
            baseName + "/Hold/" + target;

        /// <summary>"This member has arrived and is waiting in its Hold": raised by the
        /// decoder, and put down by the commit that consumed it.</summary>
        public static string HeldParameter(string baseName, string target) =>
            baseName + "/Held/" + target;

        /// <summary>The layer a group's commit gets. One per group: each waits for its own
        /// members, and a shared layer could only ever be in one of those waits at a time.</summary>
        public static string GroupLayerName(string layerName, string groupName) =>
            layerName + " " + groupName;

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
