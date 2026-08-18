using System;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The gadgets away from the values they were written against.
    ///
    /// Every other test in this repository picks its numbers by hand, and a hand-picked number
    /// is one the author already believed would work. Two things are asked here instead. First,
    /// whether the arithmetic holds over a whole range rather than at a few points — swept with
    /// a fixed seed, so a failure is reproducible and a pass is not luck. Second, what happens
    /// at the ends: values far outside the declared range, magnitudes the float itself starts to
    /// lose, and the specific places each gadget's construction is known to give out.
    ///
    /// Several of the tests below pin down limits rather than successes. That is the point —
    /// a limit nobody has written down is indistinguishable from a bug when someone finally
    /// walks into it.
    /// </summary>
    [Category("Runtime")]
    public class AapGadgetExtremesTests
    {
        /// <summary>Frames given before reading: past the deepest gadget here (the signed
        /// divide's four) with room to spare.</summary>
        const int Settle = 10;

        /// <summary>
        /// A fixed-seed sweep. Deterministic on purpose: a property test that picked fresh
        /// numbers every run would fail on somebody else's machine and pass on the next attempt,
        /// which is worse than not testing the property at all.
        /// </summary>
        sealed class Sweep
        {
            uint _state;
            public Sweep(uint seed) { _state = seed == 0 ? 1u : seed; }

            public float Next(float min, float max)
            {
                _state = _state * 1664525u + 1013904223u;
                return min + (max - min) * ((_state >> 8) / (float)(1 << 24));
            }

            /// <summary>A value in ±max but never inside ±min — for divisors, which have a hole
            /// in the middle of their domain by construction.</summary>
            public float NextAwayFromZero(float min, float max)
            {
                float magnitude = Next(min, max);
                return Next(-1f, 1f) < 0f ? -magnitude : magnitude;
            }
        }

        // ---- building ------------------------------------------------------------

        static AnimatorController NewController(params string[] floatParams)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            foreach (var name in floatParams)
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            return controller;
        }

        static AnimatorRig RigFor(AapGadgets.Kind kind, Action<AapGadgets.Request> configure = null)
        {
            var request = new AapGadgets.Request
            {
                controller = NewController("A", "B"),
                kind = kind,
                inputA = "A",
                inputB = "B",
                output = "Out",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            configure?.Invoke(request);
            string refusal = AapGadgets.Validate(request);
            Assert.IsNull(refusal, "the gadget was refused: " + refusal);
            Assert.IsTrue(AapGadgets.Apply(request), "the gadget failed to apply");
            return new AnimatorRig(request.controller);
        }

        static string Case(float a, float b, float expected, float actual) =>
            "A = " + a.ToString("R") + ", B = " + b.ToString("R")
            + " → expected " + expected.ToString("R") + ", got " + actual.ToString("R");

        // ---- swept, not sampled ---------------------------------------------------

        /// <summary>
        /// Two hundred products across the whole signed range. Four quadrants and both zeroes
        /// are a handful of cases; this asks whether the identity the gadget is built on holds
        /// everywhere in between.
        /// </summary>
        [Test]
        public void MultiplySigned_AgreesWithMultiplicationAcrossTheRange()
        {
            var sweep = new Sweep(20260810);
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
                for (int i = 0; i < 200; i++)
                {
                    float a = sweep.Next(-1f, 1f), b = sweep.Next(-1f, 1f);
                    float actual = rig.Evaluate("Out", Settle, ("A", a), ("B", b));
                    Assert.AreEqual(a * b, actual, 1e-5f, Case(a, b, a * b, actual));
                }
        }

        /// <summary>The same sweep for the quotient, over divisors kept clear of the dead zone
        /// and the ladder's floor — the two places the gadget says up front it does not go.</summary>
        [Test]
        public void DivideSigned_AgreesWithDivisionAcrossTheRange()
        {
            var sweep = new Sweep(19700101);
            using (var rig = RigFor(AapGadgets.Kind.DivideSigned,
                r => { r.rangeMin = -8f; r.rangeMax = 8f; }))
                for (int i = 0; i < 200; i++)
                {
                    float a = sweep.Next(-8f, 8f), b = sweep.NextAwayFromZero(0.25f, 8f);
                    float expected = a / b;
                    float actual = rig.Evaluate("Out", Settle, ("A", a), ("B", b));
                    Assert.AreEqual(expected, actual, Mathf.Abs(expected) * 5e-3f + 2e-3f,
                        Case(a, b, expected, actual));
                }
        }

        /// <summary>The signed adder and subtractor over the same sweep. These are the two the
        /// rest of the arithmetic is assembled from, so an error here would be everywhere.</summary>
        [Test]
        public void RangedAddAndSub_AgreeWithArithmeticAcrossTheRange()
        {
            var sweep = new Sweep(31415926);
            using (var add = RigFor(AapGadgets.Kind.AddRanged,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
            using (var sub = RigFor(AapGadgets.Kind.SubRanged,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
                for (int i = 0; i < 120; i++)
                {
                    float a = sweep.Next(-1f, 1f), b = sweep.Next(-1f, 1f);
                    float sum = add.Evaluate("Out", Settle, ("A", a), ("B", b));
                    Assert.AreEqual(a + b, sum, 1e-5f, Case(a, b, a + b, sum));
                    float difference = sub.Evaluate("Out", Settle, ("A", a), ("B", b));
                    Assert.AreEqual(a - b, difference, 1e-5f, Case(a, b, a - b, difference));
                }
        }

        /// <summary>The reciprocal over its whole working span, which is where its two halves
        /// meet and where the ladder's geometric spacing is supposed to hold the relative error
        /// flat rather than letting it grow toward the floor.</summary>
        [Test]
        public void Reciprocal_HoldsItsRelativeErrorFromTheFloorToTheCeiling()
        {
            var sweep = new Sweep(27182818);
            using (var rig = RigFor(AapGadgets.Kind.Reciprocal))
                for (int i = 0; i < 150; i++)
                {
                    // Straddling 1, from just above the ladder's floor to well past it.
                    float a = Mathf.Pow(10f, sweep.Next(-2f, 3f));
                    float expected = 1f / a;
                    float actual = rig.Evaluate("Out", Settle, ("A", a));
                    Assert.AreEqual(expected, actual, expected * 3e-3f,
                        Case(a, 0f, expected, actual));
                }
        }

        // ---- the ends of the range ------------------------------------------------

        /// <summary>
        /// Magnitudes far past anything an avatar parameter would carry. The clips hold ±1 and
        /// the weights carry the size, so the signed multiply has nothing in it that scales with
        /// the numbers — its accuracy should be the float's and not the gadget's.
        /// </summary>
        [TestCase(1000f, 1000f)]
        [TestCase(-1000f, 1000f)]
        [TestCase(1e5f, 10f)]
        [TestCase(-1e5f, -1e5f)]
        public void MultiplySigned_SurvivesLargeMagnitudes(float a, float b)
        {
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1e6f; r.rangeMax = 1e6f; }))
            {
                float actual = rig.Evaluate("Out", Settle, ("A", a), ("B", b));
                Assert.AreEqual(a * b, actual, Mathf.Abs(a * b) * 1e-4f, Case(a, b, a * b, actual));
            }
        }

        /// <summary>
        /// The hard floor under a wide range, and the reason it is not a matter of degree.
        ///
        /// A half-copy is a 1D table between the two ends of the span, so a value comes back as
        /// a blend of two numbers the size of the span and keeps the precision of *the span*,
        /// not of itself. Measured, the step it lands on is about one part in two million of the
        /// range — coarser than the twenty-four bits of the float would suggest, because the
        /// value is recovered as a difference between two numbers of the span's size. An input
        /// below that step does not arrive small. It arrives as zero, and the product with it is
        /// zero, exactly and silently.
        ///
        /// So the range is not a safety margin to be set generously: it is the unit the gadget
        /// measures in. Declare the one the values actually live in.
        /// </summary>
        [Test]
        public void SignedGadgets_LoseAnInputFinerThanTheRangesOwnResolution()
        {
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1e6f; r.rangeMax = 1e6f; }))
            {
                Assert.AreEqual(0f, rig.Evaluate("Out", Settle, ("A", 0.001f), ("B", 1000f)), 1e-3f,
                    "an operand a billionth of the range is not merely inaccurate — it is gone");
                Assert.AreEqual(1e6f, rig.Evaluate("Out", Settle, ("A", 1000f), ("B", 1000f)), 1e2f,
                    "while operands well above the step are fine");
            }

            // The same two operands, in a range a thousand times narrower.
            using (var snug = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1000f; r.rangeMax = 1000f; }))
            {
                float actual = snug.Evaluate("Out", Settle, ("A", 0.001f), ("B", 1000f));
                Assert.AreEqual(1f, actual, 0.05f, "now it survives — " + Case(0.001f, 1000f, 1f, actual));
                Assert.AreNotEqual(1f, actual,
                    "though still on the nearest step of the range rather than exactly");
            }
        }

        /// <summary>
        /// The cost of a wide range, stated as a number.
        ///
        /// The half-copies are 1D tables whose two thresholds sit at the ends of the span, so a
        /// value near zero is recovered as the difference of two large ones and keeps only the
        /// float's relative precision *of the span*, not of itself. At a span of a million that
        /// is about a tenth of a unit — which is nothing for a value of 100000 and everything
        /// for a value of 1. Declare the range you actually use.
        /// </summary>
        [Test]
        public void MultiplySigned_LosesSmallValuesInsideAHugeRange()
        {
            using (var wide = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1e6f; r.rangeMax = 1e6f; }))
            using (var snug = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -2f; r.rangeMax = 2f; }))
            {
                float wideResult = wide.Evaluate("Out", Settle, ("A", 1f), ("B", 1f));
                float snugResult = snug.Evaluate("Out", Settle, ("A", 1f), ("B", 1f));

                Assert.AreEqual(1f, snugResult, 1e-5f, "a range that fits the values is exact");
                Assert.AreEqual(1f, wideResult, 0.5f, "and a millionfold one still lands nearby");
                Assert.Greater(Mathf.Abs(wideResult - 1f), Mathf.Abs(snugResult - 1f),
                    "but it is measurably worse, which is the whole reason to declare a range");
            }
        }

        /// <summary>Past the declared range every gadget built on a 1D table clamps rather than
        /// extrapolating. Worth pinning: a clamp is a wrong answer that looks like a plausible
        /// one, and it is the failure mode of feeding a gadget a value it was not sized for.</summary>
        [Test]
        public void SignedGadgets_ClampInputsToTheDeclaredRange()
        {
            using (var rig = RigFor(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
            {
                Assert.AreEqual(1f, rig.Evaluate("Out", Settle, ("A", 50f), ("B", 1f)), 1e-4f,
                    "50 clamps to 1");
                Assert.AreEqual(-1f, rig.Evaluate("Out", Settle, ("A", -50f), ("B", 1f)), 1e-4f);
                Assert.AreEqual(1f, rig.Evaluate("Out", Settle, ("A", -50f), ("B", -50f)), 1e-4f,
                    "both ends clamp, and the signs still work out");
            }
        }

        /// <summary>The reciprocal's two documented ends: 1/240 of an input is the smallest it
        /// looks at, and 240 the largest number it will ever say.</summary>
        [Test]
        public void Reciprocal_CapsAtTwoHundredAndForty()
        {
            using (var rig = RigFor(AapGadgets.Kind.Reciprocal))
            {
                Assert.AreEqual(240f, rig.Evaluate("Out", Settle, ("A", 1f / 240f)), 1f,
                    "at the floor");
                Assert.AreEqual(240f, rig.Evaluate("Out", Settle, ("A", 1e-6f)), 1f,
                    "and below it, held rather than growing");
                Assert.AreEqual(240f, rig.Evaluate("Out", Settle, ("A", 0f)), 1f,
                    "including at zero, which is a cap and not a division");
            }
        }

        /// <summary>
        /// What the dead zone actually does, which is narrower than "fades to zero".
        ///
        /// The two sign indicators cross inside it, so their difference — which is what the
        /// quotient is scaled by — runs from −1 through 0 to +1 across the zone rather than
        /// stepping. At a divisor of exactly zero that difference is exactly zero and so is the
        /// answer. A hair off zero it is not: the difference is small but the reciprocal is at
        /// its 240 cap, and their product climbs quickly. What the zone buys is that the
        /// quotient stays continuous, bounded by |A| × 240, and changes sign by passing through
        /// zero instead of jumping the whole way across.
        /// </summary>
        [Test]
        public void DivideSigned_PassesThroughZeroInsteadOfJumpingAcrossIt()
        {
            using (var rig = RigFor(AapGadgets.Kind.DivideSigned,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; }))
            {
                Assert.AreEqual(0f, rig.Evaluate("Out", Settle, ("A", 0.5f), ("B", 0f)), 1e-3f,
                    "a divisor of exactly zero answers zero");

                float above = rig.Evaluate("Out", Settle, ("A", 0.5f), ("B", 1e-5f));
                float below = rig.Evaluate("Out", Settle, ("A", 0.5f), ("B", -1e-5f));
                Assert.Greater(above, 0f, "and either side of it keeps the divisor's sign");
                Assert.Less(below, 0f);
                Assert.AreEqual(above, -below, 1e-3f, "symmetrically");

                // |A| × the reciprocal's own cap is the most this can ever be.
                Assert.Less(Mathf.Abs(above), 0.5f * 240f, "bounded by the cap, not by luck");
            }
        }

        /// <summary>
        /// The trigonometric tables do not wrap. A period's worth of input is what they hold,
        /// and past it a 1D tree clamps to its last child — so 1.25 turns reads as 1.0 turns,
        /// not as 0.25. Anything feeding them an accumulating angle has to wrap it first.
        /// </summary>
        [Test]
        public void Trigonometry_ClampsPastOneTurnInsteadOfWrapping()
        {
            using (var rig = RigFor(AapGadgets.Kind.Sine))
            {
                Assert.AreEqual(1f, rig.Evaluate("Out", Settle, ("A", 0.25f)), 1e-3f,
                    "a quarter turn is 1");
                Assert.AreEqual(0f, rig.Evaluate("Out", Settle, ("A", 1.25f)), 1e-3f,
                    "and a turn and a quarter is not — it is held at the end of the table");
                Assert.AreEqual(0f, rig.Evaluate("Out", Settle, ("A", -3f)), 1e-3f,
                    "the same at the other end");
            }
        }

        /// <summary>
        /// The digit splitter over its whole domain, one thousandth at a time would be too many,
        /// so: the ends, a value with all three digits, and the places a decimal is exactly
        /// representable in binary and the places it is not.
        /// </summary>
        [TestCase(0f, 0f, 0f, 0f)]
        [TestCase(0.5f, 0.5f, 0f, 0f)]
        [TestCase(0.25f, 0.2f, 0.05f, 0f)]
        [TestCase(0.777f, 0.7f, 0.07f, 0.007f)]
        [TestCase(0.001f, 0f, 0f, 0.001f)]
        [TestCase(0.909f, 0.9f, 0f, 0.009f)]
        public void SeparateDigits_HoldsAcrossItsDomain(float a,
            float tenths, float hundredths, float thousandths)
        {
            using (var rig = RigFor(AapGadgets.Kind.SeparateDigits))
            {
                rig.Set("A", a).Step(Settle * 3);
                Assert.AreEqual(tenths, rig.Get("Out/Tenths"), 5e-4f, "tenths of " + a);
                Assert.AreEqual(hundredths, rig.Get("Out/Hundredths"), 5e-4f, "hundredths of " + a);
                Assert.AreEqual(thousandths, rig.Get("Out/Thousandths"), 5e-4f, "thousandths of " + a);
            }
        }

        /// <summary>
        /// The one input whose answer surprises: 1 reads as three zeroes, not as 0.9 / 0.09 /
        /// 0.009.
        ///
        /// The coarsest of the four quantizers is the *ones* place, and every digit is a
        /// difference measured against it — which is what stops 1 from arriving as 0.999. So
        /// the gadget reports the digits of the fractional part, and at exactly 1 there is no
        /// fractional part to report. There is no "ones" output to see the 1 in, either. A
        /// recipe that has to tell 1 from 0 needs its own comparison.
        /// </summary>
        [Test]
        public void SeparateDigits_ReportsTheFractionalDigitsSoOneReadsAsZeroes()
        {
            using (var rig = RigFor(AapGadgets.Kind.SeparateDigits))
            {
                rig.Set("A", 1f).Step(Settle * 3);
                Assert.AreEqual(0f, rig.Get("Out/Tenths"), 5e-4f, "1.000 has no tenths");
                Assert.AreEqual(0f, rig.Get("Out/Hundredths"), 5e-4f);
                Assert.AreEqual(0f, rig.Get("Out/Thousandths"), 5e-4f);

                // Just below it, the digits are all there — so this is a boundary, not a hole.
                rig.Set("A", 0.999f).Step(Settle * 3);
                Assert.AreEqual(0.9f, rig.Get("Out/Tenths"), 5e-4f);
                Assert.AreEqual(0.09f, rig.Get("Out/Hundredths"), 5e-4f);
                Assert.AreEqual(0.009f, rig.Get("Out/Thousandths"), 5e-4f);
            }
        }

        /// <summary>Outside 0..1 the splitter clamps like everything else, so anything at or
        /// past 1 reads as the digits of 1 and anything below 0 as the digits of 0 — a wrong
        /// answer for an out-of-domain input, but a bounded and predictable one.</summary>
        [Test]
        public void SeparateDigits_ClampsOutsideZeroToOne()
        {
            using (var rig = RigFor(AapGadgets.Kind.SeparateDigits))
            {
                rig.Set("A", 5f).Step(Settle * 3);
                Assert.AreEqual(0f, rig.Get("Out/Tenths"), 5e-4f, "clamped to 1, whose digits are 0");
                rig.Set("A", -5f).Step(Settle * 3);
                Assert.AreEqual(0f, rig.Get("Out/Tenths"), 5e-4f, "clamped to 0");
            }
        }

        /// <summary>Atan2 over the whole circle, on directions the ring does not sample, with
        /// the seam left out — the one place the gadget says its answer is a wedge wide.</summary>
        [Test]
        public void Atan2_TracksTheAngleAllTheWayRound()
        {
            var sweep = new Sweep(66260755);
            using (var rig = RigFor(AapGadgets.Kind.Atan2, r => r.atan2Directions = 32))
                for (int i = 0; i < 60; i++)
                {
                    float turn = sweep.Next(0.03f, 0.97f);
                    float radians = 2f * Mathf.PI * turn;
                    float magnitude = sweep.Next(1f, 20f);
                    float actual = rig.Evaluate("Out", Settle,
                        ("A", Mathf.Sin(radians) * magnitude), ("B", Mathf.Cos(radians) * magnitude));
                    Assert.AreEqual(turn, actual, 0.01f,
                        "turn " + turn.ToString("R") + " at magnitude " + magnitude.ToString("R")
                        + " read as " + actual.ToString("R"));
                }
        }

        /// <summary>
        /// Error along a chain, which is the question a composed recipe actually has: eight
        /// signed multiplies by 1 in a row. Each one is a pair of tables and a sum, so if any of
        /// them drifted the drift would compound — and eight of them is enough to see it.
        /// </summary>
        [Test]
        public void ErrorDoesNotCompoundAlongAChainOfGadgets()
        {
            var controller = NewController("X", "One's Twin");
            int layer = -1;
            string previous = "X";
            for (int i = 1; i <= 8; i++)
            {
                var request = new AapGadgets.Request
                {
                    controller = controller,
                    kind = AapGadgets.Kind.MultiplySigned,
                    inputA = previous,
                    inputB = "One's Twin",
                    output = "Stage/" + i,
                    rangeMin = -2f,
                    rangeMax = 2f,
                    layerIndex = layer,
                    newLayerName = "DBT",
                };
                Assert.IsTrue(AapGadgets.Apply(request), "stage " + i);
                if (layer < 0) layer = 1;
                previous = request.output;
            }

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("X", 0.7f).Set("One's Twin", 1f).Step(40);
                for (int i = 1; i <= 8; i++)
                    Assert.AreEqual(0.7f, rig.Get("Stage/" + i), 1e-5f,
                        "after " + i + " multiplications by one");
            }
        }
    }
}
