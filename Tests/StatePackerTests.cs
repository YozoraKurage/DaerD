using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class StatePackerTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        [Test]
        public void Pack_MovesStatesIntoChild_AndKeepsTransitions()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A", new Vector3(0f, 0f, 0f));
            var b = sm.AddState("B", new Vector3(100f, 0f, 0f));
            var c = sm.AddState("C", new Vector3(200f, 0f, 0f));
            a.AddTransition(b);
            b.AddTransition(c);

            var child = StatePacker.Pack(sm, new[] { b, c }, "Packed");

            Assert.IsNotNull(child);
            Assert.AreEqual(1, sm.states.Length);            // only A stays
            Assert.AreEqual(a, sm.states[0].state);
            Assert.AreEqual(1, sm.stateMachines.Length);
            Assert.AreEqual(2, child.states.Length);
            Assert.AreEqual(b, child.defaultState);
            // Transitions survive: the boundary-crossing one and the internal one.
            Assert.AreEqual(b, a.transitions[0].destinationState);
            Assert.AreEqual(c, b.transitions[0].destinationState);
            // The parent's default state (A) was not packed, so it stays.
            Assert.AreEqual(a, sm.defaultState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Pack_DefaultState_MovesWithThePackedSet()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // becomes the layer default
            var b = sm.AddState("B");
            var c = sm.AddState("C");

            var child = StatePacker.Pack(sm, new[] { a, b });

            Assert.AreEqual(a, child.defaultState);          // packed set keeps its entry point
            Assert.AreEqual(c, sm.defaultState);             // parent falls back to a remaining state

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Unpack_RestoresStates_AndRepointsTransitionsToDefaultState()
        {
            var controller = NewController(out var sm);
            var x = sm.AddState("X", new Vector3(0f, 0f, 0f));
            var child = sm.AddStateMachine("Child", new Vector3(300f, 0f, 0f));
            var p = child.AddState("P", new Vector3(10f, 10f, 0f));
            var q = child.AddState("Q", new Vector3(10f, 80f, 0f));
            child.defaultState = p;
            x.AddTransition(child);                          // state → sub-SM transition
            child.AddAnyStateTransition(q);                  // any-state rule defined inside the child

            StatePacker.Unpack(sm, child, controller);

            Assert.AreEqual(3, sm.states.Length);            // X, P, Q
            Assert.AreEqual(0, sm.stateMachines.Length);
            // The transition into the child now goes to its former default state.
            Assert.AreEqual(1, x.transitions.Length);
            Assert.IsNull(x.transitions[0].destinationStateMachine);
            Assert.AreEqual(p, x.transitions[0].destinationState);
            // The child's Any State rule was recreated on the parent.
            Assert.AreEqual(1, sm.anyStateTransitions.Length);
            Assert.AreEqual(q, sm.anyStateTransitions[0].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Unpack_RewritesExitTransitions_WhenChildHadASingleExitTarget()
        {
            var controller = NewController(out var sm);
            var x = sm.AddState("X");
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.defaultState = p;
            p.AddExitTransition();
            sm.AddStateMachineTransition(child, x);          // leaving the child went to X

            StatePacker.Unpack(sm, child, controller);

            Assert.AreEqual(1, p.transitions.Length);
            Assert.IsFalse(p.transitions[0].isExit);
            Assert.AreEqual(x, p.transitions[0].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void PackThenUnpack_RoundTripsTheStateSet()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            b.AddTransition(c);

            var child = StatePacker.Pack(sm, new[] { b, c });
            StatePacker.Unpack(sm, child, controller);

            Assert.AreEqual(3, sm.states.Length);
            Assert.AreEqual(0, sm.stateMachines.Length);
            Assert.AreEqual(a, sm.defaultState);
            Assert.AreEqual(c, b.transitions[0].destinationState);

            Object.DestroyImmediate(controller);
        }
    }
}
