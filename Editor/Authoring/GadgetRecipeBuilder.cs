using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// A parameter argument of the gadget API: either a handle the recipe declared
    /// (<c>c.FloatParameter("X")</c>) or a plain name. Gadget outputs and the intermediates
    /// they keep are machinery the recipe usually never reads back, and a string spares it a
    /// handle per name; declare one only for the parameters the rest of the recipe uses in
    /// conditions or blend trees.
    /// </summary>
    public readonly struct ParamRef
    {
        public string Name { get; }

        ParamRef(string name) { Name = name; }

        public static implicit operator ParamRef(string name) => new ParamRef(name);

        public static implicit operator ParamRef(FloatParam parameter) =>
            new ParamRef(parameter != null ? parameter.Name : null);

        public override string ToString() => Name ?? string.Empty;
    }

    /// <summary>
    /// DBT (AAP) gadgets from a recipe: the per-frame float math the wizard's "Add" menu
    /// generates — add, multiply, remap, smoothing, division, trigonometry, lookup tables —
    /// declared in code and collected into one Direct blend tree layer. Runs after the
    /// declared layers are applied.
    ///
    ///   c.Gadgets("Math")
    ///    .Multiply(hue, gain, "Hue/Scaled")
    ///    .Smooth("Hue/Scaled", "Hue/Smoothed", "Hue/Smoothing", 0.85f)
    ///    .Buffer(gain, "Gain/Delayed", 2);
    ///
    /// The layer is rebuilt from scratch on every Generate, so a recipe is repeatable: the
    /// named layer, the supporting layers some gadgets bring, and every parameter under a
    /// declared output name are swept first, then the gadgets run in declaration order.
    /// That sweep is also the contract — an output name and everything below it
    /// ("Out", "Out/Shift", "Out/2") belongs to the gadget, so nothing else may live there.
    /// Shared parameters are outside it by design and survive: the constant One, and a
    /// smoothing amount several gadgets read.
    ///
    /// Inputs must be Float parameters that already exist when the post step runs — declare
    /// them in the recipe, or let an earlier gadget in the same chain produce them.
    /// </summary>
    public sealed class GadgetRecipeBuilder
    {
        readonly ControllerBuilder _root;
        readonly string _layerName;
        readonly List<AapGadgets.Request> _requests = new List<AapGadgets.Request>();

        internal GadgetRecipeBuilder(ControllerBuilder root, string layerName)
        {
            _root = root;
            _layerName = string.IsNullOrEmpty(layerName) ? "DBT" : layerName;
            root.PostOps.Add(Run);
        }

        // ---- applying -----------------------------------------------------------

        List<string> Run(AnimatorController controller)
        {
            var warnings = new List<string>();
            if (_requests.Count == 0) return warnings;
            Undo.RegisterCompleteObjectUndo(controller, "Generate Recipe");

            // Whatever the previous Generate left goes first. A gadget refuses to write an
            // output that already exists, so without the sweep the second Generate would
            // report every gadget as a collision — and the layers would stack.
            var layerNames = new List<string> { _layerName };
            foreach (var request in _requests)
                foreach (var name in AapGadgets.SupportingLayerNames(request))
                    if (!layerNames.Contains(name)) layerNames.Add(name);
            RemoveLayers(controller, layerNames);
            RemoveOwnedParameters(controller);

            // The blend tree layer before any supporting layer: a supporting layer covers the
            // part of the range the tree can't compute and overrides what the tree wrote
            // there, which only works while it sits later in the layer list.
            foreach (var request in _requests)
                if (AapGadgets.UsesDbtLayer(request.kind))
                {
                    DbtBuilder.EnsureDirectBlendTreeLayer(controller, -1, _layerName);
                    break;
                }

            foreach (var request in _requests)
            {
                request.controller = controller;
                request.newLayerName = _layerName;
                request.layerIndex = FindLayer(controller, _layerName);

                var error = AapGadgets.Validate(request);
                if (error != null)
                {
                    warnings.Add(L.Tr("DBT gadget '{0}' in layer '{1}': {2}",
                        request.output, _layerName, error));
                    continue;
                }
                // One flush for the whole layer at the end: every gadget mints sub-assets,
                // and committing per gadget would reimport the controller once per call.
                AapGadgets.Apply(request, commitSubAssets: false);
            }
            DbtBuilder.CommitSubAssets(controller);

            // These layers are the recipe's, exactly like the ones it declares: the next
            // Generate rebuilds them by name, and the layer list says so.
            foreach (var name in layerNames)
                if (FindLayer(controller, name) >= 0 && !_root.PostLayers.Contains(name))
                    _root.PostLayers.Add(name);
            return warnings;
        }

        static void RemoveLayers(AnimatorController controller, List<string> names)
        {
            for (int i = controller.layers.Length - 1; i >= 0; i--)
                if (names.Contains(controller.layers[i].name))
                    controller.RemoveLayer(i);
        }

        /// <summary>Drops every parameter the declared gadgets own — the output itself and
        /// the namespace under it, which is where each of them keeps its intermediates
        /// ("Out/Shift", "Out/Clock", "Out/Tenths", "Out/2").</summary>
        void RemoveOwnedParameters(AnimatorController controller)
        {
            var owned = new List<string>();
            foreach (var request in _requests)
                if (!string.IsNullOrEmpty(request.output)) owned.Add(request.output);
            if (owned.Count == 0) return;

            var kept = new List<AnimatorControllerParameter>();
            foreach (var parameter in controller.parameters)
            {
                bool mine = false;
                foreach (var name in owned)
                    if (parameter.name == name
                        || parameter.name.StartsWith(name + "/", StringComparison.Ordinal))
                        mine = true;
                if (!mine) kept.Add(parameter);
            }
            if (kept.Count != controller.parameters.Length)
                controller.parameters = kept.ToArray();
        }

        static int FindLayer(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == name) return i;
            return -1;
        }

        GadgetRecipeBuilder Queue(AapGadgets.Request request, string call)
        {
            _requests.Add(request);
            // Recorded on this builder, so a run of gadget calls comes back out as the one
            // fluent chain the API is written to read as.
            _root.Script?.Call(this, call);
            return this;
        }

        // ---- recording ----------------------------------------------------------

        /// <summary>
        /// The source line for one gadget call. A trailing argument that matches its
        /// parameter's own default is dropped, so an exported call reads like the hand-written
        /// one it stands for — and stays correct, because it is the same default. Trimming
        /// stops at the first argument that differs: C# has no way to skip a positional one.
        /// </summary>
        static string Line(string method, string[] head, params (string arg, string fallback)[] tail)
        {
            int last = tail.Length;
            while (last > 0 && tail[last - 1].arg == tail[last - 1].fallback) last--;
            var args = new List<string>(head);
            for (int i = 0; i < last; i++) args.Add(tail[i].arg);
            return method + "(" + string.Join(", ", args) + ")";
        }

        /// <summary>A parameter argument as source: always a string literal. The implicit
        /// conversion into <see cref="ParamRef"/> has already dropped whichever handle it came
        /// from, and there is nothing to point back at — a name compiles just as well, which is
        /// the whole point of the type.</summary>
        static string P(ParamRef parameter) => RecipeScript.S(parameter.Name);

        // ---- arithmetic ---------------------------------------------------------

        /// <summary>output = A + B. Direct weights can't go negative, so positive inputs
        /// only — <see cref="AddRanged"/> is the signed version.</summary>
        public GadgetRecipeBuilder Add(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Add,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Add", new[] { P(a), P(b), P(output) }));

        /// <summary>output = A + B for signed inputs: both are remapped through the range
        /// first, so values outside min..max clamp.</summary>
        public GadgetRecipeBuilder AddRanged(ParamRef a, ParamRef b, ParamRef output,
            float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.AddRanged,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
                rangeMin = min,
                rangeMax = max,
            }, Line("AddRanged", new[] { P(a), P(b), P(output) },
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

        /// <summary>output = A − B. Positive inputs only; see <see cref="SubRanged"/>.</summary>
        public GadgetRecipeBuilder Sub(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Sub,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Sub", new[] { P(a), P(b), P(output) }));

        /// <summary>output = A − B for signed inputs. The negation is exact only for a
        /// symmetric range (min = −max); an asymmetric one shifts the result by min + max.</summary>
        public GadgetRecipeBuilder SubRanged(ParamRef a, ParamRef b, ParamRef output,
            float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.SubRanged,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
                rangeMin = min,
                rangeMax = max,
            }, Line("SubRanged", new[] { P(a), P(b), P(output) },
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

        /// <summary>output = A × B, via nested Direct trees. Positive inputs only.</summary>
        public GadgetRecipeBuilder Multiply(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Multiply,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Multiply", new[] { P(a), P(b), P(output) }));

        /// <summary>output = 1 / input for positive inputs, all inside the blend tree: exact
        /// above 1, a geometric lookup ladder below it. The shift the exact half computes on
        /// the way costs the result an extra frame of lag.</summary>
        public GadgetRecipeBuilder Reciprocal(ParamRef input, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Reciprocal,
                inputA = input.Name,
                output = output.Name,
            }, Line("Reciprocal", new[] { P(input), P(output) }));

        /// <summary>output = A / B for positive inputs: B's reciprocal, then A times it.
        /// Inherits <see cref="Reciprocal"/>'s extra frame of lag, and one more on top —
        /// the multiply reads last frame's reciprocal.</summary>
        public GadgetRecipeBuilder Divide(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Divide,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Divide", new[] { P(a), P(b), P(output) }));

        /// <summary>Linear remap: input over inMin..inMax → output over outMin..outMax,
        /// clamped outside. A reversed output range inverts the slope.</summary>
        public GadgetRecipeBuilder Remap(ParamRef input, ParamRef output,
            float inMin, float inMax, float outMin, float outMax) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Remap,
                inputA = input.Name,
                output = output.Name,
                inMin = inMin,
                inMax = inMax,
                rangeMin = outMin,
                rangeMax = outMax,
            }, Line("Remap", new[]
            {
                P(input), P(output), RecipeScript.F(inMin), RecipeScript.F(inMax),
                RecipeScript.F(outMin), RecipeScript.F(outMax),
            }));

        // ---- logic (inputs assumed 0..1) ----------------------------------------

        public GadgetRecipeBuilder And(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.And,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("And", new[] { P(a), P(b), P(output) }));

        public GadgetRecipeBuilder Or(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Or,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Or", new[] { P(a), P(b), P(output) }));

        public GadgetRecipeBuilder Not(ParamRef input, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Not,
                inputA = input.Name,
                output = output.Name,
            }, Line("Not", new[] { P(input), P(output) }));

        /// <summary>0 below the threshold, 1 from it up.</summary>
        public GadgetRecipeBuilder FloatAsBool(ParamRef input, ParamRef output,
            float threshold = 0.5f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.FloatAsBool,
                inputA = input.Name,
                output = output.Name,
                threshold = threshold,
            }, Line("FloatAsBool", new[] { P(input), P(output) },
                (RecipeScript.F(threshold), "0.5f")));

        // ---- smoothing and time -------------------------------------------------

        /// <summary>
        /// Exponential smoothing: output = lerp(input, output, smoothing) every frame. The
        /// smoothing amount is a Float of its own (0 follows instantly, →1 crawls) that
        /// several gadgets may share — it lives outside the output's namespace and survives
        /// a regenerate, so a recipe can drive it from a menu.
        /// </summary>
        public GadgetRecipeBuilder Smooth(ParamRef input, ParamRef output, ParamRef smoothing,
            float smoothingDefault = 0.9f, float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Smooth,
                inputA = input.Name,
                output = output.Name,
                smoothing = smoothing.Name,
                smoothingDefault = smoothingDefault,
                rangeMin = min,
                rangeMax = max,
            }, Line("Smooth", new[] { P(input), P(output), P(smoothing) },
                (RecipeScript.F(smoothingDefault), "0.9f"),
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

        /// <summary>Moves the output toward the input at a constant stepSize per frame and
        /// settles there, where <see cref="Smooth"/> eases in and never quite arrives. Drive
        /// stepSize from a <see cref="FrameTime"/> gadget to make the speed frame-rate
        /// independent.</summary>
        public GadgetRecipeBuilder SmoothLinear(ParamRef input, ParamRef output, ParamRef stepSize,
            float stepDefault = 0.05f, float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.SmoothLinear,
                inputA = input.Name,
                output = output.Name,
                smoothing = stepSize.Name,
                smoothingDefault = stepDefault,
                rangeMin = min,
                rangeMax = max,
            }, Line("SmoothLinear", new[] { P(input), P(output), P(stepSize) },
                (RecipeScript.F(stepDefault), "0.05f"),
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

        /// <summary>output = the seconds since the previous frame, from a clock on a
        /// supporting layer. One per controller is the intent — it is a shared stopwatch —
        /// and the frame the clock loops reports one large negative delta.</summary>
        public GadgetRecipeBuilder FrameTime(ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.FrameTime,
                output = output.Name,
            }, Line("FrameTime", new[] { P(output) }));

        /// <summary>
        /// output = the input, exactly N frames late (1–8). Every blend tree stage costs one
        /// frame, so branches of different depth read different frames of the same
        /// parameter — buffer the shallower one to line them up again.
        /// </summary>
        public GadgetRecipeBuilder Buffer(ParamRef input, ParamRef output, int frames = 1,
            float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Buffer,
                inputA = input.Name,
                output = output.Name,
                bufferFrames = frames,
                rangeMin = min,
                rangeMax = max,
            }, Line("Buffer", new[] { P(input), P(output) }, (frames.ToString(), "1"),
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

        // ---- functions ----------------------------------------------------------

        /// <summary>Splits a 0..1 input into its first three decimals, written as
        /// "output/Tenths", "output/Hundredths" and "output/Thousandths" — each as its own
        /// place value (0.4, 0.07, 0.003).</summary>
        public GadgetRecipeBuilder SeparateDigits(ParamRef input, ParamRef outputBase) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.SeparateDigits,
                inputA = input.Name,
                output = outputBase.Name,
            }, Line("SeparateDigits", new[] { P(input), P(outputBase) }));

        /// <summary>sin of the input in turns (0..1 is one whole period), as a 1D lookup
        /// tree inside the blend tree — the period sampled evenly and interpolated straight
        /// in between.</summary>
        public GadgetRecipeBuilder Sine(ParamRef input, ParamRef output) =>
            Trigonometry(AapGadgets.Kind.Sine, input, output);

        /// <summary>cos of the input in turns. See <see cref="Sine"/>.</summary>
        public GadgetRecipeBuilder Cosine(ParamRef input, ParamRef output) =>
            Trigonometry(AapGadgets.Kind.Cosine, input, output);

        /// <summary>tan of the input in turns, clamped near its poles. See <see cref="Sine"/>.</summary>
        public GadgetRecipeBuilder Tangent(ParamRef input, ParamRef output) =>
            Trigonometry(AapGadgets.Kind.Tangent, input, output);

        /// <summary>The three kinds are named after the methods that queue them, so the enum
        /// already spells the call that has to be written down.</summary>
        GadgetRecipeBuilder Trigonometry(AapGadgets.Kind kind, ParamRef input, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = kind,
                inputA = input.Name,
                output = output.Name,
            }, Line(kind.ToString(), new[] { P(input), P(output) }));

        /// <summary>
        /// Bakes a curve into a piecewise-linear lookup table inside the blend tree: the
        /// curve's time axis is the input, its value the output, sampled evenly at
        /// <paramref name="samples"/> points and interpolated straight in between (so a curve
        /// with corners wants a sample on each of them). Inputs outside the curve's time
        /// range clamp. No supporting layer.
        /// </summary>
        public GadgetRecipeBuilder Lut1D(ParamRef input, ParamRef output, AnimationCurve curve,
            int samples = 33)
        {
            // Only a recording run cares: a recipe holding its own curve object keeps the
            // weights, while a curve written out as source can only carry what a Keyframe
            // constructor takes — and the samples it bakes shift a little without them.
            if (_root.Script != null && RecipeScript.HasWeightedTangents(curve))
                _root.Notes.Add(L.Tr("LUT curve for '{0}' uses weighted tangents; the exported curve drops the weights.",
                    output.Name));
            return Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Lut1D,
                inputA = input.Name,
                output = output.Name,
                curve = curve,
                lutSamples = samples,
            }, Line("Lut1D", new[] { P(input), P(output), RecipeScript.Curve(curve) },
                (samples.ToString(), "33")));
        }

        /// <summary>
        /// output = atan2(y, x) in turns: 0 at +X, counter-clockwise to 1, ready to feed
        /// <see cref="Sine"/> / <see cref="Cosine"/>. A ring of <paramref name="directions"/>
        /// children is the table, so accuracy is about 1/N turn; the result collapses toward
        /// 0 near the origin, where there is no direction to read — gate it by magnitude.
        /// </summary>
        public GadgetRecipeBuilder Atan2(ParamRef y, ParamRef x, ParamRef output,
            int directions = 16) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Atan2,
                inputA = y.Name,
                inputB = x.Name,
                output = output.Name,
                atan2Directions = directions,
            }, Line("Atan2", new[] { P(y), P(x), P(output) }, (directions.ToString(), "16")));
    }
}
