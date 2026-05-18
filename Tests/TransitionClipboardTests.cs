using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class TransitionClipboardTests
    {
        [Test]
        public void Capture_Then_Apply_RoundTripsStateTransition()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            controller.AddParameter("P", AnimatorControllerParameterType.Float);

            var source = a.AddTransition(b);
            source.hasExitTime = true;
            source.exitTime = 0.42f;
            source.hasFixedDuration = false;
            source.duration = 0.13f;
            source.offset = 0.2f;
            source.canTransitionToSelf = true;
            source.AddCondition(AnimatorConditionMode.Greater, 0.5f, "P");

            var snapshot = TransitionClipboard.Capture(source);

            var destination = b.AddTransition(a);
            TransitionClipboard.Apply(destination, snapshot);

            Assert.IsTrue(destination.hasExitTime);
            Assert.AreEqual(0.42f, destination.exitTime, 1e-4f);
            Assert.IsFalse(destination.hasFixedDuration);
            Assert.AreEqual(0.13f, destination.duration, 1e-4f);
            Assert.AreEqual(0.2f, destination.offset, 1e-4f);
            Assert.IsTrue(destination.canTransitionToSelf);
            Assert.AreEqual(1, destination.conditions.Length);
            Assert.AreEqual(AnimatorConditionMode.Greater, destination.conditions[0].mode);
            Assert.AreEqual("P", destination.conditions[0].parameter);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SetConditions_ReplacesEntireList()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var transition = a.AddTransition(b);
            transition.AddCondition(AnimatorConditionMode.If, 0f, "X");

            TransitionClipboard.SetConditions(transition, new List<TransitionClipboard.ConditionData>
            {
                new TransitionClipboard.ConditionData { mode = AnimatorConditionMode.IfNot, parameter = "Y", threshold = 0f },
                new TransitionClipboard.ConditionData { mode = AnimatorConditionMode.If, parameter = "Z", threshold = 0f },
            });

            Assert.AreEqual(2, transition.conditions.Length);
            Assert.AreEqual("Y", transition.conditions[0].parameter);
            Assert.AreEqual("Z", transition.conditions[1].parameter);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ApplyToAnimatorTransition_IgnoresStateTransitionFields()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A");

            var stateTransition = sm.AddAnyStateTransition(a);
            stateTransition.exitTime = 0.9f;
            var snapshot = TransitionClipboard.Capture(stateTransition);

            var entryTransition = sm.AddEntryTransition(a);
            Assert.DoesNotThrow(() => TransitionClipboard.Apply(entryTransition, snapshot));

            Object.DestroyImmediate(controller);
        }
    }
}
