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
    ///
    /// **A range is a unit, not a safety margin.** Every gadget that takes a min and a max
    /// carries its values through 1D tables whose two thresholds sit at those ends, so a value
    /// comes back with the precision of the *range* rather than of itself — measured, about one
    /// part in two million of the span, which is coarser than the float's own 24 bits because
    /// the value is recovered as a difference between two numbers the size of the span. Declaring
    /// ±1,000,000 "to be safe" and then passing 0.001 does not give a slightly wrong answer: the
    /// input lands on zero, and the product with it is zero. Declare the range the values are
    /// actually in.
    ///
    /// **Every gadget costs a fixed number of frames** and they add along a chain — see
    /// <see cref="AapGadgets.Latency"/> for the numbers and <see cref="FramesBehind"/> for what
    /// this builder makes of them. Two branches off one signal that reach a gadget at different
    /// depths are handing it two different frames of that signal; <see cref="Buffer"/> on the
    /// shallower one is the fix, and the builder says so when it happens.
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

            // Two kinds of layer are not this step's to take away whole, and for the same
            // reason: something else in the recipe put machinery there. A layer the recipe
            // DECLARES has already been rebuilt from that declaration by the time a post step
            // runs, and a layer holding object gadget trees is shared with the toggle step —
            // removing either would throw away what the other just built, which is exactly the
            // split a shared Direct tree layer is exported as (RecipeExporter.ChildClaims).
            // What this step owns there is its own children, and the saved records name them
            // one at a time, so those are swept and the rest of the tree is left standing.
            var removed = new List<string>();
            foreach (var name in layerNames)
                if (!Declares(name) && !SharedWithObjectGadgets(controller, name))
                    removed.Add(name);

            // Where they sit now, so the rebuild can put them back. Removing a layer and
            // adding it again lands it at the end of the list, and a layer's index is what
            // decides which of two writers to the same property wins — a second Generate
            // that shuffled them would change what the controller does without anyone
            // touching the recipe. The declared layers already do this (BuildLayer moves each
            // one back to the index it was found at); this is the same courtesy for the ones
            // a post step owns.
            var previousIndices = PreviousIndices(controller, removed);
            WarnAboutLayersNobodyGenerated(controller, removed, warnings);
            RemoveLayers(controller, removed);
            foreach (var name in layerNames)
                if (!removed.Contains(name)) RemoveGadgetsIn(controller, name);
            RemoveOwnedParameters(controller);

            // The blend tree layer before any supporting layer: a supporting layer covers the
            // part of the range the tree can't compute and overrides what the tree wrote
            // there, which only works while it sits later in the layer list.
            foreach (var request in _requests)
                if (AapGadgets.UsesDbtLayer(request.kind))
                {
                    // Found by name rather than always created: a layer that survived the sweep
                    // above is the one to add to, and asking for a new one would land beside it
                    // under a numbered name.
                    DbtBuilder.EnsureDirectBlendTreeLayer(controller,
                        FindLayer(controller, _layerName), _layerName);
                    break;
                }

            // Worked out as the chain was written; said out loud here, where a recipe's
            // messages are collected.
            var refused = new HashSet<AapGadgets.Request>();
            foreach (var entry in _skew)
            {
                warnings.Add(entry.Value);
                if (_requireAligned) refused.Add(entry.Key);
            }
            OpenTheLoops(controller, warnings);

            foreach (var request in _requests)
            {
                if (refused.Contains(request)) continue;
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
            RestoreIndices(controller, previousIndices);

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

        /// <summary>Whether the recipe declares a layer by this name, and has therefore already
        /// rebuilt it before this post step ran.</summary>
        bool Declares(string name)
        {
            foreach (var layer in _root.IR.layers)
                if (layer.name == name) return true;
            return false;
        }

        /// <summary>Whether an object gadget's tree hangs in this layer — which makes the layer
        /// shared, and its other children somebody else's to rebuild.</summary>
        static bool SharedWithObjectGadgets(AnimatorController controller, string name)
        {
            var machine = MachineOf(controller, name);
            if (machine == null) return false;
            foreach (var config in GraphFrameData.GetObjectGadgets(controller))
                if (config.layer == machine) return true;
            return false;
        }

        /// <summary>
        /// Sweeps this step's own gadgets out of a layer it is not removing whole. Each saved
        /// record knows the child it hung and the parameters under its output, so taking them
        /// out one at a time leaves everything else in the tree exactly where it was — which is
        /// the whole point of not removing the layer.
        ///
        /// Every DBT gadget recorded in the layer goes, not only the ones this recipe declares:
        /// the step claims its layer, and a gadget left standing there would keep an output name
        /// the next Generate cannot reuse.
        /// </summary>
        static void RemoveGadgetsIn(AnimatorController controller, string name)
        {
            var machine = MachineOf(controller, name);
            if (machine == null) return;
            foreach (var config in GraphFrameData.GetGadgets(controller))
                if (config.layer == machine)
                    AapGadgets.RemoveGadget(controller, config);
        }

        static AnimatorStateMachine MachineOf(AnimatorController controller, string name)
        {
            foreach (var layer in controller.layers)
                if (layer.name == name) return layer.stateMachine;
            return null;
        }

        /// <summary>
        /// Says so before the sweep takes a layer no recipe generated. This step claims its
        /// layer by name, which is the same bargain a declared layer makes — except the name
        /// here has a default, and that default is "DBT", which is exactly what someone would
        /// call a blend tree layer they built by hand. Replacing it is the contract; losing it
        /// without a word is not. A layer this recipe generated on an earlier pass is recorded
        /// as code-owned and says nothing, so the warning only ever fires the first time.
        /// </summary>
        static void WarnAboutLayersNobodyGenerated(AnimatorController controller,
            List<string> names, List<string> warnings)
        {
            // The record lives in a sub-asset of the controller, so a controller that is not
            // on disk has none — and everything would look hand-made, including the layers
            // this step generated a moment ago. Nothing can be said there, so nothing is.
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller))) return;

            var owned = GraphFrameData.GetCodeOwned(controller);
            foreach (var layer in controller.layers)
            {
                if (!names.Contains(layer.name)) continue;
                if (layer.stateMachine != null && owned.ContainsKey(layer.stateMachine)) continue;
                warnings.Add(L.Tr(
                    "Layer '{0}' was already there and no recipe generated it — the DBT gadgets "
                    + "replaced it. Give one of the two another name to keep both.", layer.name));
            }
        }

        /// <summary>The layers among <paramref name="names"/> that already exist, paired with
        /// where they sit, nearest the front first.</summary>
        static List<KeyValuePair<string, int>> PreviousIndices(
            AnimatorController controller, List<string> names)
        {
            var found = new List<KeyValuePair<string, int>>();
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (names.Contains(layers[i].name))
                    found.Add(new KeyValuePair<string, int>(layers[i].name, i));
            return found;
        }

        /// <summary>
        /// Puts the rebuilt layers back at the indices they were found at. Walked front to
        /// back, so each move lands against an arrangement that already has the earlier ones
        /// in place. A layer that did not exist before this run keeps wherever it was added —
        /// there is no old index to honour, and appending is what a new layer does anyway.
        /// </summary>
        static void RestoreIndices(
            AnimatorController controller, List<KeyValuePair<string, int>> previous)
        {
            foreach (var entry in previous)
            {
                int now = FindLayer(controller, entry.Key);
                if (now >= 0) controller.MoveLayer(now, entry.Value);
            }
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
            TrackAges(request);
            // Recorded on this builder, so a run of gadget calls comes back out as the one
            // fluent chain the API is written to read as.
            _root.Script?.Call(this, call);
            return this;
        }

        // ---- frames -------------------------------------------------------------

        /// <summary>The age, in frames, of every parameter the gadgets in this chain produce.
        /// Anything not in here is driven from outside the chain and counts as current.</summary>
        readonly Dictionary<string, int> _ages = new Dictionary<string, int>();

        /// <summary>Gadgets whose inputs were not all of the same age, with what to say about
        /// each. Collected as the chain is written and reported when it runs.</summary>
        readonly List<KeyValuePair<AapGadgets.Request, string>> _skew =
            new List<KeyValuePair<AapGadgets.Request, string>>();

        bool _requireAligned;

        /// <summary>
        /// How many frames behind the chain's inputs the named parameter is.
        ///
        /// This is the number a recipe needs to place a <see cref="Buffer"/>: two branches off
        /// one source that reach a gadget at different ages are handing it different frames of
        /// the same signal, and the gap between their answers here is exactly how many frames
        /// the shallower one has to be delayed by. Zero for anything this chain did not produce
        /// — a parameter driven from outside is always current.
        ///
        /// Available while the recipe is being written, so it can be read, asserted on, or used
        /// to compute the buffer length rather than counted by hand:
        ///
        ///   var gadgets = c.Gadgets("Math").Divide(a, b, "Q").Not(flag, "Off");
        ///   gadgets.Buffer("Off", "Off/Aligned", gadgets.FramesBehind("Q") - gadgets.FramesBehind("Off"));
        ///
        /// An age is a feed-forward idea. Feed a chain back into itself — an integrator, a
        /// filter of your own, an iteration like Newton's — and the parameter no longer holds
        /// information of one age: it holds a little of every frame that has passed. The number
        /// here then describes only how fresh the newest ingredient is, which is why both
        /// smoothings are documented as filters rather than as stages. Generate says so out
        /// loud when a chain feeds back; see <see cref="Run"/>.
        /// </summary>
        public int FramesBehind(ParamRef parameter) =>
            parameter.Name != null && _ages.TryGetValue(parameter.Name, out int age) ? age : 0;

        /// <summary>
        /// Refuses to generate a gadget whose inputs are of different ages, instead of
        /// reporting it and building it anyway.
        ///
        /// Off by default because a difference in age is not always a mistake: gating a computed
        /// flag by a menu toggle mixes a one-frame value with a current one, and for a toggle
        /// nobody can flip within a frame that is a distinction without a difference. Where the
        /// two inputs really are the same signal down two paths, it is always a mistake — and
        /// this is how a recipe says which kind of chain it is.
        /// </summary>
        public GadgetRecipeBuilder RequireAligned()
        {
            _requireAligned = true;
            return this;
        }

        /// <summary>Which of the chain's own inputs each parameter was computed from. A name
        /// nothing here produced is its own source.</summary>
        readonly Dictionary<string, HashSet<string>> _sources = new Dictionary<string, HashSet<string>>();

        HashSet<string> SourcesOf(string name)
        {
            if (name != null && _sources.TryGetValue(name, out var known)) return known;
            var self = new HashSet<string>();
            if (name != null) self.Add(name);
            return self;
        }

        /// <summary>Ages this gadget's outputs, and notes it down when two inputs that came from
        /// the same place arrive at different times.</summary>
        void TrackAges(AapGadgets.Request request)
        {
            string first = null, second = null;
            foreach (var name in ValueInputs(request))
                if (first == null) first = name; else second = name;

            int firstAge = FramesBehind(first), secondAge = second == null ? 0 : FramesBehind(second);
            int oldest = second == null ? firstAge : Mathf.Max(firstAge, secondAge);

            // Two ages only disagree in a way anyone can act on when both inputs are the same
            // signal down two paths of different depth. Inputs that came from different places
            // are allowed to be different ages — a rate multiplied by a frame time is the
            // documented way to build a frame-rate independent step, and holding those two to
            // the same age would flag the very thing the gadgets are for.
            if (second != null && firstAge != secondAge && SourcesOf(first).Overlaps(SourcesOf(second)))
            {
                bool firstIsOlder = firstAge > secondAge;
                string older = firstIsOlder ? first : second, newer = firstIsOlder ? second : first;
                int gap = Mathf.Abs(firstAge - secondAge);
                _skew.Add(new KeyValuePair<AapGadgets.Request, string>(request, L.Tr(
                    "DBT gadget '{0}' in layer '{1}' reads two different frames of the same "
                    + "signal: '{2}' is {3} frame(s) behind and '{4}' is {5}. Buffer the newer "
                    + "one by {6} frame(s) and read the copy — Buffer(\"{4}\", \"{4}/Aligned\", {6}).",
                    request.output, _layerName, older, Mathf.Max(firstAge, secondAge),
                    newer, Mathf.Min(firstAge, secondAge), gap)));
            }

            var roots = new HashSet<string>();
            if (first != null) roots.UnionWith(SourcesOf(first));
            if (second != null) roots.UnionWith(SourcesOf(second));
            RecordAges(request, oldest + AapGadgets.Latency(request), roots);
        }

        /// <summary>
        /// Makes a chain that feeds back into itself buildable, and says that it does.
        ///
        /// Two separate things go wrong when a gadget reads a parameter a later gadget writes.
        ///
        /// It would not build at all. Every output is swept before the chain runs and recreated
        /// by the gadget that owns it, so whichever gadget reads the loop first finds nothing
        /// there and is refused for reading a parameter that does not exist — and a loop always
        /// has one, whichever order it is written in. So those names are created up front, at
        /// zero, and marked on the writing request as not-a-collision. Zero is also the loop's
        /// initial value, which is the other thing a loop needs and a one-way chain does not.
        ///
        /// And the frame counts stop describing it. The ages above come from walking the chain
        /// once from its inputs, adding each gadget's cost to the oldest thing it reads: that is
        /// arithmetic on a one-way flow. A parameter inside a loop does not hold information of
        /// one age at all — it holds a little of every frame that has passed, the way an
        /// exponential smoothing does. So <see cref="FramesBehind"/> and the alignment check
        /// stop applying around the loop, and whether the thing settles becomes a question for
        /// a running animator rather than for arithmetic. Said once per chain: the useful fact
        /// is that the arithmetic no longer holds, and repeating it per edge would bury the
        /// misalignment reports that still do.
        /// </summary>
        void OpenTheLoops(AnimatorController controller, List<string> warnings)
        {
            string reader = null, written = null, writer = null;
            var opened = new Dictionary<AapGadgets.Request, List<string>>();

            for (int i = 0; i < _requests.Count; i++)
                foreach (var input in ValueInputs(_requests[i]))
                    for (int later = i; later < _requests.Count; later++)
                    {
                        if (Array.IndexOf(AapGadgets.OutputParameters(_requests[later]), input) < 0)
                            continue;
                        if (reader == null)
                        {
                            reader = _requests[i].output;
                            written = input;
                            writer = _requests[later].output;
                        }
                        if (!opened.TryGetValue(_requests[later], out var names))
                            opened[_requests[later]] = names = new List<string>();
                        if (!names.Contains(input)) names.Add(input);
                        break;
                    }

            if (reader == null) return;

            foreach (var entry in opened)
            {
                foreach (var name in entry.Value)
                    DbtBuilder.EnsureFloatParameter(controller, name, 0f);
                entry.Key.preCreated = entry.Value.ToArray();
            }

            warnings.Add(L.Tr(
                "DBT gadget '{0}' in layer '{1}' reads '{2}', which '{3}' writes later in the "
                + "same chain — the chain feeds back. It is built, starting from zero, but frame "
                + "counts describe one-way chains: FramesBehind and the alignment check no "
                + "longer apply around the loop. Measure whether it settles instead.",
                reader, _layerName, written, writer));
        }

        /// <summary>The inputs whose values the gadget combines. A smoothing amount is left out
        /// on purpose: it is a coefficient the gadget is tuned by rather than a sample of the
        /// signal it is filtering, and holding it to the input's age would flag every smoothing
        /// driven by a frame-time gadget.</summary>
        static IEnumerable<string> ValueInputs(AapGadgets.Request request)
        {
            if (AapGadgets.NeedsInput(request.kind) && !string.IsNullOrEmpty(request.inputA))
                yield return request.inputA;
            if (AapGadgets.IsBinary(request.kind) && !string.IsNullOrEmpty(request.inputB))
                yield return request.inputB;
        }

        void RecordAges(AapGadgets.Request request, int outputAge, HashSet<string> roots)
        {
            var names = AapGadgets.OutputParameters(request);
            // A buffer's stages are one frame apart and listed in order, so each of them is
            // readable at its own age rather than at the chain's end.
            bool staged = request.kind == AapGadgets.Kind.Buffer && names.Length > 1;
            int start = outputAge - names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                _ages[names[i]] = staged ? start + i + 1 : outputAge;
                _sources[names[i]] = roots;
            }
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

        /// <summary>output = A × B, via nested Direct trees. Positive inputs only — a negative
        /// one is dropped, not multiplied, and the product reads 0. One frame;
        /// <see cref="MultiplySigned"/> is the signed version, at two.</summary>
        public GadgetRecipeBuilder Multiply(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Multiply,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Multiply", new[] { P(a), P(b), P(output) }));

        /// <summary>
        /// output = A × B for signed inputs, in two frames: each input is split into its
        /// positive and negative halves, and the four products of those halves are summed with
        /// the two cross terms negated.
        ///
        /// The range bounds the inputs — outside ±max(|min|, |max|) they clamp — but not the
        /// result, which is exact wherever the inputs reach: 8 × 8 in a ±8 range comes out 64,
        /// not clipped to 8. What the range does decide is the resolution the inputs are read
        /// at; see the class summary, because a range chosen generously is how an operand
        /// silently becomes zero.
        /// </summary>
        public GadgetRecipeBuilder MultiplySigned(ParamRef a, ParamRef b, ParamRef output,
            float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.MultiplySigned,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
                rangeMin = min,
                rangeMax = max,
            }, Line("MultiplySigned", new[] { P(a), P(b), P(output) },
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

        /// <summary>output = 1 / input for positive inputs, all inside the blend tree: exact
        /// above 1, a geometric lookup ladder below it. Two frames — the exact half computes a
        /// shift into a parameter of its own, and the ladder is delayed to match it.</summary>
        public GadgetRecipeBuilder Reciprocal(ParamRef input, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Reciprocal,
                inputA = input.Name,
                output = output.Name,
            }, Line("Reciprocal", new[] { P(input), P(output) }));

        /// <summary>
        /// output = 1 / input for a divisor that stays inside min..max, both positive. Three
        /// frames, and none of <see cref="Reciprocal"/>'s ceiling.
        ///
        /// The plain reciprocal stops at 240 because of the lookup table covering inputs below
        /// 1. Saying where the divisor lives lets this one lift it above 1 first, where the
        /// exact half carries the answer with no table involved — so there is no cap, and the
        /// accuracy is the float's rather than a sampled ladder's ~8e-4. Outside the window the
        /// answer is the reciprocal of the clamped divisor: 1/min below it, 1/max above.
        ///
        /// Prefer this whenever the divisor's range is known, which is most of the time.
        /// </summary>
        public GadgetRecipeBuilder ReciprocalRanged(ParamRef input, ParamRef output,
            float min, float max) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.ReciprocalRanged,
                inputA = input.Name,
                output = output.Name,
                inMin = min,
                inMax = max,
            }, Line("ReciprocalRanged", new[]
            {
                P(input), P(output), RecipeScript.F(min), RecipeScript.F(max),
            }));

        /// <summary>output = A / B for positive inputs: B's reciprocal, then A times it. Three
        /// frames — <see cref="Reciprocal"/>'s two and one for the multiply, with the numerator
        /// held back so both sides of it describe the same frame.</summary>
        public GadgetRecipeBuilder Divide(ParamRef a, ParamRef b, ParamRef output) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Divide,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
            }, Line("Divide", new[] { P(a), P(b), P(output) }));

        /// <summary>output = A / B for a divisor that stays inside min..max, both positive:
        /// <see cref="ReciprocalRanged"/> and a multiply, four frames, with the numerator held
        /// back to meet it. No ceiling and no ladder — the one to reach for when the divisor's
        /// range is known.</summary>
        public GadgetRecipeBuilder DivideRanged(ParamRef a, ParamRef b, ParamRef output,
            float min, float max) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.DivideRanged,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
                inMin = min,
                inMax = max,
            }, Line("DivideRanged", new[]
            {
                P(a), P(b), P(output), RecipeScript.F(min), RecipeScript.F(max),
            }));

        /// <summary>
        /// output = A / B for signed inputs, in four frames: |B| from a 1D tree with a corner at
        /// zero, its reciprocal, and the divisor's sign and the numerator's halves all held back
        /// to meet it.
        ///
        /// Near zero the divisor has no dependable sign, and what the gadget buys there is
        /// continuity rather than accuracy: at exactly 0 the answer is 0, either side of it the
        /// answer keeps the divisor's sign, and it passes through zero to change sign instead of
        /// jumping the whole way across. It does not stay near zero — a hair off, the sign
        /// indicators have barely crossed but the reciprocal is already at its cap, so the
        /// quotient climbs fast. |A| × 240 is the most it can ever be, because the reciprocal's
        /// ladder floors at 1/240.
        /// </summary>
        public GadgetRecipeBuilder DivideSigned(ParamRef a, ParamRef b, ParamRef output,
            float min = -1f, float max = 1f) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.DivideSigned,
                inputA = a.Name,
                inputB = b.Name,
                output = output.Name,
                rangeMin = min,
                rangeMax = max,
            }, Line("DivideSigned", new[] { P(a), P(b), P(output) },
                (RecipeScript.F(min), "-1"), (RecipeScript.F(max), "1")));

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

        /// <summary>
        /// Splits a 0..1 input into its first three decimals, written as "output/Tenths",
        /// "output/Hundredths" and "output/Thousandths" — each as its own place value
        /// (0.4, 0.07, 0.003). Five frames.
        ///
        /// These are the digits of the *fractional* part, and there is no "ones" output to see
        /// the rest in. An input of exactly 1 therefore reads as three zeroes rather than as
        /// 0.9 / 0.09 / 0.009, and so does anything above it, since the input clamps to 0..1
        /// first. That is the same mechanism that stops 1 arriving as 0.999 — every digit is
        /// measured against a quantizer for the ones place — but it means a recipe that has to
        /// tell 1 from 0 needs a comparison of its own.
        /// </summary>
        public GadgetRecipeBuilder SeparateDigits(ParamRef input, ParamRef outputBase) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.SeparateDigits,
                inputA = input.Name,
                output = outputBase.Name,
            }, Line("SeparateDigits", new[] { P(input), P(outputBase) }));

        /// <summary>
        /// sin of the input in turns (0..1 is one whole period), as a 1D lookup tree inside the
        /// blend tree — the period sampled evenly and interpolated straight in between.
        ///
        /// The table holds exactly one period and does not wrap: past 1 turn a 1D tree clamps to
        /// its last child, so 1.25 turns reads as 1.0 turns, not as 0.25, and −3 reads as 0.
        /// An angle that accumulates has to be wrapped into 0..1 before it gets here.
        /// </summary>
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
        /// output = √input over min..max, as a table sampled where a square root needs it — one
        /// frame, and no iteration to wait out.
        ///
        /// The samples are spaced geometrically rather than evenly, because √ turns hardest at
        /// the bottom of its range: an even table of √ over 0..4 is out by about 0.09 near zero.
        /// The window has to start above zero for the same reason, and outside it the answer is
        /// √ of the nearer end.
        /// </summary>
        public GadgetRecipeBuilder Sqrt(ParamRef input, ParamRef output,
            float min, float max, int samples = 33) =>
            Function(AapGadgets.Kind.Sqrt, "Sqrt", input, output, min, max, samples);

        /// <summary>output = 1/√input over min..max. What a normalisation actually wants, and a
        /// frame rather than the three a square root and a reciprocal in a row would cost.</summary>
        public GadgetRecipeBuilder InverseSqrt(ParamRef input, ParamRef output,
            float min, float max, int samples = 33) =>
            Function(AapGadgets.Kind.InverseSqrt, "InverseSqrt", input, output, min, max, samples);

        /// <summary>output = log₂(input) over min..max, both above zero. Base two is the one
        /// <see cref="Exp2"/> undoes; another base is this times a constant, which a
        /// <see cref="Remap"/> does for nothing.</summary>
        public GadgetRecipeBuilder Log2(ParamRef input, ParamRef output,
            float min, float max, int samples = 33) =>
            Function(AapGadgets.Kind.Log2, "Log2", input, output, min, max, samples);

        /// <summary>output = 2^input over min..max, evenly sampled — an exponential's relative
        /// error is flat that way, which is the opposite of what √ and log want.</summary>
        public GadgetRecipeBuilder Exp2(ParamRef input, ParamRef output,
            float min, float max, int samples = 33) =>
            Function(AapGadgets.Kind.Exp2, "Exp2", input, output, min, max, samples);

        GadgetRecipeBuilder Function(AapGadgets.Kind kind, string call, ParamRef input,
            ParamRef output, float min, float max, int samples) =>
            Queue(new AapGadgets.Request
            {
                kind = kind,
                inputA = input.Name,
                output = output.Name,
                inMin = min,
                inMax = max,
                lutSamples = samples,
            }, Line(call, new[]
            {
                P(input), P(output), RecipeScript.F(min), RecipeScript.F(max),
            }, (samples.ToString(), "33")));

        /// <summary>
        /// output = base^exponent, with both of them parameters. Four frames: log₂ of the base,
        /// a signed multiply by the exponent, and exp₂ back.
        ///
        /// A table holds any function of one input, but a power of two runtime values is a
        /// surface and no 1D tree holds a surface — which is why this one is assembled rather
        /// than sampled. <paramref name="min"/> and <paramref name="max"/> are the base's window
        /// (above zero, since log₂ is); <paramref name="expMin"/> and <paramref name="expMax"/>
        /// bound the exponent. The window the intermediate exponential is sampled over follows
        /// from those two, so it is not yours to get wrong.
        /// </summary>
        public GadgetRecipeBuilder Power(ParamRef b, ParamRef exponent, ParamRef output,
            float min, float max, float expMin, float expMax, int samples = 33) =>
            Queue(new AapGadgets.Request
            {
                kind = AapGadgets.Kind.Power,
                inputA = b.Name,
                inputB = exponent.Name,
                output = output.Name,
                inMin = min,
                inMax = max,
                rangeMin = expMin,
                rangeMax = expMax,
                lutSamples = samples,
            }, Line("Power", new[]
            {
                P(b), P(exponent), P(output),
                RecipeScript.F(min), RecipeScript.F(max),
                RecipeScript.F(expMin), RecipeScript.F(expMax),
            }, (samples.ToString(), "33")));

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
