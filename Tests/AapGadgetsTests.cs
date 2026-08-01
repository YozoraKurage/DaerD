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

        static float ClipValue(Motion motion, string expectedParameter)
        {
            var clip = (AnimationClip)motion;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            Assert.AreEqual(1, bindings.Length);
            Assert.AreEqual(typeof(Animator), bindings[0].type);
            Assert.AreEqual(expectedParameter, bindings[0].propertyName);
            return AnimationUtility.GetEditorCurve(clip, bindings[0]).keys[0].value;
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
