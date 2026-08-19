using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;

namespace Yozolab.DaerD.Tests
{
    public class ClipDigestTests
    {
        static AnimationClip Clip(string name) => new AnimationClip { name = name };

        static void Curve(AnimationClip clip, string path, System.Type type, string property,
            params Keyframe[] keys)
        {
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path, type, property), new AnimationCurve(keys));
        }

        [Test]
        public void ConstantCurves_AreConstants_AndTheClipIsStatic()
        {
            var clip = Clip("Toggle");
            Curve(clip, "Chara/FX", typeof(GameObject), "m_IsActive", new Keyframe(0f, 1f));
            Curve(clip, "Body", typeof(SkinnedMeshRenderer), "blendShape.Smile",
                new Keyframe(0f, 100f), new Keyframe(1f, 100f));

            var facts = ClipDigest.Collect(clip);

            Assert.IsFalse(facts.animated);
            Assert.AreEqual(2, facts.constants.Count);
            Assert.AreEqual(0, facts.motion.Count);
            var smile = facts.constants.Find(f => f.property == "blendShape.Smile");
            Assert.IsTrue(smile.constant);
            Assert.AreEqual(100f, smile.value);
            Assert.AreEqual(2, smile.keys);

            Object.DestroyImmediate(clip);
        }

        [Test]
        public void VaryingCurve_ReportsKeyCountAndRange()
        {
            var clip = Clip("Motion");
            Curve(clip, "Hips", typeof(Transform), "m_LocalPosition.y",
                new Keyframe(0f, 0f), new Keyframe(0.5f, 0.75f), new Keyframe(1f, 0.25f));

            var facts = ClipDigest.Collect(clip);

            Assert.IsTrue(facts.animated);
            Assert.AreEqual(1, facts.motion.Count);
            Assert.IsFalse(facts.motion[0].constant);
            Assert.AreEqual(3, facts.motion[0].keys);
            Assert.AreEqual(0f, facts.motion[0].min);
            Assert.AreEqual(0.75f, facts.motion[0].max);

            Object.DestroyImmediate(clip);
        }

        [Test]
        public void AnimatorRootBinding_IsAParameterWrite_NotAConstant()
        {
            var clip = Clip("AAP");
            Curve(clip, string.Empty, typeof(Animator), "Volume", new Keyframe(0f, 0.5f));
            // Under a child path it drives a nested animator, not a parameter of this one.
            Curve(clip, "Child", typeof(Animator), "Volume", new Keyframe(0f, 1f));

            var facts = ClipDigest.Collect(clip);

            Assert.AreEqual(1, facts.parameters.Count);
            Assert.AreEqual("Volume", facts.parameters[0].property);
            Assert.AreEqual(0.5f, facts.parameters[0].value);
            Assert.AreEqual(1, facts.constants.Count);   // the child-path one

            Object.DestroyImmediate(clip);
        }

        [Test]
        public void MuscleRootAndFingerCurves_AreTalliedByRegion_NotListed()
        {
            var clip = Clip("Pose");
            // The three humanoid spellings: a HumanTrait muscle, a finger, a root curve.
            Curve(clip, string.Empty, typeof(Animator), HumanTrait.MuscleName[0],
                new Keyframe(0f, 0.3f));
            Curve(clip, string.Empty, typeof(Animator), "LeftHand.Index.1 Stretched",
                new Keyframe(0f, 0.6f));
            Curve(clip, string.Empty, typeof(Animator), "RootT.x",
                new Keyframe(0f, 0f), new Keyframe(1f, 1f));

            var facts = ClipDigest.Collect(clip);

            Assert.AreEqual(0, facts.parameters.Count);
            Assert.IsNotNull(facts.muscles);
            Assert.AreEqual(3, facts.muscles.total);
            Assert.AreEqual(1, facts.muscles.animated);   // only RootT.x varies
            Assert.IsTrue(facts.animated);
            Assert.IsTrue(facts.muscles.regions.ContainsKey("LeftFingers"));
            Assert.IsTrue(facts.muscles.regions.ContainsKey("Root"));

            Object.DestroyImmediate(clip);
        }

        [Test]
        public void LoopFlag_ComesFromClipSettings()
        {
            var clip = Clip("Loop");
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            Assert.IsTrue(ClipDigest.Collect(clip).loop);

            Object.DestroyImmediate(clip);
        }

        [Test]
        public void ObjectReferenceCurve_ListsNamesAndPrettifiesTheArrayPath()
        {
            var clip = Clip("Swap");
            var a = new Texture2D(1, 1) { name = "MatA" };
            var b = new Texture2D(1, 1) { name = "MatB" };
            AnimationUtility.SetObjectReferenceCurve(clip,
                EditorCurveBinding.PPtrCurve("Body", typeof(SkinnedMeshRenderer),
                    "m_Materials.Array.data[0]"),
                new[]
                {
                    new ObjectReferenceKeyframe { time = 0f, value = a },
                    new ObjectReferenceKeyframe { time = 1f, value = b },
                });

            var facts = ClipDigest.Collect(clip);

            Assert.AreEqual(1, facts.objectRefs.Count);
            Assert.AreEqual("m_Materials[0]", facts.objectRefs[0].property);
            Assert.AreEqual("Texture2D", facts.objectRefs[0].valueType);
            Assert.AreEqual(2, facts.objectRefs[0].keys.Count);
            Assert.AreEqual("MatA", facts.objectRefs[0].keys[0].value);
            Assert.IsTrue(facts.animated);   // a multi-key swap animates even with no floats

            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(a);
            Object.DestroyImmediate(b);
        }

        [Test]
        public void Format_StatesTheHeadlineFactsAndValues()
        {
            var clip = Clip("Toggle");
            Curve(clip, "Chara/FX", typeof(GameObject), "m_IsActive", new Keyframe(0f, 1f));

            var text = ClipDigest.Format(ClipDigest.Collect(clip));

            StringAssert.Contains("clip \"Toggle\"", text);
            StringAssert.Contains(", static", text);
            StringAssert.Contains("Chara/FX <GameObject> m_IsActive = 1", text);

            Object.DestroyImmediate(clip);
        }

        [Test]
        public void Format_AnEmptyClip_SaysSo()
        {
            var clip = Clip("Empty");
            StringAssert.Contains("empty", ClipDigest.Format(ClipDigest.Collect(clip)));
            Object.DestroyImmediate(clip);
        }

        [Test]
        public void CollectFromController_FindsClipsWithSites_IncludingParkedAndTreed()
        {
            var controller = new AnimatorController();
            controller.AddLayer("FX");
            var sm = controller.layers[0].stateMachine;
            var shared = Clip("Shared");
            sm.AddState("On").motion = shared;          // default state
            sm.AddState("Parked").motion = Clip("Parked");   // nothing leads here — still listed
            var tree = new BlendTree { name = "Blend" };
            tree.AddChild(shared);
            sm.AddState("Tree").motion = tree;

            var uses = ClipDigest.CollectFromController(controller);

            Assert.AreEqual(2, uses.Count);
            var sharedUse = uses.Find(u => u.clip.name == "Shared");
            Assert.AreEqual(2, sharedUse.sites.Count);
            StringAssert.Contains("FX/On", sharedUse.sites[0]);
            StringAssert.Contains("tree \"Blend\"", sharedUse.sites[1]);
            Assert.IsNotNull(uses.Find(u => u.clip.name == "Parked"));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FormatTrees_ShowsAxesThresholdsAndDirectWeights()
        {
            var controller = new AnimatorController();
            controller.AddLayer("FX");
            var lut = new BlendTree
            {
                name = "Lut",
                blendType = BlendTreeType.Simple1D,
                blendParameter = "Index",
                // Left on, Unity redistributes thresholds evenly and 2 becomes 1.
                useAutomaticThresholds = false,
            };
            lut.AddChild(Clip("Low"), 0f);
            lut.AddChild(Clip("High"), 2f);
            var direct = new BlendTree { name = "Gadget", blendType = BlendTreeType.Direct };
            direct.AddChild(Clip("X_aap"));
            var children = direct.children;
            children[0].directBlendParameter = "GadgetX";
            direct.children = children;
            direct.AddChild(lut);
            controller.layers[0].stateMachine.AddState("Tree").motion = direct;

            var text = ClipDigest.FormatTrees(controller);

            StringAssert.Contains("FX/Tree:", text);
            StringAssert.Contains("tree \"Gadget\" Direct:", text);
            StringAssert.Contains("\"X_aap\" x GadgetX", text);
            StringAssert.Contains("tree \"Lut\" 1D(Index):", text);
            StringAssert.Contains("\"High\" @ 2", text);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FormatTrees_NoTrees_IsEmpty()
        {
            var controller = new AnimatorController();
            controller.AddLayer("FX");
            controller.layers[0].stateMachine.AddState("Plain").motion = Clip("Plain");

            Assert.AreEqual("", ClipDigest.FormatTrees(controller));

            Object.DestroyImmediate(controller);
        }
    }
}
