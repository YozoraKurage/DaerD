using System.Collections.Generic;
using UnityEngine;
// Stated in AsyncSyncBuilder's vocabulary, and leaning on the schedule it prices — both
// moved out of that class unchanged, and the imports keep the moved code reading as it did.
using static Yozolab.DaerD.AsyncSyncSchedule;
using IndexEncoding = Yozolab.DaerD.AsyncSyncBuilder.IndexEncoding;
using Request = Yozolab.DaerD.AsyncSyncBuilder.Request;
using Slot = Yozolab.DaerD.AsyncSyncBuilder.Slot;

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
        /// index values. A tie goes to the Int purely on tidiness — one parameter in the store
        /// and one condition per decoder route instead of eight. Both are equally safe on the
        /// wire: expression parameters arrive together, so the index bits can't be read
        /// half-updated.
        /// </summary>
        public static IndexEncoding ResolveEncoding(Request r)
        {
            if (r == null || r.encoding != IndexEncoding.Auto) return r?.encoding ?? IndexEncoding.Int;
            int values = Mathf.Max(2, IndexValues(r));
            return NetworkSyncBuilder.BitsRequired(values) < 8 ? IndexEncoding.Bool : IndexEncoding.Int;
        }

        /// <summary>
        /// Distinct values the index takes: one per slot, and one more for every slot a clock
        /// gives a second phase to (see <see cref="AsyncSyncSchedule.BuildClock"/>). This, not
        /// the slot count, is what the index has to be wide enough for — and with no clock the
        /// two are the same number, which is why nothing about an unclocked setup moves.
        /// </summary>
        public static int IndexValues(Request r)
        {
            var slots = BuildSlots(r);
            return BuildClock(r, slots, EffectiveSchedule(r, slots)).indexValues;
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

        /// <summary>The widest batch of one type across the slots.</summary>
        static int ChannelsUsed(Request r, AnimatorControllerParameterType type)
        {
            int used = 0;
            foreach (var slot in BuildSlots(r))
                used = Mathf.Max(used, ChannelsInSlot(r, slot, type));
            return used;
        }

        /// <summary>
        /// Targets of one type inside a single slot — the channels of that type the slot needs,
        /// and (taken as the maximum over the slots) the number the setup generates. Counted
        /// per type rather than read off the slot's size: a slot may carry several types at
        /// once, and each type numbers its channels from 0 of its own, so a slot's target
        /// count says nothing about how many channels of any one type it wants.
        ///
        /// Its own method because the automatic batching cannot build a slot that mixes types
        /// — only an explicit grid can — and this is where that rule is pinned.
        /// </summary>
        internal static int ChannelsInSlot(Request r, Slot slot,
            AnimatorControllerParameterType type)
        {
            int used = 0;
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in slot.targets)
            {
                var parameter = byName.Find(name);
                if (parameter != null && parameter.type == type) used++;
            }
            return used;
        }

        /// <summary>Seconds for one full pass of the schedule — the worst-case age of a
        /// regular value.</summary>
        public static float CycleSeconds(Request r) =>
            r == null ? 0f : EffectiveSchedule(r, BuildSlots(r)).Count * r.stepSeconds;

        /// <summary>
        /// The longest one pass can take once requests are in play. A detour spends a step and
        /// gives the ring's place back, and a detour state carries no routes of its own, so the
        /// worst a pass can be driven to is one detour between every two steps — twice the
        /// nominal, and no worse however hard the flags are raised. Equal to
        /// <see cref="CycleSeconds"/> for a setup nobody can request from.
        /// </summary>
        public static float WorstCycleSeconds(Request r) =>
            CycleSeconds(r) * (AsyncSyncBuilder.RequestableTargets(r).Count > 0 ? 2f : 1f);

        /// <summary>
        /// The longest a target can go without being sent, from the actual schedule: the
        /// widest gap between two steps that carry it, counted around the wrap, × the step.
        /// This is what the wizard shows per row — the honest number, after weight
        /// normalization and capping.
        ///
        /// The worst gap rather than the average one, because the question the number answers
        /// is how stale a remote's copy can be. Rate-derived passes spread their visits
        /// evenly, so for those the two agree; a cycle timed by hand can put both visits of a
        /// slot in the same half of the pass, and only the worst gap says so.
        ///
        /// Counted per target, not per slot: an explicit grid may name one target in several
        /// sets, and then its wait is shorter than any one of those slots' — nothing but the
        /// steps that actually carry it can say by how much.
        /// </summary>
        public static Dictionary<string, float> RefreshIntervals(Request r)
        {
            var intervals = new Dictionary<string, float>();
            if (r == null) return intervals;
            var slots = BuildSlots(r);
            var schedule = EffectiveSchedule(r, slots);
            if (schedule.Count == 0) return intervals;

            var visits = new Dictionary<string, List<int>>();
            for (int step = 0; step < schedule.Count; step++)
                foreach (var name in slots[schedule[step]].targets)
                {
                    if (!visits.TryGetValue(name, out var seen))
                        visits[name] = seen = new List<int>();
                    seen.Add(step);
                }

            foreach (var entry in visits)
            {
                var seen = entry.Value;
                // Seeded with the gap that closes the ring, which for a single visit is the
                // whole pass — exactly the right answer for a target sent once.
                int gap = seen[0] + schedule.Count - seen[seen.Count - 1];
                for (int j = 1; j < seen.Count; j++)
                    gap = Mathf.Max(gap, seen[j] - seen[j - 1]);
                intervals[entry.Key] = gap * r.stepSeconds;
            }
            return intervals;
        }

        /// <summary>
        /// Slots that can still be added without spending another synced bit. The Bool index
        /// only grows at powers of two, so the tail of each range is free; an Int index has room
        /// for 255 slots from the start.
        ///
        /// Counted in index values, which is a slot each until a clock is paying for a repeat —
        /// a new slot that ends up beside itself then takes two of these rather than one.
        /// </summary>
        public static int FreeSlots(Request r)
        {
            if (r?.targets == null || ResolveEncoding(r) != IndexEncoding.Bool) return 0;
            int count = Mathf.Max(2, IndexValues(r));
            int capacity = 1 << NetworkSyncBuilder.BitsRequired(count);
            return Mathf.Max(0, capacity - count);
        }

        /// <summary>Synced bits the generated parameters will occupy.</summary>
        public static int CompressedBits(Request r)
        {
            // A clock is free under the Int index — 8 bits hold 255 values however they are
            // shared out — and under the Bool index costs a bit only when the phases push the
            // count past the power of two the slots alone sat under.
            int bits = ResolveEncoding(r) == IndexEncoding.Int
                ? 8
                : NetworkSyncBuilder.BitsRequired(Mathf.Max(2, IndexValues(r)));
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
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in r.targets)
            {
                var parameter = byName.Find(name);
                if (parameter == null) continue;
                bits += parameter.type == AnimatorControllerParameterType.Bool ? 1 : 8;
            }
            return bits;
        }

        internal static List<AnimatorControllerParameterType> ChannelTypes(Request r)
        {
            var types = new List<AnimatorControllerParameterType>();
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in r.targets)
            {
                var parameter = byName.Find(name);
                if (parameter != null && !types.Contains(parameter.type))
                    types.Add(parameter.type);
            }
            return types;
        }
    }
}
