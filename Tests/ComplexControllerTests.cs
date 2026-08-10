using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Authoring;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The features under one roof rather than one at a time. Every other fixture here builds
    /// the smallest controller that can show its own point, which is the right way to localise
    /// a failure but leaves the interesting question unasked: the round trip has to survive a
    /// controller where a state's speed comes from a parameter that a driver on another layer
    /// writes, where a blend tree nests three deep under a synced layer's override, where a
    /// sub-machine's exit is somebody else's Any State route. Nothing here is exotic on its
    /// own — an avatar controller of any size is all of it at once.
    /// </summary>
    public class ComplexControllerTests
    {
        readonly List<Object> _cleanup = new List<Object>();
        AnimationClip _idle;
        AnimationClip _walk;
        AnimationClip _run;
        AnimationClip _wave;

        [SetUp]
        public void SetUp()
        {
            _idle = Clip("Idle");
            _walk = Clip("Walk");
            _run = Clip("Run");
            _wave = Clip("Wave");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
                if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        AnimationClip Clip(string name)
        {
            // A curve, so the clip has a real length: normalized exit times need one.
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0f, 1f, 1f));
            _cleanup.Add(clip);
            return clip;
        }

        T Track<T>(T o) where T : Object
        {
            _cleanup.Add(o);
            return o;
        }

        /// <summary>BehaviourJson takes an EditorJsonUtility snapshot, not a bare field bag.</summary>
        static string Snapshot(string payload = null, int number = 0)
        {
            var template = ScriptableObject.CreateInstance<IRTestBehaviour>();
            template.payload = payload;
            template.number = number;
            string json = EditorJsonUtility.ToJson(template);
            Object.DestroyImmediate(template);
            return json;
        }

        TestRecipe NewRecipe(AnimatorController target, System.Action<ControllerBuilder> body)
        {
            var recipe = ScriptableObject.CreateInstance<TestRecipe>();
            recipe.targetController = target;
            recipe.body = body;
            _cleanup.Add(recipe);
            return recipe;
        }

        // ---- the declaration under test ---------------------------------------

        /// <summary>
        /// Four parameter types with defaults, four layers, three levels of sub-machine, every
        /// blend tree kind including one nested inside another, drivers of all four entry
        /// kinds, behaviours on states and on machines, a synced layer overriding two of the
        /// states it mirrors, and transitions carrying every timing and interruption field
        /// the IR models. Written once and reused, so a test that changes it changes all of
        /// them — which is the point.
        /// </summary>
        void BuildComplex(ControllerBuilder c)
        {
            var blend = c.FloatParameter("Blend", 0.35f);
            var lift = c.FloatParameter("Lift");
            var speed = c.FloatParameter("Speed", 1f);
            var motionTime = c.FloatParameter("MotionTime");
            var go = c.BoolParameter("Go");
            var crouched = c.BoolParameter("Crouched", true);
            var mode = c.IntParameter("Mode", 2);
            var fire = c.TriggerParameter("Fire");
            var mirrored = c.BoolParameter("Mirrored");
            c.FloatParameter("Unreferenced", 0.5f);

            // ---- Locomotion: nesting, cross-machine routes, every transition field ----
            var loco = c.Layer("Locomotion").WithWeight(1f).WithIkPass();

            var idle = loco.NewState("Idle").WithAnimation(_idle).At(0, 0).Default()
                .WithWriteDefaultsSetTo(false)
                .WithTag("Ground");

            var moving = loco.NewSubStateMachine("Moving").At(300, 0);
            var walk = moving.NewState("Walk").WithAnimation(_walk).At(0, 0)
                .WithSpeed(speed)
                .WithCycleOffsetSetTo(0.25f)
                .WithWriteDefaultsSetTo(false);
            var run = moving.NewState("Run").WithAnimation(_run).At(0, 120)
                .WithMirror(mirrored)
                .WithWriteDefaultsSetTo(false);

            var airborne = moving.NewSubStateMachine("Airborne").At(300, 60);
            var rising = airborne.NewState("Rising").WithAnimation(_idle).At(0, 0)
                .WithMotionTime(motionTime)
                .WithWriteDefaultsSetTo(false);
            var falling = airborne.NewState("Falling").WithAnimation(_idle).At(0, 120)
                .WithFootIkSetTo(true)
                .WithWriteDefaultsSetTo(false);

            idle.TransitionsTo(moving)
                .When(go.IsTrue())
                .And(mode.IsNotEqualTo(0))
                .AfterAnimationIsAtLeastAtNormalized(0.8f)
                .WithTransitionDurationSeconds(0.15f)
                .WithOffset(0.1f)
                .WithInterruption(TransitionInterruptionSource.Destination);

            walk.TransitionsTo(run)
                .When(blend.IsGreaterThan(0.6f))
                .WithTransitionDurationNormalized(0.3f)
                .WithNoOrderedInterruption();

            run.TransitionsTo(airborne).When(lift.IsGreaterThan(0.1f));
            rising.TransitionsTo(falling).When(lift.IsLessThan(0f)).AfterAnimationFinishes();
            falling.Exits().When(lift.IsGreaterThan(-0.01f)).WithTransitionDurationSeconds(0.2f);
            airborne.Exits().When(crouched.IsTrue());
            moving.TransitionsTo(idle).When(go.IsFalse());

            loco.AnyTransitionsTo(idle).When(fire.IsSet()).WithTransitionToSelf();
            loco.EntryTransitionsTo(walk).When(mode.IsEqualTo(1));
            loco.EntryAt(-200, 0).ExitAt(700, 0).AnyStateAt(-200, 120).ParentAt(700, 120);
            moving.EntryAt(-150, 0).ExitAt(400, 0).AnyStateAt(-150, 100).ParentAt(400, 100);

            // ---- Gesture: blend trees nested three deep, one of every kind ----
            var gesture = c.Layer("Gesture").WithWeight(0.75f).Additive();

            var direct = c.NewBlendTree("Direct").Direct()
                .WithAnimation(_wave, lift)
                .WithAnimation(_idle, blend);

            var cartesian = c.NewBlendTree("Cartesian").FreeformCartesian2D(blend, lift)
                .WithAnimation(_walk, -1f, -1f)
                .WithAnimation(_run, 1f, 1f)
                .WithAnimation(direct, 0f, 1f);

            var linear = c.NewBlendTree("Linear").Simple1D(blend)
                .AutoThresholds(false)
                .ThresholdRange(0f, 1f)
                .NormalizedBlendValues()
                .WithAnimation(_idle, 0f)
                .WithAnimation(cartesian, 0.5f)
                .WithAnimation(_wave, 1f);

            var blended = gesture.NewState("Blended").WithAnimation(linear).At(0, 0)
                .WithWriteDefaultsSetTo(false);
            var still = gesture.NewState("Still").WithAnimation(_idle).At(0, 150)
                .WithWriteDefaultsSetTo(false);

            blended.TransitionsTo(still).When(go.IsFalse()).WithTransitionDurationSeconds(0.05f);
            still.TransitionsTo(blended).When(go.IsTrue()).WithTransitionDurationSeconds(0.05f);

            // ---- Drivers: all four entry kinds, on a layer that reads none of them ----
            var logic = c.Layer("Logic").WithWeight(1f);
            var arm = logic.NewState("Arm").WithAnimation(_idle).At(0, 0).Default()
                .WithWriteDefaultsSetTo(false)
                .NewDriver("Arming")
                .DrivingLocally()
                .Drives(mode, 3f)
                .DrivingIncreases(blend, 0.1f)
                .DrivingRandomizes(lift, -1f, 1f)
                .DrivingCopies(blend, lift);
            var disarm = logic.NewState("Disarm").WithAnimation(_idle).At(0, 150)
                .WithWriteDefaultsSetTo(false)
                .Drives(crouched, false)
                .DrivingDecreases(blend, 0.2f)
                .DrivingRandomizes(go, 0.25f);

            arm.TransitionsTo(disarm).When(mode.IsGreaterThan(2)).Solo();
            disarm.TransitionsTo(arm).When(mode.IsLessThan(1)).Mute();

            // ---- Behaviours: on a state, and on a machine root ----
            logic.BehaviourJson("IRTestBehaviour", Snapshot(payload: "layer"));
            arm.BehaviourJson("IRTestBehaviour", Snapshot(number: 7), "Armed");

            // ---- A synced layer mirroring Locomotion, overriding two of its states ----
            c.SyncedLayer("Locomotion Echo", "Locomotion")
                .WithWeight(0.5f)
                .AffectsTiming()
                .Override("Idle", _wave)
                .Override("Moving/Walk", _run);
        }

        // ---- what the declaration is worth ------------------------------------

        [Test]
        public void Generate_LandsWithoutComplaint_AndTheControllerMatchesTheDeclaration()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, BuildComplex);

            var warnings = recipe.Generate();

            Assert.IsEmpty(warnings, string.Join("\n", warnings));

            // The shape, pinned. Every other test here leans on this one having built what it
            // says it built — a declaration that quietly stopped emitting half of itself would
            // make round trips and renames pass by having nothing to carry.
            Assert.AreEqual(4, controller.layers.Length, "layers");
            Assert.AreEqual(10, controller.parameters.Length, "parameters");
            Assert.AreEqual(9, Count(controller.AllStates()), "states");
            Assert.AreEqual(5, Count(controller.AllStateMachines()), "state machines");
            Assert.AreEqual(13, Count(controller.AllTransitions()), "transitions");
            Assert.AreEqual(3, Count(controller.AllBlendTrees()), "blend trees");
            // Two declared outright, plus the driver each of the two Logic states carries.
            Assert.AreEqual(4, Count(controller.AllBehaviours()), "behaviours");
        }

        static int Count<T>(IEnumerable<T> items)
        {
            int n = 0;
            foreach (var unused in items) n++;
            return n;
        }

        [Test]
        public void Generate_Twice_LeavesTheSameController()
        {
            var controller = Track(new AnimatorController());
            var recipe = NewRecipe(controller, BuildComplex);

            Assert.IsEmpty(recipe.Generate());
            var first = ControllerIR.Parse(controller);

            Assert.IsEmpty(recipe.Generate());
            var second = ControllerIR.Parse(controller);

            var drift = ControllerIRDiff.Compare(first, second);
            Assert.IsEmpty(drift, string.Join("\n", drift));
        }

        [Test]
        public void Export_ReplaysTheWholeControllerBack()
        {
            var controller = Track(new AnimatorController());
            Assert.IsEmpty(NewRecipe(controller, BuildComplex).Generate());

            var result = RecipeExporter.Export(controller, null, "ComplexRecipe", "Test.Space");
            Assert.IsEmpty(result.warnings, string.Join("\n", result.warnings));

            result.replayed.Bake();
            var drift = ControllerIRDiff.Compare(ControllerIR.Parse(controller), result.replayed.IR);
            Assert.IsEmpty(drift, string.Join("\n", drift));
        }

        [Test]
        public void Export_OfAGeneratedControllerIsStable()
        {
            var controller = Track(new AnimatorController());
            Assert.IsEmpty(NewRecipe(controller, BuildComplex).Generate());

            string first = RecipeExporter.Export(controller, null, "ComplexRecipe", null).code;
            string second = RecipeExporter.Export(controller, null, "ComplexRecipe", null).code;

            Assert.AreEqual(first, second, "two exports of one controller disagree");
        }

        /// <summary>
        /// The rename has to reach a parameter that is read as a transition condition, as a
        /// blend tree's axis, as a driver's destination and as a driver's copy source, all in
        /// the same pass — and leave nothing behind under the old name.
        /// </summary>
        [Test]
        public void Rename_ReachesEveryKindOfReferenceAtOnce()
        {
            var controller = Track(new AnimatorController());
            Assert.IsEmpty(NewRecipe(controller, BuildComplex).Generate());

            Assert.IsTrue(ParameterRenamer.Rename(controller, "Blend", "Mixed"));

            var usages = new HashSet<string>();
            foreach (var bt in controller.AllBlendTrees())
            {
                usages.Add(bt.blendParameter);
                foreach (var child in bt.children)
                    usages.Add(child.directBlendParameter);
            }
            CollectionAssert.DoesNotContain(usages, "Blend");
            CollectionAssert.Contains(usages, "Mixed");

            foreach (var state in controller.AllStates())
                foreach (var transition in state.transitions)
                    foreach (var condition in transition.conditions)
                        Assert.AreNotEqual("Blend", condition.parameter);

            foreach (var behaviour in controller.AllBehaviours())
                Assert.IsFalse(VrcParameterDriver.References(behaviour, "Blend"),
                    "a driver still points at the old name");
        }

        /// <summary>
        /// The analyzer has opinions a healthy controller can still earn — mixed write
        /// defaults, a layer full of terminal states — and those are advice, not defects.
        /// What it must not say about a controller the recipe just built is that something is
        /// broken: a state with no motion, a behaviour slot with nothing in it, a condition on
        /// a parameter that is not there, two things sharing a name. Those would mean the
        /// build itself came out wrong.
        /// </summary>
        [Test]
        public void Analyzer_ReportsNothingBroken_AndStillSpotsTheParameterNobodyReads()
        {
            var controller = Track(new AnimatorController());
            Assert.IsEmpty(NewRecipe(controller, BuildComplex).Generate());

            var broken = new HashSet<IssueKind>
            {
                IssueKind.MissingMotion, IssueKind.MissingBehaviour, IssueKind.InvalidCondition,
                IssueKind.DuplicateName, IssueKind.UnreachableState, IssueKind.EmptyLayer,
            };
            var found = new List<string>();
            foreach (var issue in ControllerAnalyzer.Analyze(controller))
                if (broken.Contains(issue.kind))
                    found.Add(issue.kind + ": " + issue.message);

            Assert.IsEmpty(found, string.Join("\n", found));
            CollectionAssert.Contains(
                ControllerAnalyzer.FindUnusedParameters(controller), "Unreferenced");
        }

        /// <summary>Copying the layer with the nested trees into a controller that shares none
        /// of its parameters: the paste has to recreate them, and the layer has to arrive
        /// whole — three levels of tree, the behaviours, the transitions.</summary>
        [Test]
        public void LayerClipboard_CarriesTheNestedTreesIntoAStrangeController()
        {
            var source = Track(new AnimatorController());
            Assert.IsEmpty(NewRecipe(source, BuildComplex).Generate());

            int gesture = IndexOfLayer(source, "Gesture");
            LayerClipboard.Copy(source, gesture);

            var destination = Track(new AnimatorController());
            destination.AddLayer("Base");
            Assert.GreaterOrEqual(LayerClipboard.Paste(destination), 0);

            int pasted = IndexOfLayer(destination, "Gesture");
            Assert.GreaterOrEqual(pasted, 0, "the layer did not arrive");

            var names = new HashSet<string>();
            foreach (var p in destination.parameters) names.Add(p.name);
            CollectionAssert.Contains(names, "Blend");
            CollectionAssert.Contains(names, "Lift");

            int trees = 0;
            foreach (var bt in destination.AllBlendTrees()) trees++;
            Assert.AreEqual(3, trees, "the nested trees did not all travel");
        }

        // ---- the post steps: gadgets and async sync ----------------------------

        /// <summary>Gadget and async-sync setups are kept in a hidden sub-asset of the
        /// .controller, so they need one that exists on disk — an in-memory controller has
        /// nowhere to put the record and the second Generate would find nothing to rebuild.
        /// </summary>
        static void WithSavedController(System.Action<AnimatorController> body)
        {
            const string path = "Assets/DaerDComplexTest.controller";
            AssetDatabase.CreateAsset(new AnimatorController(), path);
            try { body(AssetDatabase.LoadAssetAtPath<AnimatorController>(path)); }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        /// <summary>A DBT of chained gadgets — each one reading what the one before wrote —
        /// and an async-sync ring over the parameters that feed it.</summary>
        void BuildWithPostSteps(ControllerBuilder c)
        {
            var raw = c.FloatParameter("Raw", 0.25f);
            var other = c.FloatParameter("Other");
            var smoothing = c.FloatParameter("Smoothing", 0.9f);
            var smoothed = c.FloatParameter("Raw/Smoothed");
            var scaled = c.FloatParameter("Raw/Scaled");
            var mixed = c.FloatParameter("Mixed");

            var layer = c.Layer("Visible");
            layer.NewState("Only").WithAnimation(_idle).At(0, 0).Default()
                .WithWriteDefaultsSetTo(false);

            c.Gadgets("DBT")
                .Smooth(raw, smoothed, smoothing)
                .Remap(smoothed, scaled, -1f, 1f, 0f, 1f)
                .Multiply(scaled, other, mixed);

            // The ring carries the discrete parameters. Floats can ride it too, but they come
            // back 8-bit quantized for everyone but the wearer and DaerD says so — advice
            // worth keeping loud, so it is not what this test spends its assertion on. The
            // step is above VRChat's own sync cadence, below which remotes skip slots.
            // Enough of them that multiplexing is worth it: four Ints are 32 synced bits on
            // their own, and DaerD refuses to pretend a ring over three parameters saves
            // anything.
            c.IntParameter("Mode", 1);
            c.IntParameter("Outfit");
            c.IntParameter("Hue");
            c.IntParameter("Level");
            c.BoolParameter("Flag");
            c.BoolParameter("Toggle");
            c.AsyncSync("Async")
                .Targets("Mode", "Outfit", "Hue", "Level", "Flag", "Toggle")
                .Rate("Mode", 2)
                .Requestable("Flag")
                .Step(0.5f);
        }

        /// <summary>
        /// Generate hands back problems and advice in one list of strings, and an async-sync
        /// ring always has advice: what a full pass costs in latency is a property of the
        /// feature, not a fault in the setup. So the assertion is on what got built. That the
        /// advice is only ever the ring's is what the gadget-only test below is for.
        /// </summary>
        [Test]
        public void PostSteps_BuildTheirLayers()
        {
            WithSavedController(controller =>
            {
                NewRecipe(controller, BuildWithPostSteps).Generate();

                Assert.GreaterOrEqual(IndexOfLayer(controller, "Visible"), 0, "the plain layer");
                Assert.GreaterOrEqual(IndexOfLayer(controller, "DBT"), 0, "the gadget layer");

                // Each gadget's output exists as a Float the next one could read.
                var names = new HashSet<string>();
                foreach (var p in controller.parameters) names.Add(p.name);
                CollectionAssert.IsSubsetOf(
                    new[] { "Raw/Smoothed", "Raw/Scaled", "Mixed", "Smoothing" }, names);
            });
        }

        /// <summary>The same build without the ring: a chain of gadgets, each reading what the
        /// one before wrote, has nothing to warn about. Which is what lets the test above
        /// leave the ring's own advice unasserted rather than unnoticed.</summary>
        [Test]
        public void Gadgets_ChainWithoutAWordOfComplaint()
        {
            WithSavedController(controller =>
            {
                var warnings = NewRecipe(controller, c =>
                {
                    var raw = c.FloatParameter("Raw", 0.25f);
                    var other = c.FloatParameter("Other");
                    var smoothing = c.FloatParameter("Smoothing", 0.9f);
                    var smoothed = c.FloatParameter("Raw/Smoothed");
                    var scaled = c.FloatParameter("Raw/Scaled");
                    var mixed = c.FloatParameter("Mixed");
                    c.Layer("Visible").NewState("Only").WithAnimation(_idle).At(0, 0).Default()
                        .WithWriteDefaultsSetTo(false);
                    c.Gadgets("DBT")
                        .Smooth(raw, smoothed, smoothing)
                        .Remap(smoothed, scaled, -1f, 1f, 0f, 1f)
                        .Multiply(scaled, other, mixed);
                }).Generate();

                Assert.IsEmpty(warnings, string.Join("\n", warnings));
            });
        }

        /// <summary>
        /// The post steps are the part a rebuild has to be most careful with: they do not
        /// declare states the way a layer does, they regenerate whole layers from a saved
        /// record. Running Generate twice is where stacking shows up — a ring built on top of
        /// the ring already there, a gadget's tree added beside its previous self.
        /// </summary>
        [Test]
        public void PostSteps_RegenerateInPlace_RatherThanStacking()
        {
            WithSavedController(controller =>
            {
                var recipe = NewRecipe(controller, BuildWithPostSteps);
                var firstRun = recipe.Generate();
                int layers = controller.layers.Length;
                int parameters = controller.parameters.Length;
                var first = ControllerIR.Parse(controller);

                var secondRun = recipe.Generate();

                CollectionAssert.AreEqual(firstRun, secondRun,
                    "the second pass had something new to say about an unchanged setup");
                Assert.AreEqual(layers, controller.layers.Length, "a layer was added twice");
                Assert.AreEqual(parameters, controller.parameters.Length,
                    "a parameter was added twice");
                CollectionAssert.AreEquivalent(Describe(first), Describe(ControllerIR.Parse(controller)),
                    "the parameters are not the same set after a second pass");
                CollectionAssert.AreEquivalent(new[] { "Visible", "DBT", "Async" },
                    LayerNames(controller), "the layers are not the same set");

                // Only the layer the recipe declares outright is compared structurally. A post
                // step regenerates its layer from the saved record, and the AAP clips inside
                // it are fresh sub-assets each time — the IR compares a motion by identity, so
                // two clips built alike and named alike still read as different. That is what
                // Compare means when it says post steps declare nothing comparable. The two
                // assertions above are what can be said about those layers: same names, same
                // parameters, and no second copy of either.
                var declared = new HashSet<string> { "Visible" };
                var empty = new HashSet<string>();
                var drift = ControllerIRDiff.Compare(
                    first.FilterTo(declared, empty),
                    ControllerIR.Parse(controller).FilterTo(declared, empty));
                Assert.IsEmpty(drift, string.Join("\n", drift));
            });
        }

        /// <summary>
        /// What the rebuild does to the order, written down rather than left as a surprise.
        /// A layer's index decides what it overrides in an animator, so two post steps
        /// trading places across a Generate is not the same kind of nothing that a reordered
        /// parameter list is. Recorded, not endorsed: if the intent is that a regenerate
        /// leaves the controller byte-for-byte where it was, this test is the one to delete
        /// and the post steps are what to fix.
        /// </summary>
        [Test]
        public void ReGenerate_ReordersWhatThePostStepsOwn()
        {
            WithSavedController(controller =>
            {
                var recipe = NewRecipe(controller, BuildWithPostSteps);
                recipe.Generate();
                var before = LayerNames(controller);

                recipe.Generate();
                var after = LayerNames(controller);

                CollectionAssert.AreEquivalent(before, after, "a layer went missing or doubled");
                CollectionAssert.AreNotEqual(before, after,
                    "the post steps kept their order this time — if that is now guaranteed, "
                    + "this test has outlived its point and the rebuild is order-stable");
            });
        }

        static List<string> LayerNames(AnimatorController controller)
        {
            var names = new List<string>();
            foreach (var layer in controller.layers) names.Add(layer.name);
            return names;
        }

        /// <summary>Every parameter as name, type and defaults — a set, with the order the
        /// list happens to be in thrown away.</summary>
        static List<string> Describe(ControllerIR ir)
        {
            var described = new List<string>();
            foreach (var p in ir.parameters)
                described.Add($"{p.name} {p.type} {p.defaultFloat} {p.defaultInt} {p.defaultBool}");
            return described;
        }

        static int IndexOfLayer(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == name) return i;
            return -1;
        }
    }
}
