using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Gadgets wired into each other, not one at a time.
    ///
    /// Every gadget on its own is a small enough claim that reading the tree nearly settles it.
    /// Composition is where the claims stop being independent: a gadget reads a parameter
    /// another one writes, so the second sees the first's *previous* frame, and a value that
    /// took four hops to arrive is four frames older than one that took one. Nothing in the
    /// asset records that — depth is a property of the graph, and skew is a property of the
    /// graph plus time. These tests hold the whole rack against arithmetic worked out
    /// beforehand, and one of them holds a moving input against it, where the skew is real.
    /// </summary>
    [Category("Runtime")]
    public class AapGadgetCompositionTests
    {
        /// <summary>
        /// A controller collecting gadgets into one shared DBT layer, the way the wizard does
        /// when you keep picking the same target layer. Gadgets go in dependency order, since a
        /// request is refused unless the parameters it reads already exist.
        /// </summary>
        sealed class Rack
        {
            public readonly AnimatorController Controller = new AnimatorController();
            public readonly HashSet<AapGadgets.Kind> Kinds = new HashSet<AapGadgets.Kind>();
            int _layerIndex = -1;

            public Rack(params string[] floatParams)
            {
                Controller.AddLayer("Base");
                foreach (var name in floatParams)
                    Controller.AddParameter(name, AnimatorControllerParameterType.Float);
            }

            public Rack Gadget(AapGadgets.Kind kind, string output, string a, string b = null,
                Action<AapGadgets.Request> configure = null)
            {
                var request = new AapGadgets.Request
                {
                    controller = Controller,
                    kind = kind,
                    inputA = a,
                    inputB = b,
                    output = output,
                    layerIndex = _layerIndex,
                    newLayerName = "DBT",
                };
                configure?.Invoke(request);

                string label = kind + " → '" + output + "'";
                string refusal = AapGadgets.Validate(request);
                Assert.IsNull(refusal, label + " was refused: " + refusal);
                Assert.IsTrue(AapGadgets.Apply(request), label + " failed to apply");

                // The first gadget builds the layer; everything after it joins that one. A
                // supporting layer (FrameTime's clock) is appended at the end, so index 1 stays
                // the rack.
                if (_layerIndex < 0) _layerIndex = 1;
                Kinds.Add(kind);
                return this;
            }

            public AnimatorRig Run() => new AnimatorRig(Controller);
        }

        // ---- signed multiplication, which no single gadget can do -------------------

        /// <summary>
        /// The Multiply gadget is positive-only: Direct weights stop at zero, so a negative
        /// input is not multiplied but dropped. Signed multiplication has to be *built*, and
        /// the algebra is what the rack is for — shift both operands into 0..1, multiply there,
        /// and undo the shift with the ranged adders:
        ///
        ///     u = (x+1)/2,  v = (y+1)/2
        ///     x·y = (2u-1)(2v-1) = 4uv - 2u - 2v + 1
        ///
        /// Eight gadgets, six deep, and every constant in it (the 4, the two 2s, the 1) is
        /// carried by a remap's output range or by the Direct trees' own constant One. The
        /// structural tests can confirm each of the eight; only running it says the algebra
        /// came out.
        /// </summary>
        [TestCase(0.5f, -0.8f, -0.4f)]
        [TestCase(-0.6f, -0.5f, 0.3f)]
        [TestCase(1f, 1f, 1f)]
        [TestCase(-1f, 1f, -1f)]
        [TestCase(0f, 0.7f, 0f)]
        [TestCase(0.25f, 0.25f, 0.0625f)]
        public void SignedMultiply_BuiltFromEightGadgets(float x, float y, float expected)
        {
            var rack = new Rack("X", "Y");
            // Into 0..1, where the positive-only multiply is exact.
            rack.Gadget(AapGadgets.Kind.Remap, "U", "X",
                configure: r => { r.inMin = -1f; r.inMax = 1f; r.rangeMin = 0f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.Remap, "V", "Y",
                configure: r => { r.inMin = -1f; r.inMax = 1f; r.rangeMin = 0f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.Multiply, "P", "U", "V");

            // The three scaled terms. A remap's output range is where the constants live.
            rack.Gadget(AapGadgets.Kind.Remap, "Q", "P",
                configure: r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 0f; r.rangeMax = 4f; });
            rack.Gadget(AapGadgets.Kind.Remap, "TwoU", "U",
                configure: r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 0f; r.rangeMax = 2f; });
            rack.Gadget(AapGadgets.Kind.Remap, "TwoV", "V",
                configure: r => { r.inMin = 0f; r.inMax = 1f; r.rangeMin = 0f; r.rangeMax = 2f; });

            // 4uv - 2u - 2v + 1, the last term taken from the constant the Direct trees already
            // keep at 1 for their own weights.
            rack.Gadget(AapGadgets.Kind.SubRanged, "S1", "Q", "TwoU",
                configure: r => { r.rangeMin = -4f; r.rangeMax = 4f; });
            rack.Gadget(AapGadgets.Kind.SubRanged, "S2", "S1", "TwoV",
                configure: r => { r.rangeMin = -8f; r.rangeMax = 8f; });
            rack.Gadget(AapGadgets.Kind.AddRanged, "Product", "S2", "One",
                configure: r => { r.rangeMin = -8f; r.rangeMax = 8f; });

            using (var rig = rack.Run())
                Assert.AreEqual(expected, rig.Evaluate("Product", 30, ("X", x), ("Y", y)), 3e-3f);
        }

        // ---- depth, and the buffer that cancels it ---------------------------------

        /// <summary>
        /// The claim the Buffer gadget exists for, stated as an experiment.
        ///
        /// Two branches leave the same input. One goes through two gadgets (Not twice, which is
        /// the identity by way of two hops); the other through a two-frame buffer. They are the
        /// same value at the same age, so their difference must be zero on *every* frame — even
        /// while the input is moving. The control is the same comparison against the raw input,
        /// which is two frames younger: that one has to break, or the buffer was never needed
        /// and this test proves nothing.
        ///
        /// A settled rack cannot tell these apart. Only a moving one can.
        /// </summary>
        [Test]
        public void Buffer_AlignsBranchesOfDifferentDepthWhileTheInputMoves()
        {
            var rack = new Rack("A");
            rack.Gadget(AapGadgets.Kind.Not, "N1", "A");
            rack.Gadget(AapGadgets.Kind.Not, "Deep", "N1");
            rack.Gadget(AapGadgets.Kind.Buffer, "Aligned", "A",
                configure: r => { r.bufferFrames = 2; r.rangeMin = 0f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.SubRanged, "Matched", "Deep", "Aligned",
                configure: r => { r.rangeMin = -1f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.SubRanged, "Skewed", "Deep", "A",
                configure: r => { r.rangeMin = -1f; r.rangeMax = 1f; });

            using (var rig = rack.Run())
            {
                rig.Set("A", 0f).Step(20);
                Assert.AreEqual(0f, rig.Get("Matched"), 1e-4f, "at rest the two branches agree");
                Assert.AreEqual(0f, rig.Get("Skewed"), 1e-4f, "and so does the unbuffered one");

                // Walk the input up in steps big enough that a two-frame lag is unmistakable.
                float worstMatched = 0f, worstSkewed = 0f;
                for (int i = 1; i <= 10; i++)
                {
                    rig.Set("A", i / 10f).Step();
                    worstMatched = Mathf.Max(worstMatched, Mathf.Abs(rig.Get("Matched")));
                    worstSkewed = Mathf.Max(worstSkewed, Mathf.Abs(rig.Get("Skewed")));
                }

                Assert.Less(worstMatched, 1e-3f,
                    "the buffered branch should stay aligned through the whole ramp");
                Assert.Greater(worstSkewed, 0.1f,
                    "the unbuffered comparison should have drifted — otherwise the buffer is "
                    + "cancelling a skew that was never there and this test is vacuous");
            }
        }

        // ---- the clock, and what it buys -------------------------------------------

        /// <summary>
        /// "Driving stepSize from a FrameTime gadget makes the speed independent of the frame
        /// rate" — the one sentence in the gadget documentation that is purely about time, and
        /// therefore the one no structural test can reach at all.
        ///
        /// The same rack is run for half a second twice, at 60 and at 120 frames per second, and
        /// asked how far it travelled. Beside it runs the naive version, whose step is a
        /// constant per frame: that one has to travel about twice as far at twice the frame
        /// rate, or "independent of the frame rate" is not saying anything.
        /// </summary>
        [Test]
        public void FrameTime_MakesSmoothLinearTravelByTheClockAndNotByTheFrame()
        {
            // Rate is in units per second, and the multiply turns it into units per frame.
            Rack Timed()
            {
                var rack = new Rack("Target", "Rate");
                rack.Gadget(AapGadgets.Kind.FrameTime, "Dt", null);
                rack.Gadget(AapGadgets.Kind.Multiply, "Step", "Dt", "Rate");
                rack.Gadget(AapGadgets.Kind.SmoothLinear, "Tracked", "Target",
                    configure: r => { r.smoothing = "Step"; r.rangeMin = -2f; r.rangeMax = 2f; });
                return rack;
            }

            // The same gadget with a step that knows nothing about time.
            Rack Naive()
            {
                var rack = new Rack("Target", "Step");
                rack.Gadget(AapGadgets.Kind.SmoothLinear, "Tracked", "Target",
                    configure: r => { r.smoothing = "Step"; r.rangeMin = -2f; r.rangeMax = 2f; });
                return rack;
            }

            float TravelTimed(int frames, float dt)
            {
                using (var rig = Timed().Run())
                {
                    // Target far enough away that the whole run stays in the constant-speed
                    // stretch, short of the ramp the gadget lands with.
                    rig.Set("Target", 2f).Set("Rate", 1f).Step(frames, dt);
                    return rig.Get("Tracked");
                }
            }

            float TravelNaive(int frames, float dt)
            {
                using (var rig = Naive().Run())
                {
                    rig.Set("Target", 2f).Set("Step", 1f / 60f).Step(frames, dt);
                    return rig.Get("Tracked");
                }
            }

            float timed60 = TravelTimed(30, 1f / 60f);      // half a second
            float timed120 = TravelTimed(60, 1f / 120f);    // the same half second
            float naive60 = TravelNaive(30, 1f / 60f);
            float naive120 = TravelNaive(60, 1f / 120f);

            // Both runs lose their first few frames to the pipeline filling (the clock needs a
            // frame to report, the multiply another), and those frames are worth less time at
            // 120 fps — so the two are close, not equal.
            Assert.AreEqual(0.5f, timed60, 0.1f, "half a second at 60 fps");
            Assert.AreEqual(timed60, timed120, 0.08f,
                "the clock-driven step should cover the same ground in the same time");

            Assert.Greater(naive120, naive60 * 1.7f,
                "the naive step should travel with the frame count — otherwise the comparison "
                + "above is not measuring anything");
        }

        // ---- all of them, at once ----------------------------------------------------

        /// <summary>
        /// Every gadget kind there is, in one layer, wired into each other wherever one's output
        /// can be another's input: a joystick becomes an angle, the angle becomes three
        /// trigonometric readings, a throttle goes through a baked curve and then through the
        /// arithmetic, the logic gadgets gate on it, division and reciprocal read the sum, and
        /// the three time-shaped gadgets (both smoothings and the buffer) trail behind it.
        ///
        /// Twenty-three gadgets, twenty-two kinds, one Direct root summing all of them, and the
        /// deepest result seven hops from its input. Every value below was worked out on paper
        /// first; the point of the test is that the rack agrees.
        ///
        /// The count assertion at the end is a tripwire: add a gadget kind to DaerD and this
        /// test fails until the new kind is wired into the rack too.
        /// </summary>
        [Test]
        public void EveryKind_InOneRack_ComputesWhatThePaperSays()
        {
            var rack = new Rack("X", "Y", "Throttle", "Enable", "Rate", "Ease");

            // The clock first, so the layer it brings lands behind the rack and not inside it.
            rack.Gadget(AapGadgets.Kind.FrameTime, "Dt", null);
            rack.Gadget(AapGadgets.Kind.Multiply, "Step", "Dt", "Rate");

            // Direction: a vector becomes a turn, and the turn becomes its three readings.
            rack.Gadget(AapGadgets.Kind.Atan2, "Turn", "Y", "X", r => r.atan2Directions = 16);
            rack.Gadget(AapGadgets.Kind.Sine, "Wave", "Turn");
            rack.Gadget(AapGadgets.Kind.Cosine, "Quad", "Turn");
            rack.Gadget(AapGadgets.Kind.Tangent, "Slope", "Turn");

            // Throttle through a baked response curve: 0 → 0, 1 → 0.8, sampled on quarters.
            var response = AnimationCurve.Linear(0f, 0f, 1f, 0.8f);
            rack.Gadget(AapGadgets.Kind.Lut1D, "Curve", "Throttle",
                configure: r => { r.curve = response; r.lutSamples = 5; });

            // Logic, gating on the throttle being past half.
            rack.Gadget(AapGadgets.Kind.FloatAsBool, "Hot", "Throttle", configure: r => r.threshold = 0.5f);
            rack.Gadget(AapGadgets.Kind.And, "Armed", "Hot", "Enable");
            rack.Gadget(AapGadgets.Kind.Or, "Any", "Hot", "Enable");
            rack.Gadget(AapGadgets.Kind.Not, "Cold", "Hot");

            // Arithmetic over the curve and the throttle.
            rack.Gadget(AapGadgets.Kind.Add, "Sum", "Throttle", "Curve");
            rack.Gadget(AapGadgets.Kind.Sub, "Gap", "Sum", "Throttle");
            rack.Gadget(AapGadgets.Kind.Multiply, "Scale", "Curve", "Throttle");

            // The signed pair, over two readings that can both go negative.
            rack.Gadget(AapGadgets.Kind.AddRanged, "Blend", "Wave", "Quad",
                configure: r => { r.rangeMin = -2f; r.rangeMax = 2f; });
            rack.Gadget(AapGadgets.Kind.SubRanged, "Swing", "Wave", "Quad",
                configure: r => { r.rangeMin = -2f; r.rangeMax = 2f; });

            // The signed pair the plain Multiply and Divide cannot do: both readings here can
            // go negative, and the positive-only gadgets would drop them rather than use them.
            rack.Gadget(AapGadgets.Kind.MultiplySigned, "Signed", "Wave", "Quad",
                configure: r => { r.rangeMin = -1f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.DivideSigned, "Quotient", "Blend", "Wave",
                configure: r => { r.rangeMin = -2f; r.rangeMax = 2f; });

            // The tangent's ±100 band folded into 0..1.
            rack.Gadget(AapGadgets.Kind.Remap, "Norm", "Slope",
                configure: r => { r.inMin = -100f; r.inMax = 100f; r.rangeMin = 0f; r.rangeMax = 1f; });

            // Division, both ways round.
            rack.Gadget(AapGadgets.Kind.Reciprocal, "Inv", "Sum");
            rack.Gadget(AapGadgets.Kind.Divide, "Ratio", "Scale", "Sum");

            // The three that are about time rather than value.
            rack.Gadget(AapGadgets.Kind.SmoothLinear, "Tracked", "Throttle",
                configure: r => { r.smoothing = "Step"; r.rangeMin = -1f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.Smooth, "Eased", "Throttle",
                configure: r => { r.smoothing = "Ease"; r.rangeMin = -1f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.Buffer, "Late", "Curve",
                configure: r => { r.bufferFrames = 3; r.rangeMin = 0f; r.rangeMax = 1f; });

            // And the readout.
            rack.Gadget(AapGadgets.Kind.SeparateDigits, "Digits", "Curve");

            Assert.AreEqual(Enum.GetValues(typeof(AapGadgets.Kind)).Length, rack.Kinds.Count,
                "every gadget kind should be in the rack — a new kind belongs here too");
            Assert.AreEqual(3, rack.Controller.layers.Length,
                "one base layer, one rack, and the clock's own layer behind it");

            using (var rig = rack.Run())
            {
                // An eighth of a turn: the direction ring samples it exactly, and so do all
                // three trigonometric tables, so these are table reads and not interpolations.
                const float diagonal = 0.70710678f;
                rig.Set("X", diagonal).Set("Y", diagonal)
                   .Set("Throttle", 0.5f).Set("Enable", 1f)
                   // 1.5 units a second is 0.025 a frame here — comfortably under the ramp width
                   // the constant-speed smoothing needs to stay below to settle at all. See
                   // AapGadgetRuntimeTests.SmoothLinear_SettlesBelowTheRampWidthButSwingsAtIt.
                   .Set("Rate", 1.5f).Set("Ease", 0.5f);
                // Long enough for the deepest chain to fill and for both smoothings to arrive.
                rig.Step(300);

                Assert.AreEqual(1f / 60f, rig.Get("Dt"), 1e-4f, "Dt");
                Assert.AreEqual(0.025f, rig.Get("Step"), 1e-3f, "Step = Dt × 1.5");

                Assert.AreEqual(0.125f, rig.Get("Turn"), 5e-3f, "Turn = atan2(y, x)");
                Assert.AreEqual(diagonal, rig.Get("Wave"), 1e-3f, "Wave = sin(Turn)");
                Assert.AreEqual(diagonal, rig.Get("Quad"), 1e-3f, "Quad = cos(Turn)");
                Assert.AreEqual(1f, rig.Get("Slope"), 1e-3f, "Slope = tan(Turn)");

                Assert.AreEqual(0.4f, rig.Get("Curve"), 1e-3f, "Curve = response(0.5)");

                Assert.AreEqual(1f, rig.Get("Hot"), 1e-4f, "Hot");
                Assert.AreEqual(1f, rig.Get("Armed"), 1e-4f, "Armed = Hot AND Enable");
                Assert.AreEqual(1f, rig.Get("Any"), 1e-4f, "Any = Hot OR Enable");
                Assert.AreEqual(0f, rig.Get("Cold"), 1e-4f, "Cold = NOT Hot");

                Assert.AreEqual(0.9f, rig.Get("Sum"), 1e-3f, "Sum = Throttle + Curve");
                Assert.AreEqual(0.4f, rig.Get("Gap"), 1e-3f, "Gap = Sum - Throttle");
                Assert.AreEqual(0.2f, rig.Get("Scale"), 1e-3f, "Scale = Curve × Throttle");

                Assert.AreEqual(2f * diagonal, rig.Get("Blend"), 2e-3f, "Blend = Wave + Quad");
                Assert.AreEqual(0f, rig.Get("Swing"), 2e-3f, "Swing = Wave - Quad");

                Assert.AreEqual(0.5f, rig.Get("Signed"), 2e-3f, "Signed = Wave × Quad");
                Assert.AreEqual(2f, rig.Get("Quotient"), 1.5e-2f, "Quotient = Blend / Wave");

                Assert.AreEqual(0.505f, rig.Get("Norm"), 1e-3f, "Norm = Slope folded into 0..1");

                Assert.AreEqual(1f / 0.9f, rig.Get("Inv"), 4e-3f, "Inv = 1 / Sum");
                Assert.AreEqual(0.2f / 0.9f, rig.Get("Ratio"), 2e-3f, "Ratio = Scale / Sum");

                Assert.AreEqual(0.5f, rig.Get("Tracked"), 5e-3f, "Tracked caught up to Throttle");
                Assert.AreEqual(0.5f, rig.Get("Eased"), 5e-3f, "Eased caught up to Throttle");
                Assert.AreEqual(0.4f, rig.Get("Late"), 1e-3f, "Late = Curve, three frames behind");

                Assert.AreEqual(0.4f, rig.Get("Digits/Tenths"), 5e-4f, "the tenths of Curve");
                Assert.AreEqual(0f, rig.Get("Digits/Hundredths"), 5e-4f, "the hundredths of Curve");
                Assert.AreEqual(0f, rig.Get("Digits/Thousandths"), 5e-4f, "the thousandths of Curve");
            }
        }

        /// <summary>
        /// The same rack, driven somewhere else. One set of inputs can be passed by a gadget
        /// that ignores its input entirely (a stuck output that happens to sit on the expected
        /// value), so the rack is worth asking twice — here with the throttle low enough to shut
        /// the logic gate, the joystick on an axis rather than a diagonal, and a curve reading
        /// with three digits in it.
        /// </summary>
        [Test]
        public void EveryKind_InOneRack_MovesWhenTheInputsDo()
        {
            var rack = new Rack("X", "Y", "Throttle", "Enable", "Rate", "Ease");
            rack.Gadget(AapGadgets.Kind.FrameTime, "Dt", null);
            rack.Gadget(AapGadgets.Kind.Multiply, "Step", "Dt", "Rate");
            rack.Gadget(AapGadgets.Kind.Atan2, "Turn", "Y", "X", r => r.atan2Directions = 16);
            rack.Gadget(AapGadgets.Kind.Sine, "Wave", "Turn");
            rack.Gadget(AapGadgets.Kind.Cosine, "Quad", "Turn");
            // A curve whose quarter samples are 0, 0.123, 0.246, 0.369, 0.492 — the reading at
            // 0.25 has all three decimals the digit splitter is supposed to find.
            var response = AnimationCurve.Linear(0f, 0f, 1f, 0.492f);
            rack.Gadget(AapGadgets.Kind.Lut1D, "Curve", "Throttle",
                configure: r => { r.curve = response; r.lutSamples = 5; });
            rack.Gadget(AapGadgets.Kind.FloatAsBool, "Hot", "Throttle", configure: r => r.threshold = 0.5f);
            rack.Gadget(AapGadgets.Kind.And, "Armed", "Hot", "Enable");
            rack.Gadget(AapGadgets.Kind.Or, "Any", "Hot", "Enable");
            rack.Gadget(AapGadgets.Kind.Not, "Cold", "Hot");
            rack.Gadget(AapGadgets.Kind.Add, "Sum", "Throttle", "Curve");
            rack.Gadget(AapGadgets.Kind.Sub, "Gap", "Sum", "Throttle");
            rack.Gadget(AapGadgets.Kind.Multiply, "Scale", "Curve", "Throttle");
            rack.Gadget(AapGadgets.Kind.AddRanged, "Blend", "Wave", "Quad",
                configure: r => { r.rangeMin = -2f; r.rangeMax = 2f; });
            rack.Gadget(AapGadgets.Kind.SubRanged, "Swing", "Wave", "Quad",
                configure: r => { r.rangeMin = -2f; r.rangeMax = 2f; });
            rack.Gadget(AapGadgets.Kind.Reciprocal, "Inv", "Sum");
            rack.Gadget(AapGadgets.Kind.Divide, "Ratio", "Scale", "Sum");
            rack.Gadget(AapGadgets.Kind.Smooth, "Eased", "Throttle",
                configure: r => { r.smoothing = "Ease"; r.rangeMin = -1f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.Buffer, "Late", "Curve",
                configure: r => { r.bufferFrames = 3; r.rangeMin = 0f; r.rangeMax = 1f; });
            rack.Gadget(AapGadgets.Kind.SeparateDigits, "Digits", "Curve");

            using (var rig = rack.Run())
            {
                // Straight up: a quarter turn, which the ring also samples exactly.
                rig.Set("X", 0f).Set("Y", 1f)
                   .Set("Throttle", 0.25f).Set("Enable", 0f).Set("Ease", 0.5f);
                rig.Step(200);

                Assert.AreEqual(0.25f, rig.Get("Turn"), 5e-3f, "Turn = atan2(1, 0)");
                Assert.AreEqual(1f, rig.Get("Wave"), 1e-3f, "Wave = sin(quarter turn)");
                Assert.AreEqual(0f, rig.Get("Quad"), 1e-3f, "Quad = cos(quarter turn)");
                Assert.AreEqual(1f, rig.Get("Blend"), 2e-3f, "Blend = 1 + 0");
                Assert.AreEqual(1f, rig.Get("Swing"), 2e-3f, "Swing = 1 - 0");

                Assert.AreEqual(0.123f, rig.Get("Curve"), 1e-3f, "Curve = response(0.25)");
                Assert.AreEqual(0f, rig.Get("Hot"), 1e-4f, "the throttle is below the threshold");
                Assert.AreEqual(0f, rig.Get("Armed"), 1e-4f, "0 AND 0");
                Assert.AreEqual(0f, rig.Get("Any"), 1e-4f, "0 OR 0");
                Assert.AreEqual(1f, rig.Get("Cold"), 1e-4f, "NOT 0");

                Assert.AreEqual(0.373f, rig.Get("Sum"), 1e-3f, "Sum = 0.25 + 0.123");
                Assert.AreEqual(0.123f, rig.Get("Gap"), 1e-3f, "Gap = Sum - Throttle");
                Assert.AreEqual(0.03075f, rig.Get("Scale"), 1e-3f, "Scale = 0.123 × 0.25");
                Assert.AreEqual(1f / 0.373f, rig.Get("Inv"), 0.02f, "Inv = 1 / Sum");
                Assert.AreEqual(0.03075f / 0.373f, rig.Get("Ratio"), 2e-3f, "Ratio = Scale / Sum");

                Assert.AreEqual(0.25f, rig.Get("Eased"), 5e-3f, "Eased caught up to Throttle");
                Assert.AreEqual(0.123f, rig.Get("Late"), 1e-3f, "Late = Curve, three frames behind");

                Assert.AreEqual(0.1f, rig.Get("Digits/Tenths"), 5e-4f, "the 1 of 0.123");
                Assert.AreEqual(0.02f, rig.Get("Digits/Hundredths"), 5e-4f, "the 2 of 0.123");
                Assert.AreEqual(0.003f, rig.Get("Digits/Thousandths"), 5e-4f, "the 3 of 0.123");
            }
        }
    }
}
