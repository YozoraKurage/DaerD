using System.Collections.Generic;
using UnityEngine;
// Stated in AsyncSyncBuilder's vocabulary, and leaning on the schedule it prices — both
// moved out of that class unchanged, and the imports keep the moved code reading as it did.
using static Yozolab.DaerD.AsyncSyncSchedule;
using IndexEncoding = Yozolab.DaerD.AsyncSyncBuilder.IndexEncoding;
using Request = Yozolab.DaerD.AsyncSyncBuilder.Request;

namespace Yozolab.DaerD
{
    /// <summary>
    /// What an async-sync request resolves to and what it costs, before anything is built:
    /// the index encoding Auto settles on, the clip the generated states will play, and the
    /// synced bits and refresh intervals the wizard shows to justify the whole exercise.
    /// Reads the request and the controller's parameters; writes neither.
    /// </summary>
    static class AsyncSyncCost
    {
        /// <summary>
        /// The encoding a request actually builds with. Auto weighs the two: the Bool index
        /// costs ceil(log2 N) bits against the Int's flat 8, so it wins for anything under 256
        /// slots. A tie goes to the Int purely on tidiness — one parameter in the store and one
        /// condition per decoder route instead of eight. Both are equally safe on the wire:
        /// expression parameters arrive together, so the index bits can't be read half-updated.
        /// </summary>
        public static IndexEncoding ResolveEncoding(Request r)
        {
            if (r == null || r.encoding != IndexEncoding.Auto) return r?.encoding ?? IndexEncoding.Int;
            int slots = Mathf.Max(2, BuildSlots(r).Count);
            return NetworkSyncBuilder.BitsRequired(slots) < 8 ? IndexEncoding.Bool : IndexEncoding.Int;
        }

        /// <summary>
        /// The clip the generated states will play, or null when they stay motion-less. A
        /// zero-length clip is refused: exit times are normalized to the motion, so there would
        /// be nothing to divide the step interval by.
        /// </summary>
        public static AnimationClip ResolveEmptyClip(Request r)
        {
            if (r == null || !r.assignEmptyClip) return null;
            var clip = r.emptyClip != null ? r.emptyClip : GraphFrameData.GetEmptyClip(r.controller);
            return clip != null && clip.length > 0f ? clip : null;
        }

        /// <summary>Float channels the request actually uses — capped by how many Floats any
        /// one slot really carries, so unused channels are neither created nor billed.</summary>
        public static int FloatChannelsUsed(Request r) =>
            ChannelsUsed(r, AnimatorControllerParameterType.Float);

        /// <summary>Bool channels the request actually uses. Same accounting as
        /// <see cref="FloatChannelsUsed"/>, one synced bit each instead of eight.</summary>
        public static int BoolChannelsUsed(Request r) =>
            ChannelsUsed(r, AnimatorControllerParameterType.Bool);

        /// <summary>The widest batch of one type across the slots — a slot only ever holds
        /// targets of a single type, so its first target names the type of all of them.</summary>
        static int ChannelsUsed(Request r, AnimatorControllerParameterType type)
        {
            int used = 0;
            foreach (var slot in BuildSlots(r))
            {
                if (slot.targets.Count == 0) continue;
                var parameter = DbtBuilder.FindParameter(r.controller, slot.targets[0]);
                if (parameter != null && parameter.type == type)
                    used = Mathf.Max(used, slot.targets.Count);
            }
            return used;
        }

        /// <summary>Seconds for one full pass of the schedule — the worst-case age of a
        /// regular value.</summary>
        public static float CycleSeconds(Request r) =>
            r == null ? 0f : EffectiveSchedule(r, BuildSlots(r)).Count * r.stepSeconds;

        /// <summary>
        /// Seconds between two syncs of each target, from the actual schedule: pass length ×
        /// step ÷ occurrences of the target's slot. This is what the wizard shows per row —
        /// the honest number, after weight normalization and capping.
        /// </summary>
        public static Dictionary<string, float> RefreshIntervals(Request r)
        {
            var intervals = new Dictionary<string, float>();
            if (r == null) return intervals;
            var slots = BuildSlots(r);
            var schedule = EffectiveSchedule(r, slots);
            if (schedule.Count == 0) return intervals;

            var occurrences = new int[slots.Count];
            foreach (var step in schedule) occurrences[step]++;
            for (int i = 0; i < slots.Count; i++)
            {
                if (occurrences[i] == 0) continue;
                float interval = schedule.Count * r.stepSeconds / occurrences[i];
                foreach (var name in slots[i].targets)
                    intervals[name] = interval;
            }
            return intervals;
        }

        /// <summary>
        /// Slots that can still be added without spending another synced bit. The Bool index
        /// only grows at powers of two, so the tail of each range is free; an Int index has room
        /// for 255 slots from the start.
        /// </summary>
        public static int FreeSlots(Request r)
        {
            if (r?.targets == null || ResolveEncoding(r) != IndexEncoding.Bool) return 0;
            int count = Mathf.Max(2, BuildSlots(r).Count);
            int capacity = 1 << NetworkSyncBuilder.BitsRequired(count);
            return Mathf.Max(0, capacity - count);
        }

        /// <summary>Synced bits the generated parameters will occupy.</summary>
        public static int CompressedBits(Request r)
        {
            int bits = ResolveEncoding(r) == IndexEncoding.Int
                ? 8
                : NetworkSyncBuilder.BitsRequired(Mathf.Max(2, BuildSlots(r).Count));
            foreach (var type in ChannelTypes(r))
                bits += type == AnimatorControllerParameterType.Bool ? BoolChannelsUsed(r)
                    : type == AnimatorControllerParameterType.Float ? FloatChannelsUsed(r) * 8
                    : 8;
            return bits;
        }

        /// <summary>Synced bits the targets would occupy if each synced directly.</summary>
        public static int DirectBits(Request r)
        {
            int bits = 0;
            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter == null) continue;
                bits += parameter.type == AnimatorControllerParameterType.Bool ? 1 : 8;
            }
            return bits;
        }

        internal static List<AnimatorControllerParameterType> ChannelTypes(Request r)
        {
            var types = new List<AnimatorControllerParameterType>();
            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter != null && !types.Contains(parameter.type))
                    types.Add(parameter.type);
            }
            return types;
        }
    }
}
