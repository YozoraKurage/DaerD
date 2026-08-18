using System;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The other gadget tests read the controller DaerD generated; these run it.
    ///
    /// Everything the DBT gadgets do is arithmetic a blend tree performs at runtime — Direct
    /// weights summing, 1D children interpolating, AAP clips writing the result back onto a
    /// parameter — and none of that is in the asset. A tree can have exactly the shape the
    /// structural tests demand and still compute the wrong number, because the shape is only
    /// the claim; Mecanim evaluating it is the fact. So each test here hangs the generated
    /// controller off an <see cref="AnimatorRig"/>, sets the inputs, steps frames, and reads
    /// the output parameter back.
    /// </summary>
    [Category("Runtime")]
    public class AapGadgetRuntimeTests
    {
        /// <summary>Frames given to a gadget whose output stops moving once its stages have
        /// filled. Well past the longest chain here (Divide's three, SeparateDigits' five).</summary>
        const int Settle = 12;

        // ---- building the controllers -------------------------------------------

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

        static void Apply(AapGadgets.Request request)
        {
            string refusal = AapGadgets.Validate(request);
            Assert.IsNull(refusal, "the gadget was refused: " + refusal);
            Assert.IsTrue(AapGadgets.Apply(request), "the gadget failed to apply");
        }

        /// <summary>A controller carrying one gadget over inputs A and B, ready to run.</summary>
        static AnimatorRig RigFor(AapGadgets.Kind kind, Action<AapGadgets.Request> configure = null)
        {
            var controller = NewController("A", "B");
            var request = NewRequest(controller, kind);
            configure?.Invoke(request);
            Apply(request);
            return new AnimatorRig(controller);
        }

        // ---- the rig itself ------------------------------------------------------

        /// <summary>
        /// Before any gadget is believed or disbelieved: an AAP clip on a Write-Defaults-ON
        /// state writes its parameter when the animator is stepped. Everything below is that
        /// one mechanism with arithmetic on top, so if this fails the rest of the file is
        /// measuring the rig, not DaerD.
        /// </summary>
        [Test]
        public void Rig_RunsTheAnimatorAndAnAapClipWrites()
        {
            var controller = NewController();
            controller.AddParameter("Out", AnimatorControllerParameterType.Float);
            var layers = controller.layers;
            layers[0].defaultWeight = 1f;
            controller.layers = layers;

            var state = controller.layers[0].stateMachine.AddState("Write");
            state.writeDefaultValues = true;
            state.motion = DbtBuilder.ParameterClip(controller, "Out", 0.5f);

            using (var rig = new AnimatorRig(controller))
            {
                Assert.AreEqual(0f, rig.Get("Out"), 1e-6f, "the parameter should start at its default");
                rig.Step();
                Assert.AreEqual(0.5f, rig.Get("Out"), 1e-5f, "one frame should have written the clip's value");
            }
        }

        /// <summary>The constant the Direct trees weigh their children by has to actually be 1
        /// on a running animator, or every sum below is scaled by whatever it really is.</summary>
        [Test]
        public void Rig_TheConstantOneParameterIsOne()
        {
            using (var rig = RigFor(AapGadgets.Kind.Add))
                Assert.AreEqual(1f, rig.Get("One"), 1e-6f);
        }

        // ---- arithmetic ----------------------------------------------------------

        [TestCase(0.25f, 0.5f, 0.75f)]
        [TestCase(0f, 0f, 0f)]
        [TestCase(1f, 1f, 2f)]
        [TestCase(0.125f, 0.875f, 1f)]
        public void Add_SumsItsInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Add))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        [TestCase(0.75f, 0.25f, 0.5f)]
        [TestCase(1f, 1f, 0f)]
        [TestCase(0.25f, 0.75f, -0.5f)]
        public void Sub_SubtractsItsInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Sub))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        /// <summary>What the plain Add cannot do: Direct weights stop at zero, so a negative
        /// input would simply be dropped. The ranged version remaps both inputs through 1D trees
        /// first, and the claim is that the sum comes out signed and exact.</summary>
        [TestCase(-0.5f, 0.25f, -0.25f)]
        [TestCase(-0.5f, -0.25f, -0.75f)]
        [TestCase(0.5f, 0.5f, 1f)]
        public void AddRanged_SumsSignedInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.AddRanged,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        [TestCase(-0.25f, 0.5f, -0.75f)]
        [TestCase(0.5f, -0.25f, 0.75f)]
        [TestCase(-0.5f, -0.5f, 0f)]
        public void SubRanged_SubtractsSignedInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.SubRanged,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        /// <summary>Nested Direct trees are supposed to multiply their weights on the way down.
        /// Structurally that is one tree inside another; numerically it is A × B or it is not.</summary>
        [TestCase(0.5f, 0.5f, 0.25f)]
        [TestCase(0.8f, 0.25f, 0.2f)]
        [TestCase(1f, 0f, 0f)]
        [TestCase(2f, 3f, 6f)]
        public void Multiply_MultipliesItsInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Multiply))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        /// <summary>
        /// What "positive inputs only" actually does, which is worth knowing exactly: a Direct
        /// weight below zero is clamped to zero, so a negative input is not multiplied by — it
        /// is dropped, and the product silently reads 0 rather than reading wrong.
        ///
        /// The same clamp is what a signed multiply could be built out of: weighing a child by A
        /// yields max(A, 0), so weighing one by A and another by a negated copy of A splits the
        /// input into its two halves at no cost beyond the copy.
        /// </summary>
        [TestCase(-0.5f, 0.5f, 0f)]
        [TestCase(0.5f, -0.5f, 0f)]
        [TestCase(-0.5f, -0.5f, 0f)]
        public void Multiply_DropsANegativeInputInsteadOfMultiplyingIt(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Multiply))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        /// <summary>The same clamp, on the gadget whose weights are the summands.</summary>
        [Test]
        public void Add_DropsANegativeInputInsteadOfSubtractingIt()
        {
            using (var rig = RigFor(AapGadgets.Kind.Add))
                Assert.AreEqual(0.5f, rig.Evaluate("Out", Settle, ("A", -0.5f), ("B", 0.5f)), 1e-4f);
        }

        /// <summary>The signed multiply, over all four sign combinations and both zeroes. Built
        /// out of the same clamp that stops the plain one: weighing by A gives A's positive half
        /// and weighing by a negated copy gives its negative half, so the four half-products
        /// reassemble the signed one.</summary>
        [TestCase(0.5f, 0.5f, 0.25f)]
        [TestCase(-0.5f, 0.5f, -0.25f)]
        [TestCase(0.5f, -0.5f, -0.25f)]
        [TestCase(-0.5f, -0.5f, 0.25f)]
        [TestCase(0.8f, 0.25f, 0.2f)]
        [TestCase(-0.8f, 0.25f, -0.2f)]
        [TestCase(1f, -1f, -1f)]
        [TestCase(0f, -0.7f, 0f)]
        [TestCase(-0.7f, 0f, 0f)]
        public void MultiplySigned_MultipliesWhateverTheSigns(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        /// <summary>The range bounds the inputs, not the product: the clips only ever hold ±1 and
        /// the weights carry the magnitude, so two inputs at the end of the range multiply out
        /// past it instead of clipping to it.</summary>
        [Test]
        public void MultiplySigned_LetsTheProductLeaveTheInputRange()
        {
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -8f; r.rangeMax = 8f; }))
            {
                Assert.AreEqual(64f, rig.Evaluate("Out", Settle, ("A", 8f), ("B", 8f)), 1e-2f);
                Assert.AreEqual(-48f, rig.Evaluate("Out", Settle, ("A", -8f), ("B", 6f)), 1e-2f);
            }
        }

        /// <summary>An asymmetric range still negates exactly, because the gadget works in the
        /// symmetric span that covers it rather than in the range as given — which is the one
        /// thing the ranged add and subtract cannot claim.</summary>
        [Test]
        public void MultiplySigned_IsExactOnAnAsymmetricRange()
        {
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -0.25f; r.rangeMax = 2f; }))
            {
                Assert.AreEqual(-0.5f, rig.Evaluate("Out", Settle, ("A", -0.25f), ("B", 2f)), 1e-3f);
                Assert.AreEqual(1f, rig.Evaluate("Out", Settle, ("A", 0.5f), ("B", 2f)), 1e-3f);
            }
        }

        [TestCase(1f, 4f, 0.25f)]
        [TestCase(-1f, 4f, -0.25f)]
        [TestCase(1f, -4f, -0.25f)]
        [TestCase(-1f, -4f, 0.25f)]
        [TestCase(3f, 1.5f, 2f)]
        [TestCase(-3f, -1.5f, 2f)]
        [TestCase(0f, -2f, 0f)]
        public void DivideSigned_DividesWhateverTheSigns(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.DivideSigned,
                r => { r.rangeMin = -8f; r.rangeMax = 8f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)),
                    Mathf.Max(Mathf.Abs(expected) * 4e-3f, 2e-3f));
        }

        /// <summary>A divisor with no sign left to read. The answer is 0 — chosen, not
        /// stumbled into: the two sign indicators cross inside the dead zone, and the quotient
        /// fades through it rather than slamming between ±240.</summary>
        [Test]
        public void DivideSigned_FadesToZeroWhereTheDivisorHasNoSign()
        {
            using (var rig = RigFor(AapGadgets.Kind.DivideSigned,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
                Assert.AreEqual(0f, rig.Evaluate("Out", Settle, ("A", 0.5f), ("B", 0f)), 1e-3f);
        }

        [TestCase(0f, 10f)]
        [TestCase(0.25f, 12.5f)]
        [TestCase(1f, 20f)]
        public void Remap_MapsTheInputRangeOntoTheOutputRange(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Remap,
                r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 10f; r.rangeMax = 20f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-3f);
        }

        [Test]
        public void Remap_ReversedOutputRangeInvertsTheSlope()
        {
            using (var rig = RigFor(AapGadgets.Kind.Remap,
                r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 20f; r.rangeMax = 10f; }))
                Assert.AreEqual(17.5f, rig.Evaluate("Out", Settle, ("A", 0.25f)), 1e-3f);
        }

        /// <summary>A 1D tree holds its outermost child past the end, which is the clamp every
        /// gadget's documentation leans on.</summary>
        [Test]
        public void Remap_ClampsOutsideTheInputRange()
        {
            using (var rig = RigFor(AapGadgets.Kind.Remap,
                r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 10f; r.rangeMax = 20f; }))
            {
                Assert.AreEqual(20f, rig.Evaluate("Out", Settle, ("A", 5f)), 1e-3f, "above the range");
                Assert.AreEqual(10f, rig.Evaluate("Out", Settle, ("A", -5f)), 1e-3f, "below the range");
            }
        }

        // ---- logic ---------------------------------------------------------------

        [TestCase(0f, 0f, 0f)]
        [TestCase(1f, 0f, 0f)]
        [TestCase(0f, 1f, 0f)]
        [TestCase(1f, 1f, 1f)]
        public void And_IsTheProductOfItsInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.And))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        [TestCase(0f, 0f, 0f)]
        [TestCase(1f, 0f, 1f)]
        [TestCase(0f, 1f, 1f)]
        [TestCase(1f, 1f, 1f)]
        public void Or_IsOneWhenEitherInputIs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Or))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), 1e-4f);
        }

        [TestCase(0f, 1f)]
        [TestCase(1f, 0f)]
        [TestCase(0.25f, 0.75f)]
        public void Not_InvertsItsInput(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Not))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-4f);
        }

        /// <summary>The source puts a one-hundredth-wide ramp just below the threshold; that is
        /// the shape. What matters at runtime is that a value at or above the threshold reads 1
        /// and one clearly below it reads 0.</summary>
        [TestCase(0f, 0f)]
        [TestCase(0.4f, 0f)]
        [TestCase(0.49f, 0f)]
        [TestCase(0.5f, 1f)]
        [TestCase(0.9f, 1f)]
        public void FloatAsBool_SwitchesAtTheThreshold(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.FloatAsBool, r => r.threshold = 0.5f))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-4f);
        }

        // ---- reciprocal and division ---------------------------------------------

        /// <summary>The exact half: a normalized Direct tree dividing by (input - 1) + 1.</summary>
        [TestCase(1f, 1f)]
        [TestCase(2f, 0.5f)]
        [TestCase(4f, 0.25f)]
        [TestCase(10f, 0.1f)]
        public void Reciprocal_AboveOne(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Reciprocal))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-3f);
        }

        /// <summary>The interpolated half: a geometric ladder of (1/u - 1), which the source puts
        /// within about 8e-4 relative of the true value on every rung.</summary>
        [TestCase(0.5f, 2f)]
        [TestCase(0.25f, 4f)]
        [TestCase(0.1f, 10f)]
        public void Reciprocal_BelowOne(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Reciprocal))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), expected * 2e-3f);
        }

        [TestCase(1f, 4f, 0.25f)]
        [TestCase(3f, 1.5f, 2f)]
        [TestCase(1f, 0.5f, 2f)]
        public void Divide_DividesItsInputs(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Divide))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)), expected * 3e-3f);
        }

        /// <summary>
        /// The ranged reciprocal, past where the plain one gives up. Saying the divisor stays in
        /// 0.001…1 lets it lift the divisor into the half the exact core covers, so there is no
        /// ladder in the answer — and therefore no 240.
        /// </summary>
        [TestCase(1f, 1f)]
        [TestCase(0.5f, 2f)]
        [TestCase(0.1f, 10f)]
        [TestCase(0.01f, 100f)]
        [TestCase(0.002f, 500f)]
        [TestCase(0.001f, 1000f)]
        public void ReciprocalRanged_HasNoCeilingInsideItsWindow(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.ReciprocalRanged,
                r => { r.inMin = 0.001f; r.inMax = 1f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), expected * 2e-3f);
        }

        /// <summary>Side by side at a divisor the ladder cannot reach: the plain gadget is held
        /// at its cap and the ranged one is still dividing.</summary>
        [Test]
        public void ReciprocalRanged_GoesWhereThePlainOneStops()
        {
            using (var plain = RigFor(AapGadgets.Kind.Reciprocal))
            using (var ranged = RigFor(AapGadgets.Kind.ReciprocalRanged,
                r => { r.inMin = 0.001f; r.inMax = 1f; }))
            {
                Assert.AreEqual(240f, plain.Evaluate("Out", Settle, ("A", 0.001f)), 1f);
                Assert.AreEqual(1000f, ranged.Evaluate("Out", Settle, ("A", 0.001f)), 2f);
            }
        }

        /// <summary>No table means no sampling error either: on the ladder the answer is good to
        /// about 8e-4 relative, and here it is good to the float.</summary>
        [Test]
        public void ReciprocalRanged_IsMoreExactThanTheLadder()
        {
            using (var plain = RigFor(AapGadgets.Kind.Reciprocal))
            using (var ranged = RigFor(AapGadgets.Kind.ReciprocalRanged,
                r => { r.inMin = 0.01f; r.inMax = 1f; }))
            {
                // A value deliberately between two rungs of the ladder.
                const float x = 0.037f;
                float exact = 1f / x;
                float laddered = plain.Evaluate("Out", Settle, ("A", x));
                float lifted = ranged.Evaluate("Out", Settle, ("A", x));

                Assert.AreEqual(exact, lifted, exact * 1e-5f, "the lifted answer is the float's");
                Assert.Greater(Mathf.Abs(laddered - exact), Mathf.Abs(lifted - exact),
                    "and it beats the ladder, which is sampled");
            }
        }

        /// <summary>Outside the window the divisor clamps, so the answer is the reciprocal of
        /// the clamped divisor rather than nonsense — 1/min below, 1/max above.</summary>
        [Test]
        public void ReciprocalRanged_ClampsToTheEndsOfItsWindow()
        {
            using (var rig = RigFor(AapGadgets.Kind.ReciprocalRanged,
                r => { r.inMin = 0.01f; r.inMax = 2f; }))
            {
                Assert.AreEqual(100f, rig.Evaluate("Out", Settle, ("A", 1e-6f)), 0.5f, "below the window");
                Assert.AreEqual(0.5f, rig.Evaluate("Out", Settle, ("A", 50f)), 1e-3f, "above it");
            }
        }

        [TestCase(1f, 0.001f, 1000f)]
        [TestCase(3f, 0.01f, 300f)]
        [TestCase(2f, 4f, 0.5f)]
        public void DivideRanged_DividesWithoutTheCeiling(float a, float b, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.DivideRanged,
                r => { r.inMin = 0.001f; r.inMax = 8f; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a), ("B", b)),
                    expected * 3e-3f);
        }

        /// <summary>A divisor window that starts at or below zero is refused: the lift divides
        /// by that end, and a window straddling zero has a divisor of zero inside it.</summary>
        [Test]
        public void ReciprocalRanged_RefusesAWindowThatTouchesZero()
        {
            var controller = NewController("A", "B");
            var request = NewRequest(controller, AapGadgets.Kind.ReciprocalRanged);
            request.inMin = 0f;
            request.inMax = 1f;
            Assert.IsNotNull(AapGadgets.Validate(request), "a window starting at zero");

            request.inMin = -1f;
            Assert.IsNotNull(AapGadgets.Validate(request), "and one straddling it");

            request.inMin = 0.5f;
            request.inMax = 0.5f;
            Assert.IsNotNull(AapGadgets.Validate(request), "and one with no width");
        }

        // ---- smoothing (the two that never settle) --------------------------------

        /// <summary>
        /// output = lerp(source, output, smoothing), once per frame. With smoothing at 0.5 and
        /// the source at 1 that is 1 - 0.5^n after n frames — the curve is the whole point of
        /// the gadget, and the only way to see it is to count frames.
        /// </summary>
        [Test]
        public void Smooth_ApproachesTheSourceGeometrically()
        {
            using (var rig = RigFor(AapGadgets.Kind.Smooth,
                r => { r.smoothing = "Smoothing"; r.smoothingDefault = 0.5f; }))
            {
                rig.Set("A", 1f).Set("Smoothing", 0.5f);
                for (int n = 1; n <= 6; n++)
                {
                    rig.Step();
                    Assert.AreEqual(1f - Mathf.Pow(0.5f, n), rig.Get("Out"), 2e-3f,
                        "after " + n + " frame(s)");
                }
            }
        }

        /// <summary>Smoothing 0 is the documented "follow instantly" end of the range.</summary>
        [Test]
        public void Smooth_AtZeroFollowsTheSourceInOneFrame()
        {
            using (var rig = RigFor(AapGadgets.Kind.Smooth,
                r => { r.smoothing = "Smoothing"; r.smoothingDefault = 0.5f; }))
            {
                rig.Set("A", 0.75f).Set("Smoothing", 0f).Step();
                Assert.AreEqual(0.75f, rig.Get("Out"), 1e-3f);
            }
        }

        /// <summary>Constant speed instead of a decaying one: the output should walk toward the
        /// input by about the step size each frame and — the reason for the ramp near zero —
        /// stop on the target instead of oscillating around it. The target sits in the middle of
        /// the range on purpose: at the range's edge the identity remap's clamp would hold the
        /// output still and the ramp would never be asked to do anything.</summary>
        [Test]
        public void SmoothLinear_WalksToTheInputAtAConstantSpeed()
        {
            using (var rig = RigFor(AapGadgets.Kind.SmoothLinear,
                r => { r.smoothing = "Step"; r.smoothingDefault = 0.05f; r.rangeMin = -1f; r.rangeMax = 1f; }))
            {
                rig.Set("A", 0.5f).Set("Step", 0.05f);

                rig.Step(4);
                float early = rig.Get("Out");
                Assert.Greater(early, 0.02f, "it should have started moving");
                Assert.Less(early, 0.2f, "it should not have jumped to the target");

                rig.Step(60);
                Assert.AreEqual(0.5f, rig.Get("Out"), 5e-3f, "it should have arrived");

                float settled = rig.Get("Out");
                rig.Step(60);
                Assert.AreEqual(settled, rig.Get("Out"), 1e-3f, "and stayed there, not oscillated");
            }
        }

        /// <summary>
        /// Where the constant-speed smoothing stops settling, which is a fact about the step
        /// size and not about the input.
        ///
        /// The ramp is meant to damp the last stretch: inside ±0.1 of the target the step shrinks
        /// in proportion to the distance left. But the distance it shrinks by is a parameter the
        /// same tree wrote last frame, so the correction is always one frame stale, and with an
        /// error e the loop is e(n+1) = e(n) - (step / 0.1)·e(n-1). Below the ramp width that
        /// settles; *at* it the gain is exactly 1 and the recurrence is a rotation — undamped,
        /// forever.
        ///
        /// The wizard's default step is 0.05, so what ships is on the settling side. This test is
        /// here to keep the boundary visible, because nothing in the tree's shape shows it, and a
        /// step driven by a FrameTime gadget is a step whose size the author computed rather than
        /// typed. If the damping is ever fixed, the second half of this test is what will say so.
        /// </summary>
        [Test]
        public void SmoothLinear_SettlesBelowTheRampWidthButSwingsAtIt()
        {
            float Swing(float step)
            {
                using (var rig = RigFor(AapGadgets.Kind.SmoothLinear,
                    r => { r.smoothing = "Step"; r.smoothingDefault = step; r.rangeMin = -1f; r.rangeMax = 1f; }))
                {
                    rig.Set("A", 0.5f).Set("Step", step).Step(200);
                    float low = float.MaxValue, high = float.MinValue;
                    for (int i = 0; i < 40; i++)
                    {
                        rig.Step();
                        low = Mathf.Min(low, rig.Get("Out"));
                        high = Mathf.Max(high, rig.Get("Out"));
                    }
                    return high - low;
                }
            }

            Assert.Less(Swing(0.05f), 1e-3f, "the wizard's default step settles on the target");
            Assert.Greater(Swing(0.1f), 0.1f,
                "a step as wide as the ramp never settles — it swings by about a step, forever");
        }

        // ---- lookup tables --------------------------------------------------------

        [Test]
        public void Lut1D_InterpolatesTheBakedCurve()
        {
            var curve = AnimationCurve.Linear(0f, 0f, 1f, 4f);
            using (var rig = RigFor(AapGadgets.Kind.Lut1D, r => { r.curve = curve; r.lutSamples = 5; }))
            {
                Assert.AreEqual(0f, rig.Evaluate("Out", Settle, ("A", 0f)), 1e-3f, "at the first sample");
                Assert.AreEqual(2f, rig.Evaluate("Out", Settle, ("A", 0.5f)), 1e-3f, "on a sample");
                Assert.AreEqual(1.5f, rig.Evaluate("Out", Settle, ("A", 0.375f)), 1e-3f, "between two samples");
                Assert.AreEqual(4f, rig.Evaluate("Out", Settle, ("A", 1f)), 1e-3f, "at the last sample");
                Assert.AreEqual(4f, rig.Evaluate("Out", Settle, ("A", 2f)), 1e-3f, "clamped past the end");
            }
        }

        /// <summary>The input is in turns, so 0.25 is a quarter of the circle. Sixty-four samples
        /// per period puts one exactly on every sixteenth of a turn, so these read the table
        /// rather than an interpolation of it.</summary>
        [TestCase(0f, 0f)]
        [TestCase(0.25f, 1f)]
        [TestCase(0.5f, 0f)]
        [TestCase(0.75f, -1f)]
        [TestCase(0.125f, 0.70710678f)]
        public void Sine_ReadsTheInputAsTurns(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Sine))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-4f);
        }

        /// <summary>Halfway between two samples is where a 64-sample table is least accurate; the
        /// source puts that error under 1.2e-3.</summary>
        [Test]
        public void Sine_BetweenSamplesStaysWithinTheTablesError()
        {
            using (var rig = RigFor(AapGadgets.Kind.Sine))
            {
                const float turn = 1f / 128f;
                Assert.AreEqual(Mathf.Sin(2f * Mathf.PI * turn),
                    rig.Evaluate("Out", Settle, ("A", turn)), 1.2e-3f);
            }
        }

        [TestCase(0f, 1f)]
        [TestCase(0.25f, 0f)]
        [TestCase(0.5f, -1f)]
        public void Cosine_ReadsTheInputAsTurns(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Cosine))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-4f);
        }

        [TestCase(0f, 0f)]
        [TestCase(0.125f, 1f)]
        [TestCase(0.375f, -1f)]
        public void Tangent_ReadsTheInputAsTurns(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Tangent))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 1e-3f);
        }

        /// <summary>Where tan runs away the table is pinned to its limit rather than to whatever
        /// the float rounded to.</summary>
        [Test]
        public void Tangent_IsPinnedAtItsPoles()
        {
            using (var rig = RigFor(AapGadgets.Kind.Tangent))
            {
                Assert.AreEqual(100f, rig.Evaluate("Out", Settle, ("A", 0.25f)), 1e-2f);
                Assert.AreEqual(100f, rig.Evaluate("Out", Settle, ("A", 0.75f)), 1e-2f);
            }
        }

        // ---- functions of one input ------------------------------------------------

        /// <summary>√x, sampled where a square root needs sampling. One frame, against the
        /// thirty an iteration takes to reach the same answer.</summary>
        [TestCase(0.25f, 0.5f)]
        [TestCase(1f, 1f)]
        [TestCase(2f, 1.41421356f)]
        [TestCase(3f, 1.73205081f)]
        [TestCase(4f, 2f)]
        [TestCase(0.0625f, 0.25f)]
        public void Sqrt_TakesASquareRoot(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Sqrt,
                r => { r.inMin = 0.01f; r.inMax = 16f; r.lutSamples = 65; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), expected * 2e-3f);
        }

        /// <summary>
        /// Why the samples are geometric. An evenly spaced table of the same size spends its
        /// samples where √ is nearly straight and none where it turns, and is out by a
        /// percent or more at the bottom of the window; the geometric one holds its relative
        /// error flat across the whole span, which is the same reasoning the reciprocal's
        /// ladder is built on.
        /// </summary>
        [Test]
        public void Sqrt_HoldsItsAccuracyAtTheBottomOfTheWindow()
        {
            using (var rig = RigFor(AapGadgets.Kind.Sqrt,
                r => { r.inMin = 0.01f; r.inMax = 16f; r.lutSamples = 65; }))
            {
                // Deliberately between samples, at both ends of a 1600:1 window.
                Assert.AreEqual(Mathf.Sqrt(0.013f), rig.Evaluate("Out", Settle, ("A", 0.013f)),
                    Mathf.Sqrt(0.013f) * 2e-3f, "near the floor");
                Assert.AreEqual(Mathf.Sqrt(11.3f), rig.Evaluate("Out", Settle, ("A", 11.3f)),
                    Mathf.Sqrt(11.3f) * 2e-3f, "near the ceiling");
            }
        }

        [TestCase(0.25f, 2f)]
        [TestCase(1f, 1f)]
        [TestCase(4f, 0.5f)]
        [TestCase(2f, 0.70710678f)]
        public void InverseSqrt_TakesAnInverseSquareRoot(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.InverseSqrt,
                r => { r.inMin = 0.05f; r.inMax = 16f; r.lutSamples = 65; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), expected * 2e-3f);
        }

        [TestCase(1f, 0f)]
        [TestCase(2f, 1f)]
        [TestCase(8f, 3f)]
        [TestCase(0.5f, -1f)]
        [TestCase(0.125f, -3f)]
        [TestCase(3f, 1.5849625f)]
        public void Log2_TakesALogarithm(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Log2,
                r => { r.inMin = 0.01f; r.inMax = 100f; r.lutSamples = 65; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), 5e-3f);
        }

        [TestCase(0f, 1f)]
        [TestCase(1f, 2f)]
        [TestCase(3f, 8f)]
        [TestCase(-2f, 0.25f)]
        [TestCase(0.5f, 1.41421356f)]
        public void Exp2_RaisesTwoToTheInput(float a, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Exp2,
                r => { r.inMin = -8f; r.inMax = 8f; r.lutSamples = 65; }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", a)), expected * 3e-3f);
        }

        /// <summary>The pair undoes itself, which is the property everything built on them
        /// leans on.</summary>
        [Test]
        public void Log2AndExp2_AreInverses()
        {
            var controller = NewController("A", "B");
            var log = NewRequest(controller, AapGadgets.Kind.Log2);
            log.output = "L";
            log.inMin = 0.05f;
            log.inMax = 20f;
            log.lutSamples = 65;
            Apply(log);

            var exp = NewRequest(controller, AapGadgets.Kind.Exp2);
            exp.inputA = "L";
            exp.output = "Back";
            exp.inMin = Mathf.Log(0.05f, 2f);
            exp.inMax = Mathf.Log(20f, 2f);
            exp.lutSamples = 65;
            exp.layerIndex = 1;
            Apply(exp);

            using (var rig = new AnimatorRig(controller))
                foreach (float x in new[] { 0.1f, 0.5f, 1f, 3f, 7f, 19f })
                    Assert.AreEqual(x, rig.Evaluate("Back", Settle, ("A", x)), x * 8e-3f,
                        "exp2(log2(" + x + "))");
        }

        /// <summary>
        /// A power with both the base and the exponent as parameters, which is the one a table
        /// cannot hold: a function of one input is a curve, and this is a surface.
        /// </summary>
        [TestCase(2f, 3f, 8f)]
        [TestCase(2f, 0.5f, 1.41421356f)]
        [TestCase(9f, 0.5f, 3f)]
        [TestCase(4f, -1f, 0.25f)]
        [TestCase(3f, 2f, 9f)]
        [TestCase(1.5f, 4f, 5.0625f)]
        [TestCase(5f, 0f, 1f)]
        public void Power_RaisesOneParameterToAnother(float b, float e, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Power,
                r =>
                {
                    r.inMin = 0.1f; r.inMax = 16f;      // the base's window
                    r.rangeMin = -4f; r.rangeMax = 4f;  // the exponent's range
                    r.lutSamples = 97;
                }))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", b), ("B", e)),
                    expected * 1.5e-2f);
        }

        /// <summary>The windows have to start above zero — log₂ is not defined below it, and the
        /// geometric sampling needs a ratio across the window rather than a difference.</summary>
        [Test]
        public void FunctionTables_RefuseAWindowThatTouchesZero()
        {
            foreach (var kind in new[]
                { AapGadgets.Kind.Sqrt, AapGadgets.Kind.InverseSqrt, AapGadgets.Kind.Log2,
                  AapGadgets.Kind.Power })
            {
                var request = NewRequest(NewController("A", "B"), kind);
                request.inMin = 0f;
                request.inMax = 4f;
                Assert.IsNotNull(AapGadgets.Validate(request), kind + " with a window at zero");
            }

            // Exp2 is defined everywhere and is sampled evenly, so it takes any window.
            var exp = NewRequest(NewController("A", "B"), AapGadgets.Kind.Exp2);
            exp.inMin = -8f;
            exp.inMax = 8f;
            Assert.IsNull(AapGadgets.Validate(exp), "exp2 over a window straddling zero");
        }

        /// <summary>Input A is the numerator (y) and B the denominator (x), and the result is in
        /// turns counter-clockwise from +X. These are directions the ring samples exactly.</summary>
        [TestCase(1f, 0f, 0.25f)]      // +Y
        [TestCase(0f, -1f, 0.5f)]      // -X
        [TestCase(-1f, 0f, 0.75f)]     // -Y
        [TestCase(0.70710678f, 0.70710678f, 0.125f)]
        public void Atan2_ReadsTheDirectionAsTurns(float y, float x, float expected)
        {
            using (var rig = RigFor(AapGadgets.Kind.Atan2, r => r.atan2Directions = 16))
                Assert.AreEqual(expected, rig.Evaluate("Out", Settle, ("A", y), ("B", x)), 5e-3f);
        }

        /// <summary>The result is an angle, so a vector past the ring must read the same as one
        /// on it.</summary>
        [Test]
        public void Atan2_IgnoresTheVectorsMagnitudeOutsideTheRing()
        {
            using (var rig = RigFor(AapGadgets.Kind.Atan2, r => r.atan2Directions = 16))
            {
                Assert.AreEqual(0.25f, rig.Evaluate("Out", Settle, ("A", 1f), ("B", 0f)), 5e-3f, "on the ring");
                Assert.AreEqual(0.25f, rig.Evaluate("Out", Settle, ("A", 5f), ("B", 0f)), 5e-3f, "well past it");
            }
        }

        /// <summary>The caveat the source states, pinned down so it stays true: a
        /// direction-blended tree has no direction to read at the origin, so a short vector pulls
        /// the answer toward the origin child's 0. Callers are told to gate the result by the
        /// vector's magnitude, and this is the reason.</summary>
        [Test]
        public void Atan2_CollapsesTowardZeroInsideTheRing()
        {
            using (var rig = RigFor(AapGadgets.Kind.Atan2, r => r.atan2Directions = 16))
                Assert.Less(rig.Evaluate("Out", Settle, ("A", 0.02f), ("B", 0f)), 0.2f,
                    "a near-zero vector should not read as a trustworthy quarter turn");
        }

        /// <summary>The seam: 0 and 1 are the same direction but not the same number, and the
        /// source pins the jump between them inside a narrow wedge around +X. Just outside that
        /// wedge the reading should already be an angle again, not a blend of both ends.</summary>
        [Test]
        public void Atan2_KeepsTheSeamInsideItsWedge()
        {
            using (var rig = RigFor(AapGadgets.Kind.Atan2, r => r.atan2Directions = 16))
            {
                float above = 2f * Mathf.PI * 0.02f;
                Assert.Less(rig.Evaluate("Out", Settle, ("A", Mathf.Sin(above)), ("B", Mathf.Cos(above))), 0.1f,
                    "just counter-clockwise of +X");

                float below = 2f * Mathf.PI * 0.98f;
                Assert.Greater(rig.Evaluate("Out", Settle, ("A", Mathf.Sin(below)), ("B", Mathf.Cos(below))), 0.9f,
                    "just clockwise of +X");
            }
        }

        // ---- the frame-counting gadgets -------------------------------------------

        /// <summary>
        /// The buffer exists to line two branches of different depth up on the same frame of the
        /// input, so its whole contract is a frame count. A structural test can see the chain of
        /// remaps; only a running animator can say whether the delay is the three frames that
        /// were asked for.
        /// </summary>
        [Test]
        public void Buffer_DelaysByExactlyTheFramesAskedFor()
        {
            using (var rig = RigFor(AapGadgets.Kind.Buffer,
                r => { r.bufferFrames = 3; r.rangeMin = -1f; r.rangeMax = 1f; }))
            {
                rig.Set("A", 0f).Step(Settle);
                Assert.AreEqual(0f, rig.Get("Out"), 1e-4f, "the buffer should start empty");

                rig.Set("A", 0.8f);
                rig.Step();
                Assert.AreEqual(0f, rig.Get("Out"), 1e-4f, "after 1 of 3 frames");
                rig.Step();
                Assert.AreEqual(0f, rig.Get("Out"), 1e-4f, "after 2 of 3 frames");
                rig.Step();
                Assert.AreEqual(0.8f, rig.Get("Out"), 1e-4f, "after 3 of 3 frames");
            }
        }

        [Test]
        public void Buffer_OfOneFrameIsOneHopBehind()
        {
            using (var rig = RigFor(AapGadgets.Kind.Buffer,
                r => { r.bufferFrames = 1; r.rangeMin = -1f; r.rangeMax = 1f; }))
            {
                rig.Set("A", 0f).Step(Settle);
                rig.Set("A", -0.5f).Step();
                Assert.AreEqual(-0.5f, rig.Get("Out"), 1e-4f);
            }
        }

        /// <summary>
        /// The one reading a blend tree cannot take from a parameter: how long the last frame
        /// was. A clip played against the wall clock on a layer of its own, minus the previous
        /// frame's reading of it.
        /// </summary>
        [Test]
        public void FrameTime_ReportsTheLengthOfTheLastFrame()
        {
            var controller = NewController("A", "B");
            var request = NewRequest(controller, AapGadgets.Kind.FrameTime);
            request.inputA = null;
            request.inputB = null;
            request.output = "Delta";
            Apply(request);

            using (var rig = new AnimatorRig(controller))
            {
                rig.Step(4);
                Assert.AreEqual(AnimatorRig.Dt, rig.Get("Delta"), 1e-4f);
                rig.Step();
                Assert.AreEqual(AnimatorRig.Dt, rig.Get("Delta"), 1e-4f, "and it should keep reporting it");
            }
        }

        // ---- digits ---------------------------------------------------------------

        /// <summary>
        /// The subnormal-float quantizer. Its constants are the algorithm and none of it is
        /// visible in the tree's shape, so this is the gadget a structural test can say the least
        /// about: either Mecanim's arithmetic snaps to the multiples of the smallest float there
        /// is, or the digits come out as mush.
        /// </summary>
        [TestCase(0.123f, 0.1f, 0.02f, 0.003f)]
        [TestCase(0.456f, 0.4f, 0.05f, 0.006f)]
        [TestCase(0.999f, 0.9f, 0.09f, 0.009f)]
        [TestCase(0f, 0f, 0f, 0f)]
        public void SeparateDigits_SplitsTheFirstThreeDecimals(float a,
            float tenths, float hundredths, float thousandths)
        {
            using (var rig = RigFor(AapGadgets.Kind.SeparateDigits))
            {
                rig.Set("A", a).Step(Settle * 2);
                Assert.AreEqual(tenths, rig.Get("Out/Tenths"), 5e-4f, "tenths");
                Assert.AreEqual(hundredths, rig.Get("Out/Hundredths"), 5e-4f, "hundredths");
                Assert.AreEqual(thousandths, rig.Get("Out/Thousandths"), 5e-4f, "thousandths");
            }
        }

        // ---- gadgets together ------------------------------------------------------

        /// <summary>
        /// Gadgets are meant to be stacked in one layer and chained through their outputs, which
        /// is a claim about a whole layer rather than about any one tree: the Direct root sums
        /// its children, so a gadget writing a parameter the next one reads must not be disturbed
        /// by its neighbours.
        /// </summary>
        [Test]
        public void Gadgets_ChainThroughTheirOutputsInOneLayer()
        {
            var controller = NewController("A", "B", "C");

            var sum = NewRequest(controller, AapGadgets.Kind.Add);
            sum.output = "Sum";
            Apply(sum);

            var product = NewRequest(controller, AapGadgets.Kind.Multiply);
            product.inputA = "Sum";
            product.inputB = "C";
            product.output = "Product";
            // Into the layer the first gadget created, not a new one.
            product.layerIndex = 1;
            Apply(product);

            Assert.AreEqual(2, controller.layers.Length, "both gadgets should share one layer");

            using (var rig = new AnimatorRig(controller))
            {
                float value = rig.Evaluate("Product", Settle, ("A", 0.25f), ("B", 0.75f), ("C", 2f));
                Assert.AreEqual(1f, rig.Get("Sum"), 1e-4f, "A + B");
                Assert.AreEqual(2f, value, 1e-4f, "(A + B) × C");
            }
        }

        /// <summary>An output that stopped being recomputed would decay to its default the moment
        /// Write Defaults touched it, so the layer holding still is worth one assertion.</summary>
        [Test]
        public void Gadget_HoldsItsOutputWhileTheInputsStandStill()
        {
            using (var rig = RigFor(AapGadgets.Kind.Add))
            {
                rig.Set("A", 0.3f).Set("B", 0.4f).Step(Settle);
                float settled = rig.Get("Out");
                rig.Step(120);
                Assert.AreEqual(settled, rig.Get("Out"), 1e-5f, "after two more seconds of frames");
            }
        }
    }
}
