using System.Collections.Generic;
using UnityEngine;
// The math is stated in AsyncSyncBuilder's vocabulary because it moved out of that class
// unchanged; aliasing the two DTOs keeps every signature reading as it always did.
using Request = Yozolab.DaerD.AsyncSyncBuilder.Request;
using Slot = Yozolab.DaerD.AsyncSyncBuilder.Slot;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Which slots the async-sync cycle visits and in what order: the targets grouped into
    /// slots, and the per-target sync rates turned into a pass that never puts one slot in
    /// adjacent steps. Pure math over an <see cref="AsyncSyncBuilder.Request"/> — it reads
    /// the controller's parameter types and writes nothing — which is what lets the wizard
    /// call it on every repaint and the tests exercise it without an asset.
    /// </summary>
    static class AsyncSyncSchedule
    {
        /// <summary>
        /// Groups the targets into slots, in listed order — the order IS the cycle order,
        /// which is why the wizard lets it be arranged by hand. Bool / Int targets get one
        /// slot each; Float targets batch up to <see cref="Request.floatChannels"/> per slot,
        /// but only with Floats of the same rate — a batch is revisited as a whole or not
        /// at all.
        /// </summary>
        public static List<Slot> BuildSlots(Request r)
        {
            var slots = new List<Slot>();
            if (r?.targets == null || r.controller == null) return slots;

            int channels = Mathf.Clamp(r.floatChannels, 1, 8);
            var openFloats = new Dictionary<int, Slot>();

            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter == null) continue;
                int rate = r.RateOf(name);

                if (parameter.type != AnimatorControllerParameterType.Float)
                {
                    var slot = new Slot { rate = rate };
                    slot.targets.Add(name);
                    slots.Add(slot);
                    continue;
                }

                if (!openFloats.TryGetValue(rate, out var open)
                    || open.targets.Count >= channels)
                {
                    open = new Slot { rate = rate };
                    slots.Add(open);
                    openFloats[rate] = open;
                }
                open.targets.Add(name);
            }
            return slots;
        }

        /// <summary>
        /// The order the cycle visits the slots, as indices into <see cref="BuildSlots"/>.
        /// A slot of effective weight w appears w times per pass, at positions spread as
        /// evenly as rounding allows, so a ×2 slot sits near the opposite ends of the cycle
        /// rather than twice in a row. Weights are the slot rates after two corrections
        /// (see <see cref="EffectiveWeights"/>), and the result never places one slot in
        /// adjacent steps — including across the wrap — because the decoder's Any-State
        /// transitions have canTransitionToSelf off and would not re-trigger.
        /// </summary>
        public static List<int> BuildSchedule(List<Slot> slots)
        {
            var schedule = new List<int>();
            if (slots == null || slots.Count == 0) return schedule;
            if (slots.Count == 1) { schedule.Add(0); return schedule; }

            var weights = EffectiveWeights(slots);
            int total = 0;
            foreach (var weight in weights) total += weight;

            // Heaviest slots claim their ideal (evenly spaced) positions first; lighter
            // ones probe forward from theirs. Stable: equal weights keep list order.
            var order = new List<int>();
            for (int i = 0; i < slots.Count; i++) order.Add(i);
            order.Sort((a, b) => weights[b] != weights[a] ? weights[b] - weights[a] : a - b);

            var cells = new int[total];
            for (int c = 0; c < total; c++) cells[c] = -1;
            foreach (int slot in order)
                for (int k = 0; k < weights[slot]; k++)
                {
                    int cell = Mathf.RoundToInt(k * total / (float)weights[slot]) % total;
                    while (cells[cell] >= 0) cell = (cell + 1) % total;
                    cells[cell] = slot;
                }
            schedule.AddRange(cells);

            RepairAdjacency(schedule);
            return schedule;
        }

        /// <summary>
        /// Slot rates turned into schedulable weights: divided by their common factor
        /// (all-×2 is the same cycle as all-×1, just twice the states), then any weight
        /// larger than the sum of the others is lowered to that sum — with fewer separating
        /// steps than occurrences, adjacency is unavoidable and the decoder would miss
        /// the repeats anyway.
        /// </summary>
        public static int[] EffectiveWeights(List<Slot> slots)
        {
            var weights = new int[slots.Count];
            for (int i = 0; i < slots.Count; i++)
                weights[i] = Mathf.Clamp(slots[i].rate, 1, AsyncSyncBuilder.MaxRate);
            // A lone slot has nothing to be spaced against — its weight is 1 by definition.
            // Bailing out also matters for termination: the cap condition below is always
            // "true" for a single slot (w > 0 others), which used to spin forever.
            if (weights.Length < 2)
            {
                for (int i = 0; i < weights.Length; i++) weights[i] = 1;
                return weights;
            }

            int gcd = 0;
            foreach (var weight in weights) gcd = Gcd(gcd, weight);
            if (gcd > 1)
                for (int i = 0; i < weights.Length; i++) weights[i] /= gcd;

            // Capping one weight can change the balance for another; loop to a fixed point.
            // `changed` is set only when a weight actually shrinks — flagging a no-op write
            // (cap already at the floor) would loop forever.
            for (bool changed = true; changed;)
            {
                changed = false;
                int total = 0;
                foreach (var weight in weights) total += weight;
                for (int i = 0; i < weights.Length; i++)
                {
                    int others = total - weights[i];
                    int capped = Mathf.Max(1, others);
                    if (weights[i] > others && weights[i] != capped)
                    {
                        weights[i] = capped;
                        changed = true;
                        break;
                    }
                }
            }
            return weights;
        }

        internal static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

        /// <summary>Fixes the rare rounding artefact where one slot landed in adjacent cells
        /// (cyclically): swap the duplicate with any cell that resolves it, or drop it.</summary>
        internal static void RepairAdjacency(List<int> schedule)
        {
            for (int guard = 0; guard < schedule.Count; guard++)
            {
                int bad = -1;
                for (int i = 0; i < schedule.Count && bad < 0; i++)
                    if (schedule.Count > 1 && schedule[i] == schedule[(i + 1) % schedule.Count])
                        bad = (i + 1) % schedule.Count;
                if (bad < 0) return;

                bool swapped = false;
                for (int j = 0; j < schedule.Count && !swapped; j++)
                {
                    if (schedule[j] == schedule[bad]) continue;
                    // The swap must fix `bad` without breaking j's own neighbourhood.
                    int before = (j - 1 + schedule.Count) % schedule.Count;
                    int after = (j + 1) % schedule.Count;
                    if (schedule[before] == schedule[bad] || schedule[after] == schedule[bad])
                        continue;
                    int badBefore = (bad - 1 + schedule.Count) % schedule.Count;
                    int badAfter = (bad + 1) % schedule.Count;
                    if (badBefore != j && schedule[badBefore] == schedule[j]) continue;
                    if (badAfter != j && schedule[badAfter] == schedule[j]) continue;

                    (schedule[bad], schedule[j]) = (schedule[j], schedule[bad]);
                    swapped = true;
                }
                if (!swapped) schedule.RemoveAt(bad);   // last resort: lose one occurrence
            }
        }

        /// <summary>Maps <see cref="Request.scheduleOverride"/> onto slot indices; errors
        /// (unknown name, uncovered slot, adjacent repeats) go to <paramref name="errors"/>.</summary>
        public static List<int> ResolveScheduleOverride(Request r, List<Slot> slots,
            List<string> errors)
        {
            var schedule = new List<int>();
            var slotOf = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOf[name] = i;

            foreach (var name in r.scheduleOverride)
            {
                if (!slotOf.TryGetValue(name, out int slot))
                {
                    errors?.Add(L.Tr("Schedule entry '{0}' is not a multiplexed parameter.", name));
                    return null;
                }
                schedule.Add(slot);
            }

            var visited = new HashSet<int>(schedule);
            if (visited.Count < slots.Count)
            {
                errors?.Add(L.Tr("The explicit schedule never visits some slots — every parameter must appear at least once."));
                return null;
            }
            for (int i = 0; i < schedule.Count; i++)
                if (schedule.Count > 1 && schedule[i] == schedule[(i + 1) % schedule.Count])
                {
                    errors?.Add(L.Tr("The explicit schedule puts one slot in adjacent steps (position {0}) — the decoder would not re-trigger.", i));
                    return null;
                }
            return schedule;
        }

        /// <summary>The schedule a request actually runs: the explicit override when given
        /// (and valid), the rate-based automatic one otherwise.</summary>
        public static List<int> EffectiveSchedule(Request r, List<Slot> slots)
        {
            if (r?.scheduleOverride != null && r.scheduleOverride.Count > 0)
            {
                var resolved = ResolveScheduleOverride(r, slots, null);
                if (resolved != null) return resolved;
            }
            return BuildSchedule(slots);
        }
    }
}
