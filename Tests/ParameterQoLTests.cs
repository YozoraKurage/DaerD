using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class ParameterQoLTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("A", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Float);
            return controller;
        }

        [Test]
        public void RedirectReferences_MovesUsesAndKeepsBothParameters()
        {
            var controller = NewController();
            var sm = controller.layers[0].stateMachine;
            var s1 = sm.AddState("S1");
            var s2 = sm.AddState("S2");
            var transition = s1.AddTransition(s2);
            transition.AddCondition(AnimatorConditionMode.Greater, 0.5f, "A");

            var direct = new BlendTree { name = "D", blendType = BlendTreeType.Direct };
            direct.AddChild(new AnimationClip());
            var children = direct.children;
            children[0].directBlendParameter = "A";
            direct.children = children;
            s2.motion = direct;

            Assert.IsTrue(ParameterRenamer.RedirectReferences(controller, "A", "B"));
            Assert.AreEqual("B", s1.transitions[0].conditions[0].parameter);
            Assert.AreEqual("B", ((BlendTree)s2.motion).children[0].directBlendParameter);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "A"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "B"));

            // Redirect to a missing parameter is refused.
            Assert.IsFalse(ParameterRenamer.RedirectReferences(controller, "B", "Missing"));
        }

        [Test]
        public void DeleteAndClean_RemovesConditionsOverridesAndTheParameter()
        {
            var controller = NewController();
            var sm = controller.layers[0].stateMachine;
            var s1 = sm.AddState("S1");
            var s2 = sm.AddState("S2");
            var transition = s1.AddTransition(s2);
            transition.AddCondition(AnimatorConditionMode.Greater, 0.5f, "A");
            transition.AddCondition(AnimatorConditionMode.Less, 0.5f, "B");
            s2.speedParameterActive = true;
            s2.speedParameter = "A";

            Assert.IsTrue(ParameterRenamer.DeleteAndClean(controller, "A"));

            Assert.AreEqual(1, s1.transitions[0].conditions.Length);
            Assert.AreEqual("B", s1.transitions[0].conditions[0].parameter);
            Assert.IsFalse(s2.speedParameterActive);
            Assert.AreEqual(string.Empty, s2.speedParameter);
            Assert.IsNull(DbtBuilder.FindParameter(controller, "A"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "B"));
        }

        [Test]
        public void Rename_StillCascadesAfterRefactor()
        {
            var controller = NewController();
            var sm = controller.layers[0].stateMachine;
            var s1 = sm.AddState("S1");
            var s2 = sm.AddState("S2");
            var transition = s1.AddTransition(s2);
            transition.AddCondition(AnimatorConditionMode.Greater, 0.5f, "A");

            Assert.IsTrue(ParameterRenamer.Rename(controller, "A", "A2"));
            Assert.AreEqual("A2", s1.transitions[0].conditions[0].parameter);
            Assert.IsNull(DbtBuilder.FindParameter(controller, "A"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "A2"));
        }
    }

    public class GraphLayoutDistributeTests
    {
        [Test]
        public void Distribute_SpacesStatesEvenlyBetweenTheEnds()
        {
            var controller = new AnimatorController();
            controller.AddLayer("L");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A", new Vector3(0f, 5f, 0f));
            var b = sm.AddState("B", new Vector3(10f, 6f, 0f));
            var c = sm.AddState("C", new Vector3(100f, 7f, 0f));

            GraphLayout.Distribute(sm, new[] { a, b, c }, GraphLayout.AlignAxis.Row);

            var states = sm.states;
            Assert.AreEqual(0f, states[0].position.x);
            Assert.AreEqual(50f, states[1].position.x);
            Assert.AreEqual(100f, states[2].position.x);
            Assert.AreEqual(6f, states[1].position.y);   // the other axis is untouched
        }

        [Test]
        public void Distribute_NeedsAtLeastThreeStates()
        {
            var controller = new AnimatorController();
            controller.AddLayer("L");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A", new Vector3(0f, 0f, 0f));
            var b = sm.AddState("B", new Vector3(10f, 0f, 0f));

            GraphLayout.Distribute(sm, new[] { a, b }, GraphLayout.AlignAxis.Row);
            Assert.AreEqual(10f, sm.states[1].position.x);   // untouched
        }
    }
}
