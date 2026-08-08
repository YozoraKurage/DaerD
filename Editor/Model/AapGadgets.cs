using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Pure-C# ports of the ylaac AAP gadget templates — no AnimatorAsCode
    /// dependency. Each gadget is a blend tree that computes a float operation once per
    /// frame inside a Write-Defaults-ON Direct blend tree layer, writing its result through
    /// AAP clips (one-key clips animating an Animator parameter).
    ///
    /// Direct blend trees sum weight × value over their children, which gives:
    ///   Add       output = A + B            (weights are the inputs; positive values only)
    ///   Sub       output = A - B            (B weighs a "-1" clip; positive values only)
    ///   Multiply  output = A × B            (nested Direct trees multiply their weights)
    ///   Ranged Add/Sub remap both inputs through 1D trees first, so negatives work too.
    /// Logic gadgets assume 0..1 inputs.
    ///
    /// Three gadget families need more than a blend tree child, and add a layer of their own
    /// at the end of the controller: division (a curve covers the half a Direct weight cannot
    /// reach), frame time (a clock clip to subtract from itself) and the trigonometric curves
    /// (played by motion time, which no blend tree can drive). Reference:
    /// https://vrc.school/docs/Other/Advanced-BlendTrees
    /// </summary>
    static class AapGadgets
    {
        public enum Kind
        {
            Smooth,
            Add,
            AddRanged,
            Sub,
            SubRanged,
            Multiply,
            And,
            Or,
            Not,
            FloatAsBool,
            Remap,
            Reciprocal,
            Divide,
            FrameTime,
            SmoothLinear,
            SeparateDigits,
            Sine,
            Cosine,
            Tangent,
        }

        public static bool IsBinary(Kind kind) =>
            kind == Kind.Add || kind == Kind.AddRanged || kind == Kind.Sub
            || kind == Kind.SubRanged || kind == Kind.Multiply
            || kind == Kind.And || kind == Kind.Or || kind == Kind.Divide;

        public static bool UsesRange(Kind kind) =>
            kind == Kind.Smooth || kind == Kind.AddRanged || kind == Kind.SubRanged
            || kind == Kind.Remap || kind == Kind.SmoothLinear;

        /// <summary>Kinds that take a second, shareable Float setting how fast they follow.</summary>
        public static bool UsesSmoothing(Kind kind) => kind == Kind.Smooth || kind == Kind.SmoothLinear;

        /// <summary>FrameTime reads the animator's own clock, so it has no input parameter.</summary>
        public static bool NeedsInput(Kind kind) => kind != Kind.FrameTime;

        /// <summary>False for the kinds that are nothing but a layer: a curve played by motion
        /// time cannot live inside a blend tree, so they need neither a Direct tree nor the
        /// layer choice that comes with it.</summary>
        public static bool UsesDbtLayer(Kind kind) =>
            kind != Kind.Sine && kind != Kind.Cosine && kind != Kind.Tangent;

        /// <summary>Kinds that add a layer of their own — either instead of a blend tree child
        /// or on top of one. Layer order carries meaning here (see the builders), which the
        /// wizard says out loud.</summary>
        public static bool CreatesSupportingLayer(Kind kind) =>
            kind == Kind.Reciprocal || kind == Kind.Divide || kind == Kind.FrameTime
            || !UsesDbtLayer(kind);

        public class Request
        {
            public AnimatorController controller;
            public Kind kind;
            public string inputA;
            /// <summary>Second input; only read for binary kinds.</summary>
            public string inputB;
            /// <summary>Result parameter written by the gadget. Must be new. SeparateDigits
            /// treats it as a base name and writes one parameter per digit under it — see
            /// <see cref="OutputParameters"/>.</summary>
            public string output;
            /// <summary>Value range: input+output range for ranged Add/Sub, output range for Remap.</summary>
            public float rangeMin = -1f;
            public float rangeMax = 1f;
            /// <summary>Input range mapped from — Remap only.</summary>
            public float inMin = 0f;
            public float inMax = 1f;
            /// <summary>FloatAsBool: values at or above this become 1.</summary>
            public float threshold = 0.5f;
            /// <summary>Smooth: the smoothing-amount parameter. SmoothLinear: the step size.
            /// Either may exist already as a Float, so gadgets can share one.</summary>
            public string smoothing;
            /// <summary>Default value stored on <see cref="smoothing"/> when it is created.</summary>
            public float smoothingDefault = 0.9f;
            /// <summary>Existing DBT (or empty) layer to add the gadget to, or -1 to create one.</summary>
            public int layerIndex = -1;
            public string newLayerName = "DBT";
        }

        /// <summary>
        /// The parameters the gadget writes as its result. One for every kind but
        /// SeparateDigits, which splits the input into three and uses the requested output as
        /// the base name for them. The private parameters a gadget keeps for itself live under
        /// the same names, so checking these keeps them clear too.
        /// </summary>
        public static string[] OutputParameters(Request r) =>
            r.kind == Kind.SeparateDigits
                ? new[] { r.output + "/Tenths", r.output + "/Hundredths", r.output + "/Thousandths" }
                : new[] { r.output };

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");

            // Smoothing has its own parameter rules (feedback output, shared smoothing
            // amount); the whole request is delegated to AapSmoothing.
            if (r.kind == Kind.Smooth)
                return AapSmoothing.Validate(ToSmoothingRequest(r));

            if (NeedsInput(r.kind) && !IsFloat(controller, r.inputA))
                return L.Tr("The source must be an existing Float parameter.");
            if (IsBinary(r.kind))
            {
                if (!IsFloat(controller, r.inputB))
                    return L.Tr("The second input must be an existing Float parameter.");
            }

            if (string.IsNullOrEmpty(r.output) || r.output == r.inputA || r.output == r.inputB)
                return L.Tr("The output parameter needs a name different from the inputs.");
            foreach (var name in OutputParameters(r))
                if (DbtBuilder.FindParameter(controller, name) != null)
                    return L.Tr("A parameter named '{0}' already exists.", name);

            if (UsesSmoothing(r.kind))
            {
                if (string.IsNullOrEmpty(r.smoothing) || r.smoothing == r.inputA || r.smoothing == r.output)
                    return L.Tr("The smoothing parameter needs its own name.");
                var smoothing = DbtBuilder.FindParameter(controller, r.smoothing);
                if (smoothing != null && smoothing.type != AnimatorControllerParameterType.Float)
                    return L.Tr("Parameter '{0}' exists but is not a Float.", r.smoothing);
            }

            if ((r.kind == Kind.AddRanged || r.kind == Kind.SubRanged || r.kind == Kind.SmoothLinear)
                && !(r.rangeMin < r.rangeMax))
                return L.Tr("Range Min must be smaller than Range Max.");
            if (r.kind == Kind.Remap && !(r.inMin < r.inMax))
                return L.Tr("Input Min must be smaller than Input Max.");

            // The layer-only kinds bring their own layer; there is no target to check.
            if (!UsesDbtLayer(r.kind)) return null;
            return AapSmoothing.ValidateLayerChoice(controller, r.layerIndex, r.newLayerName);
        }

        static AapSmoothing.Request ToSmoothingRequest(Request r) => new AapSmoothing.Request
        {
            controller = r.controller,
            source = r.inputA,
            output = r.output,
            smoothing = r.smoothing,
            smoothingDefault = r.smoothingDefault,
            rangeMin = r.rangeMin,
            rangeMax = r.rangeMax,
            layerIndex = r.layerIndex,
            newLayerName = r.newLayerName,
        };

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.</summary>
        public static bool Apply(Request r)
        {
            if (r.kind == Kind.Smooth)
                return AapSmoothing.Apply(ToSmoothingRequest(r));

            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("DBT Gadget"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "DBT Gadget");

                foreach (var name in OutputParameters(r))
                    DbtBuilder.EnsureFloatParameter(controller, name, 0f);
                // A step size below zero would drive the output away from the input; the
                // wizard offers no such value, but a recipe could ask for one.
                if (r.kind == Kind.SmoothLinear)
                    DbtBuilder.EnsureFloatParameter(controller, r.smoothing, Mathf.Max(0f, r.smoothingDefault));

                if (UsesDbtLayer(r.kind))
                {
                    string one = DbtBuilder.EnsureConstantOneParameter(controller);
                    var root = DbtBuilder.EnsureDirectBlendTreeLayer(controller, r.layerIndex, r.newLayerName);
                    DbtBuilder.AddDirectChild(root, Build(r, controller, one), one);
                }
                else
                {
                    BuildTrigonometryLayer(controller, r.kind, r.inputA, r.output);
                }
                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        static BlendTree Build(Request r, AnimatorController c, string one)
        {
            string a = r.inputA, b = r.inputB, output = r.output;
            switch (r.kind)
            {
                case Kind.Add: return Add(c, a, b, output);
                case Kind.AddRanged: return AddRanged(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.Sub: return Sub(c, a, b, output);
                case Kind.SubRanged: return SubRanged(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.Multiply: return Multiply(c, a, b, output);
                case Kind.And: return And(c, a, b, output);
                case Kind.Or: return Or(c, a, b, output);
                case Kind.Not: return Not(c, a, output);
                case Kind.FloatAsBool: return FloatAsBool(c, a, output, r.threshold);
                case Kind.Remap: return Remap(c, a, output, r.rangeMin, r.rangeMax, r.inMin, r.inMax);
                case Kind.Reciprocal: return Reciprocal(c, a, output, one);
                case Kind.Divide: return Divide(c, a, b, output, one);
                case Kind.FrameTime: return FrameTime(c, output);
                case Kind.SmoothLinear:
                    return SmoothLinear(c, a, output, one, r.smoothing, r.rangeMin, r.rangeMax);
                case Kind.SeparateDigits: return SeparateDigits(c, a, output, one);
                default: return null;
            }
        }

        // ---- arithmetic --------------------------------------------------------

        /// <summary>output = A + B. Direct weights can't go negative, so positive values only;
        /// use <see cref="AddRanged"/> for signed inputs.</summary>
        public static BlendTree Add(AnimatorController c, string a, string b, string output)
        {
            var one = DbtBuilder.ParameterClip(c, output, 1f);
            var tree = DbtBuilder.DirectTree(c, Name("Add", a, b));
            DbtBuilder.AddDirectChild(tree, one, a);
            DbtBuilder.AddDirectChild(tree, one, b);
            return tree;
        }

        /// <summary>output = A - B. Positive values only; use <see cref="SubRanged"/> for signed inputs.</summary>
        public static BlendTree Sub(AnimatorController c, string a, string b, string output)
        {
            var plus = DbtBuilder.ParameterClip(c, output, 1f);
            var minus = DbtBuilder.ParameterClip(c, output, -1f);
            var tree = DbtBuilder.DirectTree(c, Name("Sub", a, b));
            DbtBuilder.AddDirectChild(tree, plus, a);
            DbtBuilder.AddDirectChild(tree, minus, b);
            return tree;
        }

        /// <summary>Signed add: both inputs are remapped through 1D trees whose leaves write
        /// the output, and the Direct parent sums the two contributions.</summary>
        public static BlendTree AddRanged(AnimatorController c, string a, string b, string output,
            string one, float min, float max)
        {
            var clipMin = DbtBuilder.ParameterClip(c, output, min);
            var clipMax = DbtBuilder.ParameterClip(c, output, max);

            var treeA = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(a) + " (Ranged)", a);
            treeA.AddChild(clipMin, min);
            treeA.AddChild(clipMax, max);
            var treeB = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (Ranged)", b);
            treeB.AddChild(clipMin, min);
            treeB.AddChild(clipMax, max);

            var tree = DbtBuilder.DirectTree(c, Name("Add", a, b));
            DbtBuilder.AddDirectChild(tree, treeA, one);
            DbtBuilder.AddDirectChild(tree, treeB, one);
            return tree;
        }

        /// <summary>Signed subtract: like <see cref="AddRanged"/>, but B's leaves are swapped
        /// so its contribution is negated. Inherited from the template: the negation is exact
        /// only for a symmetric range (min = -max); an asymmetric range shifts the result by
        /// (min + max).</summary>
        public static BlendTree SubRanged(AnimatorController c, string a, string b, string output,
            string one, float min, float max)
        {
            var clipMin = DbtBuilder.ParameterClip(c, output, min);
            var clipMax = DbtBuilder.ParameterClip(c, output, max);

            var treeA = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(a) + " (Ranged)", a);
            treeA.AddChild(clipMin, min);
            treeA.AddChild(clipMax, max);
            var treeB = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (Ranged, negated)", b);
            treeB.AddChild(clipMax, min);
            treeB.AddChild(clipMin, max);

            var tree = DbtBuilder.DirectTree(c, Name("Sub", a, b));
            DbtBuilder.AddDirectChild(tree, treeA, one);
            DbtBuilder.AddDirectChild(tree, treeB, one);
            return tree;
        }

        /// <summary>output = A × B via nested Direct trees (weights multiply down the tree).
        /// Positive values only.</summary>
        public static BlendTree Multiply(AnimatorController c, string a, string b, string output)
        {
            var one = DbtBuilder.ParameterClip(c, output, 1f);
            var inner = DbtBuilder.DirectTree(c, Name("Mul", a, b) + " (Inner)");
            DbtBuilder.AddDirectChild(inner, one, b);
            var tree = DbtBuilder.DirectTree(c, Name("Mul", a, b));
            DbtBuilder.AddDirectChild(tree, inner, a);
            return tree;
        }

        /// <summary>Linear remap: input over [inMin, inMax] → output over [outMin, outMax]
        /// (reversed output ranges are allowed and invert the slope).</summary>
        public static BlendTree Remap(AnimatorController c, string input, string output,
            float outMin, float outMax, float inMin, float inMax)
        {
            var tree = DbtBuilder.Tree1D(c, Name("Remap", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, outMin), inMin);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, outMax), inMax);
            return tree;
        }

        // ---- logic (inputs assumed 0..1) ---------------------------------------

        public static BlendTree And(AnimatorController c, string a, string b, string output)
        {
            var zero = DbtBuilder.ParameterClip(c, output, 0f);
            var one = DbtBuilder.ParameterClip(c, output, 1f);

            var inner = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (And)", b);
            inner.AddChild(zero, 0f);
            inner.AddChild(one, 1f);
            var tree = DbtBuilder.Tree1D(c, Name("And", a, b), a);
            tree.AddChild(zero, 0f);
            tree.AddChild(inner, 1f);
            return tree;
        }

        public static BlendTree Or(AnimatorController c, string a, string b, string output)
        {
            var zero = DbtBuilder.ParameterClip(c, output, 0f);
            var one = DbtBuilder.ParameterClip(c, output, 1f);

            var inner = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(b) + " (Or)", b);
            inner.AddChild(zero, 0f);
            inner.AddChild(one, 1f);
            var tree = DbtBuilder.Tree1D(c, Name("Or", a, b), a);
            tree.AddChild(inner, 0f);
            tree.AddChild(one, 1f);
            return tree;
        }

        public static BlendTree Not(AnimatorController c, string input, string output)
        {
            var tree = DbtBuilder.Tree1D(c, Name("Not", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 1f), 0f);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 0f), 1f);
            return tree;
        }

        /// <summary>0 below the threshold, 1 from the threshold up. The template's version
        /// stacked two children on one threshold; a narrow ramp just below it is equivalent
        /// and keeps the tree well-formed.</summary>
        public static BlendTree FloatAsBool(AnimatorController c, string input, string output, float threshold)
        {
            const float epsilon = 0.01f;
            var tree = DbtBuilder.Tree1D(c, Name("Bool", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 0f), threshold - epsilon);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, 1f), threshold);
            return tree;
        }

        // ---- reciprocal and division -------------------------------------------

        /// <summary>Samples on the 1/x curve, and the span its times are spread over. Both are
        /// only a resolution: the curve is read by motion time, which normalizes it away.</summary>
        const int ReciprocalSamples = 240;
        const float ReciprocalSpan = 100f;

        /// <summary>
        /// output = 1 / input, for positive inputs, in two halves.
        ///
        /// Above 1 a normalized Direct tree does it: normalizing divides every weight by the
        /// weight sum, so a motion that writes nothing weighing (input - 1) next to an
        /// "output = 1" clip weighing 1 leaves the clip at 1 / ((input - 1) + 1). The shift is
        /// computed by a sibling tree, which costs the result one extra frame of lag.
        ///
        /// Below 1 the same trick is out of reach — the shift would need a negative weight, and
        /// Direct weights stop at 0 — so a supporting layer takes over there
        /// (<see cref="BuildReciprocalLayer"/>).
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Reciprocal(AnimatorController c, string input, string output, string one)
        {
            string shift = output + "/Shift", name = Name("Reciprocal", input, null);
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            DbtBuilder.EnsureFloatParameter(c, shift, 0f);

            var core = DbtBuilder.DirectTree(c, name + " (Core)");
            DbtBuilder.SetNormalizedBlendValues(core, true);
            DbtBuilder.AddDirectChild(core, DbtBuilder.EmptyClip(c, "Weight Only"), shift);
            DbtBuilder.AddDirectChild(core, DbtBuilder.ParameterClip(c, output, 1f), one);

            var tree = DbtBuilder.DirectTree(c, name);
            DbtBuilder.AddDirectChild(tree, Sub(c, input, one, shift), one);   // shift = input - 1
            DbtBuilder.AddDirectChild(tree, core, one);

            BuildReciprocalLayer(c, input, output);
            return tree;
        }

        /// <summary>
        /// The 0 &lt; x &lt; 1 half of <see cref="Reciprocal"/>. A state whose motion time is the
        /// input plays a curve that holds span/t; the clip spans exactly that many seconds, so
        /// normalized time input reads at span × input, where the curve is 1 / input. The state
        /// only runs below 1 — above it the layer idles and the blend tree's value stands.
        /// </summary>
        static void BuildReciprocalLayer(AnimatorController c, string input, string output)
        {
            var stateMachine = AddSupportingLayer(c, output + " 1/x");

            var curve = new AnimationCurve();
            for (int i = 1; i <= ReciprocalSamples; i++)
                curve.AddKey(new Keyframe(ReciprocalSpan / i, i));
            SmoothTangents(curve);
            // The keys crowd into the first hundredth of the span, so the clip carries a frame
            // rate fine enough to tell them apart in the curve editor.
            var clip = DbtBuilder.CurveClip(c, DbtBuilder.Sanitize(output) + " = 1/x",
                output, curve, 1000f);

            var idle = AddSupportingState(stateMachine, "Idle", new Vector3(300f, 60f, 0f),
                DbtBuilder.EmptyClip(c, "Idle"));
            var inverse = AddSupportingState(stateMachine, "1/x", new Vector3(300f, 170f, 0f), clip);
            inverse.timeParameterActive = true;
            inverse.timeParameter = input;
            stateMachine.defaultState = idle;

            InstantTransition(idle, inverse, AnimatorConditionMode.Less, 1f, input);
            InstantTransition(inverse, idle, AnimatorConditionMode.Greater, 1f, input);
        }

        /// <summary>output = A / B, for positive inputs: B's reciprocal into a parameter of its
        /// own, then A × that. Inherits <see cref="Reciprocal"/>'s supporting layer, and one
        /// more frame of lag on top of its two — the multiply reads last frame's reciprocal.</summary>
        public static BlendTree Divide(AnimatorController c, string a, string b, string output, string one)
        {
            string inverse = output + "/Inv";
            DbtBuilder.EnsureFloatParameter(c, output, 0f);

            var tree = DbtBuilder.DirectTree(c, Name("Div", a, b));
            DbtBuilder.AddDirectChild(tree, Reciprocal(c, b, inverse, one), one);
            DbtBuilder.AddDirectChild(tree, Multiply(c, a, inverse, output), one);
            return tree;
        }

        // ---- frame time --------------------------------------------------------

        /// <summary>How long the clock runs before it loops, in seconds.</summary>
        const float ClockSeconds = 2000f;

        /// <summary>
        /// output = the seconds that passed since the previous frame. A supporting layer runs
        /// the clock — a looping clip counting one unit per second into "output/Clock" — and
        /// the blend tree keeps the previous reading in "output/Last":
        ///     output = Clock - Last,  then  Last = Clock
        /// Both are written from the same tree, so every weight reads the value the frame
        /// started with and Last is still one frame behind when it is subtracted.
        ///
        /// One per controller is the intent: the clock is a shared stopwatch, and a second copy
        /// buys nothing but another layer. The clip loops, so the frame that wraps reports one
        /// large negative delta.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree FrameTime(AnimatorController c, string output)
        {
            string clock = output + "/Clock", last = output + "/Last";
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            DbtBuilder.EnsureFloatParameter(c, clock, 0f);
            DbtBuilder.EnsureFloatParameter(c, last, 0f);

            BuildClockLayer(c, output, clock);

            var tree = DbtBuilder.DirectTree(c, Name("Delta", output, null));
            DbtBuilder.AddDirectChild(tree, DbtBuilder.ParameterClip(c, output, 1f), clock);
            DbtBuilder.AddDirectChild(tree, DbtBuilder.ParameterClip(c, output, -1f), last);
            DbtBuilder.AddDirectChild(tree, DbtBuilder.ParameterClip(c, last, 1f), clock);
            return tree;
        }

        static void BuildClockLayer(AnimatorController c, string output, string clock)
        {
            var stateMachine = AddSupportingLayer(c, output + " Clock");
            // One unit per second. The tangents carry the same slope, so the ramp between the
            // two keys stays exactly linear instead of easing at its ends.
            var curve = new AnimationCurve(
                new Keyframe(0f, 0f, 1f, 1f),
                new Keyframe(ClockSeconds, ClockSeconds, 1f, 1f));
            var clip = DbtBuilder.CurveClip(c, DbtBuilder.Sanitize(clock), clock, curve, 60f);
            MakeLooping(clip);
            AddSupportingState(stateMachine, "Clock", new Vector3(300f, 60f, 0f), clip);
        }

        // ---- linear smoothing ---------------------------------------------------

        /// <summary>Where the step ramp saturates, in parameter units. The convention this
        /// technique is written against; see <see cref="SmoothLinear"/>.</summary>
        const float StepRamp = 0.1f;

        /// <summary>
        /// Moves the output toward the input at a constant speed — stepSize per frame — where
        /// <see cref="AapSmoothing"/> eases in and never quite arrives. Four Direct children:
        /// three remaps write the difference into "output/Delta" (the input added, the output
        /// subtracted) and hold the output at its current value, and a 1D tree over Delta adds
        /// ±1 × stepSize, ramping through 0 inside the last ±<see cref="StepRamp"/> so the
        /// output settles instead of jittering around the target.
        ///
        /// Those ±0.1 thresholds stay in parameter units at any range: the three remaps are
        /// identities, so the range only decides how far the values may travel, never their
        /// scale. Driving stepSize from a <see cref="FrameTime"/> gadget makes the speed
        /// independent of the frame rate.
        ///
        /// As in <see cref="SubRanged"/>, subtracting by reversing a remap is exact only for a
        /// symmetric range; an asymmetric one biases Delta by (min + max).
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree SmoothLinear(AnimatorController c, string input, string output,
            string one, string stepSize, float min, float max)
        {
            string delta = output + "/Delta";
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            DbtBuilder.EnsureFloatParameter(c, delta, 0f);

            var step = DbtBuilder.Tree1D(c, Name("Step", output, null), delta);
            step.AddChild(DbtBuilder.ParameterClip(c, output, -1f), -StepRamp);
            step.AddChild(DbtBuilder.ParameterClip(c, output, 0f), 0f);
            step.AddChild(DbtBuilder.ParameterClip(c, output, 1f), StepRamp);

            var tree = DbtBuilder.DirectTree(c, Name("Smooth", input, null));
            DbtBuilder.AddDirectChild(tree, Remap(c, input, delta, min, max, min, max), one);
            DbtBuilder.AddDirectChild(tree, Remap(c, output, delta, max, min, min, max), one);
            DbtBuilder.AddDirectChild(tree, Remap(c, output, output, min, max, min, max), one);
            DbtBuilder.AddDirectChild(tree, step, stepSize);
            return tree;
        }

        // ---- digit separation ---------------------------------------------------

        /// <summary>
        /// Splits a 0..1 input into its first three decimals, each written as its own place
        /// value: Tenths lands on 0, 0.1 … 0.9, Hundredths on 0, 0.01 … 0.09 and Thousandths on
        /// 0, 0.001 … 0.009.
        ///
        /// Every place is quantized through subnormal floats. A 1D tree maps the (offset) input
        /// onto 0 … N × 1.4013e-45, and 1.4013e-45 is the smallest positive float there is: the
        /// product cannot land between two of its multiples, so it snaps to the nearest one —
        /// N + 1 levels, N being 1, 10, 100 and 1000. A second tree divides by the same constant
        /// to read the level back as 0..1. Subtracting the coarser quantization from the finer
        /// one then leaves exactly one digit.
        ///
        /// This is a subnormal-float division trick, and the constants ARE the algorithm: the
        /// offsets turn "round to nearest" into "floor" and place each threshold midway between
        /// two steps of the next place down, which is what makes the differences come out as
        /// clean digits. Rounding them off breaks the gadget.
        ///
        /// Each stage reads the parameter the stage before it writes, so the digits settle a few
        /// frames after the input moves.
        /// </summary>
        public static BlendTree SeparateDigits(AnimatorController c, string input, string output, string one)
        {
            string proxy = output + "/Proxy";
            DbtBuilder.EnsureFloatParameter(c, proxy, 0f);

            string Offset(string place) => output + "/Offset/" + place;
            string Subnormal(string place) => output + "/Subnormal/" + place;
            string Quantized(string place) => output + "/Quantized/" + place;

            var offsets = DbtBuilder.DirectTree(c, "Digit Offsets");
            void AddOffset(string place, float atZero, float atOne)
            {
                DbtBuilder.EnsureFloatParameter(c, Offset(place), 0f);
                DbtBuilder.AddDirectChild(offsets, Remap(c, proxy, Offset(place), atZero, atOne, 0f, 1f), one);
            }
            AddOffset("Ones", -0.49999f, 0.50001f);
            AddOffset("Tenths", -0.044999f, 0.95001f);
            AddOffset("Hundredths", -0.0044999f, 0.99501f);

            // One quantizer: source × step snaps to the multiples of the smallest float there
            // is, then dividing by the same step reads the level back as 0..1.
            BlendTree Quantize(string source, string place, float step, float readBack)
            {
                DbtBuilder.EnsureFloatParameter(c, Subnormal(place), 0f);
                DbtBuilder.EnsureFloatParameter(c, Quantized(place), 0f);
                var stage = DbtBuilder.DirectTree(c, "Quantize " + place);
                DbtBuilder.AddDirectChild(stage,
                    Remap(c, source, Subnormal(place), 0f, step, 0f, 1f), one);
                DbtBuilder.AddDirectChild(stage,
                    Remap(c, Subnormal(place), Quantized(place), 0f, 1f, 0f, readBack), one);
                return stage;
            }

            var levels = DbtBuilder.DirectTree(c, "Digit Levels");
            DbtBuilder.AddDirectChild(levels,
                Quantize(Offset("Ones"), "Ones", 1.4013e-45f, 1.401298e-45f), one);
            DbtBuilder.AddDirectChild(levels,
                Quantize(Offset("Tenths"), "Tenths", 1.4013e-44f, 1.401298e-44f), one);
            DbtBuilder.AddDirectChild(levels,
                Quantize(Offset("Hundredths"), "Hundredths", 1.4013e-43f, 1.401298e-43f), one);
            // The finest place rounds the input to the nearest thousandth, with no offset — it
            // is the one the three coarser floors are measured against.
            DbtBuilder.AddDirectChild(levels,
                Quantize(proxy, "Thousandths", 1.4013e-42f, 1.401298e-42f), one);

            var results = DbtBuilder.DirectTree(c, "Digit Results");
            DbtBuilder.AddDirectChild(results,
                Sub(c, Quantized("Tenths"), Quantized("Ones"), output + "/Tenths"), one);
            DbtBuilder.AddDirectChild(results,
                Sub(c, Quantized("Hundredths"), Quantized("Tenths"), output + "/Hundredths"), one);
            DbtBuilder.AddDirectChild(results,
                Sub(c, Quantized("Thousandths"), Quantized("Hundredths"), output + "/Thousandths"), one);

            var tree = DbtBuilder.DirectTree(c, Name("Digits", input, null));
            // A private copy of the input, clamped to the 0..1 the rest of the gadget assumes.
            DbtBuilder.AddDirectChild(tree, Remap(c, input, proxy, 0f, 1f, 0f, 1f), one);
            DbtBuilder.AddDirectChild(tree, offsets, one);
            DbtBuilder.AddDirectChild(tree, levels, one);
            DbtBuilder.AddDirectChild(tree, results, one);
            return tree;
        }

        // ---- trigonometry -------------------------------------------------------

        /// <summary>Samples per period, and the value tan is held to near its poles.</summary>
        const int TrigSamples = 64;
        const float TangentLimit = 100f;

        /// <summary>
        /// sin / cos / tan of the input, as a curve read by motion time: the state's normalized
        /// time IS the input, so 0..1 walks one whole period (0 to 2π) of a one-second clip.
        /// Nothing here can live in a blend tree — motion time belongs to a state — so these
        /// kinds are a layer and nothing else.
        /// </summary>
        static void BuildTrigonometryLayer(AnimatorController c, Kind kind, string input, string output)
        {
            string label = kind == Kind.Sine ? "sin" : kind == Kind.Cosine ? "cos" : "tan";
            var stateMachine = AddSupportingLayer(c, output + " " + label + "(x)");

            var curve = new AnimationCurve();
            for (int i = 0; i <= TrigSamples; i++)
                curve.AddKey(new Keyframe((float)i / TrigSamples, TrigValue(kind, i)));
            SmoothTangents(curve);

            var clip = DbtBuilder.CurveClip(c,
                DbtBuilder.Sanitize(output) + " = " + label + "(x)", output, curve, TrigSamples);
            var state = AddSupportingState(stateMachine, label + "(x)", new Vector3(300f, 60f, 0f), clip);
            state.timeParameterActive = true;
            state.timeParameter = input;
        }

        static float TrigValue(Kind kind, int sample)
        {
            float angle = 2f * Mathf.PI * sample / TrigSamples;
            if (kind == Kind.Sine) return Mathf.Sin(angle);
            if (kind == Kind.Cosine) return Mathf.Cos(angle);
            // tan runs away at a quarter and three quarters of the period, and with a sample
            // count divisible by four those poles land exactly on a sample — where the float is
            // meaningless (its sign depends on which side of π/2 the angle rounds to). Pin them
            // to +limit: tan climbs on the way in, so the drop to the negative branch then
            // happens at the pole instead of one sample early. The rest is clamped to the band,
            // which is what keeps the curve a finite lookup table at all.
            if (sample * 4 == TrigSamples || sample * 4 == TrigSamples * 3) return TangentLimit;
            return Mathf.Clamp(Mathf.Tan(angle), -TangentLimit, TangentLimit);
        }

        // ---- supporting layers --------------------------------------------------

        /// <summary>Adds a layer at the end of the controller. Last is the point: these layers
        /// write parameters the blend tree writes too, and only a later layer overrides an
        /// earlier one.</summary>
        static AnimatorStateMachine AddSupportingLayer(AnimatorController controller, string name)
        {
            controller.AddLayer(DbtBuilder.UniqueLayerName(controller, name));
            var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1f;
            controller.layers = layers;
            var stateMachine = layers[layers.Length - 1].stateMachine;
            Undo.RegisterCompleteObjectUndo(stateMachine, "DBT Gadget");
            return stateMachine;
        }

        /// <summary>A state on a supporting layer. Write Defaults stays OFF on purpose: with it
        /// on, a state that animates nothing (or animates only part of what the layer touches)
        /// writes default values over whatever the blend tree just computed.</summary>
        static AnimatorState AddSupportingState(AnimatorStateMachine stateMachine, string name,
            Vector3 position, Motion motion)
        {
            var state = stateMachine.AddState(name, position);
            state.writeDefaultValues = false;
            state.motion = motion;
            EditorUtility.SetDirty(state);
            return state;
        }

        static void InstantTransition(AnimatorState from, AnimatorState to,
            AnimatorConditionMode mode, float threshold, string parameter)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(mode, threshold, parameter);
            EditorUtility.SetDirty(transition);
        }

        /// <summary>Auto tangents on every key: these curves stand for smooth functions, and the
        /// flat tangents a bare Keyframe carries would make them ripple between the samples.</summary>
        static void SmoothTangents(AnimationCurve curve)
        {
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Auto);
            }
        }

        /// <summary>Mecanim reads looping from the clip's settings, not from wrapMode.</summary>
        static void MakeLooping(AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        static string Name(string op, string a, string b) =>
            b != null
                ? op + " " + DbtBuilder.Sanitize(a) + ", " + DbtBuilder.Sanitize(b)
                : op + " " + DbtBuilder.Sanitize(a);

        static bool IsFloat(AnimatorController controller, string name)
        {
            var p = DbtBuilder.FindParameter(controller, name);
            return p != null && p.type == AnimatorControllerParameterType.Float;
        }
    }
}
