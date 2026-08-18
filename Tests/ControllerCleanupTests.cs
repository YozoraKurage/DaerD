using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;

namespace Yozolab.DaerD.Tests
{
    public class ControllerCleanupTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        static void DestroyAll(params Object[] objects)
        {
            foreach (var o in objects)
                if (o != null) Object.DestroyImmediate(o);
        }

        // ---- clip index ------------------------------------------------------

        [Test]
        public void CollectClipUsages_FindsDirectAndBlendTreeClips()
        {
            var controller = NewController(out var sm);
            var walk = new AnimationClip { name = "Walk" };
            var run = new AnimationClip { name = "Run" };
            var a = sm.AddState("A");
            a.motion = walk;
            var tree = new BlendTree { name = "Move" };
            tree.AddChild(run);
            tree.AddChild(walk);
            var b = sm.AddState("B");
            b.motion = tree;

            var entries = ControllerCleanup.CollectClipUsages(controller);

            Assert.AreEqual(2, entries.Count);   // sorted by name: Run, Walk
            Assert.AreSame(run, entries[0].clip);
            Assert.AreEqual(1, entries[0].usages.Count);
            Assert.AreSame(b, entries[0].usages[0].state);
            StringAssert.Contains("Move", entries[0].usages[0].label);

            Assert.AreSame(walk, entries[1].clip);
            Assert.AreEqual(2, entries[1].usages.Count);   // A directly, B via the tree

            DestroyAll(controller, walk, run, tree);
        }

        [Test]
        public void CollectClipUsages_ListsAClipOncePerState_EvenAcrossSlots()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip { name = "C" };
            var tree = new BlendTree { name = "T" };
            tree.AddChild(clip);
            tree.AddChild(clip);
            var state = sm.AddState("S");
            state.motion = tree;

            var entries = ControllerCleanup.CollectClipUsages(controller);

            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(1, entries[0].usages.Count);

