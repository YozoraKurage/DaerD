using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The split-by-type proposal: what it offers, what it refuses to offer, and what the
    /// rings it produces carry over from the setup they came out of.
    ///
    /// The refusals are the half worth testing hardest. A split is one-way — nothing puts two
    /// rings back into one — so an offer that should not have been made costs its author the
    /// setup they had.
    /// </summary>
    public class AsyncSyncSplitTests
    {
        DaerDLanguage _savedLanguage;

        // The advice is asserted by English substring; pin the language.
        [OneTimeSetUp]
        public void ForceEnglish()
        {
            _savedLanguage = L.Language;
            L.Language = DaerDLanguage.English;
        }

        [OneTimeTearDown]
        public void RestoreLanguage() => L.Language = _savedLanguage;

        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F1", AnimatorControllerParameterType.Float);
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            controller.AddParameter("B1", AnimatorControllerParameterType.Bool);
            controller.AddParameter("B2", AnimatorControllerParameterType.Bool);
            return controller;
        }

        static AsyncSyncBuilder.Request NewRequest(AnimatorController controller,
            params string[] targets)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
                encoding = AsyncSyncBuilder.IndexEncoding.Int,
                skipDrivers = true,
                addToStore = false,
                assignEmptyClip = false,
            };
            request.targets.AddRange(targets);
            return request;
        }

        static GraphFrameData.AsyncSyncConfig.StepSpec Step(params string[] targets)
        {
            var step = new GraphFrameData.AsyncSyncConfig.StepSpec();
            step.targets.AddRange(targets);
            return step;
        }

        // ---- what is on offer -------------------------------------------------

        [Test]
        public void ByType_GivesEachTypeItsOwnRing()
        {
            var request = NewRequest(NewController(), "F1", "B1", "F2", "B2");

            var split = AsyncSyncSplit.ByType(request);

            Assert.AreEqual(2, split.Count, "one ring per type");
            CollectionAssert.AreEqual(new List<string> { "F1", "F2" }, split[0].targets);
            CollectionAssert.AreEqual(new List<string> { "B1", "B2" }, split[1].targets);
            foreach (var one in split)
                Assert.IsNull(AsyncSyncBuilder.Validate(one), "every ring has to build");
        }

        /// <summary>The first ring keeps the name and the layer, so a split regenerates that
        /// layer instead of renaming every parameter the store already syncs.</summary>
        [Test]
        public void ByType_LeavesTheFirstRingWhereTheSetupWas()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1", "B2");
            request.layerIndex = 0;

            var split = AsyncSyncSplit.ByType(request);

            Assert.AreEqual("Async", split[0].baseName);
            Assert.AreEqual(0, split[0].layerIndex);
            Assert.AreNotEqual("Async", split[1].baseName, "two setups can't share a namespace");
            Assert.AreEqual(-1, split[1].layerIndex, "the other rings are new layers");
        }

        /// <summary>Every ring's pass is shorter than the one they came out of — which is the
        /// entire argument for spending another index.</summary>
        [Test]
        public void ByType_ShortensEveryPass()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1", "B2");
            float before = AsyncSyncBuilder.CycleSeconds(request);

            foreach (var one in AsyncSyncSplit.ByType(request))
                Assert.Less(AsyncSyncBuilder.CycleSeconds(one), before);
        }

        [Test]
        public void ByType_CarriesWeightsRequestsAndSlotBreaksOver()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1", "B2");
            request.rates["F2"] = 2;
            request.requestTargets.Add("B1");
            request.slotBreaks.Add("B2");

            var split = AsyncSyncSplit.ByType(request);

            Assert.AreEqual(2, split[0].RateOf("F2"));
            CollectionAssert.Contains(split[1].requestTargets, "B1");
            CollectionAssert.Contains(split[1].slotBreaks, "B2");
            CollectionAssert.DoesNotContain(split[0].requestTargets, "B1",
                "a ring must not carry another ring's targets");
        }

        /// <summary>A group inside one type survives the split with the ring it belongs to.</summary>
        [Test]
        public void ByType_KeepsAGroupThatLivesInsideOneType()
        {
            var controller = NewController();
            controller.AddParameter("B3", AnimatorControllerParameterType.Bool);
            var request = NewRequest(controller, "F1", "F2", "B1", "B2", "B3");
            var group = new GraphFrameData.AsyncSyncConfig.SyncGroup { name = "Pair" };
            group.members.AddRange(new[] { "B1", "B2" });
            request.groups.Add(group);

            var split = AsyncSyncSplit.ByType(request);

            Assert.AreEqual(0, split[0].groups.Count);
            Assert.AreEqual(1, split[1].groups.Count);
            CollectionAssert.AreEqual(new List<string> { "B1", "B2" }, split[1].groups[0].members);
        }

        // ---- what is refused --------------------------------------------------

        /// <summary>A step that mixes types has said those targets are sent together — one
        /// driver copies them in one go — and no arrangement of two rings can promise it.</summary>
        [Test]
        public void ByType_IsSilentWhenAStepMixesTypes()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1", "B2");
            request.steps.Add(Step("F1", "B1"));
            request.steps.Add(Step("F2", "B2"));

            Assert.IsTrue(AsyncSyncSplit.MixesTypes(request));
            Assert.IsEmpty(AsyncSyncSplit.ByType(request));
        }

        /// <summary>The case groups exist for: members whose types differ cannot share a step,
        /// and a split would leave each ring committing half of the set on its own.</summary>
        [Test]
        public void ByType_IsSilentWhenAGroupSpansTypes()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1", "B2");
            var group = new GraphFrameData.AsyncSyncConfig.SyncGroup { name = "Both" };
            group.members.AddRange(new[] { "F1", "B1" });
            request.groups.Add(group);

            Assert.IsTrue(AsyncSyncSplit.GroupsSpanTypes(request));
            Assert.IsEmpty(AsyncSyncSplit.ByType(request));
        }

        /// <summary>One target of a type is not a ring: it would be a single slot, and an index
        /// that never changes decodes exactly once.</summary>
        [Test]
        public void ByType_IsSilentWhenATypeCannotFillARingOfItsOwn()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1");

            Assert.IsEmpty(AsyncSyncSplit.ByType(request));
        }

        [Test]
        public void ByType_IsSilentForASetupOfOneType()
        {
            Assert.IsEmpty(AsyncSyncSplit.ByType(NewRequest(NewController(), "F1", "F2")));
        }

        // ---- the advice and the doing of it ------------------------------------

        [Test]
        public void Warnings_ProposeTheSplitWithBothNumbers()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1", "B2");

            var advice = AsyncSyncBuilder.Warnings(request)
                .Find(w => w.Contains("One ring per type"));

            Assert.IsNotNull(advice, "the proposal is what the wizard draws its button under");
            StringAssert.Contains("synced bit", advice, "the price is part of the proposal");
        }

        [Test]
        public void Warnings_DoNotProposeASplitThatCannotBeBuilt()
        {
            var request = NewRequest(NewController(), "F1", "F2", "B1");

            Assert.IsFalse(AsyncSyncBuilder.Warnings(request)
                .Exists(w => w.Contains("One ring per type")));
        }

        /// <summary>A setup only persists on a controller with an asset to hold it, which is
        /// what these two tests need to see — the split's whole shape is "one saved setup
        /// becomes several".</summary>
        static AnimatorController OnDisk(string path)
        {
            AssetDatabase.CreateAsset(new AnimatorController(), path);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            controller.AddLayer("Base");
            controller.AddParameter("F1", AnimatorControllerParameterType.Float);
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            controller.AddParameter("B1", AnimatorControllerParameterType.Bool);
            controller.AddParameter("B2", AnimatorControllerParameterType.Bool);
            return controller;
        }

        [Test]
        public void Apply_BuildsEveryRingAndSavesEverySetup()
        {
            const string path = "Assets/DaerDAsyncSyncSplitTest.controller";
            var controller = OnDisk(path);
            try
            {
                int layers = controller.layers.Length;
                var request = NewRequest(controller, "F1", "F2", "B1", "B2");

                Assert.IsTrue(AsyncSyncSplit.Apply(AsyncSyncSplit.ByType(request)));

                var configs = GraphFrameData.GetAsyncSyncs(controller);
                Assert.AreEqual(2, configs.Count, "one saved setup per ring");
                CollectionAssert.AreEqual(new List<string> { "F1", "F2" }, configs[0].targets);
                CollectionAssert.AreEqual(new List<string> { "B1", "B2" }, configs[1].targets);
                Assert.AreEqual(layers + 2, controller.layers.Length, "a layer each");
                Assert.AreNotEqual(configs[0].baseName, configs[1].baseName);
                // Each ring syncs its own index; two setups sharing one would decode the
                // other's steps as their own.
                Assert.IsNotNull(DbtBuilder.FindParameter(controller,
                    AsyncSyncBuilder.IndexParameter(configs[0].baseName)));
                Assert.IsNotNull(DbtBuilder.FindParameter(controller,
                    AsyncSyncBuilder.IndexParameter(configs[1].baseName)));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        /// <summary>The ring keeping the layer regenerates it rather than adding one beside
        /// it — the whole reason it keeps the base name too.</summary>
        [Test]
        public void Apply_RegeneratesTheSetupsOwnLayerInPlace()
        {
            const string path = "Assets/DaerDAsyncSyncSplitPlaceTest.controller";
            var controller = OnDisk(path);
            try
            {
                Assert.IsTrue(AsyncSyncBuilder.Apply(
                    NewRequest(controller, "F1", "F2", "B1", "B2")));
                int layers = controller.layers.Length;
                var saved = GraphFrameData.GetAsyncSyncs(controller)[0];
                var machine = saved.layer;

                var again = NewRequest(controller, "F1", "F2", "B1", "B2");
                again.layerIndex = AsyncSyncBuilder.LayerIndexOf(controller, saved);
                Assert.IsTrue(AsyncSyncSplit.Apply(AsyncSyncSplit.ByType(again)));

                Assert.AreEqual(layers + 1, controller.layers.Length, "one new ring, not two");
                var configs = GraphFrameData.GetAsyncSyncs(controller);
                Assert.AreEqual(machine, configs[0].layer, "the first ring stayed where it was");
                CollectionAssert.AreEqual(new List<string> { "F1", "F2" }, configs[0].targets);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
