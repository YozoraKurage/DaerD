using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class AapGadgetsTests
    {
        static AnimatorController NewController(params string[] floatParams)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            foreach (var name in floatParams)
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            return controller;
        }

        static AapGadgets.Request NewRequest(AnimatorController controller, AapGadgets.Kind kind) =>
            new AapGadgets.Request
            {
                controller = controller,
                kind = kind,
                inputA = "A",
                inputB = "B",
                output = "Out",
                layerIndex = -1,
                newLayerName = "DBT",
            };

        /// <summary>The gadget attached to the DBT layer created by the request.</summary>
        static BlendTree GadgetRoot(AnimatorController controller)
        {
            var layer = controller.layers[1];
            var root = (BlendTree)layer.stateMachine.states[0].state.motion;
            Assert.AreEqual(BlendTreeType.Direct, root.blendType);
            Assert.AreEqual("One", root.children[0].directBlendParameter);
            return (BlendTree)root.children[0].motion;
        }

        /// <summary>The one AAP curve of a generated clip, with its binding checked.</summary>
        static AnimationCurve ClipCurve(Motion motion, string expectedParameter)
        {
            var clip = (AnimationClip)motion;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(1, bindings.Length);
            Assert.AreEqual(typeof(Animator), bindings[0].type);
            Assert.AreEqual(expectedParameter, bindings[0].propertyName);
            return AnimationUtility.GetEditorCurve(clip, bindings[0]);
        }

        static float ClipValue(Motion motion, string expectedParameter) =>
            ClipCurve(motion, expectedParameter).keys[0].value;

        static AnimatorControllerLayer FindLayer(AnimatorController controller, string name)
        {
            foreach (var layer in controller.layers)
                if (layer.name == name) return layer;
            Assert.Fail("no layer named '" + name + "'");
            return null;
        }

        static AnimatorState FindState(AnimatorStateMachine stateMachine, string name)
        {
            foreach (var child in stateMachine.states)
                if (child.state.name == name) return child.state;
            Assert.Fail("no state named '" + name + "'");
            return null;
        }

        [Test]
        public void Add_WeightsTheOutputClipByBothInputs()
        {
            var controller = NewController("A", "B");
            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, AapGadgets.Kind.Add)));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(BlendTreeType.Direct, gadget.blendType);
            Assert.AreEqual(2, gadget.children.Length);
            Assert.AreEqual("A", gadget.children[0].directBlendParameter);
            Assert.AreEqual("B", gadget.children[1].directBlendParameter);
            Assert.AreSame(gadget.children[0].motion, gadget.children[1].motion);
            Assert.AreEqual(1f, ClipValue(gadget.children[0].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Sub_UsesAMinusOneClipForB()
        {
            var controller = NewController("A", "B");
            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, AapGadgets.Kind.Sub)));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(1f, ClipValue(gadget.children[0].motion, "Out"), 1e-4f);
            Assert.AreEqual(-1f, ClipValue(gadget.children[1].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SubRanged_SwapsTheLeavesOnB()
        {
            var controller = NewController("A", "B");
            var request = NewRequest(controller, AapGadgets.Kind.SubRanged);
            request.rangeMin = -1f;
            request.rangeMax = 1f;
            Assert.IsTrue(AapGadgets.Apply(request));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual("One", gadget.children[0].directBlendParameter);
            var treeA = (BlendTree)gadget.children[0].motion;
            var treeB = (BlendTree)gadget.children[1].motion;
            Assert.AreEqual("A", treeA.blendParameter);
            Assert.AreEqual("B", treeB.blendParameter);
            // A ascends min→max; B descends so its contribution is negated.
            Assert.AreEqual(-1f, ClipValue(treeA.children[0].motion, "Out"), 1e-4f);
            Assert.AreEqual(1f, ClipValue(treeA.children[1].motion, "Out"), 1e-4f);
            Assert.AreEqual(1f, ClipValue(treeB.children[0].motion, "Out"), 1e-4f);
            Assert.AreEqual(-1f, ClipValue(treeB.children[1].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Multiply_NestsDirectTrees()
        {
            var controller = NewController("A", "B");
            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, AapGadgets.Kind.Multiply)));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(BlendTreeType.Direct, gadget.blendType);
            Assert.AreEqual("A", gadget.children[0].directBlendParameter);
            var inner = (BlendTree)gadget.children[0].motion;
            Assert.AreEqual(BlendTreeType.Direct, inner.blendType);
            Assert.AreEqual("B", inner.children[0].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(inner.children[0].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Not_InvertsTheLeaves()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.Not);
            request.inputB = null;
            Assert.IsTrue(AapGadgets.Apply(request));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual("A", gadget.blendParameter);
            Assert.AreEqual(1f, ClipValue(gadget.children[0].motion, "Out"), 1e-4f);
            Assert.AreEqual(0f, ClipValue(gadget.children[1].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Remap_MapsInputRangeToOutputRange()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.Remap);
            request.inputB = null;
            request.inMin = 0f;
            request.inMax = 1f;
            request.rangeMin = 2f;
            request.rangeMax = -2f;   // reversed output range is allowed
            Assert.IsTrue(AapGadgets.Apply(request));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(0f, gadget.children[0].threshold, 1e-4f);
            Assert.AreEqual(1f, gadget.children[1].threshold, 1e-4f);
            Assert.AreEqual(2f, ClipValue(gadget.children[0].motion, "Out"), 1e-4f);
            Assert.AreEqual(-2f, ClipValue(gadget.children[1].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void And_NestsBInsideAsOneBranch()
        {
            var controller = NewController("A", "B");
            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, AapGadgets.Kind.And)));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual("A", gadget.blendParameter);
            Assert.AreEqual(0f, ClipValue(gadget.children[0].motion, "Out"), 1e-4f);
            var inner = (BlendTree)gadget.children[1].motion;
            Assert.AreEqual("B", inner.blendParameter);
            Assert.AreEqual(1f, ClipValue(inner.children[1].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Smooth_DelegatesToAapSmoothing()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.Smooth);
            request.inputB = null;
            request.output = "A/Smoothed";
            request.smoothing = "A/Smoothing";
            request.smoothingDefault = 0.8f;
            Assert.IsTrue(AapGadgets.Apply(request));

            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "A/Smoothed"));
            Assert.AreEqual(0.8f, DbtBuilder.FindParameter(controller, "A/Smoothing").defaultFloat, 1e-4f);

            var gadget = GadgetRoot(controller);
            Assert.AreEqual("A/Smoothing", gadget.blendParameter);   // the smoothing crossfade

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Validate_RejectsBrokenRequests()
        {
            var controller = NewController("A", "B");

            var missingB = NewRequest(controller, AapGadgets.Kind.Add);
            missingB.inputB = "Missing";
            Assert.IsNotNull(AapGadgets.Validate(missingB));

            var outputTaken = NewRequest(controller, AapGadgets.Kind.Add);
            outputTaken.output = "B";
            Assert.IsNotNull(AapGadgets.Validate(outputTaken));

            var badRange = NewRequest(controller, AapGadgets.Kind.AddRanged);
            badRange.rangeMin = 1f;
            badRange.rangeMax = -1f;
            Assert.IsNotNull(AapGadgets.Validate(badRange));

            var badInputRange = NewRequest(controller, AapGadgets.Kind.Remap);
            badInputRange.inputB = null;
            badInputRange.inMin = 1f;
            badInputRange.inMax = 1f;
            Assert.IsNotNull(AapGadgets.Validate(badInputRange));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Reciprocal_NormalizesTheCoreAndCoversTheRestWithALayer()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.Reciprocal);
            request.inputB = null;
            Assert.IsTrue(AapGadgets.Apply(request));

            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Out/Shift"));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(2, gadget.children.Length);
            // Out/Shift = A - 1, so the normalized core divides by (Shift + 1) = A.
            var shift = (BlendTree)gadget.children[0].motion;
            Assert.AreEqual("A", shift.children[0].directBlendParameter);
            Assert.AreEqual("One", shift.children[1].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(shift.children[0].motion, "Out/Shift"), 1e-4f);
            Assert.AreEqual(-1f, ClipValue(shift.children[1].motion, "Out/Shift"), 1e-4f);

            var core = (BlendTree)gadget.children[1].motion;
            using (var so = new SerializedObject(core))
                Assert.IsTrue(so.FindProperty("m_NormalizedBlendValues").boolValue);
            Assert.AreEqual("Out/Shift", core.children[0].directBlendParameter);
            Assert.AreEqual(0, AnimationUtility.GetCurveBindings((AnimationClip)core.children[0].motion).Length,
                "the shift only carries weight; it must not write anything");
            Assert.AreEqual("One", core.children[1].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(core.children[1].motion, "Out"), 1e-4f);

            // Below 1 a motion-time curve takes over.
            var stateMachine = FindLayer(controller, "Out 1/x").stateMachine;
            var idle = FindState(stateMachine, "Idle");
            var inverse = FindState(stateMachine, "1/x");
            Assert.AreSame(idle, stateMachine.defaultState);
            Assert.IsTrue(inverse.timeParameterActive);
            Assert.AreEqual("A", inverse.timeParameter);

            var curve = ClipCurve(inverse.motion, "Out");
            Assert.AreEqual(1f, curve.Evaluate(100f), 1e-3f);    // the clip's end is input 1
            Assert.AreEqual(4f, curve.Evaluate(25f), 1e-3f);     // a quarter in reads 1 / 0.25

            Assert.AreEqual(1, idle.transitions.Length);
            Assert.AreSame(inverse, idle.transitions[0].destinationState);
            Assert.AreEqual(AnimatorConditionMode.Less, idle.transitions[0].conditions[0].mode);
            Assert.AreEqual("A", idle.transitions[0].conditions[0].parameter);
            Assert.AreEqual(1f, idle.transitions[0].conditions[0].threshold, 1e-4f);
            Assert.AreEqual(AnimatorConditionMode.Greater, inverse.transitions[0].conditions[0].mode);
            Assert.AreSame(idle, inverse.transitions[0].destinationState);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Divide_MultipliesAByTheReciprocalOfB()
        {
            var controller = NewController("A", "B");
            Assert.IsTrue(AapGadgets.Apply(NewRequest(controller, AapGadgets.Kind.Divide)));

            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Out/Inv"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Out/Inv/Shift"));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(2, gadget.children.Length);
            var multiply = (BlendTree)gadget.children[1].motion;
            Assert.AreEqual("A", multiply.children[0].directBlendParameter);
            var inner = (BlendTree)multiply.children[0].motion;
            Assert.AreEqual("Out/Inv", inner.children[0].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(inner.children[0].motion, "Out"), 1e-4f);

            // The reciprocal — and its layer — belong to B, the divisor.
            Assert.AreEqual("B", FindState(FindLayer(controller, "Out/Inv 1/x").stateMachine, "1/x").timeParameter);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FrameTime_SubtractsTheClockFromItsPreviousReading()
        {
            var controller = NewController();
            var request = NewRequest(controller, AapGadgets.Kind.FrameTime);
            request.inputA = null;
            request.inputB = null;
            request.output = "FrameTime";
            Assert.IsTrue(AapGadgets.Apply(request));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(3, gadget.children.Length);
            Assert.AreEqual("FrameTime/Clock", gadget.children[0].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(gadget.children[0].motion, "FrameTime"), 1e-4f);
            Assert.AreEqual("FrameTime/Last", gadget.children[1].directBlendParameter);
            Assert.AreEqual(-1f, ClipValue(gadget.children[1].motion, "FrameTime"), 1e-4f);
            // The same weight writes the reading back for the next frame to subtract.
            Assert.AreEqual("FrameTime/Clock", gadget.children[2].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(gadget.children[2].motion, "FrameTime/Last"), 1e-4f);

            var stateMachine = FindLayer(controller, "FrameTime Clock").stateMachine;
            Assert.AreEqual(1, stateMachine.states.Length);
            var clip = (AnimationClip)stateMachine.states[0].state.motion;
            Assert.IsTrue(AnimationUtility.GetAnimationClipSettings(clip).loopTime);
            var curve = ClipCurve(clip, "FrameTime/Clock");
            Assert.AreEqual(0f, curve.keys[0].time, 1e-4f);
            Assert.AreEqual(0f, curve.keys[0].value, 1e-4f);
            // One unit per second, for 2000 of them.
            Assert.AreEqual(2000f, curve.keys[1].time, 1e-4f);
            Assert.AreEqual(2000f, curve.keys[1].value, 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SmoothLinear_StepsTheOutputTowardTheInput()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.SmoothLinear);
            request.inputB = null;
            request.smoothing = "StepSize";
            request.smoothingDefault = 0.05f;
            Assert.IsTrue(AapGadgets.Apply(request));

            Assert.AreEqual(0.05f, DbtBuilder.FindParameter(controller, "StepSize").defaultFloat, 1e-4f);
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Out/Delta"));

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(4, gadget.children.Length);
            Assert.AreEqual("One", gadget.children[0].directBlendParameter);
            Assert.AreEqual("One", gadget.children[1].directBlendParameter);
            Assert.AreEqual("One", gadget.children[2].directBlendParameter);
            Assert.AreEqual("StepSize", gadget.children[3].directBlendParameter);

            // Delta = input - output: the input's remap ascends, the output's descends.
            var fromInput = (BlendTree)gadget.children[0].motion;
            Assert.AreEqual("A", fromInput.blendParameter);
            Assert.AreEqual(-1f, ClipValue(fromInput.children[0].motion, "Out/Delta"), 1e-4f);
            Assert.AreEqual(1f, ClipValue(fromInput.children[1].motion, "Out/Delta"), 1e-4f);
            var fromOutput = (BlendTree)gadget.children[1].motion;
            Assert.AreEqual("Out", fromOutput.blendParameter);
            Assert.AreEqual(1f, ClipValue(fromOutput.children[0].motion, "Out/Delta"), 1e-4f);
            Assert.AreEqual(-1f, ClipValue(fromOutput.children[1].motion, "Out/Delta"), 1e-4f);
            // The third child holds the output where it is, so the step is what moves it.
            Assert.AreEqual("Out", ((BlendTree)gadget.children[2].motion).blendParameter);

            var step = (BlendTree)gadget.children[3].motion;
            Assert.AreEqual("Out/Delta", step.blendParameter);
            Assert.AreEqual(3, step.children.Length);
            Assert.AreEqual(-0.1f, step.children[0].threshold, 1e-4f);
            Assert.AreEqual(0f, step.children[1].threshold, 1e-4f);
            Assert.AreEqual(0.1f, step.children[2].threshold, 1e-4f);
            Assert.AreEqual(-1f, ClipValue(step.children[0].motion, "Out"), 1e-4f);
            Assert.AreEqual(0f, ClipValue(step.children[1].motion, "Out"), 1e-4f);
            Assert.AreEqual(1f, ClipValue(step.children[2].motion, "Out"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SeparateDigits_QuantizesThroughSubnormalFloats()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.SeparateDigits);
            request.inputB = null;
            request.output = "A/Digits";
            Assert.IsTrue(AapGadgets.Apply(request));

            foreach (var name in new[]
            {
                "A/Digits/Tenths", "A/Digits/Hundredths", "A/Digits/Thousandths",
                "A/Digits/Proxy", "A/Digits/Offset/Ones",
                "A/Digits/Subnormal/Thousandths", "A/Digits/Quantized/Thousandths",
            })
                Assert.IsNotNull(DbtBuilder.FindParameter(controller, name), name);

            var gadget = GadgetRoot(controller);
            Assert.AreEqual(4, gadget.children.Length);   // proxy, offsets, levels, results

            // The offset that turns "round to nearest" into "floor" for the integer part.
            var offsets = (BlendTree)gadget.children[1].motion;
            var ones = (BlendTree)offsets.children[0].motion;
            Assert.AreEqual(-0.49999f, ClipValue(ones.children[0].motion, "A/Digits/Offset/Ones"), 1e-7f);
            Assert.AreEqual(0.50001f, ClipValue(ones.children[1].motion, "A/Digits/Offset/Ones"), 1e-7f);

            // The quantizer: multiply onto the multiples of the smallest float there is, then
            // read the level back by dividing by the same constant.
            var levels = (BlendTree)gadget.children[2].motion;
            var onesLevel = (BlendTree)levels.children[0].motion;
            Assert.AreEqual(1f,
                ClipValue(((BlendTree)onesLevel.children[0].motion).children[1].motion, "A/Digits/Subnormal/Ones")
                    / float.Epsilon, 1e-3f);
            Assert.AreEqual(1f,
                ((BlendTree)onesLevel.children[1].motion).children[1].threshold / float.Epsilon, 1e-3f);
            var thousandthsLevel = (BlendTree)levels.children[3].motion;
            Assert.AreEqual(1000f,
                ClipValue(((BlendTree)thousandthsLevel.children[0].motion).children[1].motion,
                    "A/Digits/Subnormal/Thousandths") / float.Epsilon, 1e-1f,
                "the finest place needs a thousand levels between 0 and 1");

            // Each digit is the finer quantization minus the coarser one.
            var results = (BlendTree)gadget.children[3].motion;
            var tenths = (BlendTree)results.children[0].motion;
            Assert.AreEqual("A/Digits/Quantized/Tenths", tenths.children[0].directBlendParameter);
            Assert.AreEqual("A/Digits/Quantized/Ones", tenths.children[1].directBlendParameter);
            Assert.AreEqual(1f, ClipValue(tenths.children[0].motion, "A/Digits/Tenths"), 1e-4f);
            Assert.AreEqual(-1f, ClipValue(tenths.children[1].motion, "A/Digits/Tenths"), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Sine_IsALayerOfItsOwnDrivenByMotionTime()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.Sine);
            request.inputB = null;
            Assert.IsTrue(AapGadgets.Apply(request));

            Assert.AreEqual(2, controller.layers.Length, "no blend tree layer is needed");
            Assert.IsNull(DbtBuilder.FindParameter(controller, "One"), "and no weight parameter either");
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Out"));

            var state = FindState(FindLayer(controller, "Out sin(x)").stateMachine, "sin(x)");
            Assert.IsTrue(state.timeParameterActive);
            Assert.AreEqual("A", state.timeParameter);

            var clip = (AnimationClip)state.motion;
            Assert.AreEqual(1f, clip.length, 1e-4f, "one second of clip is one whole turn");
            var curve = ClipCurve(clip, "Out");
            Assert.AreEqual(0f, curve.Evaluate(0f), 1e-4f);
            Assert.AreEqual(1f, curve.Evaluate(0.25f), 1e-4f);
            Assert.AreEqual(0f, curve.Evaluate(0.5f), 1e-3f);
            Assert.AreEqual(-1f, curve.Evaluate(0.75f), 1e-4f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Tangent_ClampsThePoles()
        {
            var controller = NewController("A");
            var request = NewRequest(controller, AapGadgets.Kind.Tangent);
            request.inputB = null;
            Assert.IsTrue(AapGadgets.Apply(request));

            var curve = ClipCurve(FindState(FindLayer(controller, "Out tan(x)").stateMachine, "tan(x)").motion, "Out");
            Assert.AreEqual(0f, curve.Evaluate(0f), 1e-4f);
            Assert.AreEqual(1f, curve.Evaluate(0.125f), 1e-3f);    // tan(45°)
            foreach (var key in curve.keys)
                Assert.LessOrEqual(Mathf.Abs(key.value), 100f, "the poles stay finite");
            Assert.AreEqual(100f, curve.Evaluate(0.25f), 1e-3f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Validate_CoversTheKindsWithTheirOwnParameterRules()
        {
            var controller = NewController("A");

            var frameTime = NewRequest(controller, AapGadgets.Kind.FrameTime);
            frameTime.inputA = null;
            frameTime.inputB = null;
            frameTime.output = "FrameTime";
            Assert.IsNull(AapGadgets.Validate(frameTime), "FrameTime reads the clock, not an input");

            var digits = NewRequest(controller, AapGadgets.Kind.SeparateDigits);
            digits.inputB = null;
            Assert.IsNull(AapGadgets.Validate(digits));
            controller.AddParameter("Out/Hundredths", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(AapGadgets.Validate(digits), "every digit parameter has to be free");

            var linear = NewRequest(controller, AapGadgets.Kind.SmoothLinear);
            linear.inputB = null;
            linear.smoothing = null;
            Assert.IsNotNull(AapGadgets.Validate(linear), "the step size needs a parameter");
            linear.smoothing = "StepSize";
            Assert.IsNull(AapGadgets.Validate(linear));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void SetNormalizedBlendValues_FlipsTheHiddenFlag()
        {
            var tree = new BlendTree { blendType = BlendTreeType.Direct };
            DbtBuilder.SetNormalizedBlendValues(tree, true);

            using (var so = new SerializedObject(tree))
                Assert.IsTrue(so.FindProperty("m_NormalizedBlendValues").boolValue);

            Object.DestroyImmediate(tree);
        }
    }
}
