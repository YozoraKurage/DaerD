using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Finding the Animator that is running the controller being edited, and reading it. The
    /// resolution order is the interesting half — the scene usually holds more than one avatar,
    /// and reading an arbitrary one would be worse than reading none.
    /// </summary>
    public class LiveAnimatorTests
    {
        static AnimatorController NewController(string name)
        {
            var controller = new AnimatorController { name = name };
            controller.AddLayer("Base");
            return controller;
        }

        static Animator NewAnimator(string name, RuntimeAnimatorController controller)
        {
            var animator = new GameObject(name).AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            return animator;
        }

        [Test]
        public void Runs_SeesThroughStackedOverrideControllers()
        {
            var controller = NewController("Base");
            var other = NewController("Other");
            var first = new AnimatorOverrideController(controller);
            var second = new AnimatorOverrideController(first);

            var animator = NewAnimator("Rig", second);

            Assert.IsTrue(LiveAnimator.Runs(animator, controller));
            Assert.IsFalse(LiveAnimator.Runs(animator, other));
            Assert.IsFalse(LiveAnimator.Runs(null, controller));
            Assert.IsFalse(LiveAnimator.Runs(animator, null));

            Object.DestroyImmediate(animator.gameObject);
            Object.DestroyImmediate(second);
            Object.DestroyImmediate(first);
            Object.DestroyImmediate(other);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Resolve_PrefersThePinnedAnimatorOverEverythingElse()
        {
            var controller = NewController("C");
            var pinned = NewAnimator("Pinned", controller);
            var selected = NewAnimator("Selected", controller);

            var resolution = LiveAnimator.Resolve(controller, pinned, selected.gameObject);

            Assert.AreSame(pinned, resolution.animator);
            Assert.IsFalse(resolution.ambiguous);

            Object.DestroyImmediate(pinned.gameObject);
            Object.DestroyImmediate(selected.gameObject);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Resolve_IgnoresAPinPointingAtAnotherController()
        {
            var controller = NewController("C");
            var other = NewController("Other");
            var stale = NewAnimator("Stale", other);
            var selected = NewAnimator("Selected", controller);

            var resolution = LiveAnimator.Resolve(controller, stale, selected.gameObject);

            Assert.AreSame(selected, resolution.animator);

            Object.DestroyImmediate(stale.gameObject);
            Object.DestroyImmediate(selected.gameObject);
            Object.DestroyImmediate(other);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Resolve_TakesTheAnimatorAboveWhateverIsSelected()
        {
            var controller = NewController("C");
            var animator = NewAnimator("Avatar", controller);
            // Clicking a mesh under an avatar is how people select it; the Animator is up top.
            var mesh = new GameObject("Body");
            mesh.transform.SetParent(animator.transform);

            var resolution = LiveAnimator.Resolve(controller, null, mesh);

            Assert.AreSame(animator, resolution.animator);

            Object.DestroyImmediate(animator.gameObject);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Resolve_TakesTheSceneWhenOnlyOneAnimatorRunsTheController()
        {
            var controller = NewController("C");
            var animator = NewAnimator("Only", controller);

            var resolution = LiveAnimator.Resolve(controller, null, null);

            Assert.AreSame(animator, resolution.animator);
            Assert.IsFalse(resolution.ambiguous);

            Object.DestroyImmediate(animator.gameObject);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Resolve_WithTwoCandidatesAndNothingChosen_IsAmbiguous()
        {
            var controller = NewController("C");
            var first = NewAnimator("A", controller);
            var second = NewAnimator("B", controller);

            var resolution = LiveAnimator.Resolve(controller, null, null);

            Assert.IsNull(resolution.animator, "reading an arbitrary one would be worse than none");
            Assert.IsTrue(resolution.ambiguous);

            Object.DestroyImmediate(first.gameObject);
            Object.DestroyImmediate(second.gameObject);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Resolve_WithNoCandidate_IsEmptyButNotAmbiguous()
        {
            var controller = NewController("C");

            var resolution = LiveAnimator.Resolve(controller, null, null);

            Assert.IsNull(resolution.animator);
            Assert.IsFalse(resolution.ambiguous);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Has_AnswersForTheRunningControllersNamesAndTypes()
        {
            var controller = NewController("C");
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Flag", AnimatorControllerParameterType.Bool);

            using (var rig = new AnimatorRig(controller))
            {
                var live = new LiveAnimator();
                live.Bind(rig.Root.GetComponent<Animator>());

                Assert.IsTrue(live.Has("Speed", AnimatorControllerParameterType.Float));
                Assert.IsTrue(live.Has("Flag", AnimatorControllerParameterType.Bool));
                // The name has to match the type too: asking an Animator for a parameter it
                // does not have that way logs an error per call, at repaint rate.
                Assert.IsFalse(live.Has("Speed", AnimatorControllerParameterType.Int));
                Assert.IsFalse(live.Has("Missing", AnimatorControllerParameterType.Float));
                Assert.AreEqual(0f, live.GetFloat("Missing"), 1e-6f);
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Values_ComeFromTheRunningAnimator_NotTheControllersDefaults()
        {
            var controller = NewController("C");
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

            using (var rig = new AnimatorRig(controller))
            {
                var live = new LiveAnimator();
                live.Bind(rig.Root.GetComponent<Animator>());

                live.SetFloat("Speed", 0.8f);
                rig.Step();

                Assert.AreEqual(0.8f, live.GetFloat("Speed"), 1e-4f);
                foreach (var p in controller.parameters)
                    if (p.name == "Speed")
                        Assert.AreEqual(0f, p.defaultFloat, 1e-6f, "the asset must be untouched");
            }
            Object.DestroyImmediate(controller);
        }

        /// <summary>
        /// The reason the panel is worth changing: a gadget's output exists only on the running
        /// Animator. The asset says the parameter starts at zero and nothing else.
        /// </summary>
        [Test]
        public void AGadgetsOutput_IsReadableLive_AndReportsItselfAsCurveDriven()
        {
            var controller = NewController("C");
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            Assert.IsTrue(AapGadgets.Apply(new AapGadgets.Request
            {
                controller = controller,
                kind = AapGadgets.Kind.Smooth,
                inputA = "Speed",
                output = "Speed/Smoothed",
                smoothing = "Speed/Smoothing",
                smoothingDefault = 0.5f,
                rangeMin = -1f,
                rangeMax = 1f,
                layerIndex = -1,
                newLayerName = "DBT",
            }));

            using (var rig = new AnimatorRig(controller))
            {
                var live = new LiveAnimator();
                live.Bind(rig.Root.GetComponent<Animator>());

                rig.Set("Speed", 1f).Step(20);

                Assert.Greater(live.GetFloat("Speed/Smoothed"), 0.5f,
                    "the smoothing should have climbed most of the way to the input");
                Assert.IsTrue(live.IsCurveDriven("Speed/Smoothed"),
                    "the animation system is writing it — the runtime's own answer to 'is this an AAP'");
                Assert.IsFalse(live.IsCurveDriven("Speed"), "this one comes from outside");
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Poll_OutsidePlayMode_ReadsNothing()
        {
            var controller = NewController("C");
            var animator = NewAnimator("Rig", controller);

            var live = new LiveAnimator();
            live.Bind(animator);
            Assert.IsNotNull(live.Current);

            live.Poll(controller);   // the tests never run inside play mode

            Assert.IsNull(live.Current);
            Assert.IsFalse(live.IsLive);

            Object.DestroyImmediate(animator.gameObject);
            Object.DestroyImmediate(controller);
        }
    }
}
