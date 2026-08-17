using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The object toggles, run instead of read.
    ///
    /// What a structural test can check about a toggle is that the clips carry an m_IsActive
    /// curve for the path that was asked for. What it cannot check is the part that actually
    /// goes wrong in practice: whether that path resolves against the Animator's root to the
    /// object the user picked, whether the Bool transitions fire in both directions, and
    /// whether the two states — Write Defaults deliberately OFF — leave everything they don't
    /// name alone. Those are runtime facts, so these tests build a hierarchy, drive it with an
    /// <see cref="AnimatorRig"/>, and read <c>activeSelf</c> back off the objects.
    /// </summary>
    [Category("Runtime")]
    public class ToggleRuntimeTests
    {
        /// <summary>Avatar / Body / Hat — one nested path ("Body/Hat") and one shallow sibling,
        /// which is what makes "the right object, and only it" a question worth asking.</summary>
        static GameObject Hierarchy(out GameObject body, out GameObject hat, out GameObject bag)
        {
            var root = new GameObject("Avatar");
            body = new GameObject("Body");
            body.transform.SetParent(root.transform);
            hat = new GameObject("Hat");
            hat.transform.SetParent(body.transform);
            bag = new GameObject("Bag");
            bag.transform.SetParent(root.transform);
            return root;
        }

        static ToggleBuilder.Plan NewPlan(AnimatorController controller,
            ToggleBuilder.Mode mode, params string[] paths)
        {
            var plan = new ToggleBuilder.Plan
            {
                controller = controller,
                mode = mode,
                name = "Hat",
                parameter = "Hat",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            foreach (var path in paths)
                plan.targets.Add(new ToggleBuilder.Target { path = path });
            return plan;
        }

        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            return controller;
        }

        /// <summary>The core, driven the way <see cref="ObjectGadgets"/> drives it. The paths are
        /// written out here instead of being derived from a prefab because what these tests are
        /// about is the machinery playing them — deriving them is the model's half, and it has a
        /// prefab of its own to be tested against.</summary>
        static void Build(ToggleBuilder.Plan plan)
        {
            var onClip = ToggleBuilder.BuildClip(plan, on: true);
            var offClip = ToggleBuilder.BuildClip(plan, on: false);
            if (plan.mode == ToggleBuilder.Mode.Layer)
                ToggleBuilder.BuildLayer(plan, onClip, offClip, out _);
            else
                ToggleBuilder.BuildDirectBlendTree(plan, onClip, offClip, out _);
        }

        // ---- the Bool layer -------------------------------------------------------

        [Test]
        public void Layer_TurnsTheTargetOffAndOnAgain()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.Layer, "Body/Hat"));

            var root = Hierarchy(out var body, out var hat, out var bag);
            using (var rig = new AnimatorRig(controller, root))
            {
                rig.Step();
                Assert.IsFalse(hat.activeSelf, "the default is off, so the first frame should hide it");
                Assert.AreEqual("Hat OFF", rig.CurrentState(1, "Hat OFF", "Hat ON"));

                rig.Set("Hat", true).Step(3);
                Assert.IsTrue(hat.activeSelf, "setting the bool should show it");
                Assert.AreEqual("Hat ON", rig.CurrentState(1, "Hat OFF", "Hat ON"));

                rig.Set("Hat", false).Step(3);
                Assert.IsFalse(hat.activeSelf, "and clearing it should hide it again");

                Assert.IsTrue(body.activeSelf, "the path's parent is not a target");
                Assert.IsTrue(bag.activeSelf, "nor is anything else in the hierarchy");
            }
        }

        /// <summary>The default belongs to the parameter, and the layer is supposed to start on
        /// the state that matches it rather than transition into it on the first frame.</summary>
        [Test]
        public void Layer_StartsOnWhenTheDefaultSaysSo()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Body/Hat");
            plan.defaultOn = true;
            Build(plan);

            var root = Hierarchy(out _, out var hat, out _);
            using (var rig = new AnimatorRig(controller, root))
            {
                rig.Step();
                Assert.IsTrue(hat.activeSelf);
                Assert.AreEqual("Hat ON", rig.CurrentState(1, "Hat OFF", "Hat ON"));
            }
        }

        /// <summary>An inverted target hides when the toggle is on — the "toggle ON means take
        /// it off" case the wizard offers per target.</summary>
        [Test]
        public void Layer_InvertedTargetGoesTheOtherWay()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer, "Body/Hat");
            plan.targets.Add(new ToggleBuilder.Target { path = "Bag", activeWhenOn = false });
            Build(plan);

            var root = Hierarchy(out _, out var hat, out var bag);
            using (var rig = new AnimatorRig(controller, root))
            {
                rig.Step();
                Assert.IsFalse(hat.activeSelf, "off: the plain target is hidden");
                Assert.IsTrue(bag.activeSelf, "off: the inverted target is shown");

                rig.Set("Hat", true).Step(3);
                Assert.IsTrue(hat.activeSelf, "on: the plain target is shown");
                Assert.IsFalse(bag.activeSelf, "on: the inverted target is hidden");
            }
        }

        /// <summary>A target that only drives a component: the GameObject stays as it is and the
        /// component's enabled flag follows the toggle.</summary>
        [Test]
        public void Layer_ComponentBindingFollowsTheToggle()
        {
            var controller = NewController();
            var plan = NewPlan(controller, ToggleBuilder.Mode.Layer);
            var target = new ToggleBuilder.Target { path = "Bag", toggleActive = false };
            target.bindings.Add(ToggleBuilder.Binding.Enabled(typeof(BoxCollider)));
            plan.targets.Add(target);
            Build(plan);

            var root = Hierarchy(out _, out _, out var bag);
            var collider = bag.AddComponent<BoxCollider>();
            using (var rig = new AnimatorRig(controller, root))
            {
                rig.Step();
                Assert.IsFalse(collider.enabled, "off");
                Assert.IsTrue(bag.activeSelf, "the object itself is not a target");

                rig.Set("Hat", true).Step(3);
                Assert.IsTrue(collider.enabled, "on");
                Assert.IsTrue(bag.activeSelf, "still not a target");
            }
        }

        // ---- the Direct blend tree ------------------------------------------------

        /// <summary>The other wiring for the same clips: a 1D tree in a Write-Defaults-ON layer,
        /// blended by a Float, so many toggles share one layer instead of one each.</summary>
        [Test]
        public void DirectBlendTree_TurnsTheTargetOffAndOnWithAFloat()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Body/Hat"));

            var root = Hierarchy(out _, out var hat, out var bag);
            using (var rig = new AnimatorRig(controller, root))
            {
                rig.Step();
                Assert.IsFalse(hat.activeSelf, "at 0");

                rig.Set("Hat", 1f).Step();
                Assert.IsTrue(hat.activeSelf, "at 1");

                rig.Set("Hat", 0f).Step();
                Assert.IsFalse(hat.activeSelf, "back at 0");

                Assert.IsTrue(bag.activeSelf, "and nothing else moved");
            }
        }

        /// <summary>Two toggles in one Direct layer, which is the mode's whole reason to exist:
        /// each has to drive its own target and leave the other's alone.</summary>
        [Test]
        public void DirectBlendTree_TwoTogglesInOneLayerStayIndependent()
        {
            var controller = NewController();
            Build(NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Body/Hat"));

            var second = NewPlan(controller, ToggleBuilder.Mode.DirectBlendTree, "Bag");
            second.name = "Bag";
            second.parameter = "Bag";
            second.layerIndex = 1;
            Build(second);

            Assert.AreEqual(2, controller.layers.Length, "both toggles should share one layer");

            var root = Hierarchy(out _, out var hat, out var bag);
            using (var rig = new AnimatorRig(controller, root))
            {
                rig.Set("Hat", 1f).Step();
                Assert.IsTrue(hat.activeSelf);
                Assert.IsFalse(bag.activeSelf);

                rig.Set("Hat", 0f).Set("Bag", 1f).Step();
                Assert.IsFalse(hat.activeSelf);
                Assert.IsTrue(bag.activeSelf);
            }
        }
    }
}
