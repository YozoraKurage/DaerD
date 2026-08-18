using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;

namespace Yozolab.DaerD.Tests
{
    public class ControllerLocatorTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        [Test]
        public void Locate_FindsAStateInANestedStateMachine()
        {
            var controller = NewController(out var sm);
            controller.AddLayer("Second");
            var sub = sm.AddStateMachine("Sub");
            var state = sub.AddState("S");

            var location = ControllerLocator.Locate(controller, state);

            Assert.IsNotNull(location);
            Assert.AreEqual(0, location.layerIndex);
            Assert.AreEqual(2, location.stateMachinePath.Count);
            Assert.AreSame(sm, location.stateMachinePath[0]);
            Assert.AreSame(sub, location.stateMachinePath[1]);
            Assert.AreSame(state, location.target);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Locate_FindsATransition_InItsSourceStatesView()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            var transition = a.AddTransition(b);

            var location = ControllerLocator.Locate(controller, transition);

            Assert.IsNotNull(location);
            Assert.AreEqual(1, location.stateMachinePath.Count);
            Assert.AreSame(transition, location.target);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Locate_MapsABlendTreeToItsOwningState()
        {
            var controller = NewController(out var sm);
            var inner = new BlendTree { name = "Inner" };
            var outer = new BlendTree { name = "Outer" };
            outer.AddChild(inner);
            var state = sm.AddState("S");
            state.motion = outer;

            var location = ControllerLocator.Locate(controller, inner);

            Assert.IsNotNull(location);
            Assert.AreSame(state, location.target);

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(inner);
            Object.DestroyImmediate(outer);
        }

        [Test]
        public void Locate_FindsASubStateMachine_InItsParentsView()
        {
            var controller = NewController(out var sm);
            var sub = sm.AddStateMachine("Sub");
            var nested = sub.AddStateMachine("Nested");

            var location = ControllerLocator.Locate(controller, nested);

            Assert.IsNotNull(location);
            // The nested SM's node lives in Sub's view.
            Assert.AreEqual(2, location.stateMachinePath.Count);
            Assert.AreSame(sub, location.stateMachinePath[1]);
            Assert.AreSame(nested, location.target);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Locate_ReturnsNull_ForObjectsOutsideTheController()
        {
            var controller = NewController(out _);
            var stray = new AnimationClip { name = "Stray" };

            Assert.IsNull(ControllerLocator.Locate(controller, stray));
            Assert.IsNull(ControllerLocator.Locate(controller, null));

            Object.DestroyImmediate(controller);
            Object.DestroyImmediate(stray);
        }
    }
}
