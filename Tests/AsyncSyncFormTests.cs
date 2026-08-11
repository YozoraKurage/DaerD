using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The setup form as the model sees it: a saved setup goes in through
    /// <see cref="AsyncSyncForm.LoadConfig"/> and a request comes out of
    /// <see cref="AsyncSyncForm.BuildRequest"/>, and what happens in between must not lose
    /// anything the form has no control for. Only that path is exercised — the drawing needs
    /// an IMGUI event loop — but it is the path an Apply runs through, which is where a
    /// dropped field turns into a rebuilt layer and an overwritten setup.
    /// </summary>
    public class AsyncSyncFormTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);
            return controller;
        }

        static GraphFrameData.AsyncSyncConfig Config(params string[] schedule)
        {
            return new GraphFrameData.AsyncSyncConfig
            {
                baseName = "Async",
                encoding = (int)AsyncSyncBuilder.IndexEncoding.Int,
                stepSeconds = 0.3f,
                floatChannels = 1,
                boolChannels = 1,
                targets = new List<string> { "F", "B", "I" },
                schedule = new List<string>(schedule),
            };
        }

        static AsyncSyncBuilder.Request Rebuild(AnimatorController controller,
            GraphFrameData.AsyncSyncConfig config)
        {
            var form = new AsyncSyncForm();
            form.SetController(controller);
            form.LoadConfig(config);
            return form.BuildRequest(-1);
        }

        /// <summary>
        /// The one that matters: a setup carrying an explicit cycle — the only way to get one
        /// is c.AsyncSync().Schedule(…), the wizard having no editor for it — must come back
        /// out of the form exactly as it went in. Opening the panel and pressing Apply without
        /// touching anything used to rebuild the layer on the rate-derived pass and then save
        /// the empty cycle over the author's, losing it from the saved setup as well.
        /// </summary>
        [Test]
        public void ACarriedCycle_SurvivesLoadAndRebuild_Verbatim()
        {
            var controller = NewController();
            var request = Rebuild(controller, Config("F", "B", "I", "B"));

            CollectionAssert.AreEqual(new[] { "F", "B", "I", "B" }, request.scheduleOverride);
            // And is what actually gets built, rather than merely carried alongside: four
            // steps is the cycle's length, where the rates would have given three.
            var slots = AsyncSyncBuilder.BuildSlots(request);
            Assert.AreEqual(4, AsyncSyncBuilder.EffectiveSchedule(request, slots).Count);

            request.skipDrivers = true;   // the SDK is not what this test is about
            Assert.IsNull(AsyncSyncBuilder.Validate(request));

            Object.DestroyImmediate(controller);
        }

        /// <summary>A grid says which targets share a step as well as when, so it outranks the
        /// cycle — the same precedence <see cref="AsyncSyncSchedule.EffectiveSchedule"/> keeps.
        /// The cycle is still carried, not dropped: it is the saved setup's, and only the user
        /// leaving the grid should decide its fate.</summary>
        [Test]
        public void AGridOutranksACarriedCycle()
        {
            var controller = NewController();
            var config = Config("F", "B", "I", "B");
            config.steps = new List<GraphFrameData.AsyncSyncConfig.StepSpec>
            {
                new GraphFrameData.AsyncSyncConfig.StepSpec { targets = { "F" } },
                new GraphFrameData.AsyncSyncConfig.StepSpec { targets = { "B" } },
                new GraphFrameData.AsyncSyncConfig.StepSpec { targets = { "I" } },
            };

            var request = Rebuild(controller, config);
            CollectionAssert.IsEmpty(request.scheduleOverride);
            Assert.AreEqual(3, request.steps.Count);

            Object.DestroyImmediate(controller);
        }

        /// <summary>A cycle naming something that is no longer multiplexed is brought back
        /// into line rather than passed through to fail validation — the forgiving half of the
        /// contract <see cref="AsyncSyncSchedule.RepairScheduleOverride"/> documents.</summary>
        [Test]
        public void ACycleTheSlotsOutgrew_IsRepaired()
        {
            var controller = NewController();
            var request = Rebuild(controller, Config("F", "B", "I", "Gone"));

            CollectionAssert.AreEqual(new[] { "F", "B", "I" }, request.scheduleOverride);
            Assert.IsNotNull(
                AsyncSyncBuilder.ResolveScheduleOverride(
                    request, AsyncSyncBuilder.BuildSlots(request), null),
                "the repaired cycle must resolve against the current slots");

            Object.DestroyImmediate(controller);
        }

        /// <summary>Nothing schedulable left means the rates, which is how both repairs spell
        /// giving up — better than a layer the decoder cannot run.</summary>
        [Test]
        public void ACycleOfNothingButGoneNames_FallsBackToTheRates()
        {
            var controller = NewController();
            var request = Rebuild(controller, Config("Gone", "Also Gone"));

            CollectionAssert.IsEmpty(request.scheduleOverride);
            // The rate-derived pass is still there to fall back ON.
            var slots = AsyncSyncBuilder.BuildSlots(request);
            Assert.AreEqual(3, AsyncSyncBuilder.EffectiveSchedule(request, slots).Count);

            Object.DestroyImmediate(controller);
        }

        /// <summary>A setup that never had a cycle is unaffected: no override appears from
        /// nowhere, and the pass stays the rate-derived one those setups already had.</summary>
        [Test]
        public void ASetupWithoutACycle_StaysOnTheRates()
        {
            var controller = NewController();
            var request = Rebuild(controller, Config());

            CollectionAssert.IsEmpty(request.scheduleOverride);
            CollectionAssert.IsEmpty(request.steps);

            Object.DestroyImmediate(controller);
        }

        /// <summary>The saved slot breaks reach the request. The draw path's own handling of
        /// them (a Split toggle the channel count has disabled must not write its greyed-out
        /// value back) needs an IMGUI event loop and is not covered here.</summary>
        [Test]
        public void SlotBreaksReachTheRequest()
        {
            var controller = NewController();
            var config = Config();
            config.floatChannels = 2;
            config.slotBreaks = new List<string> { "F" };

            CollectionAssert.AreEqual(new[] { "F" }, Rebuild(controller, config).slotBreaks);

            Object.DestroyImmediate(controller);
        }
    }
}
