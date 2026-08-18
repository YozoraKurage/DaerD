using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The exponential smoothing gadget, end to end — the one whose tree reads the parameter it
    /// writes, which is why it is worth a file of its own even though it is a kind like any
    /// other.
    /// </summary>
    public class SmoothGadgetTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        /// <summary>Through <see cref="AapGadgets"/>, the way the wizard and a recipe do.</summary>
        static AapGadgets.Request NewRequest(AnimatorController controller, string source) =>
            new AapGadgets.Request
            {
                controller = controller,
                kind = AapGadgets.Kind.Smooth,
                inputA = source,
                output = source + "/Smoothed",
                smoothing = source + "/Smoothing",
                smoothingDefault = 0.9f,
                rangeMin = -1f,
                rangeMax = 1f,
                layerIndex = -1,
                newLayerName = "DBT",
            };

        static AnimatorControllerParameter FindParameter(AnimatorController controller, string name)
        {
            foreach (var p in controller.parameters)
                if (p.name == name) return p;
            return null;
        }

        [Test]
        public void Apply_BuildsTheFullGadget()
        {
            var controller = NewController(out _);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

            Assert.IsNull(AapGadgets.Validate(NewRequest(controller, "Speed")));
            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, "Speed")));

            // Parameters: smoothed copy, smoothing amount (default 0.9) and the constant One.
            Assert.AreEqual(AnimatorControllerParameterType.Float, FindParameter(controller, "Speed/Smoothed").type);
            Assert.AreEqual(0.9f, FindParameter(controller, "Speed/Smoothing").defaultFloat, 1e-4f);
            Assert.AreEqual(1f, FindParameter(controller, "One").defaultFloat, 1e-4f);

            // Layer: one WD-ON state whose motion is the Direct root.
            Assert.AreEqual(2, controller.layers.Length);
            var layer = controller.layers[1];
            Assert.AreEqual("DBT", layer.name);
            Assert.AreEqual(1f, layer.defaultWeight, 1e-4f);
            Assert.AreEqual(1, layer.stateMachine.states.Length);
            var state = layer.stateMachine.states[0].state;
            Assert.IsTrue(state.writeDefaultValues);
            Assert.AreSame(state, layer.stateMachine.defaultState);

            var root = (BlendTree)state.motion;
            Assert.AreEqual(BlendTreeType.Direct, root.blendType);
            Assert.AreEqual(1, root.children.Length);
            Assert.AreEqual("One", root.children[0].directBlendParameter);

            // Gadget: smoothing selector crossfading input → feedback.
            var smooth = (BlendTree)root.children[0].motion;
            Assert.AreEqual("Speed/Smoothing", smooth.blendParameter);
            Assert.AreEqual(2, smooth.children.Length);
            Assert.AreEqual(0f, smooth.children[0].threshold, 1e-4f);
            Assert.AreEqual(1f, smooth.children[1].threshold, 1e-4f);

            var input = (BlendTree)smooth.children[0].motion;
            Assert.AreEqual("Speed", input.blendParameter);
            var feedback = (BlendTree)smooth.children[1].motion;
            Assert.AreEqual("Speed/Smoothed", feedback.blendParameter);
            Assert.AreEqual(-1f, input.children[0].threshold, 1e-4f);
            Assert.AreEqual(1f, input.children[1].threshold, 1e-4f);

            // The AAP leaves animate the output parameter on the Animator itself and are
            // shared by both trees.
            var clipMin = (AnimationClip)input.children[0].motion;
            Assert.AreSame(clipMin, feedback.children[0].motion);
            var bindings = AnimationUtility.GetCurveBindings(clipMin);
            Assert.AreEqual(1, bindings.Length);
            Assert.AreEqual(typeof(Animator), bindings[0].type);
            Assert.AreEqual(string.Empty, bindings[0].path);
            Assert.AreEqual("Speed/Smoothed", bindings[0].propertyName);
            Assert.AreEqual(-1f, AnimationUtility.GetEditorCurve(clipMin, bindings[0]).keys[0].value, 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Apply_ReusesTheDbtLayerAndTheOneParameter()
        {
            var controller = NewController(out _);
            controller.AddParameter("A", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Float);

            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, "A")));
            var second = NewRequest(controller, "B");
            second.layerIndex = 1;   // the DBT layer created by the first run
            Assert.IsTrue(AapGadgets.Apply(second));

            Assert.AreEqual(2, controller.layers.Length);
            var layer = controller.layers[1];
            Assert.AreEqual(1, layer.stateMachine.states.Length);
            var root = (BlendTree)layer.stateMachine.states[0].state.motion;
            Assert.AreEqual(2, root.children.Length);
            Assert.AreEqual("One", root.children[1].directBlendParameter);

            int ones = 0;
            foreach (var p in controller.parameters)
                if (p.name == "One") ones++;
            Assert.AreEqual(1, ones);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Validate_RejectsBrokenRequests()
        {
            var controller = NewController(out _);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Flag", AnimatorControllerParameterType.Bool);

            Assert.IsNotNull(AapGadgets.Validate(NewRequest(controller, "Missing")));
            Assert.IsNotNull(AapGadgets.Validate(NewRequest(controller, "Flag")));   // not a Float

            var clash = NewRequest(controller, "Speed");
            clash.output = "Flag";   // name already taken
            Assert.IsNotNull(AapGadgets.Validate(clash));

            var range = NewRequest(controller, "Speed");
            range.rangeMin = 1f;
            range.rangeMax = 1f;
            Assert.IsNotNull(AapGadgets.Validate(range));

            var sameName = NewRequest(controller, "Speed");
            sameName.output = "Speed";
            Assert.IsNotNull(AapGadgets.Validate(sameName));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void EnsureConstantOne_SkipsUnusableNames()
        {
            var controller = NewController(out _);
            controller.AddParameter("One", AnimatorControllerParameterType.Bool);

            Assert.AreEqual("DBT/One", DbtBuilder.EnsureConstantOneParameter(controller));
            Assert.AreEqual(1f, FindParameter(controller, "DBT/One").defaultFloat, 1e-4f);

            Object.DestroyImmediate(controller);
        }
    }
}
