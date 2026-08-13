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
        DaerDLanguage _savedLanguage;

        // One test down here reads a warning by its English words; pin the language so the
        // suite passes on a Japanese editor too.
        [OneTimeSetUp]
        public void ForceEnglish()
        {
            _savedLanguage = L.Language;
            L.Language = DaerDLanguage.English;
        }

        [OneTimeTearDown]
        public void RestoreLanguage() => L.Language = _savedLanguage;

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

        // ---- repairing a hand-timed cycle ------------------------------------

        static AnimatorController Floats(params string[] names)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            foreach (var name in names)
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            return controller;
        }

        static AsyncSyncBuilder.Request Multiplexing(AnimatorController controller,
            params string[] targets)
        {
            var request = new AsyncSyncBuilder.Request { controller = controller };
            request.targets.AddRange(targets);
            return request;
        }

        /// <summary>What every repair must be able to promise: whatever comes back, if it is
        /// not empty, is a cycle the decoder can actually run.</summary>
        static List<string> Repair(AsyncSyncBuilder.Request request, params string[] schedule)
        {
            var repaired = AsyncSyncSchedule.RepairScheduleOverride(request,
                new List<string>(schedule));
            if (repaired.Count == 0) return repaired;

            var errors = new List<string>();
            request.scheduleOverride.Clear();
            request.scheduleOverride.AddRange(repaired);
            Assert.IsNotNull(
                AsyncSyncSchedule.ResolveScheduleOverride(request,
                    AsyncSyncSchedule.BuildSlots(request), errors),
                "a repaired cycle must resolve: " + string.Join(", ", errors));
            return repaired;
        }

        [Test]
        public void RepairScheduleOverride_LeavesAValidCycleAlone()
        {
            // The one that matters most: the wizard repairs on every edit, so a repair that
            // shuffled a cycle it had no quarrel with would move steps under the user's hand.
            var controller = Floats("A", "B", "C");
            var request = Multiplexing(controller, "A", "B", "C");

            CollectionAssert.AreEqual(new[] { "A", "B", "A", "C" },
                Repair(request, "A", "B", "A", "C"));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairScheduleOverride_DropsStepsForParametersThatAreGone()
        {
            var controller = Floats("A", "B", "C", "D");
            // D was unticked since the cycle was written; its step goes with it.
            var request = Multiplexing(controller, "A", "B", "C");

            CollectionAssert.AreEqual(new[] { "A", "B", "A", "C" },
                Repair(request, "A", "B", "A", "C", "D"));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairScheduleOverride_GivesANewlyTickedParameterAStep()
        {
            var controller = Floats("A", "B", "C");
            var request = Multiplexing(controller, "A", "B", "C");

            // C joined after the cycle was written. Appending matches the tick list, which
            // also appends — and a slot nothing visits cannot land beside itself.
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, Repair(request, "A", "B"));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairScheduleOverride_SettlesASlotThatBatchingMerged()
        {
            var controller = Floats("A", "B", "C");
            var request = Multiplexing(controller, "A", "B", "C");
            // Two channels put A and B in one slot, so a cycle that alternated them is now
            // asking for the same slot three times over, twice in a row.
            request.floatChannels = 2;

            var repaired = Repair(request, "A", "B", "A", "C");

            Assert.AreEqual(2, repaired.Count);
            CollectionAssert.AreEqual(new[] { "A", "C" }, repaired);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairScheduleOverride_WithNothingToSchedule_FallsBackToTheRates()
        {
            var controller = Floats("A", "B");
            // One slot has no cycle to speak of; an empty result is how the caller is told
            // to stop overriding.
            Assert.AreEqual(0,
                AsyncSyncSchedule.RepairScheduleOverride(Multiplexing(controller, "A"),
                    new List<string> { "A" }).Count);
            // Nothing recognisable left either.
            Assert.AreEqual(0,
                AsyncSyncSchedule.RepairScheduleOverride(Multiplexing(controller, "A", "B"),
                    new List<string> { "Gone" }).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void NextStepSlot_PicksTheLeastVisitedSlotThatTouchesNeitherEnd()
        {
            // 1 and 3 are free of both ends; 3 has been visited least.
            Assert.AreEqual(3, AsyncSyncSchedule.NextStepSlot(new List<int> { 0, 1, 0, 2 }, 4));
            Assert.AreEqual(1, AsyncSyncSchedule.NextStepSlot(new List<int> { 0, 1, 2 }, 3));
            // Two slots have no such slot — their cycle can only be even — so the far end is
            // given up and the wrap is left for the repair to settle.
            Assert.AreEqual(0, AsyncSyncSchedule.NextStepSlot(new List<int> { 0, 1 }, 2));
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

        // ---- steps written out as sets ---------------------------------------

        static GraphFrameData.AsyncSyncConfig.StepSpec Step(params string[] targets)
        {
            var step = new GraphFrameData.AsyncSyncConfig.StepSpec();
            step.targets.AddRange(targets);
            return step;
        }

        static AsyncSyncBuilder.Request Sending(AnimatorController controller,
            string[] targets, params string[][] steps)
        {
            var request = Multiplexing(controller, targets);
            foreach (var step in steps) request.steps.Add(Step(step));
            return request;
        }

        /// <summary>The grid's sets, one per step, as the names they normalize to.</summary>
        static List<string[]> Grid(List<GraphFrameData.AsyncSyncConfig.StepSpec> steps)
        {
            var grid = new List<string[]>();
            foreach (var step in steps) grid.Add(step.targets.ToArray());
            return grid;
        }

        static void AssertGrid(List<GraphFrameData.AsyncSyncConfig.StepSpec> steps,
            params string[][] expected)
        {
            var actual = Grid(steps);
            Assert.AreEqual(expected.Length, actual.Count,
                "step count: " + string.Join(" | ", actual.ConvertAll(s => string.Join(",", s))));
            for (int i = 0; i < expected.Length; i++)
                CollectionAssert.AreEqual(expected[i], actual[i], "step " + (i + 1));
        }

        [Test]
        public void BuildSlots_FromSteps_MakesTheDistinctSetsTheSlots()
        {
            var controller = Floats("A", "B", "C");
            // Two steps naming the same targets are one slot revisited, whatever order they
            // list them in — otherwise the second would be an index carrying an identical copy.
            var request = Sending(controller, new[] { "A", "B", "C" },
                new[] { "A", "B" }, new[] { "A", "C" }, new[] { "B", "A" });
            request.floatChannels = 2;

            var slots = AsyncSyncSchedule.BuildSlots(request);
            Assert.AreEqual(2, slots.Count);
            CollectionAssert.AreEqual(new[] { "A", "B" }, slots[0].targets,
                "the set is put back into target order, so it has one spelling");
            CollectionAssert.AreEqual(new[] { "A", "C" }, slots[1].targets);
            CollectionAssert.AreEqual(new[] { 0, 1, 0 },
                AsyncSyncSchedule.EffectiveSchedule(request, slots));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void BuildSlots_FromSteps_DropWhatIsNotMultiplexed_AndTakeOverTheOtherInputs()
        {
            var controller = Floats("A", "B", "C");
            var request = Sending(controller, new[] { "A", "B" },
                new[] { "A", "Gone" }, new[] { "B", "C" });
            // A grid answers what the rates and the cycle answered, so both are ignored: this
            // is a set of slots the greedy batching could not have produced anyway.
            request.rates["A"] = 4;
            request.slotBreaks.Add("B");
            request.scheduleOverride.AddRange(new[] { "B", "A", "B" });

            var slots = AsyncSyncSchedule.BuildSlots(request);
            Assert.AreEqual(2, slots.Count);
            CollectionAssert.AreEqual(new[] { "A" }, slots[0].targets, "'Gone' is not a target");
            CollectionAssert.AreEqual(new[] { "B" }, slots[1].targets, "'C' is not a target");
            Assert.AreEqual(1, slots[0].rate, "rates say nothing about a grid");
            CollectionAssert.AreEqual(new[] { 0, 1 },
                AsyncSyncSchedule.EffectiveSchedule(request, slots));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void EffectiveSchedule_FromSteps_IsReturnedEvenWhenItCannotRun()
        {
            var controller = Floats("A", "B");
            // Two steps sending the same set: the decoder would never see the second. The
            // editor draws this and Validate refuses it — falling back to the rates here
            // would redraw a cycle nobody asked for at the moment an edit went wrong.
            var request = Sending(controller, new[] { "A", "B" },
                new[] { "A" }, new[] { "A" }, new[] { "B" });

            CollectionAssert.AreEqual(new[] { 0, 0, 1 },
                AsyncSyncSchedule.EffectiveSchedule(request,
                    AsyncSyncSchedule.BuildSlots(request)));

            Object.DestroyImmediate(controller);
        }

        // ---- repairing a grid ------------------------------------------------

        [Test]
        public void RepairSteps_LeavesAValidGridAlone()
        {
            // The one that matters most, for the same reason as the cycle's repair: this runs
            // on every edit, and a grid it shuffled would move cells under the user's hand.
            var controller = Floats("A", "B", "C");
            var request = Sending(controller, new[] { "A", "B", "C" },
                new[] { "A", "B" }, new[] { "A", "C" });
            request.floatChannels = 2;

            var repaired = AsyncSyncSchedule.RepairSteps(request, request.steps);
            AssertGrid(repaired, new[] { "A", "B" }, new[] { "A", "C" });

            // And it is a fixed point: repairing the repair changes nothing further.
            AssertGrid(AsyncSyncSchedule.RepairSteps(request, repaired),
                new[] { "A", "B" }, new[] { "A", "C" });

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairSteps_DropsParametersThatAreGone_AndEmptiedSteps()
        {
            var controller = Floats("A", "B", "C");
            var request = Sending(controller, new[] { "A", "B" },
                new[] { "A" }, new[] { "C" }, new[] { "B", "C" });

            // C was unticked: its step goes with it, and the step it shared stays.
            AssertGrid(AsyncSyncSchedule.RepairSteps(request, request.steps),
                new[] { "A" }, new[] { "B" });

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairSteps_GivesANewlyTickedParameterAStepWithRoom()
        {
            var controller = Floats("A", "B", "C");
            var request = Sending(controller, new[] { "A", "B", "C" },
                new[] { "A" }, new[] { "B" });
            request.floatChannels = 2;

            // C joined after the grid was written and rides along in the first step that has
            // a channel free — which cannot make that step equal another, C being in no other.
            AssertGrid(AsyncSyncSchedule.RepairSteps(request, request.steps),
                new[] { "A", "C" }, new[] { "B" });

            // With no room anywhere it takes a step of its own instead.
            request.floatChannels = 1;
            AssertGrid(AsyncSyncSchedule.RepairSteps(request, request.steps),
                new[] { "A" }, new[] { "B" }, new[] { "C" });

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairSteps_TrimsAStepTheChannelsNoLongerHold()
        {
            var controller = Floats("A", "B", "C", "D");
            var request = Sending(controller, new[] { "A", "B", "C", "D" },
                new[] { "A", "B" }, new[] { "C", "D" });
            // The channel count came down to 1 after the grid was written: each step keeps the
            // target it named first, and the two that fell out get steps of their own.
            request.floatChannels = 1;

            AssertGrid(AsyncSyncSchedule.RepairSteps(request, request.steps),
                new[] { "A" }, new[] { "C" }, new[] { "B" }, new[] { "D" });

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairSteps_DropsAStepThatRepeatsItsNeighbour_IncludingAcrossTheWrap()
        {
            var controller = Floats("A", "B");
            var request = Sending(controller, new[] { "A", "B" },
                new[] { "A" }, new[] { "A" }, new[] { "B" }, new[] { "A" });

            // The run of A collapses to one, and the last A is the first A's neighbour across
            // the wrap, so it goes too.
            AssertGrid(AsyncSyncSchedule.RepairSteps(request, request.steps),
                new[] { "A" }, new[] { "B" });

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairSteps_WithNothingSchedulable_FallsBackToTheRates()
        {
            var controller = Floats("A", "B");
            // Every step sends the same thing: one slot is no cycle, and an empty result is
            // how the caller is told to stop overriding.
            var same = Sending(controller, new[] { "A" }, new[] { "A" }, new[] { "A" });
            Assert.AreEqual(0, AsyncSyncSchedule.RepairSteps(same, same.steps).Count);

            // Nothing recognisable left either.
            var stale = Sending(controller, new[] { "A", "B" }, new[] { "Gone" });
            Assert.AreEqual(0, AsyncSyncSchedule.RepairSteps(stale, stale.steps).Count);

            Object.DestroyImmediate(controller);
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

        // ---- the clock -------------------------------------------------------

        static AsyncSyncBuilder.Request Clocked() =>
            new AsyncSyncBuilder.Request { allowRepeatSteps = true };

        static bool HasAdjacency(List<int> schedule)
        {
            for (int i = 0; i < schedule.Count; i++)
                if (schedule.Count > 1 && schedule[i] == schedule[(i + 1) % schedule.Count])
                    return true;
            return false;
        }

        [Test]
        public void BuildClock_WithoutOne_LeavesTheIndexAsTheSlotNumber()
        {
            // The property everything else rests on: an unclocked setup runs the same code
            // and comes out of it exactly as it did before clocks existed.
            var clock = AsyncSyncSchedule.BuildClock(new AsyncSyncBuilder.Request(), Slots(1, 1, 1),
                new List<int> { 0, 1, 2 });
            CollectionAssert.AreEqual(new[] { 0, 0, 0 }, clock.stepPhases);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, clock.slotPhases);
            Assert.AreEqual(3, clock.indexValues);
            for (int i = 0; i < 3; i++) Assert.AreEqual(i, clock.Index(i, 0));
            Assert.IsTrue(clock.separates);
        }

        [Test]
        public void BuildClock_AlternatesOnlyWhereASlotSitsBesideItself()
        {
            var clock = AsyncSyncSchedule.BuildClock(Clocked(), Slots(1, 1, 1),
                new List<int> { 0, 0, 1, 2 });

            CollectionAssert.AreEqual(new[] { 0, 1, 0, 0 }, clock.stepPhases);
            CollectionAssert.AreEqual(new[] { 2, 1, 1 }, clock.slotPhases,
                "only the slot that repeats needs a second decoder state");
            // Phases laid end to end, so the slots that don't repeat cost one value each.
            Assert.AreEqual(4, clock.indexValues);
            Assert.AreEqual(0, clock.Index(0, 0));
            Assert.AreEqual(1, clock.Index(0, 1));
            Assert.AreEqual(2, clock.Index(1, 0));
            Assert.AreEqual(3, clock.Index(2, 0));
            Assert.IsTrue(clock.separates);
        }

        [Test]
        public void BuildClock_ColoursARunThatStraddlesTheWrap()
        {
            // Steps 3, 0 and 1 are one run of slot 0 read around the wrap. Walking the pass
            // from the step that STARTS a run is what gets all three coloured; a walk from
            // position 0 would have split the run and left the wrap repeating a phase.
            var clock = AsyncSyncSchedule.BuildClock(Clocked(), Slots(1, 1),
                new List<int> { 0, 0, 1, 0 });

            CollectionAssert.AreEqual(new[] { 1, 0, 0, 0 }, clock.stepPhases);
            CollectionAssert.AreEqual(new[] { 2, 1 }, clock.slotPhases);
            Assert.IsTrue(clock.separates);
        }

        [Test]
        public void BuildClock_CannotColourAnOddPassOfOneSlot()
        {
            // Every step sending one slot closes the alternation into a ring, and an odd ring
            // has no two-colouring — the one shape Validate has to refuse outright.
            Assert.IsFalse(
                AsyncSyncSchedule.BuildClock(Clocked(), Slots(1), new List<int> { 0, 0, 0 }).separates);
            Assert.IsFalse(
                AsyncSyncSchedule.BuildClock(Clocked(), Slots(1), new List<int> { 0 }).separates);

            var even = AsyncSyncSchedule.BuildClock(Clocked(), Slots(1), new List<int> { 0, 0 });
            Assert.IsTrue(even.separates);
            CollectionAssert.AreEqual(new[] { 0, 1 }, even.stepPhases);
            Assert.AreEqual(2, even.indexValues);
        }

        [Test]
        public void EffectiveSchedule_WithOneSlotAndAClock_RunsItTwice()
        {
            var slots = Slots(1);
            CollectionAssert.AreEqual(new[] { 0 },
                AsyncSyncSchedule.EffectiveSchedule(new AsyncSyncBuilder.Request(), slots),
                "unclocked, a lone slot is the single step Validate refuses");
            CollectionAssert.AreEqual(new[] { 0, 0 },
                AsyncSyncSchedule.EffectiveSchedule(Clocked(), slots),
                "the clock is the whole cycle when there is no other slot to alternate with");
        }

        [Test]
        public void BuildSchedule_WithAClock_LeavesTheRoundingArtefactWhereItLanded()
        {
            // ×3/×6/×7 is one of the mixes whose even spread rounds two visits of one slot
            // into neighbouring cells. Unclocked they are traded apart; with a clock there is
            // nothing to trade — and trading is what can cost a visit outright.
            var slots = Slots(3, 6, 7);
            var traded = AsyncSyncSchedule.BuildSchedule(slots);
            var asPlaced = AsyncSyncSchedule.BuildSchedule(slots, allowRepeats: true);

            Assert.IsFalse(HasAdjacency(traded), "the unclocked pass is repaired as it always was");
            Assert.IsTrue(HasAdjacency(asPlaced), "the clocked pass keeps the placement as spread");
            Assert.AreEqual(16, asPlaced.Count, "and every visit the weights asked for");
        }

        [Test]
        public void ResolveScheduleOverride_TakesARepeatWhenAClockPaysForIt()
        {
            var slots = Slots(1, 1, 1);
            var request = Clocked();
            request.scheduleOverride.AddRange(new[] { "P0", "P0", "P1", "P2" });

            var errors = new List<string>();
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 2 },
                AsyncSyncSchedule.ResolveScheduleOverride(request, slots, errors));
            CollectionAssert.IsEmpty(errors);
        }

        [Test]
        public void RepairScheduleOverride_KeepsARepeatWhenAClockPaysForIt()
        {
            var controller = Floats("A", "B", "C");
            var request = Multiplexing(controller, "A", "B", "C");
            request.allowRepeatSteps = true;

            CollectionAssert.AreEqual(new[] { "A", "A", "B", "C" },
                Repair(request, "A", "A", "B", "C"));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void RepairSteps_KeepsARepeatedStepWhenAClockPaysForIt()
        {
            var controller = Floats("A", "B");
            var request = Sending(controller, new[] { "A", "B" },
                new[] { "A" }, new[] { "A" }, new[] { "B" });
            request.allowRepeatSteps = true;

            // The run of A collapses without a clock (see the test above); with one it is the
            // pass its author drew, and the repair has nothing to say about it.
            AssertGrid(AsyncSyncSchedule.RepairSteps(request, request.steps),
                new[] { "A" }, new[] { "A" }, new[] { "B" });

            Object.DestroyImmediate(controller);
        }

        // ---- properties of the placement --------------------------------------
        //
        // The tests above pin the layouts the placement produces, one weight vector at a
        // time. These two say what must be true of ALL of them, over every weight vector up
        // to four slots — which is the only way to check a promise the wizard makes in the
        // abstract ("a bigger share means a shorter wait") against a placement that rounds,
        // probes for a free cell, and repairs adjacency afterwards.

        static string[] Names(int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = "P" + i;
            return names;
        }

        /// <summary>Every weight vector of the given length, weights 1..max. Enumerated rather
        /// than sampled: the space is small, and a property that fails on one vector in a
        /// hundred has to fail the same way on every run.</summary>
        static List<int[]> RateVectors(int count, int max)
        {
            var vectors = new List<int[]> { new int[count] };
            for (int i = 0; i < count; i++) vectors[0][i] = 1;
            for (int i = 0; i < count; i++)
            {
                var grown = new List<int[]>();
                foreach (var vector in vectors)
                    for (int rate = 1; rate <= max; rate++)
                    {
                        var next = (int[])vector.Clone();
                        next[i] = rate;
                        grown.Add(next);
                    }
                vectors = grown;
            }
            return vectors;
        }

        /// <summary>One target per slot, and a one-second step so an interval reads directly
        /// as a number of steps. Batching would merge the slots, and the placement is what
        /// these are about.</summary>
        static AsyncSyncBuilder.Request Weighted(AnimatorController controller, int[] rates)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                stepSeconds = 1f,
            };
            for (int i = 0; i < rates.Length; i++)
            {
                request.targets.Add("P" + i);
                if (rates[i] > 1) request.rates["P" + i] = rates[i];
            }
            return request;
        }

        /// <summary>
        /// The monotonicity the ×N control promises: raising one target's weight never makes
        /// that target's own worst wait longer. It can leave it alone — a weight the other
        /// slots cannot separate is capped, and a pass of two slots has nowhere to put a
        /// second visit — but it must never go the other way, because that is the one outcome
        /// nothing in the wizard would explain.
        ///
        /// Said of the target that was raised, not of the others: a longer pass is exactly
        /// what everyone else pays for it, and the label under the rows says so.
        /// </summary>
        [Test]
        public void RefreshIntervals_NeverLengthenTheWaitOfTheTargetWhoseWeightRose()
        {
            for (int count = 2; count <= 4; count++)
            {
                var controller = Floats(Names(count));
                foreach (var baseline in RateVectors(count, 2))
                    for (int raised = 0; raised < count; raised++)
                    {
                        float previous = float.MaxValue;
                        for (int weight = baseline[raised];
                             weight <= AsyncSyncBuilder.MaxRate; weight++)
                        {
                            var rates = (int[])baseline.Clone();
                            rates[raised] = weight;
                            float wait = AsyncSyncBuilder.RefreshIntervals(
                                Weighted(controller, rates))["P" + raised];
                            Assert.LessOrEqual(wait, previous + 0.001f,
                                "P" + raised + " waits longer at ×" + weight
                                + " than it did one step of weight ago: ["
                                + string.Join(",", rates) + "]");
                            previous = wait;
                        }
                    }
                Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// Every target gets the number of places its weight asked for, and none is ever left
        /// out of the pass. The count is the half of a weight that IS a promise — the spacing
        /// is not, as the test below this one says — and the wizard warns where the cap makes
        /// even the count impossible, so a silent loss here would be a warning that never
        /// fires.
        /// </summary>
        [Test]
        public void EffectiveSchedule_GivesEveryWeightThePlacesItAsksFor()
        {
            for (int count = 2; count <= 4; count++)
            {
                var controller = Floats(Names(count));
                foreach (var rates in RateVectors(count, 4))
                {
                    var request = Weighted(controller, rates);
                    var slots = AsyncSyncBuilder.BuildSlots(request);
                    var schedule = AsyncSyncBuilder.EffectiveSchedule(request, slots);
                    var weights = AsyncSyncBuilder.EffectiveWeights(slots);

                    var visits = new int[slots.Count];
                    foreach (int step in schedule) visits[step]++;
                    for (int i = 0; i < slots.Count; i++)
                        Assert.AreEqual(weights[i], visits[i],
                            "P" + i + " of [" + string.Join(",", rates)
                            + "] is not sent as often as its weight asks");
                }
                Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// How far the actual wait may fall behind the one a share of the pass suggests
        /// (ceil(T / k) steps for a slot visited k times out of T).
        ///
        /// It falls behind often, and not because the placement is careless: the pass is
        /// exactly as long as the weights add up to, so every window is a share of a cycle
        /// with no slack, and a set of such windows frequently has no arrangement at all —
        /// see the case pinned below. What must hold is that a weight never buys less than
        /// half of what it looks like it buys; below that the ×N control would be telling a
        /// story about freshness that the pass does not keep at all.
        ///
        /// The bound is checked over every weight vector up to four slots, which is the space
        /// it is claimed for. It is not a theorem about the placement — it is the line under
        /// which a rewrite of the placement loop has broken something.
        /// </summary>
        [Test]
        public void RefreshIntervals_NeverFallMoreThanHalfBehindTheWindowAShareSuggests()
        {
            for (int count = 2; count <= 4; count++)
            {
                var controller = Floats(Names(count));
                foreach (var rates in RateVectors(count, 4))
                {
                    var request = Weighted(controller, rates);
                    var windows = AsyncSyncBuilder.RefreshWindows(request);
                    var intervals = AsyncSyncBuilder.RefreshIntervals(request);
                    for (int i = 0; i < count; i++)
                        Assert.Less(intervals["P" + i], windows["P" + i] * 2f,
                            "P" + i + " of [" + string.Join(",", rates)
                            + "] waits " + intervals["P" + i] + " steps against a window of "
                            + windows["P" + i]);
                }
                Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// The smallest pass whose windows cannot all be met, pinned because the wizard now
        /// warns about the shape and the reason has to stay written down somewhere.
        ///
        /// Weights 1, 2 and 3 make a pass of six. The ×3 slot needs every other step, which
        /// leaves the other three steps two apart — and any two of three cells two apart are
        /// four apart across the wrap. So the ×2 slot waits four steps rather than the three
        /// its share suggests, and no arrangement does better. RefreshIntervals reports the
        /// four: it is measured off the pass that will be built, not off the pass the weights
        /// would like.
        /// </summary>
        [Test]
        public void RefreshIntervals_ReportTheWaitAPackedPassActuallyGives()
        {
            var controller = Floats("P0", "P1", "P2");
            var request = Weighted(controller, new[] { 1, 2, 3 });

            Assert.AreEqual(6f, AsyncSyncBuilder.CycleSeconds(request), 0.001f);
            Assert.AreEqual(3f, AsyncSyncBuilder.RefreshWindows(request)["P1"], 0.001f,
                "two places in a pass of six suggest three");
            Assert.AreEqual(4f, AsyncSyncBuilder.RefreshIntervals(request)["P1"], 0.001f,
                "and four is what the pass can give");

            Object.DestroyImmediate(controller);
        }

        /// <summary>The wizard says so, once the gap is half again as long as the share
        /// suggests — the point at which the number under the row and the ×N above it have
        /// stopped telling the same story.</summary>
        [Test]
        public void Warnings_CallOutAWeightTheOtherWeightsCannotSpaceOut()
        {
            var controller = Floats("P0", "P1", "P2", "P3");

            // [4,4,3,3]: fourteen steps, and the last slot placed lands on three of them with
            // nine steps between two of the sends.
            Assert.IsTrue(AsyncSyncBuilder.Warnings(Weighted(controller, new[] { 4, 4, 3, 3 }))
                .Exists(w => w.Contains("nowhere evenly spaced")));
            // Weights that divide the pass evenly have nothing to say.
            Assert.IsFalse(AsyncSyncBuilder.Warnings(Weighted(controller, new[] { 1, 1, 1, 1 }))
                .Exists(w => w.Contains("nowhere evenly spaced")));
            Assert.IsFalse(AsyncSyncBuilder.Warnings(Weighted(controller, new[] { 2, 1, 2, 1 }))
                .Exists(w => w.Contains("nowhere evenly spaced")));

            Object.DestroyImmediate(controller);
        }
    }
}
