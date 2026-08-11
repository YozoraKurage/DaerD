using System.Collections.Generic;
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
    /// A second family gets there by interpolation instead of arithmetic: a tree's own
    /// blending IS a lookup table, so sampling a function onto the children's thresholds
    /// (Lut1D, the trigonometric kinds, the sub-1 half of division) or onto a ring of
    /// directions (Atan2) evaluates it without leaving the tree.
    ///
    /// One gadget still needs more than a blend tree child, and adds a layer of its own at the
    /// end of the controller: frame time, whose clock is a clip played against the wall clock
    /// — the one thing a blend tree has no way to read. Reference:
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
            Lut1D,
            Atan2,
            Buffer,
            // Appended, and to be appended to: a saved gadget records its kind as this enum's
            // number, so inserting one anywhere else would rename every gadget after it in
            // every controller already built.
            MultiplySigned,
            DivideSigned,
            ReciprocalRanged,
            DivideRanged,
            Sqrt,
            InverseSqrt,
            Log2,
            Exp2,
            Power,
        }

        // Must stay in AapGadgets.Kind order.
        public static readonly string[] KindLabels =
        {
            "Smooth", "Add", "Add (Ranged)", "Sub", "Sub (Ranged)", "Multiply",
            "And", "Or", "Not", "Float As Bool", "Remap",
            "Reciprocal", "Divide", "Frame Time", "Smooth (Linear)", "Separate Digits",
            "Sine", "Cosine", "Tangent", "LUT (Curve)", "Atan2", "Buffer (Delay)",
            "Multiply (Signed)", "Divide (Signed)",
            "Reciprocal (Ranged)", "Divide (Ranged)",
            "Square Root", "Inverse Square Root", "Log2", "Exp2", "Power",
        };

        /// <summary>Kinds that are a fixed function sampled into a table, and so take the window
        /// they are sampled over and the number of samples to spend on it.</summary>
        public static bool IsFunctionTable(Kind kind) =>
            kind == Kind.Sqrt || kind == Kind.InverseSqrt
            || kind == Kind.Log2 || kind == Kind.Exp2;

        /// <summary>Kinds that read <see cref="Request.lutSamples"/>.</summary>
        public static bool UsesSamples(Kind kind) =>
            kind == Kind.Lut1D || IsFunctionTable(kind) || kind == Kind.Power;

        /// <summary>Function tables whose window has to stay above zero, because the function
        /// does — and because their samples are spaced geometrically, which needs a ratio.</summary>
        public static bool NeedsPositiveWindow(Kind kind) =>
            kind == Kind.ReciprocalRanged || kind == Kind.DivideRanged
            || kind == Kind.Sqrt || kind == Kind.InverseSqrt || kind == Kind.Log2
            || kind == Kind.Power;

        public static bool IsBinary(Kind kind) =>
            kind == Kind.Add || kind == Kind.AddRanged || kind == Kind.Sub
            || kind == Kind.SubRanged || kind == Kind.Multiply
            || kind == Kind.And || kind == Kind.Or || kind == Kind.Divide
            || kind == Kind.Atan2
            || kind == Kind.MultiplySigned || kind == Kind.DivideSigned
            || kind == Kind.DivideRanged || kind == Kind.Power;

        /// <summary>Kinds that take an *input* range as well as (or instead of) an output one.
        /// Remap maps from it; the ranged reciprocal and divide use it to say where the divisor
        /// lives, which is what lets them skip the lookup ladder altogether.</summary>
        public static bool UsesInputRange(Kind kind) =>
            kind == Kind.Remap || kind == Kind.ReciprocalRanged || kind == Kind.DivideRanged
            || IsFunctionTable(kind) || kind == Kind.Power;

        public static bool UsesRange(Kind kind) =>
            kind == Kind.Smooth || kind == Kind.AddRanged || kind == Kind.SubRanged
            || kind == Kind.Remap || kind == Kind.SmoothLinear || kind == Kind.Buffer
            || kind == Kind.MultiplySigned || kind == Kind.DivideSigned
            // The exponent's range, which is what the intermediate product is sized from.
            || kind == Kind.Power;

        /// <summary>Kinds that take a second, shareable Float setting how fast they follow.</summary>
        public static bool UsesSmoothing(Kind kind) => kind == Kind.Smooth || kind == Kind.SmoothLinear;

        /// <summary>FrameTime reads the animator's own clock, so it has no input parameter.</summary>
        public static bool NeedsInput(Kind kind) => kind != Kind.FrameTime;

        /// <summary>Kinds that compute inside a Direct blend tree, and so want a target layer
        /// to be added to. Constant since the trigonometric curves became 1D lookup trees:
        /// nothing is a layer and nothing else any more. It stays as a predicate because it is
        /// the question the callers actually ask — the wizard before it offers a layer choice,
        /// a recipe before it creates the layer — and a kind that cannot be expressed as a
        /// tree would be back to answering false.</summary>
        public static bool UsesDbtLayer(Kind kind) => true;

        /// <summary>Kinds that add a layer of their own on top of their blend tree child. Only
        /// the frame clock is left, and its layer has to stay after the blend tree layer to
        /// read as one, which the wizard says out loud.</summary>
        public static bool CreatesSupportingLayer(Kind kind) => kind == Kind.FrameTime;

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
            /// <summary>Lut1D: the function to bake — time axis is the input, value the output.</summary>
            public AnimationCurve curve;
            /// <summary>Lut1D: evenly spaced samples baked into the tree (2..128).</summary>
            public int lutSamples = 33;
            /// <summary>Buffer: how many frames late the copy runs (1..8) — one identity
            /// stage per frame.</summary>
            public int bufferFrames = 1;
            /// <summary>Atan2: directions sampled around the circle (8..64).</summary>
            public int atan2Directions = 16;
            /// <summary>Existing DBT (or empty) layer to add the gadget to, or -1 to create one.</summary>
            public int layerIndex = -1;
            public string newLayerName = "DBT";
            /// <summary>The saved gadget this request regenerates, or null for a new one. It
            /// buys the request two things: the names that gadget owns don't read as taken
            /// during validation, and everything it built is swept just before this one is —
            /// so regenerating lands in the same place instead of beside itself.</summary>
            public GraphFrameData.AapGadgetConfig replaces;
            /// <summary>Output names that already exist because the caller created them up
            /// front, and are therefore not collisions. A chain that feeds back needs this: the
            /// gadget reading the loop runs before the one writing it, so the parameter has to
            /// be there first, and it belongs to the writer all the same.</summary>
            public string[] preCreated;
        }

        /// <summary>
        /// The parameters the gadget writes as its result. One for every kind but
        /// SeparateDigits, which splits the input into three and uses the requested output as
        /// the base name for them. The private parameters a gadget keeps for itself live under
        /// the same names, so checking these keeps them clear too.
        /// </summary>
        public static string[] OutputParameters(Request r)
        {
            if (r.kind == Kind.SeparateDigits)
                return new[] { r.output + "/Tenths", r.output + "/Hundredths", r.output + "/Thousandths" };
            if (r.kind == Kind.Buffer && r.bufferFrames > 1)
            {
                // The chain's intermediate stages are parameters of their own, and a stage
                // name that already belongs to something else corrupts silently — so they go
                // through the same "must be new" gate as the output.
                var names = new string[Mathf.Clamp(r.bufferFrames, MinBufferFrames, MaxBufferFrames)];
                for (int i = 0; i < names.Length - 1; i++)
                    names[i] = BufferStage(r.output, i + 1);
                names[names.Length - 1] = r.output;
                return names;
            }
            return new[] { r.output };
        }

        /// <summary>Intermediate parameter of a buffer chain: "output/1", "output/2", …</summary>
        public static string BufferStage(string output, int stage) => output + "/" + stage;

        /// <summary>
        /// How many frames pass between an input of this gadget changing and its output holding
        /// the answer. Fixed per kind, and the same whatever the values going in — which is not
        /// a coincidence but a thing each gadget is built to guarantee, because a cost that
        /// depended on the data would be a cost nothing could be lined up against.
        ///
        /// A gadget reading its inputs and writing AAP clips costs one frame: Mecanim evaluates
        /// from the values the frame started with and applies the writes at the end. Every frame
        /// beyond that is an intermediate parameter the gadget keeps for itself, because the
        /// stage reading one sees what the previous evaluation wrote.
        ///
        /// Latencies add along a chain, which is what makes them worth stating: two branches off
        /// one input that arrive at different totals are reading different frames of it, and the
        /// difference is the number of frames a <see cref="Kind.Buffer"/> on the shallower branch
        /// has to make up. <c>GadgetRecipeBuilder</c> does that arithmetic for a recipe.
        ///
        /// The two smoothings are filters rather than stages: their output is not their input
        /// delayed but a running function of it, and the number here is only how long the first
        /// response takes. Settling is a matter of the smoothing amount, not of the graph.
        /// </summary>
        public static int Latency(Request r)
        {
            switch (r.kind)
            {
                // The one whose cost is an argument rather than a consequence.
                case Kind.Buffer:
                    return Mathf.Clamp(r.bufferFrames, MinBufferFrames, MaxBufferFrames);
                // The shift the exact core computes, and the delayed copy the ladder reads to
                // stay level with it.
                case Kind.Reciprocal:
                    return 2;
                // The four half-copies, then the four products of them.
                case Kind.MultiplySigned:
                    return 2;
                // The lift into the core's half, the shift, and the core.
                case Kind.ReciprocalRanged:
                    return 3;
                // The reciprocal's two, and the multiply that reads it.
                case Kind.Divide:
                    return 3;
                // The ranged reciprocal's three, and the multiply.
                case Kind.DivideRanged:
                    return 4;
                // A log table, a signed multiply, and an exp table.
                case Kind.Power:
                    return 4;
                // The magnitude, its reciprocal's two, and the stage that puts the sign back.
                case Kind.DivideSigned:
                    return 4;
                // The clamped copy, the offsets, the subnormal products, the read-back, and the
                // differences that are the digits.
                case Kind.SeparateDigits:
                    return 5;
                // The step reads a difference this gadget wrote last frame, so the constant-speed
                // smoothing takes one frame longer than the exponential one to answer at all.
                case Kind.SmoothLinear:
                    return 2;
                default:
                    return 1;
            }
        }

        /// <summary>
        /// The layers this request has to claim, under the exact names the builders give them.
        /// Anything that regenerates — a recipe rebuilds its gadget layer on every Generate —
        /// needs them by name: the builders take a free name when one is taken, so a leftover
        /// copy wouldn't be replaced but joined by a numbered twin still writing the same
        /// output.
        ///
        /// Only FrameTime still builds one. Reciprocal, Divide and the trigonometric kinds
        /// carried a motion-time layer each in earlier versions and now compute inside the
        /// blend tree; their old names stay listed here so that regenerating a recipe — or the
        /// caller sweeping before it applies a gadget — reclaims the layer such a controller is
        /// still carrying instead of stranding it beside the new tree, both writing the same
        /// output. Removing a layer that isn't there is a no-op, so listing costs nothing.
        /// </summary>
        public static string[] SupportingLayerNames(Request r)
        {
            switch (r.kind)
            {
                case Kind.Reciprocal: return new[] { ReciprocalLayerName(r.output) };
                case Kind.Divide: return new[] { ReciprocalLayerName(InverseParameter(r.output)) };
                case Kind.FrameTime: return new[] { ClockLayerName(r.output) };
                case Kind.Sine:
                case Kind.Cosine:
                case Kind.Tangent: return new[] { TrigLayerName(r.kind, r.output) };
                default: return new string[0];
            }
        }

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");

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
                if (DbtBuilder.FindParameter(controller, name) != null && !Reclaims(r, name))
                    return L.Tr("A parameter named '{0}' already exists.", name);

            if (UsesSmoothing(r.kind))
            {
                if (string.IsNullOrEmpty(r.smoothing) || r.smoothing == r.inputA || r.smoothing == r.output)
                    return L.Tr("The smoothing parameter needs its own name.");
                var smoothing = DbtBuilder.FindParameter(controller, r.smoothing);
                if (smoothing != null && smoothing.type != AnimatorControllerParameterType.Float)
                    return L.Tr("Parameter '{0}' exists but is not a Float.", r.smoothing);
            }

            // Every kind whose range is a window values travel inside. Remap is the one kind
            // that uses the range without being in this list: its range is a destination, and
            // reversing it is how a recipe asks for an inverted slope.
            if ((r.kind == Kind.Smooth || r.kind == Kind.AddRanged || r.kind == Kind.SubRanged
                || r.kind == Kind.SmoothLinear || r.kind == Kind.Buffer
                || r.kind == Kind.MultiplySigned || r.kind == Kind.DivideSigned
                || r.kind == Kind.Power)
                && !(r.rangeMin < r.rangeMax))
                return L.Tr("Range Min must be smaller than Range Max.");
            if (UsesInputRange(r.kind) && !(r.inMin < r.inMax))
                return L.Tr("Input Min must be smaller than Input Max.");
            // The lift divides by the window's lower end, and the geometrically sampled tables
            // take a ratio across it — both of which need the whole window above zero, which is
            // also where 1/x, 1/√x and log x are defined at all.
            if (NeedsPositiveWindow(r.kind) && !(r.inMin > 0f))
                return L.Tr("The input range must start above zero for this gadget.");
            if (UsesSamples(r.kind) && (r.lutSamples < MinLutSamples || r.lutSamples > MaxLutSamples))
                return L.Tr("Samples must be between 2 and 128.");
            if (r.kind == Kind.Lut1D)
            {
                if (r.curve == null || r.curve.length < 2)
                    return L.Tr("The LUT needs a curve with at least two keys.");
                if (!(r.curve.keys[r.curve.length - 1].time > r.curve.keys[0].time))
                    return L.Tr("The curve's keys must span a time range.");
            }
            if (r.kind == Kind.Atan2
                && (r.atan2Directions < MinAtan2Directions || r.atan2Directions > MaxAtan2Directions))
                return L.Tr("Directions must be between 8 and 64.");
            if (r.kind == Kind.Buffer
                && (r.bufferFrames < MinBufferFrames || r.bufferFrames > MaxBufferFrames))
                return L.Tr("Frames must be between 1 and 8.");

            return DbtBuilder.ValidateLayerChoice(controller, r.layerIndex, r.newLayerName);
        }

        /// <summary>Whether the name is one the gadget being regenerated already owns. Those
        /// parameters exist because the previous run of this very request created them, and
        /// they are swept before the new ones are built — reading them as a collision would
        /// make a gadget impossible to regenerate under its own name.</summary>
        static bool Reclaims(Request r, string name) =>
            (r.replaces != null && r.replaces.Owns(name))
            || (r.preCreated != null && System.Array.IndexOf(r.preCreated, name) >= 0);

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.
        /// Turning <paramref name="commitSubAssets"/> off leaves the flush to the caller: a
        /// batch that applies several gadgets in a row pays one reimport at the end instead
        /// of one per gadget.</summary>
        public static bool Apply(Request r, bool commitSubAssets = true)
        {
            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("DBT Gadget"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "DBT Gadget");

                // Every kind is a blend tree child; the one builder that still wants a layer of
                // its own (FrameTime's clock) adds it on the way. The host is resolved before
                // the sweep below, which may drop layers and shift every index after them.
                string one = DbtBuilder.EnsureConstantOneParameter(controller);
                var root = DbtBuilder.EnsureDirectBlendTreeLayer(controller, r.layerIndex, r.newLayerName);
                // A regenerate clears the old gadget out here: after Validate (so a refused
                // request leaves it standing) and before anything is created, because the sweep
                // takes the whole output namespace with it — including, when the name is
                // unchanged, the parameters this run is about to add.
                if (r.replaces != null) RemoveGadget(controller, r.replaces);

                foreach (var name in OutputParameters(r))
                    DbtBuilder.EnsureFloatParameter(controller, name, OutputDefault(r, controller));
                if (UsesSmoothing(r.kind))
                    DbtBuilder.EnsureFloatParameter(controller, r.smoothing, SmoothingDefault(r));

                var child = Build(r, controller, one);
                DbtBuilder.AddDirectChild(root, child, one);
                SaveConfig(r, DbtBuilder.HostingMachine(controller, root), child);
                EditorUtility.SetDirty(controller);
            }
            // Everything the gadget built is a sub-asset; one flush shows the whole batch.
            if (commitSubAssets) DbtBuilder.CommitSubAssets(controller);
            return true;
        }

        /// <summary>
        /// What the gadget's output parameter rests at before anything drives it. Zero for a
        /// chain, whose output is written from its inputs every frame and so is only ever seen
        /// at its default on the frame the controller loads.
        ///
        /// <see cref="Kind.Smooth"/> is the exception, because its output is also one of its
        /// inputs: the feedback branch reads the value the gadget wrote last frame. Seeding it
        /// with the input's own resting value starts the loop settled — from zero it would
        /// instead be seen easing up to the input on load, which is a real movement and not a
        /// rounding artefact.
        /// </summary>
        static float OutputDefault(Request r, AnimatorController controller)
        {
            if (r.kind != Kind.Smooth) return 0f;
            var source = DbtBuilder.FindParameter(controller, r.inputA);
            return source != null ? source.defaultFloat : 0f;
        }

        /// <summary>
        /// The follow-speed parameter's default. The two smoothing kinds read it on different
        /// scales — a 0..1 blend for the exponential one, parameter units per frame for the
        /// linear one — so all they share is that the value must not run backwards. A step
        /// below zero would drive the output away from its input; the wizard offers no such
        /// value, but a recipe could ask for one.
        /// </summary>
        static float SmoothingDefault(Request r) =>
            r.kind == Kind.Smooth ? Mathf.Clamp01(r.smoothingDefault) : Mathf.Max(0f, r.smoothingDefault);

        // ---- removing a gadget --------------------------------------------------

        /// <summary>
        /// Takes one saved gadget back out: the child it hung off the layer's root tree, the
        /// sub-assets under that child, the parameters in its output namespace, the layers it
        /// brought with it, and the record itself.
        ///
        /// Sub-assets are left unflushed on purpose — <see cref="Apply"/> calls this on the way
        /// to building the replacement and pays for one reimport, not two. A caller that only
        /// deletes finishes with <see cref="DbtBuilder.CommitSubAssets"/>.
        /// </summary>
        public static void RemoveGadget(AnimatorController controller,
            GraphFrameData.AapGadgetConfig config)
        {
            if (controller == null || config == null) return;
            Undo.RegisterCompleteObjectUndo(controller, "Remove DBT Gadget");

            var root = HostRootTree(controller, config.layer);
            if (root != null && config.tree != null)
            {
                var kept = new List<ChildMotion>();
                foreach (var child in root.children)
                    if (child.motion != config.tree) kept.Add(child);
                if (kept.Count != root.children.Length)
                {
                    Undo.RegisterCompleteObjectUndo(root, "Remove DBT Gadget");
                    root.children = kept.ToArray();
                    EditorUtility.SetDirty(root);
                }
            }
            DestroySubtree(config.tree);
            RemoveOwnedParameters(controller, config);
            RemoveSupportingLayers(controller, ToRequest(config, controller));
            GraphFrameData.RemoveGadget(controller, config.output);
            EditorUtility.SetDirty(controller);
        }

        /// <summary>The root Direct tree of the layer a saved gadget lives in, or null when the
        /// layer (or its tree) is already gone — the gadget's own pieces are then unreachable
        /// and only its parameters and record are left to clean up.</summary>
        static BlendTree HostRootTree(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (machine == null) return null;
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != machine) continue;
                foreach (var child in machine.states)
                    if (child.state != null && child.state.motion is BlendTree root
                        && root.blendType == BlendTreeType.Direct)
                        return root;
            }
            return null;
        }

        /// <summary>
        /// Destroys the trees and clips hanging off one gadget's child. Gathered into a set
        /// first because a gadget shares assets with itself: <see cref="AddRanged"/>'s two range
        /// clips hang under both of its 1D trees, and the lookup kinds mint one clip per
        /// distinct value and hand it to every sample that lands on it.
        ///
        /// Across gadgets nothing is shared — <see cref="DbtBuilder.ParameterClip"/> makes a new
        /// clip on every call and no builder passes a tree to a second gadget — so the whole
        /// sub-tree is this gadget's to delete.
        /// </summary>
        static void DestroySubtree(Motion tree)
        {
            if (tree == null) return;
            var doomed = new HashSet<Object>();
            void Collect(Motion motion)
            {
                if (motion == null || !doomed.Add(motion)) return;
                if (motion is BlendTree branch)
                    foreach (var child in branch.children)
                        Collect(child.motion);
            }
            Collect(tree);
            foreach (var asset in doomed)
                Undo.DestroyObjectImmediate(asset);
        }

        /// <summary>Drops the parameters this gadget owns: the output and the namespace under
        /// it. The smoothing amount and the constant One sit outside that namespace on purpose —
        /// they are shared with the other gadgets and have to survive.</summary>
        static void RemoveOwnedParameters(AnimatorController controller,
            GraphFrameData.AapGadgetConfig config)
        {
            var kept = new List<AnimatorControllerParameter>();
            foreach (var parameter in controller.parameters)
                if (!config.Owns(parameter.name)) kept.Add(parameter);
            if (kept.Count != controller.parameters.Length)
                controller.parameters = kept.ToArray();
        }

        /// <summary>Removes the layers this gadget claims by name — FrameTime's clock, and the
        /// layers older versions of the other kinds left behind. Removing one that isn't there
        /// is a no-op, which is what makes listing the legacy names free.</summary>
        static void RemoveSupportingLayers(AnimatorController controller, Request r)
        {
            var claimed = new List<string>(SupportingLayerNames(r));
            if (claimed.Count == 0) return;
            for (int i = controller.layers.Length - 1; i >= 0; i--)
                if (claimed.Contains(controller.layers[i].name))
                    controller.RemoveLayer(i);
        }

        // ---- saved configuration -----------------------------------------------

        /// <summary>
        /// Records what was just built with the controller. Every route into a gadget lands
        /// here — the wizard and <c>GadgetRecipeBuilder</c> alike go through
        /// <see cref="Apply"/> — so a recipe that destroys and rebuilds its gadget layer
        /// re-records every gadget on the way, and the entries heal themselves instead of
        /// going stale.
        ///
        /// An in-memory controller has no asset to keep the holder in, so the record falls on
        /// the floor there; that is the same fate the async-sync config has, for the same
        /// reason, and the gadget itself is built either way.
        /// </summary>
        static void SaveConfig(Request r, AnimatorStateMachine machine, Motion tree)
        {
            if (machine == null || tree == null) return;
            GraphFrameData.SaveGadget(r.controller, ToConfig(r, machine, tree));
        }

        internal static GraphFrameData.AapGadgetConfig ToConfig(Request r,
            AnimatorStateMachine machine, Motion tree) =>
            new GraphFrameData.AapGadgetConfig
            {
                layer = machine,
                tree = tree,
                kind = (int)r.kind,
                inputA = r.inputA,
                inputB = r.inputB,
                output = r.output,
                rangeMin = r.rangeMin,
                rangeMax = r.rangeMax,
                inMin = r.inMin,
                inMax = r.inMax,
                threshold = r.threshold,
                smoothing = r.smoothing,
                smoothingDefault = r.smoothingDefault,
                curve = CopyCurve(r.curve),
                lutSamples = r.lutSamples,
                bufferFrames = r.bufferFrames,
                atan2Directions = r.atan2Directions,
            };

        /// <summary>A saved config back as the request that made it — the wizard prefills its
        /// form from this, and the exporter reads the gadget call out of it. The target layer
        /// is the one the record points at; a record whose layer is gone lands on -1, which
        /// builds a new one.</summary>
        internal static Request ToRequest(GraphFrameData.AapGadgetConfig config,
            AnimatorController controller)
        {
            int layerIndex = LayerIndexOf(controller, config.layer);
            return new Request
            {
                controller = controller,
                kind = (Kind)config.kind,
                inputA = config.inputA,
                inputB = config.inputB,
                output = config.output,
                rangeMin = config.rangeMin,
                rangeMax = config.rangeMax,
                inMin = config.inMin,
                inMax = config.inMax,
                threshold = config.threshold,
                smoothing = config.smoothing,
                smoothingDefault = config.smoothingDefault,
                curve = CopyCurve(config.curve),
                lutSamples = config.lutSamples,
                bufferFrames = config.bufferFrames,
                atan2Directions = config.atan2Directions,
                layerIndex = layerIndex,
                newLayerName = layerIndex >= 0 ? controller.layers[layerIndex].name : "DBT",
            };
        }

        /// <summary>An AnimationCurve is a reference: shared between the request and the saved
        /// record, an edit on either side would rewrite the other. Only the keys travel — the
        /// wrap modes decide nothing, since the LUT samples strictly inside the key span.</summary>
        static AnimationCurve CopyCurve(AnimationCurve curve) =>
            curve == null ? null : new AnimationCurve(curve.keys);

        static int LayerIndexOf(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (controller == null || machine == null) return -1;
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == machine) return i;
            return -1;
        }

        static BlendTree Build(Request r, AnimatorController c, string one)
        {
            string a = r.inputA, b = r.inputB, output = r.output;
            switch (r.kind)
            {
                case Kind.Smooth:
                    return Smooth(c, a, output, r.smoothing, r.rangeMin, r.rangeMax);
                case Kind.Add: return Add(c, a, b, output);
                case Kind.AddRanged: return AddRanged(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.Sub: return Sub(c, a, b, output);
                case Kind.SubRanged: return SubRanged(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.Multiply: return Multiply(c, a, b, output);
                case Kind.MultiplySigned:
                    return MultiplySigned(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.DivideSigned:
                    return DivideSigned(c, a, b, output, one, r.rangeMin, r.rangeMax);
                case Kind.And: return And(c, a, b, output);
                case Kind.Or: return Or(c, a, b, output);
                case Kind.Not: return Not(c, a, output);
                case Kind.FloatAsBool: return FloatAsBool(c, a, output, r.threshold);
                case Kind.Remap: return Remap(c, a, output, r.rangeMin, r.rangeMax, r.inMin, r.inMax);
                case Kind.Reciprocal: return Reciprocal(c, a, output, one);
                case Kind.ReciprocalRanged:
                    return ReciprocalRanged(c, a, output, one, r.inMin, r.inMax);
                case Kind.Divide: return Divide(c, a, b, output, one);
                case Kind.DivideRanged:
                    return DivideRanged(c, a, b, output, one, r.inMin, r.inMax);
                case Kind.FrameTime: return FrameTime(c, output);
                case Kind.SmoothLinear:
                    return SmoothLinear(c, a, output, one, r.smoothing, r.rangeMin, r.rangeMax);
                case Kind.SeparateDigits: return SeparateDigits(c, a, output, one);
                case Kind.Sine:
                case Kind.Cosine:
                case Kind.Tangent: return Trigonometry(c, r.kind, a, output);
                case Kind.Lut1D: return Lut1D(c, a, output, r.curve, r.lutSamples);
                case Kind.Sqrt: return Sqrt(c, a, output, r.inMin, r.inMax, r.lutSamples);
                case Kind.InverseSqrt:
                    return InverseSqrt(c, a, output, r.inMin, r.inMax, r.lutSamples);
                case Kind.Log2: return Log2(c, a, output, r.inMin, r.inMax, r.lutSamples);
                case Kind.Exp2: return Exp2(c, a, output, r.inMin, r.inMax, r.lutSamples);
                case Kind.Power:
                    return Power(c, a, b, output, one, r.inMin, r.inMax,
                        r.rangeMin, r.rangeMax, r.lutSamples);
                // Input A is the numerator of atan2 and input B the denominator, so the
                // wizard's A/B pair reads in the order the function's arguments do.
                case Kind.Atan2: return Atan2(c, a, b, output, r.atan2Directions);
                case Kind.Buffer:
                    return Buffer(c, a, output, one, r.rangeMin, r.rangeMax, r.bufferFrames);
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

        // ---- signed multiplication and division ---------------------------------

        /// <summary>
        /// The symmetric span a signed gadget works in. Its copies are 1D remaps, and a remap
        /// can only reach values inside the range it was given — so negating over an asymmetric
        /// min..max would land outside it and clamp. Taking the wider end of the range and
        /// working in ±that keeps every input's negation representable, at the cost of a table
        /// slightly wider than asked for, which costs nothing: a 1D tree's accuracy does not
        /// depend on how far apart its two thresholds are.
        /// </summary>
        static float SignedSpan(float min, float max) => Mathf.Max(Mathf.Abs(min), Mathf.Abs(max));

        /// <summary>
        /// Splits one signed input into a copy and a negated copy, both one frame late. Weighing
        /// a Direct child by the copy gives max(x, 0) and by the negated copy max(−x, 0), because
        /// a weight below zero is clamped away — so between them the two carry the whole signed
        /// value as a pair of non-negative weights.
        ///
        /// Both are made, not just the negation: the pair has to be the same age as each other
        /// and as the other input's pair, and a stage reading the live input beside a copy of it
        /// would be reading two different frames.
        /// </summary>
        static void SignedCopies(AnimatorController c, BlendTree parent, string input,
            string positive, string negative, string one, float span)
        {
            DbtBuilder.EnsureFloatParameter(c, positive, 0f);
            DbtBuilder.EnsureFloatParameter(c, negative, 0f);
            DbtBuilder.AddDirectChild(parent, Remap(c, input, positive, -span, span, -span, span), one);
            DbtBuilder.AddDirectChild(parent, Remap(c, input, negative, span, -span, -span, span), one);
        }

        /// <summary>
        /// Nested Direct trees, one per weight, with an "output = sign" clip at the bottom: the
        /// whole thing contributes sign × the product of every weight, since weights multiply on
        /// the way down. Each weight clamps at zero, which is what makes one of these a single
        /// quadrant of a signed product rather than the product itself.
        /// </summary>
        static BlendTree WeightedProduct(AnimatorController c, string output, float sign,
            params string[] weights)
        {
            Motion inner = DbtBuilder.ParameterClip(c, output, sign);
            BlendTree tree = null;
            for (int i = weights.Length - 1; i >= 0; i--)
            {
                tree = DbtBuilder.DirectTree(c, "× " + DbtBuilder.Sanitize(weights[i]));
                DbtBuilder.AddDirectChild(tree, inner, weights[i]);
                inner = tree;
            }
            return tree;
        }

        /// <summary>
        /// output = A × B for signed inputs, in two frames.
        ///
        /// <see cref="Multiply"/> cannot: a Direct weight stops at zero, so a negative operand
        /// is not multiplied by but dropped, and the product reads 0. That clamp is also the way
        /// out, because weighing by x and by −x picks out x's two halves:
        ///
        ///     A·B = A⁺B⁺ + A⁻B⁻ − A⁺B⁻ − A⁻B⁺
        ///
        /// Four nested pairs, two of them writing a −1 clip instead of a +1, summed by the tree
        /// they hang off. The first frame makes the four half-copies; the second reads them, so
        /// the answer is A and B as they were two frames ago — the same two frames whichever
        /// quadrant they are in, which is the point.
        ///
        /// The range bounds the *inputs*: outside ±<see cref="SignedSpan"/> the copies clamp.
        /// The result does not need a range of its own — the clips only ever hold ±1 and the
        /// weights carry the magnitude, so a product of two values at the end of the range comes
        /// out exact rather than clipped to it.
        /// </summary>
        public static BlendTree MultiplySigned(AnimatorController c, string a, string b,
            string output, string one, float min, float max)
        {
            float span = SignedSpan(min, max);
            string aPos = output + "/A", aNeg = output + "/NegA";
            string bPos = output + "/B", bNeg = output + "/NegB";

            var tree = DbtBuilder.DirectTree(c, Name("Mul", a, b) + " (Signed)");
            SignedCopies(c, tree, a, aPos, aNeg, one, span);
            SignedCopies(c, tree, b, bPos, bNeg, one, span);

            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, 1f, aPos, bPos), one);
            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, 1f, aNeg, bNeg), one);
            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, -1f, aPos, bNeg), one);
            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, -1f, aNeg, bPos), one);
            return tree;
        }

        /// <summary>How far from zero a divisor has to be before it counts as having a sign, as
        /// a fraction of the span. Inside it the two indicators cross and the quotient fades
        /// through zero rather than jumping between ±240.</summary>
        const float SignDeadZone = 1e-3f;

        /// <summary>
        /// output = A / B for signed inputs, in four frames.
        ///
        ///     A / B = (A⁺ − A⁻) × (1 / |B|) × (sign⁺ − sign⁻)
        ///
        /// The magnitude is free: a 1D tree with a corner at zero *is* the absolute value, one
        /// frame like any other table. Its reciprocal is <see cref="Reciprocal"/>'s two on top of
        /// that, so it lands at three — and the numerator's halves and the divisor's sign are
        /// walked through two identity remaps each so that they land at three as well. The last
        /// stage weighs four nested products and is therefore the fourth frame, reading one
        /// moment of A and B rather than three different ones.
        ///
        /// What <see cref="SignDeadZone"/> buys near zero is continuity, not accuracy. The two
        /// indicators cross inside it, so their difference runs from −1 through 0 to +1 instead
        /// of stepping: at a divisor of exactly 0 the answer is exactly 0, and either side of it
        /// the answer keeps the divisor's sign and changes sign by passing through zero rather
        /// than jumping the width of the cap. It does not stay *near* zero — a hair off, the
        /// difference is still small but the reciprocal is already pinned at its ceiling, and the
        /// product climbs quickly. |A| × 240 bounds it, because the ladder floors at 1/240.
        /// </summary>
        public static BlendTree DivideSigned(AnimatorController c, string a, string b,
            string output, string one, float min, float max)
        {
            float span = SignedSpan(min, max), edge = span * SignDeadZone;
            string magnitude = output + "/Abs", inverse = InverseParameter(output);
            string aWait = output + "/A/1", aHeld = output + "/A/2";
            string bWait = output + "/B/1", bHeld = output + "/B/2";
            string aPos = output + "/A", aNeg = output + "/NegA";
            string signPos = output + "/Sign", signNeg = output + "/NegSign";

            foreach (var name in new[] { magnitude, aWait, aHeld, bWait, bHeld, signPos, signNeg })
                DbtBuilder.EnsureFloatParameter(c, name, 0f);

            var tree = DbtBuilder.DirectTree(c, Name("Div", a, b) + " (Signed)");

            // |B|, and its reciprocal two frames after that.
            var abs = DbtBuilder.Tree1D(c, Name("Abs", b, null), b);
            abs.AddChild(DbtBuilder.ParameterClip(c, magnitude, span), -span);
            abs.AddChild(DbtBuilder.ParameterClip(c, magnitude, 0f), 0f);
            abs.AddChild(DbtBuilder.ParameterClip(c, magnitude, span), span);
            DbtBuilder.AddDirectChild(tree, abs, one);
            DbtBuilder.AddDirectChild(tree, Reciprocal(c, magnitude, inverse, one), one);

            // Both inputs wait out those three frames before they are split.
            DbtBuilder.AddDirectChild(tree, Remap(c, a, aWait, -span, span, -span, span), one);
            DbtBuilder.AddDirectChild(tree, Remap(c, aWait, aHeld, -span, span, -span, span), one);
            SignedCopies(c, tree, aHeld, aPos, aNeg, one, span);

            DbtBuilder.AddDirectChild(tree, Remap(c, b, bWait, -span, span, -span, span), one);
            DbtBuilder.AddDirectChild(tree, Remap(c, bWait, bHeld, -span, span, -span, span), one);
            DbtBuilder.AddDirectChild(tree, SignIndicator(c, bHeld, signPos, span, edge, false), one);
            DbtBuilder.AddDirectChild(tree, SignIndicator(c, bHeld, signNeg, span, edge, true), one);

            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, 1f, aPos, inverse, signPos), one);
            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, 1f, aNeg, inverse, signNeg), one);
            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, -1f, aPos, inverse, signNeg), one);
            DbtBuilder.AddDirectChild(tree, WeightedProduct(c, output, -1f, aNeg, inverse, signPos), one);
            return tree;
        }

        /// <summary>1 on one side of zero and 0 on the other, with the changeover squeezed into
        /// the dead zone either side of it. A 1D tree cannot step, so the pair of thresholds at
        /// ±edge is what stands in for one.</summary>
        static BlendTree SignIndicator(AnimatorController c, string input, string output,
            float span, float edge, bool negative)
        {
            float low = negative ? 1f : 0f, high = negative ? 0f : 1f;
            var tree = DbtBuilder.Tree1D(c, Name("Sign", input, null), input);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, low), -span);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, low), -edge);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, high), edge);
            tree.AddChild(DbtBuilder.ParameterClip(c, output, high), span);
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

        /// <summary>The smallest input the sub-1 lookup table covers, and how many rungs its
        /// ladder takes to get there. 1/240 caps the output at 240, the same ceiling the curve
        /// this table replaced was drawn to.</summary>
        const float ReciprocalFloor = 1f / 240f;
        const int ReciprocalSteps = 96;

        /// <summary>
        /// output = 1 / input, for positive inputs, in two halves that add up inside one Direct
        /// tree — Direct children animating the same AAP stack, so the tree's sum IS the sum.
        ///
        /// From 1 up the core half is exact: a normalized Direct tree divides every weight by
        /// the weight sum, so a motion that writes nothing weighing (input - 1) next to an
        /// "output = 1" clip weighing 1 leaves the clip at 1 / ((input - 1) + 1). The shift is
        /// computed by a sibling child, which costs the result one extra frame of lag. Below 1
        /// that trick is out of reach — the shift would have to weigh negative, and Direct
        /// weights stop at 0 — so the core holds still at exactly 1 over the whole half.
        ///
        /// Which is what the other half is for: a 1D lookup table holding (1/u - 1), so core
        /// plus table is 1 + (1/u - 1) = 1/u. Its top threshold is input 1 holding 0, and a 1D
        /// tree clamps to its last child above it, so from 1 up the table adds nothing at all
        /// and the exact half stands alone (<see cref="ReciprocalBelowOne"/>).
        ///
        /// One frame of a crossing is wrong: the core reads last frame's shift while the table
        /// reads this frame's input, so for one frame after the input crosses 1 the two halves
        /// are describing different inputs. The layer this replaced handed over with the same
        /// class of artifact.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Reciprocal(AnimatorController c, string input, string output, string one)
        {
            string shift = output + "/Shift", delayed = output + "/Delayed";
            string name = Name("Reciprocal", input, null);
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            DbtBuilder.EnsureFloatParameter(c, shift, 0f);
            DbtBuilder.EnsureFloatParameter(c, delayed, 0f);

            var core = DbtBuilder.DirectTree(c, name + " (Core)");
            DbtBuilder.SetNormalizedBlendValues(core, true);
            DbtBuilder.AddDirectChild(core, DbtBuilder.EmptyClip(c, "Weight Only"), shift);
            DbtBuilder.AddDirectChild(core, DbtBuilder.ParameterClip(c, output, 1f), one);

            var tree = DbtBuilder.DirectTree(c, name);
            DbtBuilder.AddDirectChild(tree, Sub(c, input, one, shift), one);   // shift = input - 1
            // The table reads a copy rather than the input, so that both halves are describing
            // the same frame: the core is a frame behind by construction (it reads the shift a
            // sibling computed), and a table reading the live input would be a frame ahead of
            // it. Same age is what makes the sum a reciprocal at all times instead of only at
            // rest — it is what closes the wrong frame at the crossing — and it is what makes
            // this gadget cost two frames for every input rather than two above 1 and one below.
            DbtBuilder.AddDirectChild(tree, Copy(c, input, delayed), one);
            DbtBuilder.AddDirectChild(tree, core, one);
            DbtBuilder.AddDirectChild(tree, ReciprocalBelowOne(c, delayed, output, name), one);
            return tree;
        }

        /// <summary>
        /// output = input, one frame later, for a non-negative input — a Direct child weighing
        /// an "output = 1" clip by the source, so the weight carries the value and the clip
        /// carries nothing but the unit.
        ///
        /// Where <see cref="Buffer"/> would do the same job with a 1D remap, this needs no range
        /// to interpolate over and so cannot clamp: the callers here are delaying values whose
        /// magnitude is whatever the arithmetic before them produced, and there would be no
        /// honest range to name. The cost is that a negative input arrives as zero, which is
        /// the same bargain every positive-only gadget makes.
        /// </summary>
        static BlendTree Copy(AnimatorController c, string input, string output)
        {
            var tree = DbtBuilder.DirectTree(c, Name("Copy", input, null));
            DbtBuilder.AddDirectChild(tree, DbtBuilder.ParameterClip(c, output, 1f), input);
            return tree;
        }

        /// <summary>
        /// The 0 &lt; x &lt; 1 half of <see cref="Reciprocal"/>, as the amount the core is short
        /// of 1/x there: a 1D table of (1/u - 1), ending at threshold 1 with value 0.
        ///
        /// Its thresholds are a geometric ladder from <see cref="ReciprocalFloor"/> up to 1, not
        /// an even one. Interpolating 1/u straight between u and r×u overshoots it by at most
        /// (√r + 1/√r - 2) *relative*, and that figure depends on the ratio alone — so a ladder
        /// of one ratio is equally accurate on every rung: about 8e-4 for the 240^(1/96) these
        /// use. Even spacing would spend every sample where 1/u is nearly flat and none where it
        /// is a cliff, and be worthless approaching 0. Below the floor the tree clamps, which is
        /// what caps the output at 240.
        /// </summary>
        static BlendTree ReciprocalBelowOne(AnimatorController c, string input, string output, string name)
        {
            var tree = DbtBuilder.Tree1D(c, name + " (Below One)", input);
            // Counting the ladder down puts the thresholds in ascending order, as a 1D tree
            // wants them. Sharing clips by value the way Lut1D does costs nothing and is what
            // the rest of the file does; here every rung is its own value, so it never fires.
            var clips = new Dictionary<float, AnimationClip>();
            for (int k = ReciprocalSteps; k >= 0; k--)
            {
                float u = Mathf.Pow(ReciprocalFloor, (float)k / ReciprocalSteps);
                float value = 1f / u - 1f;
                if (!clips.TryGetValue(value, out var clip))
                    clips[value] = clip = DbtBuilder.ParameterClip(c, output, value);
                tree.AddChild(clip, u);
            }
            return tree;
        }

        /// <summary>
        /// output = 1 / input for a divisor that stays inside [min, max], both positive — with
        /// no lookup ladder in it, and so no ceiling of its own.
        ///
        /// <see cref="Reciprocal"/> stops at 240 because of the table that covers inputs below
        /// 1, and that table is only there because the exact core needs a weight that would have
        /// to go negative down there. The core itself has no ceiling: it divides by a shift a
        /// sibling wrote, which is a float and not a table. So a divisor whose range is known
        /// can be lifted into the core's half and never meet the ladder at all:
        ///
        ///     1 / x = (1 / min) · 1 / (x / min),   and x / min is 1 or more by construction.
        ///
        /// The scaling back is free — the core's own clip carries 1/min instead of 1, so the
        /// division and the rescale are the same write. Three frames: the lift, the shift, the
        /// core. Accuracy is the float's rather than a sampled table's, which also makes this
        /// the reciprocal to iterate against when a recipe wants more digits than the ladder's
        /// ~8e-4.
        ///
        /// Outside the window the lift clamps, and the answer is the reciprocal of the clamped
        /// divisor — 1/min below it and 1/max above — which is the honest reading of "the
        /// divisor was supposed to be in here".
        /// </summary>
        public static BlendTree ReciprocalRanged(AnimatorController c, string input, string output,
            string one, float min, float max)
        {
            string lifted = output + "/Lifted", shift = output + "/Shift";
            string name = Name("Reciprocal", input, null) + " (Ranged)";
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            DbtBuilder.EnsureFloatParameter(c, lifted, 0f);
            DbtBuilder.EnsureFloatParameter(c, shift, 0f);

            var core = DbtBuilder.DirectTree(c, name + " (Core)");
            DbtBuilder.SetNormalizedBlendValues(core, true);
            DbtBuilder.AddDirectChild(core, DbtBuilder.EmptyClip(c, "Weight Only"), shift);
            DbtBuilder.AddDirectChild(core, DbtBuilder.ParameterClip(c, output, 1f / min), one);

            var tree = DbtBuilder.DirectTree(c, name);
            DbtBuilder.AddDirectChild(tree, Remap(c, input, lifted, 1f, max / min, min, max), one);
            DbtBuilder.AddDirectChild(tree, Sub(c, lifted, one, shift), one);
            DbtBuilder.AddDirectChild(tree, core, one);
            return tree;
        }

        /// <summary>output = A / B for a divisor inside [min, max], both positive:
        /// <see cref="ReciprocalRanged"/>'s three frames and one for the multiply, with the
        /// numerator held back the same three so the quotient is of one moment. Four frames, and
        /// none of <see cref="Divide"/>'s ceiling.</summary>
        public static BlendTree DivideRanged(AnimatorController c, string a, string b,
            string output, string one, float min, float max)
        {
            string inverse = InverseParameter(output);
            string first = output + "/Num/1", second = output + "/Num/2", numerator = output + "/Num";
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            foreach (var name in new[] { first, second, numerator })
                DbtBuilder.EnsureFloatParameter(c, name, 0f);

            var tree = DbtBuilder.DirectTree(c, Name("Div", a, b) + " (Ranged)");
            DbtBuilder.AddDirectChild(tree, ReciprocalRanged(c, b, inverse, one, min, max), one);
            DbtBuilder.AddDirectChild(tree, Copy(c, a, first), one);
            DbtBuilder.AddDirectChild(tree, Copy(c, first, second), one);
            DbtBuilder.AddDirectChild(tree, Copy(c, second, numerator), one);
            DbtBuilder.AddDirectChild(tree, Multiply(c, numerator, inverse, output), one);
            return tree;
        }

        /// <summary>The layer <see cref="Reciprocal"/> used to be half of. Kept for reclaiming
        /// one from a controller an older version built — see <see cref="SupportingLayerNames"/>.
        /// </summary>
        static string ReciprocalLayerName(string output) => output + " 1/x";

        /// <summary>output = A / B, for positive inputs: B's reciprocal into a parameter of its
        /// own, then A × that — three frames, on top of <see cref="Reciprocal"/>'s two.
        ///
        /// The numerator is held back by the same two frames the reciprocal takes. Without that
        /// the multiply would pair this frame's A with a reciprocal of B from two frames ago,
        /// which is a quotient of no particular moment: right once the inputs stop moving, and
        /// wrong every frame they are moving. Delayed, the answer is A / B as they both were
        /// three frames ago.</summary>
        public static BlendTree Divide(AnimatorController c, string a, string b, string output, string one)
        {
            string inverse = InverseParameter(output);
            string stage = output + "/Num/1", numerator = output + "/Num";
            DbtBuilder.EnsureFloatParameter(c, output, 0f);
            DbtBuilder.EnsureFloatParameter(c, stage, 0f);
            DbtBuilder.EnsureFloatParameter(c, numerator, 0f);

            var tree = DbtBuilder.DirectTree(c, Name("Div", a, b));
            DbtBuilder.AddDirectChild(tree, Reciprocal(c, b, inverse, one), one);
            DbtBuilder.AddDirectChild(tree, Copy(c, a, stage), one);
            DbtBuilder.AddDirectChild(tree, Copy(c, stage, numerator), one);
            DbtBuilder.AddDirectChild(tree, Multiply(c, numerator, inverse, output), one);
            return tree;
        }

        /// <summary>Where <see cref="Divide"/> keeps the divisor's reciprocal — and so the
        /// output name the inner gadget's legacy layer was named after.</summary>
        static string InverseParameter(string output) => output + "/Inv";

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

        static string ClockLayerName(string output) => output + " Clock";

        static void BuildClockLayer(AnimatorController c, string output, string clock)
        {
            var stateMachine = AddSupportingLayer(c, ClockLayerName(output));
            // One unit per second. The tangents carry the same slope, so the ramp between the
            // two keys stays exactly linear instead of easing at its ends.
            var curve = new AnimationCurve(
                new Keyframe(0f, 0f, 1f, 1f),
                new Keyframe(ClockSeconds, ClockSeconds, 1f, 1f));
            var clip = DbtBuilder.CurveClip(c, DbtBuilder.Sanitize(clock), clock, curve, 60f);
            MakeLooping(clip);
            AddSupportingState(stateMachine, "Clock", new Vector3(300f, 60f, 0f), clip);
        }

        // ---- smoothing ----------------------------------------------------------

        /// <summary>
        /// Every frame, <c>output = lerp(input, output, smoothing)</c>: a 1D tree over the
        /// smoothing amount cross-fades between a tree that follows the input (at 0) and one
        /// driven by the output itself (at 1). Both leaves are the same pair of AAP clips —
        /// one-key clips animating the output parameter on the Animator — so whichever branch
        /// carries the weight writes the same parameter, and the blend between them is the
        /// smoothing.
        ///
        /// The one gadget whose tree is a loop rather than a chain: the output is both what it
        /// writes and what one of its branches reads. That is also why
        /// <see cref="OutputDefault"/> seeds the output with the input's resting value — read
        /// on the frame it is written, an output starting at 0 would have the gadget ease up
        /// from 0 on load rather than begin settled.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Smooth(AnimatorController c, string input, string output,
            string smoothing, float min, float max)
        {
            // The two AAP leaves, shared by the input and the feedback tree.
            var clipMin = DbtBuilder.ParameterClip(c, output, min);
            var clipMax = DbtBuilder.ParameterClip(c, output, max);

            var inputTree = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(input) + " (Input)", input);
            inputTree.AddChild(clipMin, min);
            inputTree.AddChild(clipMax, max);

            var feedbackTree = DbtBuilder.Tree1D(c, DbtBuilder.Sanitize(output) + " (Feedback)", output);
            feedbackTree.AddChild(clipMin, min);
            feedbackTree.AddChild(clipMax, max);

            var smoothTree = DbtBuilder.Tree1D(c, "Smooth " + DbtBuilder.Sanitize(input), smoothing);
            smoothTree.AddChild(inputTree, 0f);
            smoothTree.AddChild(feedbackTree, 1f);
            return smoothTree;
        }

        /// <summary>Where the step ramp saturates, in parameter units. The convention this
        /// technique is written against; see <see cref="SmoothLinear"/>.</summary>
        const float StepRamp = 0.1f;

        /// <summary>
        /// Moves the output toward the input at a constant speed — stepSize per frame — where
        /// <see cref="Smooth"/> eases in and never quite arrives. Four Direct children:
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

        static string TrigLabel(Kind kind) =>
            kind == Kind.Sine ? "sin" : kind == Kind.Cosine ? "cos" : "tan";

        /// <summary>The layer these kinds used to be. Kept for reclaiming one from a controller
        /// an older version built — see <see cref="SupportingLayerNames"/>.</summary>
        static string TrigLayerName(Kind kind, string output) =>
            output + " " + TrigLabel(kind) + "(x)";

        /// <summary>
        /// sin / cos / tan of the input in turns: 0..1 walks one whole period, 0 to 2π. These
        /// used to be a curve read by a state's motion time, which no blend tree can drive; a
        /// 1D tree gets to the same place without leaving the tree, because blending between
        /// adjacent thresholds IS interpolating a table — children holding f(i/N) at threshold
        /// i/N are the function, sampled. Same trick as <see cref="Lut1D"/>, with the curve
        /// fixed and the sample count chosen for it.
        ///
        /// <see cref="TrigSamples"/> per period leaves sin and cos within ~1.2e-3 of the true
        /// value everywhere, comfortably inside the 1/127 a synced float can carry anyway — and
        /// it is divisible by four, which is what lets <see cref="TrigValue"/> put a sample
        /// exactly on tan's poles instead of near them.
        ///
        /// The table is one period and a 1D tree clamps past its outermost child, so this does
        /// not wrap: 1.25 turns reads as 1 turn and −3 reads as 0. An angle that accumulates has
        /// to be brought back into 0..1 before it arrives.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Trigonometry(AnimatorController c, Kind kind, string input, string output)
        {
            var tree = DbtBuilder.Tree1D(c, Name(TrigLabel(kind), input, null), input);
            // A period revisits its values: sin and cos mirror around every quarter turn, and
            // tan's two poles are pinned to the same limit. Sharing clips by value the way
            // Lut1D does keeps the sub-asset count down to the values that are distinct as
            // floats — mirrored samples only collide when the arithmetic lands on the same bits,
            // which is most but not all of them.
            var clips = new Dictionary<float, AnimationClip>();
            for (int i = 0; i <= TrigSamples; i++)
            {
                float value = TrigValue(kind, i);
                if (!clips.TryGetValue(value, out var clip))
                    clips[value] = clip = DbtBuilder.ParameterClip(c, output, value);
                tree.AddChild(clip, (float)i / TrigSamples);
            }
            return tree;
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
            // which is what keeps the table finite at all.
            if (sample * 4 == TrigSamples || sample * 4 == TrigSamples * 3) return TangentLimit;
            return Mathf.Clamp(Mathf.Tan(angle), -TangentLimit, TangentLimit);
        }

        // ---- lookup tables ------------------------------------------------------

        /// <summary>How many samples <see cref="Lut1D"/> accepts. Two is the smallest tree that
        /// interpolates at all; the ceiling only keeps a slider from filling a controller with
        /// thousands of sub-assets.</summary>
        public const int MinLutSamples = 2;
        public const int MaxLutSamples = 128;

        /// <summary>
        /// Bakes an arbitrary curve into a piecewise-linear lookup table: a 1D tree blends
        /// linearly between adjacent thresholds, so children holding f(t_i) at threshold t_i
        /// evaluate f for any input in between. The whole gadget is one blend tree — no
        /// motion-time state, and so no layer of its own.
        ///
        /// What lands in the tree is the curve *sampled*, not the curve: its Hermite tangents
        /// only decide the values read at the sample points, and between them the tree
        /// interpolates straight. Accuracy is therefore the sample count's business, and a
        /// curve with corners wants a sample on each of them. Inputs outside the curve's time
        /// range clamp to the first and last child, as any 1D tree does.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Lut1D(AnimatorController c, string input, string output,
            AnimationCurve curve, int samples)
        {
            // The wizard can't ask for fewer than two, but a recipe could — and one sample
            // divides by zero below rather than producing a degenerate tree.
            samples = Mathf.Clamp(samples, MinLutSamples, MaxLutSamples);

            var keys = curve.keys;
            float from = keys[0].time, to = keys[keys.Length - 1].time;

            var tree = DbtBuilder.Tree1D(c, Name("Lut", input, null), input);
            // A flat stretch of the curve would otherwise mint one identical clip per sample;
            // sharing them by value keeps the sub-asset count down to the distinct outputs.
            var clips = new Dictionary<float, AnimationClip>();
            for (int i = 0; i < samples; i++)
            {
                float t = Mathf.Lerp(from, to, (float)i / (samples - 1));
                float value = curve.Evaluate(t);
                if (!clips.TryGetValue(value, out var clip))
                    clips[value] = clip = DbtBuilder.ParameterClip(c, output, value);
                tree.AddChild(clip, t);
            }
            return tree;
        }

        // ---- functions of one input ----------------------------------------------

        /// <summary>
        /// A fixed function over a declared window, sampled into a 1D tree — the same trick
        /// <see cref="Lut1D"/> and the trigonometric kinds use, with the curve known in advance
        /// so nobody has to draw it. One frame, like any other table.
        ///
        /// Where the samples go matters more than how many there are. Spaced evenly, a table of
        /// √x or log x spends most of its samples where the function is nearly straight and none
        /// where it turns hardest — √x over 0..4 in 33 even steps is out by 0.09 near zero, which
        /// is not a rounding error. Spaced geometrically, the error of interpolating a power or a
        /// logarithm depends only on the *ratio* between neighbouring samples, so it is the same
        /// on every rung of the ladder, which is the same reasoning
        /// <see cref="ReciprocalBelowOne"/> is built on. Exponentials are the other way round —
        /// their relative error is flat under even spacing — so each kind says which it wants.
        ///
        /// Outside the window a 1D tree clamps, so the answer there is the function's value at
        /// the nearer end. The window is the promise about the input.
        /// </summary>
        static BlendTree FunctionTable(AnimatorController c, string input, string output,
            string label, System.Func<float, float> f, float min, float max, int samples,
            bool geometric)
        {
            samples = Mathf.Clamp(samples, MinLutSamples, MaxLutSamples);
            var tree = DbtBuilder.Tree1D(c, Name(label, input, null), input);
            // Flat stretches would otherwise mint one identical clip per sample.
            var clips = new Dictionary<float, AnimationClip>();
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / (samples - 1);
                float x = geometric ? min * Mathf.Pow(max / min, t) : Mathf.Lerp(min, max, t);
                float value = f(x);
                if (!clips.TryGetValue(value, out var clip))
                    clips[value] = clip = DbtBuilder.ParameterClip(c, output, value);
                tree.AddChild(clip, x);
            }
            return tree;
        }

        /// <summary>output = √input over min..max. Geometrically sampled, because √ turns hardest
        /// at the bottom of its range and an even table would spend nothing there.</summary>
        public static BlendTree Sqrt(AnimatorController c, string input, string output,
            float min, float max, int samples) =>
            FunctionTable(c, input, output, "Sqrt", Mathf.Sqrt, min, max, samples, true);

        /// <summary>output = 1/√input over min..max — the one a normalisation wants, and cheaper
        /// than a square root and a reciprocal in a row (one frame against three).</summary>
        public static BlendTree InverseSqrt(AnimatorController c, string input, string output,
            float min, float max, int samples) =>
            FunctionTable(c, input, output, "InvSqrt", x => 1f / Mathf.Sqrt(x),
                min, max, samples, true);

        /// <summary>output = log₂(input) over min..max, both above zero. Base two because it is
        /// the one <see cref="Exp2"/> undoes, and the pair is what <see cref="Power"/> is made
        /// of; any other base is this times a constant, which a remap does for free.</summary>
        public static BlendTree Log2(AnimatorController c, string input, string output,
            float min, float max, int samples) =>
            FunctionTable(c, input, output, "Log2", x => Mathf.Log(x, 2f),
                min, max, samples, true);

        /// <summary>output = 2^input over min..max. Evenly sampled: the relative error of
        /// interpolating an exponential is the same everywhere under even spacing, which is the
        /// opposite of what the power functions above want.</summary>
        public static BlendTree Exp2(AnimatorController c, string input, string output,
            float min, float max, int samples) =>
            FunctionTable(c, input, output, "Exp2", x => Mathf.Pow(2f, x),
                min, max, samples, false);

        /// <summary>
        /// output = base^exponent, with both of them parameters — which is the reason this is a
        /// gadget and not a table. A table can hold any function of one input; a power of two
        /// runtime values is a surface, and no 1D tree holds a surface.
        ///
        ///     x^y = 2^(y · log₂ x)
        ///
        /// so it is the two tables above with a signed multiply between them: log₂ of the base
        /// (1 frame), times the exponent (2), back through exp₂ (1). Four frames.
        ///
        /// Three windows have to line up and this works them out from two. The base's window is
        /// declared; the exponent's range is declared; the product's range is then the four
        /// corners of the two, and that is what the exp₂ table is sampled over. Getting that
        /// last one wrong would clamp the result rather than compute it, which is exactly the
        /// arithmetic a caller should not have to do by hand.
        /// </summary>
        public static BlendTree Power(AnimatorController c, string b, string e, string output,
            string one, float min, float max, float expMin, float expMax, int samples)
        {
            string log = output + "/Log", held = output + "/Exponent", product = output + "/Exp";
            DbtBuilder.EnsureFloatParameter(c, log, 0f);
            DbtBuilder.EnsureFloatParameter(c, held, 0f);
            DbtBuilder.EnsureFloatParameter(c, product, 0f);

            float lowLog = Mathf.Log(min, 2f), highLog = Mathf.Log(max, 2f);
            float lowProduct = Mathf.Min(
                Mathf.Min(lowLog * expMin, lowLog * expMax),
                Mathf.Min(highLog * expMin, highLog * expMax));
            float highProduct = Mathf.Max(
                Mathf.Max(lowLog * expMin, lowLog * expMax),
                Mathf.Max(highLog * expMin, highLog * expMax));
            // The signed multiply reads both operands through tables spanning ±this, and its
            // own result needs no range at all.
            float span = Mathf.Max(Mathf.Max(Mathf.Abs(lowLog), Mathf.Abs(highLog)),
                Mathf.Max(Mathf.Abs(expMin), Mathf.Abs(expMax)));

            var tree = DbtBuilder.DirectTree(c, Name("Pow", b, e));
            DbtBuilder.AddDirectChild(tree, Log2(c, b, log, min, max, samples), one);
            // The exponent waits out the log table's frame. Without it the multiply would pair a
            // logarithm of the base as it was last frame with an exponent as it is now, and the
            // gadget would cost four frames when the base moved and three when the exponent did
            // — a latency that is a property of which input you touched, which is exactly what
            // nothing downstream can line up against.
            DbtBuilder.AddDirectChild(tree,
                Remap(c, e, held, expMin, expMax, expMin, expMax), one);
            DbtBuilder.AddDirectChild(tree,
                MultiplySigned(c, log, held, product, one, -span, span), one);
            DbtBuilder.AddDirectChild(tree,
                Exp2(c, product, output, lowProduct, highProduct, samples), one);
            return tree;
        }

        /// <summary>How many directions <see cref="Atan2"/> accepts around the circle. Eight is
        /// the coarsest ring that still reads as a circle at all; the ceiling only keeps a
        /// slider from filling a controller with clips.</summary>
        public const int MinAtan2Directions = 8;
        public const int MaxAtan2Directions = 64;

        /// <summary>How far off +X the two halves of the seam sit, in turns.</summary>
        const float Atan2Seam = 0.004f;

        /// <summary>
        /// output = atan2(Y, X), in turns: 0 at +X, counter-clockwise to 1 at +X again. Turns
        /// are the unit the <see cref="Kind.Sine"/> / <see cref="Kind.Cosine"/> gadgets read,
        /// so the result feeds them directly.
        ///
        /// A 2D Freeform Directional tree blends by direction, and near-linearly in the angle
        /// between two neighbouring children, so a ring of children holding their own angle is
        /// an angle lookup table: exact on the sampled directions and interpolated within
        /// ~1/directions turn of them. Accuracy is therefore the direction count's business,
        /// and it costs one clip per direction.
        ///
        /// Two details the caller has to live with. The seam is real — 0 and 1 are the same
        /// direction but not the same number — so +X is covered by two children at ±ε instead
        /// of one, which pins the jump inside a 2ε-wide wedge instead of letting it smear over
        /// a whole wedge between two directions. And the origin child (value 0) is what the
        /// field collapses toward as the vector shrinks, because a direction-blended tree has
        /// no direction to read there: gate the result by the vector's magnitude.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Atan2(AnimatorController c, string y, string x, string output, int directions)
        {
            // The wizard keeps to the range, but a recipe could ask for anything — and a ring
            // of one or two children has no circle left to interpolate around.
            directions = Mathf.Clamp(directions, MinAtan2Directions, MaxAtan2Directions);

            var tree = DbtBuilder.Tree2DFreeformDirectional(c, Name("Atan2", y, x), x, y);

            tree.AddChild(DbtBuilder.ParameterClip(c, output, 0f), Vector2.zero);
            // Children in ascending angle, the +X sample split across the two ends of the range.
            AddDirection(c, tree, output, Atan2Seam);
            for (int k = 1; k < directions; k++)
                AddDirection(c, tree, output, (float)k / directions);
            AddDirection(c, tree, output, 1f - Atan2Seam);
            return tree;
        }

        /// <summary>One ring child: the direction <paramref name="turn"/> points in, holding
        /// that same turn as its value.</summary>
        static void AddDirection(AnimatorController c, BlendTree tree, string output, float turn)
        {
            float angle = 2f * Mathf.PI * turn;
            tree.AddChild(DbtBuilder.ParameterClip(c, output, turn),
                new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)));
        }

        // ---- buffering ----------------------------------------------------------

        /// <summary>How many frames a buffer may span. One is the common case; the ceiling
        /// only keeps a slider from minting stage parameters nobody needs.</summary>
        public const int MinBufferFrames = 1;
        public const int MaxBufferFrames = 8;

        /// <summary>
        /// output = the input, exactly <paramref name="frames"/> frames late. Every parameter
        /// hop inside the blend tree costs one frame — a stage reads what the previous
        /// evaluation wrote — so two branches tapping the same input at different pipeline
        /// depths see different frames of it, and anything comparing or combining them works
        /// on skewed data. A buffer is the alignment tool: a chain of identity remaps, one
        /// per frame, inserted on the shallower branch so both signals arrive at the same age.
        ///
        /// The copy is only faithful inside min..max — a 1D tree clamps outside its outer
        /// thresholds, like every gadget here.
        /// Reference: https://vrc.school/docs/Other/Advanced-BlendTrees
        /// </summary>
        public static BlendTree Buffer(AnimatorController c, string input, string output,
            string one, float min, float max, int frames)
        {
            frames = Mathf.Clamp(frames, MinBufferFrames, MaxBufferFrames);
            if (frames == 1)
                return Remap(c, input, output, min, max, min, max);

            var tree = DbtBuilder.DirectTree(c, Name("Buffer", input, null));
            string from = input;
            for (int i = 0; i < frames; i++)
            {
                string to = i == frames - 1 ? output : BufferStage(output, i + 1);
                DbtBuilder.EnsureFloatParameter(c, to, 0f);
                DbtBuilder.AddDirectChild(tree, Remap(c, from, to, min, max, min, max), one);
                from = to;
            }
            return tree;
        }

        // ---- supporting layers --------------------------------------------------

        /// <summary>Adds a layer at the end of the controller. Last is the point: a supporting
        /// layer feeds the gadget's blend tree and may write over what it computed, and only a
        /// later layer has the last word on a parameter both touch.</summary>
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

        /// <summary>Auto tangents on every key: a curve like this stands for a smooth function,
        /// and the flat tangents a bare Keyframe carries would make it ripple between the
        /// samples. Public — and now only — for the wizard, which draws its curve presets the
        /// same way before handing them to <see cref="Lut1D"/>.</summary>
        public static void SmoothTangents(AnimationCurve curve)
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
