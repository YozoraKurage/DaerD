using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class TransitionBatchTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        [Test]
        public void Chain_ConnectsStatesInOrder()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");

            var created = TransitionBatch.Chain(new[] { a, b, c });

            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(b, a.transitions[0].destinationState);
            Assert.AreEqual(c, b.transitions[0].destinationState);
            Assert.AreEqual(0, c.transitions.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FanOut_ConnectsSourceToEveryTarget_SkippingSelf()
        {
            var controller = NewController(out var sm);
            var hub = sm.AddState("Hub");
            var b = sm.AddState("B");
            var c = sm.AddState("C");

            var created = TransitionBatch.FanOut(hub, new[] { b, c, hub });

            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(2, hub.transitions.Length);
            Assert.AreEqual(b, hub.transitions[0].destinationState);
            Assert.AreEqual(c, hub.transitions[1].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FanIn_ConnectsEverySourceToTarget()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var target = sm.AddState("Target");

            var created = TransitionBatch.FanIn(new[] { a, b }, target);

            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(target, a.transitions[0].destinationState);
            Assert.AreEqual(target, b.transitions[0].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Chain_WithFewerThanTwoStates_IsNoOp()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");

            Assert.AreEqual(0, TransitionBatch.Chain(new[] { a }).Count);
            Assert.AreEqual(0, TransitionBatch.Chain(null).Count);
            Assert.AreEqual(0, a.transitions.Length);

            Object.DestroyImmediate(controller);
        }
    }
}