            DestroyAll(controller, clip, tree);
        }

        [Test]
        public void CollectClipUsages_CarriesTheDrillPathIntoSubStateMachines()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip { name = "C" };
            var sub = sm.AddStateMachine("Sub");
            var state = sub.AddState("S");
            state.motion = clip;

            var entries = ControllerCleanup.CollectClipUsages(controller);

            Assert.AreEqual(1, entries.Count);
            var usage = entries[0].usages[0];
            Assert.AreEqual(0, usage.layerIndex);
            Assert.AreEqual(2, usage.stateMachinePath.Count);
            Assert.AreSame(sm, usage.stateMachinePath[0]);
            Assert.AreSame(sub, usage.stateMachinePath[1]);
            Assert.AreEqual("Base / Sub / S", usage.label);

            DestroyAll(controller, clip);
        }

        [Test]
        public void CollectClipUsages_IncludesSyncedLayerOverrides()
        {
            var controller = NewController(out var sm);
            controller.AddLayer("Sync");
            var baseClip = new AnimationClip { name = "BaseClip" };
            var overrideClip = new AnimationClip { name = "OverrideClip" };
            var state = sm.AddState("S");
            state.motion = baseClip;

            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            layers[1].SetOverrideMotion(state, overrideClip);
            controller.layers = layers;

            var entries = ControllerCleanup.CollectClipUsages(controller);

            Assert.AreEqual(2, entries.Count);
            Assert.AreSame(baseClip, entries[0].clip);
            Assert.AreSame(overrideClip, entries[1].clip);
            var usage = entries[1].usages[0];
            // Navigation targets the source layer — the synced layer has no SM of its own.
            Assert.AreEqual(0, usage.layerIndex);
            Assert.AreSame(state, usage.state);
            StringAssert.Contains("(sync)", usage.label);

            DestroyAll(controller, baseClip, overrideClip);
        }

        // ---- clip replacement ------------------------------------------------

        [Test]
        public void ReplaceClip_SwapsStateBlendTreeAndSyncedReferences()
        {
            var controller = NewController(out var sm);
            controller.AddLayer("Sync");
            var from = new AnimationClip { name = "From" };
            var to = new AnimationClip { name = "To" };
            var other = new AnimationClip { name = "Other" };

            var a = sm.AddState("A");
            a.motion = from;
            var tree = new BlendTree { name = "T" };
            tree.children = new[]
            {
                new ChildMotion { motion = from, timeScale = 1f },
                new ChildMotion { motion = other, timeScale = 1f },
            };
            var b = sm.AddState("B");
            b.motion = tree;

            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            layers[1].SetOverrideMotion(b, from);
            controller.layers = layers;

            int replaced = ControllerCleanup.ReplaceClip(controller, from, to);

            Assert.AreEqual(3, replaced);
            Assert.AreSame(to, a.motion);
            Assert.AreSame(to, tree.children[0].motion);
            Assert.AreSame(other, tree.children[1].motion);   // untouched
            Assert.AreSame(to, controller.layers[1].GetOverrideMotion(b));

            DestroyAll(controller, from, to, other, tree);
        }

        [Test]
        public void ReplaceClip_ReturnsZero_ForNoOpArguments()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip { name = "C" };
            sm.AddState("A").motion = clip;

            Assert.AreEqual(0, ControllerCleanup.ReplaceClip(controller, clip, clip));
            Assert.AreEqual(0, ControllerCleanup.ReplaceClip(controller, clip, null));
            Assert.AreEqual(0, ControllerCleanup.ReplaceClip(controller, null, clip));

            DestroyAll(controller, clip);
        }

        // ---- leftover sub-assets ---------------------------------------------

        [Test]
        public void FindLeftovers_FlagsUnreachableSubAssets_AndKeepsReachableOnes()
        {
            var controller = NewController(out var sm);
            var used = new AnimationClip { name = "Used" };
            var state = sm.AddState("A");
            state.motion = used;
            var other = sm.AddState("B");
            var transition = state.AddTransition(other);
            var orphanTree = new BlendTree { name = "Old" };
            var orphanClip = new AnimationClip { name = "Gone" };
            var frameData = ScriptableObject.CreateInstance<GraphFrameData>();

            var all = new Object[]
                { controller, sm, state, other, transition, used, orphanTree, orphanClip, frameData };
            var leftovers = ControllerCleanup.FindLeftovers(controller, all);

            Assert.AreEqual(2, leftovers.Count);
            CollectionAssert.Contains(leftovers, orphanTree);
            CollectionAssert.Contains(leftovers, orphanClip);

            DestroyAll(controller, used, orphanTree, orphanClip, frameData);
        }

        [Test]
        public void FindLeftovers_KeepsNestedBlendTreeClips()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip { name = "Deep" };
            var inner = new BlendTree { name = "Inner" };
            inner.AddChild(clip);
            var outer = new BlendTree { name = "Outer" };
            outer.AddChild(inner);
            var state = sm.AddState("S");
            state.motion = outer;

            var all = new Object[] { controller, sm, state, outer, inner, clip };
            Assert.AreEqual(0, ControllerCleanup.FindLeftovers(controller, all).Count);

            DestroyAll(controller, clip, inner, outer);
        }

        [Test]
        public void FindLeftovers_KeepsAssetsOfOtherControllersInTheSameFile()
        {
            var controller = NewController(out _);
            var other = new AnimatorController();
            other.AddLayer("X");
            var otherSm = other.layers[0].stateMachine;
            var otherClip = new AnimationClip { name = "OtherClip" };
            var otherState = otherSm.AddState("O");
            otherState.motion = otherClip;

            var all = new Object[] { controller, other, otherSm, otherState, otherClip };
            Assert.AreEqual(0, ControllerCleanup.FindLeftovers(controller, all).Count);

            DestroyAll(controller, other, otherClip);
        }

        [Test]
        public void FindLeftovers_KeepsSyncedLayerOverrideMotions()
        {
            var controller = NewController(out var sm);
            controller.AddLayer("Sync");
            var overrideClip = new AnimationClip { name = "Override" };
            var state = sm.AddState("S");

            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            layers[1].SetOverrideMotion(state, overrideClip);
            controller.layers = layers;

            var all = new Object[] { controller, sm, state, overrideClip };
            Assert.AreEqual(0, ControllerCleanup.FindLeftovers(controller, all).Count);

            DestroyAll(controller, overrideClip);
        }

        [Test]
        public void FindLeftovers_KeepsTheDesignatedEmptyClip()
        {
            var controller = NewController(out _);
            var emptyClip = new AnimationClip { name = "Empty" };
            var frameData = ScriptableObject.CreateInstance<GraphFrameData>();
            frameData.emptyClip = emptyClip;

            var all = new Object[] { controller, frameData, emptyClip };
            Assert.AreEqual(0, ControllerCleanup.FindLeftovers(controller, all).Count);

            DestroyAll(controller, frameData, emptyClip);
        }

        // ---- exposed sub-assets ----------------------------------------------

        [Test]
        public void FindExposed_FlagsInUseSubAssetsThatLostTheirHiddenFlag()
        {
            var controller = NewController(out var sm);
            var state = sm.AddState("A");
            var driver = state.AddStateMachineBehaviour(typeof(CleanupTestBehaviour));
            driver.hideFlags = HideFlags.None;                     // what an old paste left behind
            var hiddenState = sm.AddState("B");
            hiddenState.hideFlags = HideFlags.HideInHierarchy;
            var clip = new AnimationClip { name = "Visible" };      // clips are meant to be visible

            var exposed = ControllerCleanup.FindExposed(new Object[] { controller, sm, state, hiddenState, driver, clip });

            CollectionAssert.Contains(exposed, driver);
            CollectionAssert.DoesNotContain(exposed, clip);
            CollectionAssert.DoesNotContain(exposed, hiddenState);
            CollectionAssert.DoesNotContain(exposed, controller);

            DestroyAll(controller, clip);
        }

        [Test]
        public void HideSubAssets_RestoresTheFlag_WithoutDestroyingAnything()
        {
            var controller = NewController(out var sm);
            var state = sm.AddState("A");
            var driver = state.AddStateMachineBehaviour(typeof(CleanupTestBehaviour));
            driver.hideFlags = HideFlags.None;

            int hidden = ControllerCleanup.HideSubAssets(controller, new Object[] { driver, null });

            Assert.AreEqual(1, hidden);
            Assert.IsFalse(driver == null, "hiding must not destroy the behaviour");
            Assert.AreNotEqual(HideFlags.None, driver.hideFlags & HideFlags.HideInHierarchy);
            Assert.AreEqual(1, state.behaviours.Length, "the state still uses it");
            Assert.AreEqual(0, ControllerCleanup.FindExposed(new Object[] { driver }).Count);

            DestroyAll(controller);
        }

        [Test]
        public void DeleteSubAssets_DestroysTheObjects_AndReportsTheCount()
        {
            var controller = NewController(out _);
            var orphan = new AnimationClip { name = "Gone" };

            int deleted = ControllerCleanup.DeleteSubAssets(controller, new Object[] { orphan, null });

            Assert.AreEqual(1, deleted);
            Assert.IsTrue(orphan == null);   // Unity fake-null after destroy

            DestroyAll(controller);
        }
    }
}
