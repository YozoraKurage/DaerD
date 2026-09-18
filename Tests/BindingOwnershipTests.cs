using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The ownership walk: which layer writes which binding in EVERY state it can reach, which
    /// writes it in only some, and what goes into the union in the first place. Generic
    /// hierarchy paths throughout — the shape is what is under test, never anyone's avatar.
    /// </summary>
    public class BindingOwnershipTests
    {
        readonly List<Object> _trash = new List<Object>();

        [TearDown]
        public void DestroyEverythingMade()
        {
            foreach (var made in _trash)
                if (made != null) Object.DestroyImmediate(made);
            _trash.Clear();
        }

        AnimatorController NewController()
        {
            var controller = new AnimatorController();
            _trash.Add(controller);
            return controller;
        }

        /// <summary>A clip that holds one Transform property at a constant value. Written
        /// through SetEditorCurve rather than AnimationClip.SetCurve because SetCurve fills in
        /// the whole Vector3 — asking it for m_LocalPosition.x lands three bindings in the clip,
        /// and these tests are about counting bindings.</summary>
        AnimationClip Moves(string name, params string[] paths)
        {
            var clip = new AnimationClip { name = name };
            foreach (var path in paths) Hold(clip, path);
            _trash.Add(clip);
            return clip;
        }

        static void Hold(AnimationClip clip, string path) =>
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0f, 1f, 1f));

        static BindingKey Moved(string path) =>
            new BindingKey(path, typeof(Transform), "m_LocalPosition.x");

        static LayerBindings LayerOf(BindingOwnership ownership, int index) => ownership.Layers[index];

        [Test]
        public void AStateThatWritesEverywhere_Owns_AndOneThatDoesNot_OnlyTouches()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var idle = sm.AddState("Idle");
            var wave = sm.AddState("Wave");
            idle.AddTransition(wave);
            // Both states hold Body; only Wave holds Hand.
            idle.motion = Moves("Idle", "Body");
            wave.motion = Moves("Wave", "Body", "Hand");

            var ownership = BindingOwnership.Collect(controller);
            var layer = LayerOf(ownership, 0);

            Assert.AreEqual(2, layer.PlayingStates);
            Assert.AreEqual(0, layer.SilentStates);
            Assert.IsTrue(layer.Owns(Moved("Body")), "both states hold Body");
            Assert.IsFalse(layer.Owns(Moved("Hand")), "only one of the two states holds Hand");
            Assert.IsTrue(layer.Touches(Moved("Hand")));
            CollectionAssert.AreEquivalent(new[] { Moved("Body"), Moved("Hand") }, ownership.Union);
            Assert.AreEqual(1, ownership.Owners(Moved("Body")).Count);
            Assert.AreEqual(0, ownership.Owners(Moved("Hand")).Count);
            Assert.AreEqual(1, ownership.Touchers(Moved("Hand")).Count);
        }

        [Test]
        public void AReachableStateWithNoMotion_TakesOwnershipAway()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var on = sm.AddState("On");
            var off = sm.AddState("Off");
            on.AddTransition(off);
            on.motion = Moves("On", "Body");
            // Off has no motion: with Write Defaults OFF it freezes Body, with ON it drops it.

            var layer = LayerOf(BindingOwnership.Collect(controller), 0);

            Assert.AreEqual(1, layer.PlayingStates);
            Assert.AreEqual(1, layer.SilentStates);
            Assert.IsTrue(layer.Touches(Moved("Body")));
            Assert.IsFalse(layer.Owns(Moved("Body")));
        }

        [Test]
        public void AClipWithNoKeys_IsPlaying_ButWritesNothing()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var empty = new AnimationClip { name = "Empty" };
            _trash.Add(empty);
            sm.AddState("Empty").motion = empty;

            var ownership = BindingOwnership.Collect(controller);
            var layer = LayerOf(ownership, 0);

            Assert.AreEqual(1, layer.PlayingStates, "an empty clip still plays");
            Assert.IsFalse(layer.IsSilent);
            Assert.AreEqual(0, layer.Writers.Count);
            Assert.AreEqual(0, ownership.Union.Count);
        }

        [Test]
        public void ABlendTreesLeaves_AreAllCountedAsWrittenByTheState()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var tree = new BlendTree { name = "Tree", blendType = BlendTreeType.Direct };
            _trash.Add(tree);
            tree.AddChild(Moves("Left", "Left"));
            var nested = new BlendTree { name = "Nested", blendType = BlendTreeType.Direct };
            _trash.Add(nested);
            nested.AddChild(Moves("Right", "Right"));
            tree.AddChild(nested);
            controller.layers[0].stateMachine.AddState("Tree").motion = tree;

            var ownership = BindingOwnership.Collect(controller);
            var layer = LayerOf(ownership, 0);

            Assert.IsTrue(layer.Owns(Moved("Left")));
            Assert.IsTrue(layer.Owns(Moved("Right")), "a nested child is a leaf like any other");
            Assert.AreEqual(2, ownership.Union.Count);
        }

        [Test]
        public void AnUnreachableStatesClip_IsInTheUnion_ButWritesNothing()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            sm.AddState("Idle").motion = Moves("Idle", "Body");
            // Nothing leads here, so the layer can never assert Hand — but the Animator still
            // binds it, and the frames in which nobody writes it are exactly the question.
            sm.AddState("Orphan").motion = Moves("Orphan", "Hand");

            var ownership = BindingOwnership.Collect(controller);
            var layer = LayerOf(ownership, 0);

            CollectionAssert.Contains(ownership.Union, Moved("Hand"));
            Assert.IsFalse(layer.Touches(Moved("Hand")));
            Assert.AreEqual(1, layer.PlayingStates);
            Assert.AreEqual(0, ownership.Touchers(Moved("Hand")).Count);
        }

        [Test]
        public void AnimatorRootBindings_AreLeftOutEntirely()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var clip = new AnimationClip { name = "Aap" };
            _trash.Add(clip);
            // Muscles, root motion and AAPs all bind this way; none of them is this scan's business.
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "Smoothed"),
                AnimationCurve.Constant(0f, 1f, 1f));
            Hold(clip, "Body");
            controller.layers[0].stateMachine.AddState("Drive").motion = clip;

            var ownership = BindingOwnership.Collect(controller);

            Assert.AreEqual(1, ownership.Union.Count);
            Assert.AreEqual(Moved("Body"), ownership.Union[0]);
        }

        [Test]
        public void ObjectReferenceCurves_AreBindingsToo()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            var mesh = new Mesh { name = "Swapped" };
            _trash.Add(mesh);
            var clip = new AnimationClip { name = "Swap" };
            _trash.Add(clip);
            var binding = EditorCurveBinding.PPtrCurve("Body", typeof(MeshFilter), "m_Mesh");
            AnimationUtility.SetObjectReferenceCurve(clip, binding,
                new[] { new ObjectReferenceKeyframe { time = 0f, value = mesh } });
            controller.layers[0].stateMachine.AddState("Swap").motion = clip;

            var ownership = BindingOwnership.Collect(controller);

            Assert.AreEqual(1, ownership.Union.Count);
            Assert.AreEqual(BindingKey.Of(binding), ownership.Union[0]);
            Assert.IsTrue(LayerOf(ownership, 0).Owns(BindingKey.Of(binding)));
        }

        [Test]
        public void ALayerWithNoStatesOrNoMachine_IsSilent()
        {
            var controller = NewController();
            controller.AddLayer("Base");                                  // has a machine, no states
            controller.AddLayer(new AnimatorControllerLayer { name = "Bare" });   // no machine at all

            var ownership = BindingOwnership.Collect(controller);

            Assert.AreEqual(2, ownership.Layers.Count, "silent layers are still listed, so an index is an index");
            Assert.IsTrue(LayerOf(ownership, 0).IsSilent);
            Assert.IsTrue(LayerOf(ownership, 1).IsSilent);
            Assert.AreEqual(0, LayerOf(ownership, 1).States.Count);
        }

        [Test]
        public void ASyncedLayersOverrideMotion_IsWrittenByThatLayer_AndInTheUnion()
        {
            var controller = NewController();
            controller.AddLayer("Base");
            controller.AddLayer("Synced");
            var state = controller.layers[0].stateMachine.AddState("Idle");
            state.motion = Moves("base", "Body");
            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            layers[1].SetOverrideMotion(state, Moves("override", "Hand"));
            controller.layers = layers;

            var ownership = BindingOwnership.Collect(controller);

            CollectionAssert.Contains(ownership.Union, Moved("Hand"));
            Assert.IsTrue(LayerOf(ownership, 1).Owns(Moved("Hand")));
            Assert.IsFalse(LayerOf(ownership, 1).Touches(Moved("Body")));
            Assert.IsTrue(LayerOf(ownership, 0).Owns(Moved("Body")));
        }

        [Test]
        public void TwoLayersCanEachTouchTheSameBinding_WithoutEitherOwningIt()
        {
            var controller = NewController();
            controller.AddLayer("A");
            controller.AddLayer("B");
            foreach (var layer in controller.layers)
            {
                var sm = layer.stateMachine;
                var first = sm.AddState("Writes");
                var second = sm.AddState("Quiet");
                first.AddTransition(second);
                first.motion = Moves(layer.name, "Body");
                second.motion = Moves(layer.name + " other", "Other");
            }

            var ownership = BindingOwnership.Collect(controller);

            Assert.AreEqual(0, ownership.Owners(Moved("Body")).Count);
            Assert.AreEqual(2, ownership.Touchers(Moved("Body")).Count);
        }
    }
}
