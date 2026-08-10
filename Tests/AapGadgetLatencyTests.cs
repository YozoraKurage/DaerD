using System;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// How many frames each gadget takes, measured rather than reasoned about.
    ///
    /// A gadget that reads its inputs straight off the parameters and writes its result through
    /// AAP clips costs one frame: Mecanim evaluates the frame from the values it started with
    /// and applies the writes at the end. A gadget that keeps an intermediate parameter pays a
    /// frame for each hop, because the stage reading that intermediate sees what the previous
    /// evaluation wrote. So latency is a property of the gadget's internal shape — fixed,
    /// knowable, and nowhere recorded.
    ///
    /// It has to be knowable, because composing gadgets is composing latencies: two branches
    /// off one input that arrive at different depths are looking at different frames of it, and
    /// a Buffer on the shallower one is the only fix. You cannot place that buffer without
    /// these numbers.
    ///
    /// The measurement is deliberately strict — the latency of a gadget is the first frame from
    /// which its output is correct *and stays* correct. A value the output merely passes
    /// through on its way does not count, which is what makes this able to see a gadget whose
    /// own halves disagree for a frame.
    /// </summary>
    [Category("Runtime")]
    public class AapGadgetLatencyTests
    {
        // ---- measuring -----------------------------------------------------------

        /// <summary>Steps <paramref name="frames"/> frames, recording the output each time.</summary>
        static float[] Trace(AnimatorRig rig, string output, int frames)
        {
            var values = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                rig.Step();
                values[i] = rig.Get(output);
            }
            return values;
        }

        /// <summary>The 1-based frame from which the trace is within tolerance of the expected
        /// value and never leaves it again, or the trace length + 1 when it never settles.</summary>
        static int SettledAt(float[] trace, float expected, float tolerance)
        {
            int first = trace.Length;
            for (int i = trace.Length - 1; i >= 0; i--)
            {
                if (Mathf.Abs(trace[i] - expected) > tolerance) break;
                first = i;
            }
            return first + 1;
        }

        static string Describe(float[] trace)
        {
            var text = new System.Text.StringBuilder("trace:");
            for (int i = 0; i < trace.Length; i++)
                text.Append(' ').Append(trace[i].ToString("0.####"));
            return text.ToString();
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

        static void Apply(AapGadgets.Request request)
        {
            string refusal = AapGadgets.Validate(request);
            Assert.IsNull(refusal, "the gadget was refused: " + refusal);
            Assert.IsTrue(AapGadgets.Apply(request), "the gadget failed to apply");
        }

        static AapGadgets.Request NewRequest(AapGadgets.Kind kind, Action<AapGadgets.Request> configure)
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
            return request;
        }

        static AnimatorRig RigFor(AapGadgets.Kind kind, Action<AapGadgets.Request> configure = null)
        {
            var request = NewRequest(kind, configure);
            Apply(request);
            return new AnimatorRig(request.controller);
        }

        /// <summary>
        /// Settles the gadget on one set of inputs, moves them, and reports how many frames the
        /// new answer took to arrive and stick.
        ///
        /// Every measurement here is also held against <see cref="AapGadgets.Latency"/> — the
        /// number the recipe API states and does its buffer arithmetic with. That number being
        /// right is the whole contract, and a declaration nobody measures is a comment.
        /// </summary>
        static int Latency(AapGadgets.Kind kind, Action<AapGadgets.Request> configure,
            (string name, float value)[] before, (string name, float value)[] after,
            string output, float expected, float tolerance, int window = 20)
        {
            var request = NewRequest(kind, configure);
            Apply(request);
            using (var rig = new AnimatorRig(request.controller))
            {
                foreach (var input in before) rig.Set(input.name, input.value);
                rig.Step(40);
                Assert.Greater(Mathf.Abs(rig.Get(output) - expected), tolerance,
                    "the starting value must differ from the expected one, or the measurement "
                    + "reads a latency of one no matter what the gadget does");

                foreach (var input in after) rig.Set(input.name, input.value);
                var trace = Trace(rig, output, window);
                int settled = SettledAt(trace, expected, tolerance);
                Assert.LessOrEqual(settled, window,
                    kind + " never settled on " + expected + " — " + Describe(trace));
                Assert.AreEqual(AapGadgets.Latency(request), settled,
                    kind + " costs a different number of frames than it declares — "
                    + Describe(trace));
                return settled;
            }
        }

        static (string, float)[] In(params (string, float)[] inputs) => inputs;

        // ---- the one-frame gadgets ------------------------------------------------

        /// <summary>
        /// Everything that computes straight from its inputs, with no parameter of its own in
        /// the middle. One frame, every one of them — this is the number the arithmetic and the
        /// tables all share, and the baseline every other latency is measured against.
        /// </summary>
        [Test]
        public void OneFrame_TheGadgetsThatKeepNoIntermediate()
        {
            Assert.AreEqual(1, Latency(AapGadgets.Kind.Add, null,
                In(("A", 0f), ("B", 0f)), In(("A", 0.25f), ("B", 0.5f)), "Out", 0.75f, 1e-4f), "Add");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Sub, null,
                In(("A", 0f), ("B", 0f)), In(("A", 0.75f), ("B", 0.25f)), "Out", 0.5f, 1e-4f), "Sub");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.AddRanged,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; },
                In(("A", 0f), ("B", 0f)), In(("A", -0.5f), ("B", 0.25f)), "Out", -0.25f, 1e-4f), "AddRanged");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.SubRanged,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; },
                In(("A", 0f), ("B", 0f)), In(("A", -0.25f), ("B", 0.5f)), "Out", -0.75f, 1e-4f), "SubRanged");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Multiply, null,
                In(("A", 0f), ("B", 0f)), In(("A", 0.5f), ("B", 0.5f)), "Out", 0.25f, 1e-4f), "Multiply");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.And, null,
                In(("A", 0f), ("B", 0f)), In(("A", 1f), ("B", 1f)), "Out", 1f, 1e-4f), "And");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Or, null,
                In(("A", 0f), ("B", 0f)), In(("A", 1f), ("B", 0f)), "Out", 1f, 1e-4f), "Or");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Not, null,
                In(("A", 1f)), In(("A", 0f)), "Out", 1f, 1e-4f), "Not");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.FloatAsBool, r => r.threshold = 0.5f,
                In(("A", 0f)), In(("A", 1f)), "Out", 1f, 1e-4f), "FloatAsBool");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Remap,
                r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 10f; r.rangeMax = 20f; },
                In(("A", 0f)), In(("A", 1f)), "Out", 20f, 1e-3f), "Remap");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Sine, null,
                In(("A", 0f)), In(("A", 0.25f)), "Out", 1f, 1e-3f), "Sine");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Cosine, null,
                In(("A", 0.25f)), In(("A", 0f)), "Out", 1f, 1e-3f), "Cosine");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Tangent, null,
                In(("A", 0f)), In(("A", 0.125f)), "Out", 1f, 1e-3f), "Tangent");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Lut1D,
                r => { r.curve = AnimationCurve.Linear(0f, 0f, 1f, 4f); r.lutSamples = 5; },
                In(("A", 0f)), In(("A", 1f)), "Out", 4f, 1e-3f), "Lut1D");

            Assert.AreEqual(1, Latency(AapGadgets.Kind.Atan2, r => r.atan2Directions = 16,
                In(("A", 1f), ("B", 0f)), In(("A", 0f), ("B", -1f)), "Out", 0.5f, 5e-3f), "Atan2");
        }

        /// <summary>The buffer is the one gadget whose latency is an argument rather than a
        /// consequence, which is what makes it the tool for lining the others up.</summary>
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(5)]
        [TestCase(8)]
        public void Buffer_CostsExactlyTheFramesItWasAskedFor(int frames)
        {
            Assert.AreEqual(frames, Latency(AapGadgets.Kind.Buffer,
                r => { r.bufferFrames = frames; r.rangeMin = -1f; r.rangeMax = 1f; },
                In(("A", 0f)), In(("A", 0.8f)), "Out", 0.8f, 1e-4f));
        }

        // ---- the gadgets that keep intermediates ----------------------------------

        /// <summary>
        /// Two frames, whichever half of its domain the input is in — which is a thing that had
        /// to be arranged, not a thing that fell out.
        ///
        /// The gadget is two halves that add up: an exact core, which computes the input's shift
        /// into a parameter of its own and so is a frame behind by construction, and a lookup
        /// ladder for inputs below 1. Left reading the live input, the ladder answered in one
        /// frame while the core took two, and the gadget's cost was a property of the value
        /// going in rather than of the graph — which no API can state and no buffer can line up
        /// against. The ladder reads a delayed copy instead, so both halves are always
        /// describing the same frame.
        /// </summary>
        [Test]
        public void Reciprocal_CostsTwoFramesOnBothSidesOfOne()
        {
            Assert.AreEqual(2, Latency(AapGadgets.Kind.Reciprocal, null,
                In(("A", 1f)), In(("A", 4f)), "Out", 0.25f, 1e-3f),
                "at or above 1, where the exact core carries the answer");

            Assert.AreEqual(2, Latency(AapGadgets.Kind.Reciprocal, null,
                In(("A", 0.5f)), In(("A", 0.25f)), "Out", 4f, 8e-3f),
                "below 1, where the ladder carries it");

            Assert.AreEqual(2, Latency(AapGadgets.Kind.Reciprocal, null,
                In(("A", 0.5f)), In(("A", 4f)), "Out", 0.25f, 1e-3f),
                "and across the boundary between them");
        }

        /// <summary>
        /// Same halves, same frame: crossing 1 no longer shows a value of its own. It used to —
        /// for one frame the ladder described the new input while the core still described the
        /// old one, and the sum of two different inputs' halves is the reciprocal of neither.
        /// The trace now steps from the old answer to the new one with nothing in between.
        /// </summary>
        [Test]
        public void Reciprocal_CrossesOneWithoutInventingAValue()
        {
            using (var rig = RigFor(AapGadgets.Kind.Reciprocal))
            {
                rig.Set("A", 2f).Step(40);
                Assert.AreEqual(0.5f, rig.Get("Out"), 1e-3f);

                rig.Set("A", 0.5f);
                var trace = Trace(rig, "Out", 6);
                Assert.AreEqual(2, SettledAt(trace, 2f, 8e-3f),
                    "it settles on the second frame — " + Describe(trace));
                Assert.AreEqual(0.5f, trace[0], 8e-3f,
                    "and the frame before that still holds the old answer, not one of its own — "
                    + Describe(trace));
            }
        }

        /// <summary>The reciprocal's two, and one more for the multiply that reads it. The
        /// numerator waits out those two frames inside the gadget, so the multiply is pairing
        /// readings of one frame rather than a fresh A with a stale reciprocal.</summary>
        [Test]
        public void Divide_CostsThreeFramesWhateverTheDivisor()
        {
            Assert.AreEqual(3, Latency(AapGadgets.Kind.Divide, null,
                In(("A", 1f), ("B", 1f)), In(("A", 1f), ("B", 4f)), "Out", 0.25f, 1e-3f),
                "a divisor at or above 1");

            Assert.AreEqual(3, Latency(AapGadgets.Kind.Divide, null,
                In(("A", 1f), ("B", 0.5f)), In(("A", 1f), ("B", 0.25f)), "Out", 4f, 2e-2f),
                "a divisor below 1");

            Assert.AreEqual(3, Latency(AapGadgets.Kind.Divide, null,
                In(("A", 1f), ("B", 1f)), In(("A", 3f), ("B", 1f)), "Out", 3f, 1e-2f),
                "and a numerator that moves instead");
        }

        /// <summary>Two frames: one to split both inputs into their halves, one to sum the four
        /// products of those halves. The same two in every quadrant — a sign is not a special
        /// case here, it is which of two weights happens to be non-zero.</summary>
        [TestCase(0.5f, 0.5f, -0.5f, 0.5f, -0.25f)]
        [TestCase(-0.5f, 0.5f, 0.5f, 0.5f, 0.25f)]
        [TestCase(0.5f, 0.5f, 0.25f, 0.8f, 0.2f)]
        public void MultiplySigned_CostsTwoFrames(float a0, float b0, float a1, float b1, float expected)
        {
            Assert.AreEqual(2, Latency(AapGadgets.Kind.MultiplySigned,
                r => { r.rangeMin = -1f; r.rangeMax = 1f; },
                In(("A", a0), ("B", b0)), In(("A", a1), ("B", b1)), "Out", expected, 1e-4f));
        }

        /// <summary>Four: the magnitude, the reciprocal's two, and the stage that puts the sign
        /// back on. The numerator and the sign are walked through the same three frames, so this
        /// is one number and not a range.</summary>
        [TestCase(1f, 4f, 1f, -4f, -0.25f)]
        [TestCase(1f, -4f, -1f, 2f, -0.5f)]
        [TestCase(1f, 0.5f, 1f, -0.5f, -2f)]
        public void DivideSigned_CostsFourFrames(float a0, float b0, float a1, float b1, float expected)
        {
            Assert.AreEqual(4, Latency(AapGadgets.Kind.DivideSigned,
                r => { r.rangeMin = -8f; r.rangeMax = 8f; },
                In(("A", a0), ("B", b0)), In(("A", a1), ("B", b1)), "Out", expected,
                Mathf.Max(Mathf.Abs(expected) * 4e-3f, 2e-3f)));
        }

        /// <summary>
        /// The reason the numerator waits: a quotient has to be of one moment. With A fresh and
        /// the reciprocal two frames old, moving both inputs at once produced a number that was
        /// neither the old quotient nor the new one for two frames — right only once everything
        /// stopped. This walks both inputs together and asks for the answer to be one of the
        /// two at every frame.
        /// </summary>
        [Test]
        public void Divide_HoldsAQuotientOfOneMomentWhileBothInputsMove()
        {
            using (var rig = RigFor(AapGadgets.Kind.Divide))
            {
                rig.Set("A", 1f).Set("B", 1f).Step(40);
                Assert.AreEqual(1f, rig.Get("Out"), 1e-3f);

                // 1/1 = 1 before, 6/2 = 3 after. Anything pairing one input's old reading with
                // the other's new one lands on 1/2 or 6 — nowhere near either.
                rig.Set("A", 6f).Set("B", 2f);
                var trace = Trace(rig, "Out", 8);
                for (int i = 0; i < trace.Length; i++)
                    Assert.IsTrue(Mathf.Abs(trace[i] - 1f) < 1e-2f || Mathf.Abs(trace[i] - 3f) < 3e-2f,
                        "frame " + (i + 1) + " is neither quotient — " + Describe(trace));
                Assert.AreEqual(3, SettledAt(trace, 3f, 3e-2f), Describe(trace));
            }
        }

        /// <summary>
        /// Five stages, each reading what the one before it wrote: the clamped copy of the
        /// input, the three offsets, the subnormal products, the read-back, and the differences
        /// that are the digits.
        ///
        /// The three digits are measured separately on purpose. They are subtractions of
        /// quantizer outputs, and if the quantizers they subtract sat at different depths, the
        /// digits would settle at different frames — which is both a latency the API could not
        /// state as one number and a wrong answer for every frame in between.
        /// </summary>
        [Test]
        public void SeparateDigits_CostsFiveFramesOnAllThreeDigits()
        {
            int tenths = Latency(AapGadgets.Kind.SeparateDigits, null,
                In(("A", 0f)), In(("A", 0.123f)), "Out/Tenths", 0.1f, 5e-4f);
            int hundredths = Latency(AapGadgets.Kind.SeparateDigits, null,
                In(("A", 0f)), In(("A", 0.123f)), "Out/Hundredths", 0.02f, 5e-4f);
            int thousandths = Latency(AapGadgets.Kind.SeparateDigits, null,
                In(("A", 0f)), In(("A", 0.123f)), "Out/Thousandths", 0.003f, 5e-4f);

            Assert.AreEqual(tenths, hundredths, "the digits should all be the same age");
            Assert.AreEqual(tenths, thousandths, "the digits should all be the same age");
            Assert.AreEqual(5, tenths, "and that age should be five frames");
        }

        // ---- the ones latency does not describe ------------------------------------

        /// <summary>
        /// The clock needs a frame to have a previous reading to subtract, so the first frame
        /// reports nothing and every frame after it reports the real one. Not a latency in the
        /// pipeline sense — there is no input to be late about — but a warm-up a recipe reading
        /// it on frame one still has to know about.
        /// </summary>
        [Test]
        public void FrameTime_ReadsZeroOnTheFirstFrameAndTheTruthAfterwards()
        {
            var controller = NewController();
            Apply(new AapGadgets.Request
            {
                controller = controller,
                kind = AapGadgets.Kind.FrameTime,
                output = "Dt",
                layerIndex = -1,
                newLayerName = "DBT",
            });

            using (var rig = new AnimatorRig(controller))
            {
                var trace = Trace(rig, "Dt", 6);
                Assert.AreEqual(0f, trace[0], 1e-5f, "nothing to subtract yet — " + Describe(trace));
                for (int i = 2; i < trace.Length; i++)
                    Assert.AreEqual(AnimatorRig.Dt, trace[i], 1e-4f,
                        "frame " + (i + 1) + " — " + Describe(trace));
            }
        }

        /// <summary>
        /// Both smoothings answer in one frame and arrive in their own time, so "latency" is the
        /// wrong question for them: they are filters, not stages. What a composing recipe needs
        /// to know is only the first half — a value read from a smoothing is one frame behind
        /// its input, the same as any other gadget — and that its settling time is a function of
        /// the smoothing amount and not of the graph.
        /// </summary>
        [Test]
        public void Smoothings_RespondInOneFrameAndSettleInTheirOwnTime()
        {
            using (var rig = RigFor(AapGadgets.Kind.Smooth,
                r => { r.smoothing = "Smoothing"; r.smoothingDefault = 0.5f; }))
            {
                rig.Set("A", 0f).Set("Smoothing", 0.5f).Step(40);
                Assert.AreEqual(0f, rig.Get("Out"), 1e-4f);

                rig.Set("A", 1f).Step();
                Assert.AreEqual(0.5f, rig.Get("Out"), 1e-3f, "it moves on the very next frame");
                Assert.AreNotEqual(1f, rig.Get("Out"), "but it is not there yet");
            }

            using (var rig = RigFor(AapGadgets.Kind.SmoothLinear,
                r => { r.smoothing = "Step"; r.smoothingDefault = 0.05f; r.rangeMin = -1f; r.rangeMax = 1f; }))
            {
                rig.Set("A", 0f).Set("Step", 0.05f).Step(40);
                Assert.AreEqual(0f, rig.Get("Out"), 1e-4f);

                // The step reads a difference this gadget wrote last frame, so the constant-speed
                // smoothing takes one frame to notice before it starts moving.
                rig.Set("A", 1f).Step();
                Assert.AreEqual(0f, rig.Get("Out"), 1e-6f, "still nothing after one frame");
                rig.Step();
                Assert.Greater(rig.Get("Out"), 0f, "it has started");
                Assert.Less(rig.Get("Out"), 0.2f, "and it is walking, not jumping");
            }
        }

        /// <summary>
        /// The three the measurement above cannot reach, held against what they declare anyway:
        /// a source has no input to be late about, and a filter's output is a running function
        /// of its input rather than a delayed copy. What the numbers mean for them is how long
        /// the first response takes, and the test above is what pins that down.
        /// </summary>
        [Test]
        public void Latency_TheSourceAndTheFiltersDeclareTheirFirstResponse()
        {
            Assert.AreEqual(1, AapGadgets.Latency(NewRequest(AapGadgets.Kind.FrameTime, null)),
                "the clock has a reading to subtract from the frame after the first");
            Assert.AreEqual(1, AapGadgets.Latency(NewRequest(AapGadgets.Kind.Smooth, null)),
                "the exponential smoothing moves on the next frame");
            Assert.AreEqual(2, AapGadgets.Latency(NewRequest(AapGadgets.Kind.SmoothLinear, null)),
                "the constant-speed one reads a difference it wrote itself, so it is a frame later");
        }

        /// <summary>Every kind is either measured somewhere in this file or named in the test
        /// above. A new gadget with a latency nobody checked is a number the recipe API would
        /// state on faith.</summary>
        [Test]
        public void Latency_IsDeclaredForEveryKind()
        {
            foreach (AapGadgets.Kind kind in Enum.GetValues(typeof(AapGadgets.Kind)))
                Assert.Greater(AapGadgets.Latency(NewRequest(kind, null)), 0,
                    kind + " must declare a frame cost");
        }

        // ---- latencies add ----------------------------------------------------------

        /// <summary>
        /// The property the whole idea rests on: chaining gadgets adds their latencies, with
        /// nothing else going on. Three one-frame gadgets in a row cost three frames, and a
        /// two-frame gadget in the middle of them costs four.
        ///
        /// If this were not exactly additive, no table of per-gadget frame counts could be used
        /// to place a buffer, and the numbers above would be trivia rather than a contract.
        /// </summary>
        [Test]
        public void Latencies_AddUpAlongAChain()
        {
            var controller = NewController("A");
            int layer = -1;
            void Gadget(AapGadgets.Kind kind, string output, string a, string b = null,
                Action<AapGadgets.Request> configure = null)
            {
                var request = new AapGadgets.Request
                {
                    controller = controller,
                    kind = kind,
                    inputA = a,
                    inputB = b,
                    output = output,
                    layerIndex = layer,
                    newLayerName = "DBT",
                };
                configure?.Invoke(request);
                Apply(request);
                if (layer < 0) layer = 1;
            }

            // A → Not → Not → Not, one frame each, and — through a remap that lifts the value
            // above 1, where the reciprocal's cost is its full two — a reciprocal off the second
            // hop.
            Gadget(AapGadgets.Kind.Not, "N1", "A");
            Gadget(AapGadgets.Kind.Not, "N2", "N1");
            Gadget(AapGadgets.Kind.Not, "N3", "N2");
            Gadget(AapGadgets.Kind.Remap, "Lifted", "N2",
                configure: r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 1f; r.rangeMax = 5f; });
            Gadget(AapGadgets.Kind.Reciprocal, "R", "Lifted");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("A", 0f).Step(40);
                Assert.AreEqual(1f, rig.Get("N1"), 1e-4f);
                Assert.AreEqual(0f, rig.Get("N2"), 1e-4f);

                rig.Set("A", 0.75f);
                var n1 = Trace(rig, "N1", 1)[0];
                Assert.AreEqual(0.25f, n1, 1e-4f, "one gadget, one frame");
            }

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("A", 0f).Step(40);
                rig.Set("A", 0.75f);
                Assert.AreEqual(2, SettledAt(Trace(rig, "N2", 20), 0.75f, 1e-4f), "two gadgets");
            }

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("A", 0f).Step(40);
                rig.Set("A", 0.75f);
                Assert.AreEqual(3, SettledAt(Trace(rig, "N3", 20), 0.25f, 1e-4f), "three gadgets");
            }

            using (var rig = new AnimatorRig(controller))
            {
                // A = 0 → N2 = 0 → Lifted = 1 → R = 1.  A = 1 → N2 = 1 → Lifted = 5 → R = 0.2.
                rig.Set("A", 0f).Step(40);
                Assert.AreEqual(1f, rig.Get("R"), 1e-3f);

                rig.Set("A", 1f);
                Assert.AreEqual(2 + 1 + 2, SettledAt(Trace(rig, "R", 20), 0.2f, 2e-3f),
                    "three one-frame gadgets and a two-frame one");
            }
        }
    }
}
