using System.Collections.Generic;
using UnityEngine;
// The math is stated in AsyncSyncBuilder's vocabulary because it moved out of that class
// unchanged; aliasing the two DTOs keeps every signature reading as it always did.
using Clock = Yozolab.DaerD.AsyncSyncBuilder.Clock;
using Request = Yozolab.DaerD.AsyncSyncBuilder.Request;
using Slot = Yozolab.DaerD.AsyncSyncBuilder.Slot;
using StepSpec = Yozolab.DaerD.GraphFrameData.AsyncSyncConfig.StepSpec;

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
        ///
        /// All of that is the automatic answer to "which targets share a step". A request
        /// carrying <see cref="Request.steps"/> has answered it already, and
        /// <see cref="StepSlots"/> reads the answer instead.
        /// </summary>
        public static List<Slot> BuildSlots(Request r)
        {
            var slots = new List<Slot>();
            if (r?.targets == null || r.controller == null) return slots;
            if (r.steps != null && r.steps.Count > 0) return StepSlots(r);

            int floatChannels = Mathf.Clamp(r.floatChannels, 1, 8);
            int boolChannels = Mathf.Clamp(r.boolChannels, 1, 8);
            // Keyed by (type, rate): one open batch per kind of slot. An Int's capacity of 1
            // fills its batch immediately, which is how it keeps a slot to itself.
            var open = new Dictionary<(AnimatorControllerParameterType, int), Slot>();
            var byName = DbtBuilder.ParametersByName(r.controller);

            foreach (var name in r.targets)
            {
                var parameter = byName.Find(name);
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

        // ---- steps written out as sets ---------------------------------------

        /// <summary>
        /// The slots an explicit grid describes: each step is a set of targets, and the
        /// DISTINCT sets are the slots, numbered in first-appearance order — a step that
        /// sends the same targets as an earlier one is that same slot coming round again,
        /// which is exactly what the decoder's one state per slot means.
        ///
        /// Sets are matched after normalization, so {A,B} and {B,A} are one slot rather than
        /// two indices carrying identical copies. A step that names nothing is skipped
        /// instead of becoming an empty slot: the state names and the drivers both assume a
        /// slot has a target, and <see cref="AsyncSyncBuilder.Validate"/> refuses the grid
        /// for it anyway.
        /// </summary>
        static List<Slot> StepSlots(Request r)
        {
            var slots = new List<Slot>();
            var seen = new HashSet<string>();
            foreach (var step in r.steps)
            {
                var members = NormalizeStep(r, step);
                if (members.Count == 0 || !seen.Add(StepKey(r, members))) continue;
                var slot = new Slot();
                slot.targets.AddRange(members);
                slots.Add(slot);
            }
            return slots;
        }

        /// <summary>
        /// One step's targets in canonical form: the request's own target order, deduplicated,
        /// with names that are not multiplexed (or no longer exist) dropped — the same "a stale
        /// saved entry must not block regeneration" contract <see cref="Request.rates"/> keeps.
        /// Two steps name the same slot exactly when their canonical forms match.
        /// </summary>
        internal static List<string> NormalizeStep(Request r, StepSpec step) =>
            Order(r, step?.targets);

        static List<string> Order(Request r, List<string> members)
        {
            var ordered = new List<string>();
            if (members == null || r?.targets == null) return ordered;
            var wanted = new HashSet<string>(members);
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in r.targets)
                if (wanted.Contains(name) && !ordered.Contains(name)
                    && byName.Find(name) != null)
                    ordered.Add(name);
            return ordered;
        }

        /// <summary>A canonical step's identity as target positions rather than names: a
        /// parameter name may contain any character, and positions cannot collide.</summary>
        static string StepKey(Request r, List<string> members)
        {
            var positions = new List<string>();
            foreach (var name in members) positions.Add(r.targets.IndexOf(name).ToString());
            return string.Join(",", positions);
        }

        /// <summary>Whether two canonical steps send the same set.</summary>
        internal static bool SameStep(List<string> a, List<string> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Targets of one type a single step can carry: a channel each for Floats and
        /// Bools, and the type's single channel for everything else.</summary>
        internal static int StepCapacity(Request r, AnimatorControllerParameterType type) =>
            type == AnimatorControllerParameterType.Float ? Mathf.Clamp(r.floatChannels, 1, 8)
            : type == AnimatorControllerParameterType.Bool ? Mathf.Clamp(r.boolChannels, 1, 8)
            : 1;

        /// <summary>Whether one more target of this type would still fit in the step.</summary>
        internal static bool StepHasRoom(Request r, List<string> members,
            AnimatorControllerParameterType type)
        {
            int used = 0;
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in members)
            {
                var parameter = byName.Find(name);
                if (parameter != null && parameter.type == type) used++;
            }
            return used < StepCapacity(r, type);
        }

        /// <summary>
        /// The order the cycle visits the slots, as indices into <see cref="BuildSlots"/>.
        /// A slot of effective weight w appears w times per pass, at positions spread as
        /// evenly as rounding allows, so a ×2 slot sits near the opposite ends of the cycle
        /// rather than twice in a row. Weights are the slot rates after two corrections
        /// (see <see cref="EffectiveWeights"/>), and the result never places one slot in
        /// adjacent steps — including across the wrap — because the decoder's Any-State
        /// transitions have canTransitionToSelf off and would not re-trigger.
        ///
        /// <paramref name="allowRepeats"/> is that last clause paid off by a clock. The
        /// placement is unchanged — evenly spread is what a rate means, whatever is allowed —
        /// but the rounding artefact is left where it landed instead of repaired, because the
        /// repair's last resort is to DROP a visit and the clock would have shown it.
        /// </summary>
        public static List<int> BuildSchedule(List<Slot> slots, bool allowRepeats = false)
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

            if (!allowRepeats) RepairAdjacency(schedule);
            return schedule;
        }

        /// <summary>
        /// The clock over a pass: which phase each step sends, and how many phases each slot
        /// is therefore decoded in. With <see cref="Request.allowRepeatSteps"/> off every step
        /// is phase 0 and every slot has one phase — the index-is-the-slot-number the setup
        /// had before clocks existed — so the off path is this path with a degenerate table
        /// rather than a branch of its own.
        ///
        /// Phases alternate only where they must: a step takes the opposite phase of the one
        /// before it when the two send the same slot, and phase 0 otherwise, so a slot the
        /// pass never repeats keeps a single decoder state. Reading the pass from a step that
        /// STARTS a run leaves the wrap unconstrained, which is what lets one linear walk
        /// colour a run that straddles it. The single shape with no colouring is a pass
        /// sending one slot from end to end in an odd number of steps — an odd ring — and
        /// <see cref="Clock.separates"/> is how that gets refused instead of built.
        /// </summary>
        public static Clock BuildClock(Request r, List<Slot> slots, List<int> schedule)
        {
            int steps = schedule?.Count ?? 0;
            int count = slots?.Count ?? 0;
            var stepPhases = new int[steps];
            var slotPhases = new int[count];
            for (int i = 0; i < count; i++) slotPhases[i] = 1;
            if (r == null || !r.allowRepeatSteps || steps == 0)
                return new Clock(stepPhases, slotPhases, true);

            // A step whose predecessor sends another slot is where a run begins.
            int start = -1;
            for (int k = 0; k < steps && start < 0; k++)
                if (schedule[k] != schedule[(k - 1 + steps) % steps]) start = k;
            if (start < 0)
            {
                // No run begins anywhere, so every step sends the one slot and the alternation
                // is the plain parity of the position — which closes on the wrap only when the
                // pass is even. `separates` reports the odd one rather than papering over it.
                for (int k = 0; k < steps; k++) stepPhases[k] = k % 2;
            }
            else
            {
                for (int i = 1; i < steps; i++)
                {
                    int k = (start + i) % steps, previous = (start + i - 1) % steps;
                    stepPhases[k] = schedule[k] == schedule[previous]
                        ? 1 - stepPhases[previous] : 0;
                }
            }

            // Read back off the assignment rather than derived beside it: a slot needs its
            // second decoder state exactly when some step actually sends its phase 1, and the
            // pass is separated exactly when no neighbouring pair repeats slot AND phase.
            bool separates = true;
            for (int k = 0; k < steps; k++)
            {
                int next = (k + 1) % steps;
                if (schedule[k] == schedule[next] && stepPhases[k] == stepPhases[next])
                    separates = false;
                int slot = schedule[k];
                if (slot >= 0 && slot < count)
                    slotPhases[slot] = Mathf.Max(slotPhases[slot], stepPhases[k] + 1);
            }
            return new Clock(stepPhases, slotPhases, separates);
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
            // A clock alternates the index's phase between neighbours, so a slot beside itself
            // re-triggers after all — which is the whole of what the option buys.
            if (!r.allowRepeatSteps)
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
            // of two adjacent equals, so a second one is always left behind. A clock is the
            // one case with nothing to repair — a slot beside itself is what its author asked
            // for, and moving it would rewrite the cycle they wrote.
            if (!r.allowRepeatSteps) RepairAdjacency(steps);

            // Dropping is how RepairAdjacency gives up, so a cycle it could not settle comes
            // back here still broken. Rates are a better answer than a layer that won't decode.
            // The floor of two steps holds either way: one step is no cycle at all.
            if (steps.Count < 2) return repaired;
            if (!r.allowRepeatSteps)
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

        /// <summary>The schedule a request actually runs: an explicit grid first, then the
        /// explicit cycle when given (and valid), and the rate-based automatic one otherwise.</summary>
        public static List<int> EffectiveSchedule(Request r, List<Slot> slots)
        {
            if (r?.steps != null && r.steps.Count > 0) return StepSchedule(r, slots);
            if (r?.scheduleOverride != null && r.scheduleOverride.Count > 0)
            {
                var resolved = ResolveScheduleOverride(r, slots, null);
                if (resolved != null) return resolved;
            }
            // A single slot has no other slot to alternate with, so the clock IS the cycle:
            // two steps of the one slot, phases 0 and 1. Without a clock the pass stays the
            // single step BuildSchedule gives it, and Validate refuses the setup outright.
            if (r != null && r.allowRepeatSteps && slots != null && slots.Count == 1)
                return new List<int> { 0, 0 };
            return BuildSchedule(slots, r != null && r.allowRepeatSteps);
        }

        /// <summary>
        /// An explicit grid as slot indices, one per step. Returned as written even when the
        /// decoder could not run it — unlike <see cref="ResolveScheduleOverride"/>, which
        /// refuses and lets the caller fall back. The grid editor draws what this returns, and
        /// a view that jumped back to the rate-derived pass the instant an edit went wrong
        /// would redraw someone else's cycle under their hand. Refusing is
        /// <see cref="AsyncSyncBuilder.Validate"/>'s job, and it happens before anything is built.
        /// </summary>
        static List<int> StepSchedule(Request r, List<Slot> slots)
        {
            var schedule = new List<int>();
            var slotOf = new Dictionary<string, int>();
            for (int i = 0; i < slots.Count; i++)
                slotOf[StepKey(r, slots[i].targets)] = i;

            foreach (var step in r.steps)
            {
                var members = NormalizeStep(r, step);
                if (members.Count > 0 && slotOf.TryGetValue(StepKey(r, members), out int slot))
                    schedule.Add(slot);
            }
            return schedule;
        }

        /// <summary>
        /// A saved grid brought back into line with the request: steps lose the names that are
        /// no longer targets, a step carrying more of a type than the channels hold is trimmed
        /// to what fits, steps left with nothing go, a target no step sends is given one, and a
        /// step repeating its neighbour is dropped (unless a clock is paying for it). Returns
        /// the repaired grid, or an empty list when nothing schedulable is left — which the
        /// caller reads as "fall back to the rates", the same contract
        /// <see cref="RepairScheduleOverride"/> has.
        ///
        /// Termination is structural rather than argued: every stage is one bounded pass, and
        /// none of them loops to a fixed point. That is affordable because the stages are
        /// ordered so no later one can reopen an earlier one's work — trimming and dropping
        /// only remove; coverage only ever adds a target that NO step had, so the step it
        /// joins cannot come to equal another step; and collapsing runs of equal steps, last,
        /// only removes a step whose twin stays behind. A grid that was already valid passes
        /// through all three untouched, which is the property the editor leans on: the repair
        /// runs on every edit, and one that shuffled a grid it had no quarrel with would move
        /// cells under the user's hand.
        /// </summary>
        public static List<StepSpec> RepairSteps(Request r, List<StepSpec> steps)
        {
            var repaired = new List<StepSpec>();
            if (r?.targets == null || r.controller == null || steps == null) return repaired;

            var sets = new List<List<string>>();
            foreach (var step in steps)
            {
                var members = Trim(r, NormalizeStep(r, step));
                if (members.Count > 0) sets.Add(members);
            }
            if (sets.Count == 0) return repaired;

            Cover(r, sets);
            // A clock separates equal neighbours by phase, so there is nothing to collapse —
            // and collapsing anyway would delete steps the author drew on purpose.
            if (!r.allowRepeatSteps) Collapse(sets);
            // One step is not a cycle: the index would never change and the decoder would fire
            // once and go deaf. Rates are a better answer than a layer that won't decode.
            if (sets.Count < 2) return repaired;

            foreach (var members in sets)
            {
                var step = new StepSpec();
                step.targets.AddRange(members);
                repaired.Add(step);
            }
            return repaired;
        }

        /// <summary>One step cut down to what the channels can carry, keeping the targets it
        /// names first. Trimmed rather than refused so lowering a channel count after the fact
        /// costs the tail of a step instead of the whole grid.</summary>
        static List<string> Trim(Request r, List<string> members)
        {
            var kept = new List<string>();
            var used = new Dictionary<AnimatorControllerParameterType, int>();
            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in members)
            {
                var type = byName.Find(name).type;
                used.TryGetValue(type, out int count);
                if (count >= StepCapacity(r, type)) continue;
                used[type] = count + 1;
                kept.Add(name);
            }
            return kept;
        }

        /// <summary>Gives every target a step to ride in: the first step with room for its
        /// type, or a step of its own when none has. A target reaches this only by being in no
        /// step at all, so the step it joins gains something no other step has and cannot come
        /// to equal one — which is what lets <see cref="Collapse"/> run once, afterwards.</summary>
        static void Cover(Request r, List<List<string>> sets)
        {
            var covered = new HashSet<string>();
            foreach (var members in sets)
                foreach (var name in members) covered.Add(name);

            var byName = DbtBuilder.ParametersByName(r.controller);
            foreach (var name in r.targets)
            {
                var parameter = byName.Find(name);
                if (parameter == null || !covered.Add(name)) continue;
                var host = FindRoom(r, sets, parameter.type);
                if (host == null) sets.Add(host = new List<string>());
                host.Add(name);
            }
            // Appending put the newcomers at the end of their step; canonical order is what
            // the comparisons below (and the slots) read.
            for (int i = 0; i < sets.Count; i++) sets[i] = Order(r, sets[i]);
        }

        static List<string> FindRoom(Request r, List<List<string>> sets,
            AnimatorControllerParameterType type)
        {
            foreach (var members in sets)
                if (StepHasRoom(r, members, type)) return members;
            return null;
        }

        /// <summary>Drops each step that repeats the one before it, then the last when it
        /// repeats the first. One backward pass settles the run: whatever survives a run of
        /// equals differs from the step after the run by construction. The wrap needs one
        /// removal at most for the same reason — the step that becomes last already differed
        /// from the one it followed, which is the step that equalled the first.</summary>
        static void Collapse(List<List<string>> sets)
        {
            for (int i = sets.Count - 1; i > 0; i--)
                if (SameStep(sets[i], sets[i - 1])) sets.RemoveAt(i);
            if (sets.Count > 1 && SameStep(sets[0], sets[sets.Count - 1]))
                sets.RemoveAt(sets.Count - 1);
        }
    }
}
