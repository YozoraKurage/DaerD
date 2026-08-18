using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;

namespace Yozolab.DaerD.Tests
{
    public class AapWriteScanTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Aap", AnimatorControllerParameterType.Float);
            controller.AddParameter("Plain", AnimatorControllerParameterType.Float);
            return controller;
        }

        /// <summary>The binding DaerD writes an AAP with: the Animator at the animated root,
        /// addressed by the parameter name.</summary>
        static AnimationClip AapClip(string parameter)
        {
            var clip = new AnimationClip { name = parameter + " AAP" };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), parameter),
                AnimationCurve.Constant(0f, 1f, 1f));
            return clip;
        }

        static AnimationClip Clip(string name, EditorCurveBinding binding)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip, binding, AnimationCurve.Constant(0f, 1f, 1f));
            return clip;
        }

        [Test]
        public void AnimatorBoundParameter_OnAState_IsReported()
        {
            var controller = NewController();
            controller.layers[0].stateMachine.AddState("Write").motion = AapClip("Aap");

            var written = AapWriteScan.CollectWrittenParameters(controller);

            CollectionAssert.Contains(written, "Aap");
            CollectionAssert.DoesNotContain(written, "Plain");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void InsideABlendTree_IsReported()
        {
            var controller = NewController();
            var tree = new BlendTree { name = "DBT", blendType = BlendTreeType.Direct };
            tree.AddChild(AapClip("Aap"));
            controller.layers[0].stateMachine.AddState("Tree").motion = tree;

            CollectionAssert.Contains(AapWriteScan.CollectWrittenParameters(controller), "Aap");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void MuscleCurves_AreNotMistakenForAaps()
        {
            var controller = NewController();
            // Humanoid muscles bind to typeof(Animator) with an empty path, exactly like an
            // AAP — only the name tells them apart, and this one is not a parameter.
            controller.layers[0].stateMachine.AddState("Pose").motion =
                AapClip("LeftHand.Index.1 Stretch");

            Assert.AreEqual(0, AapWriteScan.CollectWrittenParameters(controller).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void OtherBindingShapes_AreIgnored()
        {
            var controller = NewController();
            var sm = controller.layers[0].stateMachine;
            sm.AddState("Toggle").motion =
                Clip("Toggle", EditorCurveBinding.FloatCurve("Body", typeof(GameObject), "m_IsActive"));
            // An Animator binding under a child path drives a nested animator, not this one.
            sm.AddState("Nested").motion =
                Clip("Nested", EditorCurveBinding.FloatCurve("Child", typeof(Animator), "Aap"));

            Assert.AreEqual(0, AapWriteScan.CollectWrittenParameters(controller).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AClipOnAStateTheLayerCanNeverEnter_IsNotAWrite()
        {
            var controller = NewController();
            var sm = controller.layers[0].stateMachine;
            sm.AddState("Live").motion = AapClip("Plain");   // default state
            sm.AddState("Parked").motion = AapClip("Aap");   // nothing transitions here

            var written = AapWriteScan.CollectWrittenParameters(controller);

            CollectionAssert.Contains(written, "Plain");
            CollectionAssert.DoesNotContain(written, "Aap");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ByLayer_KeepsTheWritersApart()
        {
            var controller = NewController();
            controller.layers[0].stateMachine.AddState("Write").motion = AapClip("Aap");
            controller.AddLayer("Second");
            controller.layers[1].stateMachine.AddState("Write").motion = AapClip("Plain");

            var byLayer = AapWriteScan.CollectByLayer(controller);

            Assert.AreEqual(2, byLayer.Count);
            Assert.AreEqual(0, byLayer[0].layerIndex);
            CollectionAssert.AreEquivalent(new[] { "Aap" }, byLayer[0].parameters);
            Assert.AreEqual(1, byLayer[1].layerIndex);
            CollectionAssert.AreEquivalent(new[] { "Plain" }, byLayer[1].parameters);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void NoController_IsEmpty()
        {
            Assert.AreEqual(0, AapWriteScan.CollectWrittenParameters(null).Count);
        }
    }
}
