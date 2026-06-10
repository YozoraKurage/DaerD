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
        public void CrossProduct_ConnectsEverySourceToEveryTarget()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var x = sm.AddState("X");
            var y = sm.AddState("Y");

            var created = TransitionBatch.CrossProduct(new[] { a, b }, new[] { x, y });

            Assert.AreEqual(4, created.Count);
            Assert.AreEqual(2, a.transitions.Length);
            Assert.AreEqual(2, b.transitions.Length);
            Assert.AreEqual(x, a.transitions[0].destinationState);
            Assert.AreEqual(y, a.transitions[1].destinationState);
            Assert.AreEqual(x, b.transitions[0].destinationState);
            Assert.AreEqual(y, b.transitions[1].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void CrossProduct_OverlappingSets_SkipsSelfTransitions()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");

            var created = TransitionBatch.CrossProduct(new[] { a, b }, new[] { a, b });

            // A→B and B→A only; A→A and B→B are skipped.
            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(1, a.transitions.Length);
            Assert.AreEqual(1, b.transitions.Length);
            Assert.AreEqual(b, a.transitions[0].destinationState);
            Assert.AreEqual(a, b.transitions[0].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void CrossProduct_WithEmptyOrNullSets_IsNoOp()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");

            Assert.AreEqual(0, TransitionBatch.CrossProduct(null, new[] { a }).Count);
            Assert.AreEqual(0, TransitionBatch.CrossProduct(new[] { a }, null).Count);
            Assert.AreEqual(0, TransitionBatch.CrossProduct(new AnimatorState[0], new[] { a }).Count);
            Assert.AreEqual(0, a.transitions.Length);

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
