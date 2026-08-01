using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class StateSearchTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        [Test]
        public void FindsStateByName_CaseInsensitive()
        {
            var controller = NewController(out var sm);
            var state = sm.AddState("WalkRun");
            sm.AddState("Idle");

            var results = StateSearch.Find(controller, "walk");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(state, results[0].target);
            Assert.AreEqual(0, results[0].layerIndex);
            Assert.AreEqual(sm, results[0].stateMachinePath[0]);
            StringAssert.Contains("WalkRun", results[0].label);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FindsStateInsideSubStateMachine_WithDrillPath()
        {
            var controller = NewController(out var sm);
            var child = sm.AddStateMachine("Child");
            var deep = child.AddState("Deep");

            var results = StateSearch.Find(controller, "Deep");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(deep, results[0].target);
            Assert.AreEqual(2, results[0].stateMachinePath.Count);
            Assert.AreEqual(sm, results[0].stateMachinePath[0]);
            Assert.AreEqual(child, results[0].stateMachinePath[1]);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FindsSubStateMachineByName_InItsParentsView()
        {
            var controller = NewController(out var sm);
            var child = sm.AddStateMachine("Combat");

            var results = StateSearch.Find(controller, "Combat");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(child, results[0].target);
            // The SSM node is shown in the parent's view, so the drill path stops at the parent.
            Assert.AreEqual(1, results[0].stateMachinePath.Count);
            Assert.AreEqual(sm, results[0].stateMachinePath[0]);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FindsStateByMotionName()
        {
            var controller = NewController(out var sm);
            var clip = new AnimationClip { name = "SwordSlash" };
            var state = sm.AddState("Attack");
            state.motion = clip;

            var results = StateSearch.Find(controller, "sword");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(state, results[0].target);
            StringAssert.Contains("SwordSlash", results[0].label);

            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SecondLayer_ReportsItsLayerIndex()
        {
            var controller = NewController(out _);
            controller.AddLayer("FX");
            var glow = controller.layers[1].stateMachine.AddState("Glow");

            var results = StateSearch.Find(controller, "Glow");

            Assert.AreEqual(1, results.Count);
            Assert.AreEqual(glow, results[0].target);
            Assert.AreEqual(1, results[0].layerIndex);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MaxResults_CapsTheList()
        {
            var controller = NewController(out var sm);
            for (int i = 0; i < 10; i++)
                sm.AddState("Item" + i);

            Assert.AreEqual(5, StateSearch.Find(controller, "Item", 5).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void BlankQuery_ReturnsNothing()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");

            Assert.AreEqual(0, StateSearch.Find(controller, "   ").Count);
            Assert.AreEqual(0, StateSearch.Find(null, "A").Count);

            Object.DestroyImmediate(controller);
        }
    }
}
