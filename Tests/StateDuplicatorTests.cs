using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class StateDuplicatorTests
    {
        static Vector3 PositionOf(AnimatorStateMachine sm, AnimatorState state)
        {
            foreach (var cs in sm.states)
                if (cs.state == state) return cs.position;
            return Vector3.negativeInfinity;
        }

        [Test]
        public void Duplicate_CopiesFields_AndOffsetsPosition()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A", new Vector3(100f, 200f, 0f));
            a.speed = 2.5f;
            a.tag = "tagged";
            a.writeDefaultValues = false;

            var created = StateDuplicator.Duplicate(sm, new[] { a }, new Vector2(40f, 40f));

            Assert.AreEqual(1, created.Count);
            var copy = created[0];
            Assert.AreNotSame(a, copy);
            Assert.AreEqual("A 1", copy.name);                 // unique within the state machine
            Assert.AreEqual(2.5f, copy.speed, 1e-4f);
            Assert.AreEqual("tagged", copy.tag);
            Assert.IsFalse(copy.writeDefaultValues);
            Assert.AreEqual(140f, PositionOf(sm, copy).x, 1e-4f);
            Assert.AreEqual(240f, PositionOf(sm, copy).y, 1e-4f);
            // The original is untouched.
            Assert.AreEqual(100f, PositionOf(sm, a).x, 1e-4f);
            Assert.AreEqual(0, a.transitions.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_ReplicatesTransitionsInsideTheSet_AndSkipsExternalOnes()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var outside = sm.AddState("Outside");
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);

            var inner = a.AddTransition(b);
            inner.AddCondition(AnimatorConditionMode.If, 0f, "P");
            inner.duration = 0.5f;
            a.AddTransition(outside);   // crosses the duplicated set boundary

            var created = StateDuplicator.Duplicate(sm, new[] { a, b }, new Vector2(40f, 40f));

            Assert.AreEqual(2, created.Count);
            var copyA = created[0];
            var copyB = created[1];
            Assert.AreEqual(1, copyA.transitions.Length);          // only the internal one
            Assert.AreEqual(copyB, copyA.transitions[0].destinationState);
            Assert.AreEqual(0.5f, copyA.transitions[0].duration, 1e-4f);
            Assert.AreEqual(1, copyA.transitions[0].conditions.Length);
            Assert.AreEqual("P", copyA.transitions[0].conditions[0].parameter);
            // The originals keep both transitions.
            Assert.AreEqual(2, a.transitions.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_SkipsStatesNotInTheStateMachine()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Other");
            var sm = controller.layers[0].stateMachine;
            var foreign = controller.layers[1].stateMachine.AddState("Foreign");

            var created = StateDuplicator.Duplicate(sm, new[] { foreign, null }, Vector2.zero);

            Assert.AreEqual(0, created.Count);
            Assert.AreEqual(0, sm.states.Length);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MakeUniqueName_CountsUpPastTakenNames()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            sm.AddState("X");
            sm.AddState("X 1");

            Assert.AreEqual("Y", StateDuplicator.MakeUniqueName(sm, "Y"));
            Assert.AreEqual("X 2", StateDuplicator.MakeUniqueName(sm, "X"));

            Object.DestroyImmediate(controller);
        }
    }
}
