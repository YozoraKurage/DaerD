using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The scheduling math on its own. AsyncSyncBuilderTests drives it through the facade and
    /// mostly asserts properties (counts, no adjacency); these pin the layouts it actually
    /// produces, which is what a rewrite of the placement loop would quietly change.
    /// </summary>
    public class AsyncSyncScheduleTests
    {
        /// <summary>Slots straight from rates. The placement only ever reads the rate, so the
        /// targets are just names to tell the slots apart — no controller needed.</summary>
        static List<AsyncSyncBuilder.Slot> Slots(params int[] rates)
        {
            var slots = new List<AsyncSyncBuilder.Slot>();
            for (int i = 0; i < rates.Length; i++)
            {
                var slot = new AsyncSyncBuilder.Slot { rate = rates[i] };
                slot.targets.Add("P" + i);
                slots.Add(slot);
            }
            return slots;
        }

        // ---- weights ---------------------------------------------------------

        [Test]
        public void Gcd_FoldsZero_AndFindsTheCommonFactor()
        {
            // Zero is the identity the weight fold starts from.
            Assert.AreEqual(6, AsyncSyncSchedule.Gcd(0, 6));
            Assert.AreEqual(5, AsyncSyncSchedule.Gcd(5, 0));
            Assert.AreEqual(6, AsyncSyncSchedule.Gcd(12, 18));
            Assert.AreEqual(2, AsyncSyncSchedule.Gcd(6, 4));
            Assert.AreEqual(1, AsyncSyncSchedule.Gcd(7, 13));
        }

        [Test]
        public void EffectiveWeights_DivideOutTheCommonFactor()
        {
            // All ×2 is the same cycle as all ×1, just twice the states.
            CollectionAssert.AreEqual(new[] { 1, 1, 1 },
                AsyncSyncSchedule.EffectiveWeights(Slots(2, 2, 2)));
            // 6 and 4 share 2 (-> 3 and 2), and 3 is then capped to what one other slot
            // can separate.
            CollectionAssert.AreEqual(new[] { 2, 2 },
                AsyncSyncSchedule.EffectiveWeights(Slots(6, 4)));
        }

        [Test]
        public void EffectiveWeights_CapWhatTheOtherSlotsCannotSeparate()
        {
            CollectionAssert.AreEqual(new[] { 1, 1 },
                AsyncSyncSchedule.EffectiveWeights(Slots(4, 1)));
            CollectionAssert.AreEqual(new[] { 2, 1, 1 },
                AsyncSyncSchedule.EffectiveWeights(Slots(8, 1, 1)));
            // A lone slot has nothing to be spaced against — and the cap loop used to spin
            // forever on it.
            CollectionAssert.AreEqual(new[] { 1 }, AsyncSyncSchedule.EffectiveWeights(Slots(4)));
            // Rates are clamped to 1..MaxRate before anything else looks at them, so a 0 out
            // of a stale saved setup can't collapse the pass.
            CollectionAssert.AreEqual(new[] { 1, 1 },
                AsyncSyncSchedule.EffectiveWeights(Slots(0, 1)));
        }

        // ---- placement -------------------------------------------------------

        [Test]
        public void BuildSchedule_SpreadsTheHeavySlotsOverThePass()
        {
            // Weights (2,1,1) over 4 steps: slot 0 claims 0 and 2, the light ones fill in.
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 },
                AsyncSyncSchedule.BuildSchedule(Slots(2, 1, 1)));
            // (4,2,1,1) over 8: slot 0 every other step, slot 1 in the two gaps furthest
            // apart, the ×1 slots in what is left.
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2, 0, 1, 0, 3 },
                AsyncSyncSchedule.BuildSchedule(Slots(4, 2, 1, 1)));
            // Capping happens before placement, so ×8 against two ×1 slots lands exactly
            // where ×2 would.
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 },
                AsyncSyncSchedule.BuildSchedule(Slots(8, 1, 1)));
            // Equal rates are the plain round robin, whatever factor they share.
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                AsyncSyncSchedule.BuildSchedule(Slots(2, 2, 2)));
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 1 },
                AsyncSyncSchedule.BuildSchedule(Slots(6, 4)));
        }

        [Test]
        public void BuildSchedule_NeverPutsOneSlotInAdjacentSteps()
        {
            // The decoder's Any-State routes have canTransitionToSelf off, so a repeat across
            // the wrap is as invisible as one in the middle: check the cycle, not the list.
            int[][] mixes =
            {
                new[] { 1, 1 }, new[] { 8, 1 }, new[] { 2, 1, 1 }, new[] { 3, 1, 1 },
                new[] { 2, 2, 1 }, new[] { 4, 2, 1, 1 }, new[] { 8, 8, 1, 1 },
                new[] { 5, 3, 2, 1 }, new[] { 2, 2, 1, 1, 1 }, new[] { 7, 7, 7, 7, 7 },
            };
            foreach (var rates in mixes)
            {
                var schedule = AsyncSyncSchedule.BuildSchedule(Slots(rates));
                string mix = string.Join(",", rates);
                Assert.Greater(schedule.Count, 1, "rates " + mix + " must still fill a pass");
                for (int i = 0; i < schedule.Count; i++)
                    Assert.AreNotEqual(schedule[i], schedule[(i + 1) % schedule.Count],
                        "rates " + mix + " put a slot in adjacent steps");
            }
        }

        [Test]
        public void BuildSchedule_HandlesTheDegenerateCases()
        {
            CollectionAssert.IsEmpty(AsyncSyncSchedule.BuildSchedule(null));
            CollectionAssert.IsEmpty(AsyncSyncSchedule.BuildSchedule(new List<AsyncSyncBuilder.Slot>()));
            // One slot: the index would never change (Validate refuses that setup), but the
            // math still has to terminate — the wizard runs it on every repaint, including
            // right after the first box is ticked.
            CollectionAssert.AreEqual(new[] { 0 }, AsyncSyncSchedule.BuildSchedule(Slots(4)));
        }

        [Test]
        public void RepairAdjacency_SwapsTheDuplicateAway_AndIsThenAFixedPoint()
        {
            var schedule = new List<int> { 0, 0, 1, 2, 1, 2 };
            AsyncSyncSchedule.RepairAdjacency(schedule);
            CollectionAssert.AreEqual(new[] { 0, 2, 1, 0, 1, 2 }, schedule,
                "the repeated 0 trades places with the first cell that can host it");

            var again = new List<int>(schedule);
            AsyncSyncSchedule.RepairAdjacency(again);
            CollectionAssert.AreEqual(schedule, again, "a repaired cycle is left alone");
        }

        [Test]
        public void RepairAdjacency_DropsTheOccurrenceWhenNoSwapResolvesIt()
        {
            // Nothing in {0,0,1,2} can take the second 0 without landing next to the first,
            // so the extra visit is lost rather than left where the decoder would miss it.
            var schedule = new List<int> { 0, 0, 1, 2 };
            AsyncSyncSchedule.RepairAdjacency(schedule);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, schedule);
        }

        // ---- explicit schedules ----------------------------------------------

        [Test]
        public void EffectiveSchedule_TakesAValidOverride_AndFallsBackToTheRates()
        {
            var slots = Slots(1, 1, 1);
            var request = new AsyncSyncBuilder.Request();
            request.scheduleOverride.AddRange(new[] { "P0", "P1", "P0", "P2" });
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 },
                AsyncSyncSchedule.EffectiveSchedule(request, slots));

            // An override the decoder couldn't run is not an error here — Validate is what
            // blocks on it; this just goes back to the rate-based pass.
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(new[] { "P0", "P0", "P1", "P2" });
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                AsyncSyncSchedule.EffectiveSchedule(request, slots));

            request.scheduleOverride.Clear();
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                AsyncSyncSchedule.EffectiveSchedule(request, slots));
        }

        [Test]
        public void ResolveScheduleOverride_ReportsWhatTheDecoderCouldNotRun()
        {
            var slots = Slots(1, 1, 1);
            var request = new AsyncSyncBuilder.Request();
            var errors = new List<string>();

            request.scheduleOverride.AddRange(new[] { "P0", "P1", "Nope" });
            Assert.IsNull(AsyncSyncSchedule.ResolveScheduleOverride(request, slots, errors));
            Assert.AreEqual(1, errors.Count, "an unknown name stops at the first one");

            errors.Clear();
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(new[] { "P0", "P1" });
            Assert.IsNull(AsyncSyncSchedule.ResolveScheduleOverride(request, slots, errors),
                "P2 would never be sent");
            Assert.AreEqual(1, errors.Count);

            errors.Clear();
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(new[] { "P0", "P1", "P2", "P2" });
            Assert.IsNull(AsyncSyncSchedule.ResolveScheduleOverride(request, slots, errors),
                "P2 in adjacent steps would not re-trigger the decoder");
            Assert.AreEqual(1, errors.Count);

            errors.Clear();
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(new[] { "P0", "P1", "P0", "P2" });
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 },
                AsyncSyncSchedule.ResolveScheduleOverride(request, slots, errors));
            CollectionAssert.IsEmpty(errors);
        }

        // ---- slots -----------------------------------------------------------

        [Test]
        public void BuildSlots_BatchesFloatsOfTheSameRate_AndIgnoresNamesThatAreGone()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("G", AnimatorControllerParameterType.Float);
            controller.AddParameter("H", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);

            var request = new AsyncSyncBuilder.Request { controller = controller, floatChannels = 2 };
            request.targets.AddRange(new[] { "F", "G", "Gone", "H", "B" });
            request.rates["H"] = 2;

            var slots = AsyncSyncSchedule.BuildSlots(request);
            Assert.AreEqual(3, slots.Count);
            CollectionAssert.AreEqual(new[] { "F", "G" }, slots[0].targets,
                "two ×1 Floats ride the channels together");
            CollectionAssert.AreEqual(new[] { "H" }, slots[1].targets,
                "a ×2 Float can't share a slot with ×1 ones — a batch is revisited whole");
            Assert.AreEqual(2, slots[1].rate);
            CollectionAssert.AreEqual(new[] { "B" }, slots[2].targets);

            Object.DestroyImmediate(controller);
        }
    }
}
