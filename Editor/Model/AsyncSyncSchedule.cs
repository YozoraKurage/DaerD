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
        /// which is why the wizard lets it be arranged by hand. Int targets get one slot
        /// each; Float and Bool targets batch up to <see cref="Request.floatChannels"/> /
        /// <see cref="Request.boolChannels"/> per slot, but only with targets of the same
        /// type AND the same rate — a batch is revisited as a whole or not at all, and the
        /// channels a slot writes are typed. <see cref="Request.slotBreaks"/> is how a target
        /// declines the batch it would otherwise have joined.
        /// </summary>
        public static List<Slot> BuildSlots(Request r)
        {
            var slots = new List<Slot>();
            if (r?.targets == null || r.controller == null) return slots;

            int floatChannels = Mathf.Clamp(r.floatChannels, 1, 8);
            int boolChannels = Mathf.Clamp(r.boolChannels, 1, 8);
            // Keyed by (type, rate): one open batch per kind of slot. An Int's capacity of 1
            // fills its batch immediately, which is how it keeps a slot to itself.
            var open = new Dictionary<(AnimatorControllerParameterType, int), Slot>();

            foreach (var name in r.targets)
            {
                var parameter = DbtBuilder.FindParameter(r.controller, name);
                if (parameter == null) continue;
                int rate = r.RateOf(name);
                int capacity =
                    parameter.type == AnimatorControllerParameterType.Float ? floatChannels
                    : parameter.type == AnimatorControllerParameterType.Bool ? boolChannels
                    : 1;

                // A target marked as starting a slot refuses the open batch and opens its own,
                // which the targets after it may still join. That is the only say the author
                // has over which parameters share a step — and they must have one, because a
                // shared step is one driver copy: batched targets are sent together or not
                // at all, so no schedule can give them different timings.
                bool starts = r.slotBreaks != null && r.slotBreaks.Contains(name);
                var key = (parameter.type, rate);
                open.TryGetValue(key, out var slot);
                if (starts || slot == null || slot.targets.Count >= capacity)
                {
                    slot = new Slot { rate = rate };
                    slots.Add(slot);
                    open[key] = slot;
                }
                slot.targets.Add(name);
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

        /// <summary>
        /// A saved explicit cycle brought back into line with the request's current slots:
        /// steps naming a parameter that is no longer a target are dropped, slots nothing
        /// visits are given one, and a slot left beside itself is moved. Returns the repaired
        /// cycle as target names, or an empty list when there is nothing schedulable — which
        /// the caller reads as "fall back to the rates".
        ///
        /// Deliberately not folded into <see cref="ResolveScheduleOverride"/>: a recipe must
        /// keep being told about a cycle it cannot run (its author typed it), while the wizard
        /// must not dead-end on one the user is halfway through editing. Strict in the model,
        /// forgiving in the editor.
        /// </summary>
        public static List<string> RepairScheduleOverride(Request r, List<string> schedule)
        {
            var repaired = new List<string>();
            var slots = BuildSlots(r);
            if (slots.Count < 2 || schedule == null) return repaired;

            var slotOf = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                foreach (var name in slots[i].targets)
                    slotOf[name] = i;

            var steps = new List<int>();
            foreach (var name in schedule)
                if (slotOf.TryGetValue(name, out int slot))
                    steps.Add(slot);
            if (steps.Count == 0) return repaired;

            // An unvisited slot is appended rather than woven in: the tick list appends too,
            // so a parameter that was just added turns up where the eye already looks for it.
            // Appending cannot create a repeat either — the slot is absent by definition, so
            // neither the step before it nor the one it wraps onto can be it.
            var visited = new HashSet<int>(steps);
            for (int i = 0; i < slots.Count; i++)
                if (!visited.Contains(i))
                    steps.Add(i);

            // Only repeats that were already there can remain, and RepairAdjacency cannot
            // uncover a slot: the occurrence it drops as a last resort is by construction one
            // of two adjacent equals, so a second one is always left behind.
            RepairAdjacency(steps);

            // Dropping is how RepairAdjacency gives up, so a cycle it could not settle comes
            // back here still broken. Rates are a better answer than a layer that won't decode.
            if (steps.Count < 2) return repaired;
            for (int i = 0; i < steps.Count; i++)
                if (steps[i] == steps[(i + 1) % steps.Count])
                    return repaired;

            foreach (var step in steps) repaired.Add(slots[step].targets[0]);
            return repaired;
        }

        /// <summary>
        /// The slot to hand the next step to when a cycle is lengthened by hand: the
        /// least-visited one that touches neither end, so the step is valid where it lands.
        /// With only two slots no such slot exists — their cycle can only be even — and the
        /// fallback ignores the far end, leaving the wrap to
        /// <see cref="RepairScheduleOverride"/>. -1 when even that finds nothing.
        /// </summary>
        public static int NextStepSlot(List<int> steps, int slotCount)
        {
            if (slotCount <= 0) return -1;
            var visits = new int[slotCount];
            foreach (var step in steps)
                if (step >= 0 && step < slotCount) visits[step]++;
            int last = steps.Count > 0 ? steps[steps.Count - 1] : -1;
            int first = steps.Count > 0 ? steps[0] : -1;
            int picked = LeastVisited(visits, last, first);
            return picked >= 0 ? picked : LeastVisited(visits, last, -1);
        }

        static int LeastVisited(int[] visits, int avoid, int alsoAvoid)
        {
            int best = -1;
            for (int i = 0; i < visits.Length; i++)
            {
                if (i == avoid || i == alsoAvoid) continue;
                if (best < 0 || visits[i] < visits[best]) best = i;
            }
            return best;
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
