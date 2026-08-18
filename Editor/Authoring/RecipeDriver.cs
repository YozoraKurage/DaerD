using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Engine;
using Yozolab.DaerD.IR;

namespace Yozolab.DaerD.Authoring
{
    // ---- driving the builder ------------------------------------------------

    /// <summary>
    /// One export pass over the IR. Stateful because parameter handles are the recipe's
    /// vocabulary: every condition, driver entry and blend parameter goes through the
    /// typed handle declared up top, shared across layers.
    /// </summary>
    class RecipeDriver
    {
        readonly ControllerBuilder _c;
        readonly ControllerIR _ir;
        readonly List<string> _warnings;
        readonly RecipeFoldPlanner _folds;
        /// <summary>The layers that can be written back as gadget calls (empty for a
        /// controller that saved none).</summary>
        readonly RecipeExporter.GadgetPlan _gadgets;
        /// <summary>The layers that can be written back as one AsyncSync call.</summary>
        readonly RecipeExporter.AsyncSyncPlan _asyncSyncs;
        /// <summary>The layers that can be written back as Objects() toggle calls.</summary>
        readonly RecipeExporter.ObjectPlan _objects;
        /// <summary>Which children of a shared Direct tree layer the calls above rebuild — and
        /// therefore whether the layer is declared as well as called.</summary>
        readonly RecipeExporter.ChildClaims _claims;
        readonly Dictionary<string, ParamHandle> _handles =
            new Dictionary<string, ParamHandle>();
        readonly Dictionary<string, AnimatorControllerParameterType> _types =
            new Dictionary<string, AnimatorControllerParameterType>();

        public RecipeDriver(ControllerBuilder c, ControllerIR ir, List<string> warnings,
            RecipeExporter.GadgetPlan gadgets = null,
            RecipeExporter.AsyncSyncPlan asyncSyncs = null,
            RecipeExporter.ObjectPlan objects = null,
            RecipeExporter.ChildClaims claims = null)
        {
            _c = c;
            _ir = ir;
            _warnings = warnings;
            _folds = new RecipeFoldPlanner(this, c);
            _gadgets = gadgets ?? new RecipeExporter.GadgetPlan();
            _asyncSyncs = asyncSyncs ?? new RecipeExporter.AsyncSyncPlan();
            _objects = objects ?? new RecipeExporter.ObjectPlan();
            _claims = claims ?? new RecipeExporter.ChildClaims();
            foreach (var p in ir.parameters)
                _types[p.name] = p.type;
        }

        public void Run()
        {
            // Parameters first, whatever the layer layout — they're the controller-wide
            // vocabulary, and one handle per line so a long list stays scannable.
            if (_ir.parameters.Count > 0)
                _c.Script.Comment(RecipeExporter.Header("Parameters"));
            foreach (var p in _ir.parameters)
            {
                // A covered gadget's own parameters are created by its call further down; a
                // declaration here would only restate what the next Generate rebuilds anyway.
                if (_gadgets.Owns(p.name)) continue;
                // Likewise the index, channels and request flags a sync setup mints.
                if (_asyncSyncs.Owns(p.name)) continue;
                // And the parameter an object gadget created, which its own call creates again.
                if (_objects.Owns(p.name)) continue;
                _handles[p.name] = Declare(p);
            }

            foreach (var layer in _ir.layers)
            {
                _c.Script.Blank();
                if (layer.machine == null)
                {
                    string source = layer.syncedLayerIndex >= 0
                        && layer.syncedLayerIndex < _ir.layers.Count
                        ? _ir.layers[layer.syncedLayerIndex].name : "?";
                    _c.Script.Comment(RecipeExporter.Header("Synced Layer: " + layer.name + " (mirrors " + source + ")"));
                    SyncedLayer(layer);
                    continue;
                }
                _gadgets.layers.TryGetValue(layer.name, out var gadgets);
                _objects.layers.TryGetValue(layer.name, out var toggles);
                // A layer whose every child has a call is the calls and nothing else. One that
                // still holds children nobody claimed is BOTH: the remainder is declared below
                // as the tree it is, and the calls add their own children back to it on the next
                // Generate (see RecipeExporter.ChildClaims).
                bool remainder = _claims.HasLeftovers(layer.name);
                if ((gadgets != null || toggles != null) && !remainder)
                {
                    _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name
                        + " (" + ClaimLabel(gadgets != null, toggles != null) + ")"));
                    EmitClaims(layer.name, gadgets, toggles);
                    continue;
                }
                if (_asyncSyncs.layers.TryGetValue(layer.name, out var sync))
                {
                    _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name + " (async sync)"));
                    EmitAsyncSync(layer.name, sync);
                    continue;
                }
                if (_asyncSyncs.supporting.Contains(layer.name))
                {
                    _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name));
                    _c.Script.Comment("(regenerated by the async sync layer above)");
                    continue;
                }
                if (_gadgets.supporting.Contains(layer.name))
                {
                    _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name));
                    _c.Script.Comment("(regenerated by the gadget layer above)");
                    continue;
                }
                _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name));
                if (remainder)
                {
                    _c.Script.Comment("The children below are the ones no call accounts for. The"
                        + " gadget calls after them");
                    _c.Script.Comment("add their own back to this same layer, which is why it is"
                        + " declared and called both.");
                }
                // A gadget layer exports as the raw tree it is — accurate, and a wall of
                // children nobody edits by hand. The calls that build this kind of layer
                // are right there in the API, so point at them where the wall starts.
                else if (IsGadgetShapedLayer(layer.machine))
                {
                    _c.Script.Comment("Direct blend tree (DBT gadget) layer. The gadget calls that"
                        + " build this kind of layer");
                    _c.Script.Comment("(c.Gadgets(\"" + layer.name
                        + "\").Multiply / .Remap / .Smooth / .Lut1D …) edit better than the tree below.");
                }

                var lb = _c.Layer(layer.name);
                if (layer.defaultWeight != 1f) lb.WithWeight(layer.defaultWeight);
                if (layer.blending == AnimatorLayerBlendingMode.Additive) lb.Additive();
                if (layer.ikPass) lb.WithIkPass();
                if (layer.mask != null) lb.WithAvatarMask(layer.mask);

                // A layer reads in blocks: state definitions, folded uniform settings,
                // transitions, then layout — positions are the least-edited data, so
                // they live at the bottom instead of noising up every state line.
                var states = new Dictionary<string, StateBuilder>();
                var machines = new Dictionary<string, MachineBuilder>();
                var order = new List<(StateBuilder builder, ControllerIR.State state)>();
                var machineOrder = new List<(MachineBuilder builder, ControllerIR.ChildMachine child)>();
                var scopes = new List<(MachineScope scope, ControllerIR.Machine machine)>();
                var plan = RecipeFoldPlanner.PlanFolds(layer.machine);
                CreateScope(lb, layer.machine, states, machines, order, machineOrder, scopes, plan);
                _folds.EmitFolds(plan, order);

                if (RecipeFoldPlanner.HasWiring(layer.machine, string.Empty))
                {
                    _c.Script.Blank();
                    _c.Script.Comment("transitions");
                }
                WireScope(lb, layer.machine, states, machines);

                _c.Script.Blank();
                _c.Script.Comment("layout");
                _c.Script.BeginPack();
                foreach (var (sb, state) in order)
                    sb.At(state.position.x, state.position.y);
                foreach (var (mb, child) in machineOrder)
                    mb.At(child.position.x, child.position.y);
                foreach (var (scope, machine) in scopes)
                {
                    scope.EntryAt(machine.entryPosition.x, machine.entryPosition.y);
                    scope.ExitAt(machine.exitPosition.x, machine.exitPosition.y);
                    scope.AnyStateAt(machine.anyStatePosition.x, machine.anyStatePosition.y);
                    if (machine.parentPosition != Vector3.zero)
                        scope.ParentAt(machine.parentPosition.x, machine.parentPosition.y);
                }
                _c.Script.EndPack();

                if (!remainder) continue;
                _c.Script.Blank();
                _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name
                    + " (" + ClaimLabel(gadgets != null, toggles != null) + ")"));
                EmitClaims(layer.name, gadgets, toggles);
            }
        }

        /// <summary>
        /// The calls that rebuild one layer's claimed children. Gadgets before toggles, which is
        /// the order the post steps then run in — neither takes a shared layer away from the
        /// other any more, so it is no longer a requirement, but it keeps a mixed layer reading
        /// the way it was built rather than the way the exporter happened to walk it.
        /// </summary>
        void EmitClaims(string layerName, List<AapGadgets.Request> gadgets,
            List<GraphFrameData.ObjectGadgetConfig> toggles)
        {
            if (gadgets != null) EmitGadgets(layerName, gadgets);
            if (toggles != null) EmitObjects(toggles);
        }

        static string ClaimLabel(bool gadgets, bool toggles) =>
            gadgets && toggles ? "DBT gadgets, object gadgets"
            : gadgets ? "DBT gadgets" : "object gadgets";

        /// <summary>
        /// A gadget layer written back as the calls that built it, in the order its root tree
        /// holds them. The calls drive the real builder, so the lines come out recorded by
        /// construction — and the replayed builder carries the same post step the generated
        /// code will run, which is what makes the export verifiable rather than merely printed.
        /// </summary>
        /// <summary>
        /// One sync setup as the call that rebuilds it. Arguments that match the method's own
        /// default are left out entirely — same reasoning as the gadget calls' trailing-argument
        /// trimming, except each of these is its own method, so "left out" means not called.
        ///
        /// Two of the wizard's switches are not in the saved setup and are read off the result
        /// instead: whether the generated parameters reached the store, and whether the states
        /// were given a motion. Both are inferences, and both are only ever wrong in the
        /// direction of restating a default.
        /// </summary>
        void EmitAsyncSync(string layerName, AsyncSyncBuilder.Request r)
        {
            var sync = _c.AsyncSync(r.baseName);
            sync.Targets(r.targets.ToArray());
            // A grid answers what the rates, the splits and the cycle answer, and the builder
            // ignores all three once it has one. Writing them out anyway would be restating
            // inputs that do nothing — and worse than noise: .Rate("A", 2) beside a grid reads
            // as a claim about how often A is sent, which only the grid decides.
            bool grid = r.steps != null && r.steps.Count > 0;
            if (!grid)
                foreach (var target in r.targets)
                {
                    int rate = r.RateOf(target);
                    if (rate > 1) sync.Rate(target, rate);
                }
            var requestable = AsyncSyncBuilder.RequestableTargets(r);
            if (requestable.Count > 0) sync.Requestable(requestable.ToArray());
            if (r.ready) sync.Ready();
            if (r.stale) sync.Stale();
            foreach (var group in AsyncSyncBuilder.EffectiveGroups(r))
                sync.Group(group.name, group.members.ToArray());
            // Before the cycle and the grid, which are the calls it makes legal: a Sends run
            // that repeats a step reads as a mistake until the line above it says otherwise.
            if (r.allowRepeatSteps) sync.AllowRepeats();
            if (grid)
                foreach (var step in r.steps) sync.Sends(step.targets.ToArray());
            if (!grid && r.slotBreaks.Count > 0) sync.Split(r.slotBreaks.ToArray());
            if (!grid && r.scheduleOverride.Count > 0) sync.Schedule(r.scheduleOverride.ToArray());
            if (r.floatChannels != 1) sync.FloatChannels(r.floatChannels);
            if (r.boolChannels != 1) sync.BoolChannels(r.boolChannels);
            if (!Mathf.Approximately(r.stepSeconds, 0.3f)) sync.Step(r.stepSeconds);
            if (r.encoding == AsyncSyncBuilder.IndexEncoding.Int) sync.EncodingInt();
            else if (r.encoding == AsyncSyncBuilder.IndexEncoding.Bool) sync.EncodingBool();
            if (layerName != r.baseName) sync.LayerName(layerName);
            if (!StoreHasGenerated(r)) sync.NoStore();
            if (!StatesHaveMotion(r)) sync.NoEmptyClip();
        }

        /// <summary>Whether the setup's synced parameters are in the store the controller is
        /// associated with. No store at all is not evidence either way — the call would find
        /// none to write to, so it may as well keep its default.</summary>
        static bool StoreHasGenerated(AsyncSyncBuilder.Request r)
        {
            var store = ParameterStore.Of(r.controller);
            if (store == null) return true;
            foreach (var (name, _) in AsyncSyncBuilder.GeneratedParameters(r))
                if (store.Find(name) == null) return false;
            return true;
        }

        static bool StatesHaveMotion(AsyncSyncBuilder.Request r)
        {
            if (r.layerIndex < 0 || r.layerIndex >= r.controller.layers.Length) return true;
            var machine = r.controller.layers[r.layerIndex].stateMachine;
            if (machine == null) return true;
            foreach (var child in machine.states)
                if (child.state != null && child.state.motion == null)
                    return false;
            return true;
        }

        /// <summary>
        /// One layer's object gadgets as the calls that build them.
        ///
        /// What travels is each target's DERIVED path. The record holds a reference into the
        /// pinned prefab (ADR 0044) and a reference is the one thing source code cannot carry,
        /// so the export takes the path the reference resolves to right now — which is also what
        /// makes an exported recipe portable: re-pin it at another prefab and the same paths are
        /// looked up there, with whatever is missing named.
        ///
        /// A binding comes back as the most specific call that describes it, so the ordinary
        /// ones read as themselves and an exotic one still round-trips through
        /// <c>Property</c> rather than being dropped.
        /// </summary>
        void EmitObjects(List<GraphFrameData.ObjectGadgetConfig> configs)
        {
            // The shared layer is named on the call, not left to the default: a replay against a
            // controller that has no record to inherit a layer from would otherwise mint a "DBT"
            // of its own beside the one the export just declared.
            var objects = _c.Objects(_objects.treeLayer);
            foreach (var config in configs)
            {
                var toggle = objects.Toggle(config.name,
                    config.parameter == config.name ? null : config.parameter);
                if ((ToggleBuilder.Mode)config.mode == ToggleBuilder.Mode.DirectBlendTree)
                    toggle.AsTree();
                if (config.defaultOn) toggle.DefaultOn();
                if (config.declare) toggle.Declare();
                EmitClip(toggle, config.onClip, on: true);
                EmitClip(toggle, config.offClip, on: false);

                foreach (var record in config.targets)
                {
                    if (record == null) continue;
                    string path = ObjectGadgets.PathOf(_objects.root, record.target);
                    // The plan refuses a layer whose targets cannot be resolved, so this only
                    // ever holds for a record the plan already turned away.
                    if (path == null) continue;
                    if (record.toggleActive)
                    {
                        if (record.activeWhenOn) toggle.Shows(path);
                        else toggle.Hides(path);
                    }
                    else if (!record.activeWhenOn) toggle.Inverted(path);
                    foreach (var binding in record.bindings)
                        EmitBinding(toggle, path, binding);
                }
            }
        }

        const string ShapePrefix = "blendShape.";

        static void EmitBinding(ObjectToggleBuilder toggle, string path,
            GraphFrameData.BindingRecord binding)
        {
            if (binding == null || string.IsNullOrEmpty(binding.property)) return;
            if (binding.property == "m_Enabled" && binding.offValue == 0f && binding.onValue == 1f)
                toggle.Enables(path, binding.typeName);
            else if (binding.typeName == nameof(SkinnedMeshRenderer)
                && binding.property.StartsWith(ShapePrefix, System.StringComparison.Ordinal))
                toggle.BlendShape(path, binding.property.Substring(ShapePrefix.Length),
                    binding.offValue, binding.onValue);
            else
                toggle.Property(path, binding.typeName, binding.property,
                    binding.offValue, binding.onValue);
        }

        /// <summary>A supplied clip as the asset path that names it. A clip that is not the main
        /// asset at its path cannot be named that way at all — a sub-asset has no path of its
        /// own — so the export says so and leaves that side to be generated rather than writing
        /// a path that would load something else.</summary>
        void EmitClip(ObjectToggleBuilder toggle, GraphFrameData.ClipOutput output, bool on)
        {
            if (output == null || !output.userProvided || output.clip == null) return;
            string path = UnityEditor.AssetDatabase.GetAssetPath(output.clip);
            if (!string.IsNullOrEmpty(path)
                && UnityEditor.AssetDatabase.LoadAssetAtPath<AnimationClip>(path) == output.clip)
            {
                if (on) toggle.OnClip(path);
                else toggle.OffClip(path);
                return;
            }
            _warnings.Add(L.Tr("The clip '{0}' an object gadget writes into is not the main asset of a file, so a recipe cannot name it; that side is exported as a generated clip.",
                output.clip.name));
        }

        void EmitGadgets(string layerName, List<AapGadgets.Request> requests)
        {
            var gadgets = _c.Gadgets(layerName);
            foreach (var r in requests)
                switch (r.kind)
                {
                    case AapGadgets.Kind.Smooth:
                        gadgets.Smooth(r.inputA, r.output, r.smoothing, r.smoothingDefault,
                            r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.Add: gadgets.Add(r.inputA, r.inputB, r.output); break;
                    case AapGadgets.Kind.AddRanged:
                        gadgets.AddRanged(r.inputA, r.inputB, r.output, r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.Sub: gadgets.Sub(r.inputA, r.inputB, r.output); break;
                    case AapGadgets.Kind.SubRanged:
                        gadgets.SubRanged(r.inputA, r.inputB, r.output, r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.Multiply:
                        gadgets.Multiply(r.inputA, r.inputB, r.output);
                        break;
                    case AapGadgets.Kind.MultiplySigned:
                        gadgets.MultiplySigned(r.inputA, r.inputB, r.output, r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.And: gadgets.And(r.inputA, r.inputB, r.output); break;
                    case AapGadgets.Kind.Or: gadgets.Or(r.inputA, r.inputB, r.output); break;
                    case AapGadgets.Kind.Not: gadgets.Not(r.inputA, r.output); break;
                    case AapGadgets.Kind.FloatAsBool:
                        gadgets.FloatAsBool(r.inputA, r.output, r.threshold);
                        break;
                    case AapGadgets.Kind.Remap:
                        gadgets.Remap(r.inputA, r.output, r.inMin, r.inMax, r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.Reciprocal: gadgets.Reciprocal(r.inputA, r.output); break;
                    case AapGadgets.Kind.ReciprocalRanged:
                        gadgets.ReciprocalRanged(r.inputA, r.output, r.inMin, r.inMax);
                        break;
                    case AapGadgets.Kind.DivideRanged:
                        gadgets.DivideRanged(r.inputA, r.inputB, r.output, r.inMin, r.inMax);
                        break;
                    case AapGadgets.Kind.Divide:
                        gadgets.Divide(r.inputA, r.inputB, r.output);
                        break;
                    case AapGadgets.Kind.DivideSigned:
                        gadgets.DivideSigned(r.inputA, r.inputB, r.output, r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.FrameTime: gadgets.FrameTime(r.output); break;
                    case AapGadgets.Kind.SmoothLinear:
                        gadgets.SmoothLinear(r.inputA, r.output, r.smoothing, r.smoothingDefault,
                            r.rangeMin, r.rangeMax);
                        break;
                    case AapGadgets.Kind.SeparateDigits:
                        gadgets.SeparateDigits(r.inputA, r.output);
                        break;
                    case AapGadgets.Kind.Sine: gadgets.Sine(r.inputA, r.output); break;
                    case AapGadgets.Kind.Cosine: gadgets.Cosine(r.inputA, r.output); break;
                    case AapGadgets.Kind.Tangent: gadgets.Tangent(r.inputA, r.output); break;
                    case AapGadgets.Kind.Lut1D:
                        gadgets.Lut1D(r.inputA, r.output, r.curve, r.lutSamples);
                        break;
                    case AapGadgets.Kind.Sqrt:
                        gadgets.Sqrt(r.inputA, r.output, r.inMin, r.inMax, r.lutSamples);
                        break;
                    case AapGadgets.Kind.InverseSqrt:
                        gadgets.InverseSqrt(r.inputA, r.output, r.inMin, r.inMax, r.lutSamples);
                        break;
                    case AapGadgets.Kind.Log2:
                        gadgets.Log2(r.inputA, r.output, r.inMin, r.inMax, r.lutSamples);
                        break;
                    case AapGadgets.Kind.Exp2:
                        gadgets.Exp2(r.inputA, r.output, r.inMin, r.inMax, r.lutSamples);
                        break;
                    case AapGadgets.Kind.Power:
                        gadgets.Power(r.inputA, r.inputB, r.output, r.inMin, r.inMax,
                            r.rangeMin, r.rangeMax, r.lutSamples);
                        break;
                    // Input A is atan2's numerator and input B its denominator, which is the
                    // order the y, x pair the method takes reads in.
                    case AapGadgets.Kind.Atan2:
                        gadgets.Atan2(r.inputA, r.inputB, r.output, r.atan2Directions);
                        break;
                    case AapGadgets.Kind.Buffer:
                        gadgets.Buffer(r.inputA, r.output, r.bufferFrames, r.rangeMin, r.rangeMax);
                        break;
                }
        }

        /// <summary>The shape every DBT gadget layer has: one state, playing a Direct
        /// blend tree, and nothing else in the machine.</summary>
        static bool IsGadgetShapedLayer(ControllerIR.Machine machine)
        {
            if (machine == null || machine.machines.Count > 0 || machine.states.Count != 1)
                return false;
            var tree = machine.states[0].tree;
            return tree != null && tree.type == BlendTreeType.Direct;
        }

        // ---- parameter handles ------------------------------------------------

        /// <summary>A default of zero/false stays unstated: the declaration is then
        /// reference-only and Generate won't stomp a default it doesn't care about.</summary>
        ParamHandle Declare(ControllerIR.Param p)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float:
                    return p.defaultFloat != 0f
                        ? _c.FloatParameter(p.name, p.defaultFloat) : _c.FloatParameter(p.name);
                case AnimatorControllerParameterType.Int:
                    return p.defaultInt != 0
                        ? _c.IntParameter(p.name, p.defaultInt) : _c.IntParameter(p.name);
                case AnimatorControllerParameterType.Bool:
                    return p.defaultBool
                        ? _c.BoolParameter(p.name, true) : _c.BoolParameter(p.name);
                default:
                    return _c.TriggerParameter(p.name);
            }
        }

        /// <summary>The handle for a parameter name — typed by its declaration, or by
        /// <paramref name="guess"/> for names the controller never declared.</summary>
        ParamHandle Handle(string name, AnimatorControllerParameterType guess)
        {
            if (_handles.TryGetValue(name, out var existing)) return existing;
            var type = _types.TryGetValue(name, out var known) ? known : guess;
            ParamHandle made;
            switch (type)
            {
                case AnimatorControllerParameterType.Int: made = _c.IntParameter(name); break;
                case AnimatorControllerParameterType.Bool: made = _c.BoolParameter(name); break;
                case AnimatorControllerParameterType.Trigger: made = _c.TriggerParameter(name); break;
                default: made = _c.FloatParameter(name); break;
            }
            _handles[name] = made;
            return made;
        }

        // A use that needs one specific handle type (a Float blend parameter, a Bool
        // condition) while the declaration says otherwise re-declares under the needed
        // type — the builder then reports the conflict, which is the honest outcome for
        // a controller whose parameter usage disagrees with its parameter table.
        FloatParam FloatOf(string name) =>
            Handle(name, AnimatorControllerParameterType.Float) as FloatParam
                ?? _c.FloatParameter(name);

        BoolParam BoolOf(string name) =>
            Handle(name, AnimatorControllerParameterType.Bool) as BoolParam
                ?? _c.BoolParameter(name);

        IntParam IntOf(string name) =>
            Handle(name, AnimatorControllerParameterType.Int) as IntParam
                ?? _c.IntParameter(name);

        AnimatorControllerParameterType TypeOf(string name) =>
            _types.TryGetValue(name, out var type) ? type : AnimatorControllerParameterType.Float;

        // ---- layers ----------------------------------------------------------

        void SyncedLayer(ControllerIR.Layer layer)
        {
            if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= _ir.layers.Count)
            {
                _warnings.Add(L.Tr("Synced layer '{0}' points outside the exported layers and was skipped — export its source layer too.", layer.name));
                return;
            }
            var lb = _c.SyncedLayer(layer.name, _ir.layers[layer.syncedLayerIndex].name);
            if (layer.defaultWeight != 1f) lb.WithWeight(layer.defaultWeight);
            if (layer.blending == AnimatorLayerBlendingMode.Additive) lb.Additive();
            if (layer.ikPass) lb.WithIkPass();
            if (layer.mask != null) lb.WithAvatarMask(layer.mask);
            if (layer.syncedLayerAffectsTiming) lb.AffectsTiming();
            foreach (var entry in layer.syncedMotions)
                lb.Override(entry.statePath, entry.motion);
            foreach (var entry in layer.syncedBehaviours)
                foreach (var behaviour in entry.behaviours)
                    lb.OverrideBehaviourJson(entry.statePath, behaviour.typeName, behaviour.json);
        }

        void CreateScope(MachineScope scope, ControllerIR.Machine machine,
            Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines,
            List<(StateBuilder builder, ControllerIR.State state)> order,
            List<(MachineBuilder builder, ControllerIR.ChildMachine child)> machineOrder,
            List<(MachineScope scope, ControllerIR.Machine machine)> scopes, RecipeFoldPlanner.FoldPlan plan)
        {
            scopes.Add((scope, machine));
            // Machine-level behaviours before the states, where they read as belonging to
            // the machine rather than to whatever state happens to be emitted last.
            foreach (var behaviour in machine.behaviours)
                scope.BehaviourJson(behaviour.typeName, behaviour.json);

            foreach (var state in machine.states)
            {
                // A blend tree must exist as a variable before the state can reference it.
                TreeBuilder tree = state.tree != null ? EmitTree(state.tree) : null;

                var sb = scope.NewState(state.name);
                states[sb.Path] = sb;
                order.Add((sb, state));
                if (tree != null) sb.WithAnimation(tree);
                else if (state.motionAsset != null && !plan.animDeferred.Contains(state))
                    sb.WithAnimation(state.motionAsset);

                if (state.speed != 1f) sb.WithSpeedSetTo(state.speed);
                if (state.cycleOffset != 0f) sb.WithCycleOffsetSetTo(state.cycleOffset);
                if (state.mirror) sb.WithMirrorSetTo(true);
                if (state.ikOnFeet) sb.WithFootIkSetTo(true);
                if (!state.writeDefaultValues && !plan.wdDeferred.Contains(state))
                    sb.WithWriteDefaultsSetTo(false);
                if (!string.IsNullOrEmpty(state.tag)) sb.WithTag(state.tag);
                if (state.speedParameterActive) sb.WithSpeed(FloatOf(state.speedParameter));
                if (state.mirrorParameterActive) sb.WithMirror(BoolOf(state.mirrorParameter));
                if (state.cycleOffsetParameterActive)
                    sb.WithCycleOffset(FloatOf(state.cycleOffsetParameter));
                if (state.timeParameterActive) sb.WithMotionTime(FloatOf(state.timeParameter));

                if (!plan.behaviourDeferred.Contains(state))
                    foreach (var behaviour in state.behaviours)
                        EmitBehaviour(sb, behaviour);
            }

            foreach (var child in machine.machines)
            {
                // A blank line per sub-machine keeps a many-machine layer scannable.
                _c.Script.Blank();
                var mb = scope.NewSubStateMachine(child.machine.name);
                machines[mb.Prefix] = mb;
                machineOrder.Add((mb, child));
                CreateScope(mb, child.machine, states, machines, order, machineOrder, scopes, plan);
            }
        }

        // ---- blend trees ------------------------------------------------------

        TreeBuilder EmitTree(ControllerIR.Tree tree)
        {
            // Nested trees first: their variables must precede the parent's reference.
            var nested = new Dictionary<ControllerIR.TreeChild, TreeBuilder>();
            foreach (var child in tree.children)
                if (child.tree != null)
                    nested[child] = EmitTree(child.tree);

            var tb = _c.NewBlendTree(tree.name);
            bool is2D = false;
            switch (tree.type)
            {
                case BlendTreeType.Direct:
                    tb.Direct();
                    break;
                case BlendTreeType.Simple1D:
                    tb.Simple1D(FloatOf(tree.blendParameter));
                    break;
                case BlendTreeType.SimpleDirectional2D:
                    tb.SimpleDirectional2D(FloatOf(tree.blendParameter), FloatOf(tree.blendParameterY));
                    is2D = true;
                    break;
                case BlendTreeType.FreeformCartesian2D:
                    tb.FreeformCartesian2D(FloatOf(tree.blendParameter), FloatOf(tree.blendParameterY));
                    is2D = true;
                    break;
                default:
                    tb.FreeformDirectional2D(FloatOf(tree.blendParameter), FloatOf(tree.blendParameterY));
                    is2D = true;
                    break;
            }
            if (!tree.useAutomaticThresholds) tb.AutoThresholds(false);
            else if (tree.type == BlendTreeType.Simple1D
                && (tree.minThreshold != 0f || tree.maxThreshold != 1f))
                tb.ThresholdRange(tree.minThreshold, tree.maxThreshold);
            if (tree.normalizedBlendValues) tb.NormalizedBlendValues();

            foreach (var child in tree.children)
            {
                nested.TryGetValue(child, out var sub);
                // The WithAnimation overload carries the slot's defining datum
                // (threshold / blend position / direct weight); rarities go through
                // LastChild below.
                if (tree.type == BlendTreeType.Simple1D)
                {
                    if (sub != null) tb.WithAnimation(sub, child.threshold);
                    else tb.WithAnimation(child.motionAsset, child.threshold);
                }
                else if (is2D)
                {
                    if (sub != null) tb.WithAnimation(sub, child.position.x, child.position.y);
                    else tb.WithAnimation(child.motionAsset, child.position.x, child.position.y);
                }
                else if (tree.type == BlendTreeType.Direct
                    && !string.IsNullOrEmpty(child.directParameter))
                {
                    var weight = FloatOf(child.directParameter);
                    if (sub != null) tb.WithAnimation(sub, weight);
                    else tb.WithAnimation(child.motionAsset, weight);
                }
                else
                {
                    if (sub != null) tb.WithAnimation(sub);
                    else tb.WithAnimation(child.motionAsset);
                }

                if (tree.type != BlendTreeType.Simple1D && child.threshold != 0f)
                    tb.LastChild.Threshold(child.threshold);
                if (!is2D && child.position != Vector2.zero)
                    tb.LastChild.Position(child.position.x, child.position.y);
                if (child.timeScale != 1f) tb.LastChild.TimeScale(child.timeScale);
                if (child.cycleOffset != 0f) tb.LastChild.CycleOffset(child.cycleOffset);
                if (child.mirror) tb.LastChild.Mirror();
            }
            return tb;
        }

        // ---- behaviours --------------------------------------------------------

        internal void EmitBehaviour(StateBuilder sb, ControllerIR.Behaviour behaviour)
        {
            if (behaviour.driver == null)
            {
                sb.BehaviourJson(behaviour.typeName, behaviour.json);
                return;
            }

            // The Drives family writes into the state's current driver, creating the
            // first one on demand — NewDriver is only for a named or additional one,
            // or a driver with nothing in it to trigger the creation.
            var spec = behaviour.driver;
            bool named = !string.IsNullOrEmpty(behaviour.instanceName)
                && behaviour.instanceName != behaviour.typeName;
            if (named)
                sb.NewDriver(behaviour.instanceName);
            else if (HasDriverAlready(sb) || (spec.entries.Count == 0 && !spec.localOnly))
                sb.NewDriver();

            foreach (var entry in spec.entries)
                switch (entry.kind)
                {
                    case 1:
                        if (entry.value >= 0f)
                            sb.DrivingIncreases(Handle(entry.name, AnimatorControllerParameterType.Float), entry.value);
                        else
                            sb.DrivingDecreases(Handle(entry.name, AnimatorControllerParameterType.Float), -entry.value);
                        break;
                    case 2:
                        if (TypeOf(entry.name) == AnimatorControllerParameterType.Bool)
                            sb.DrivingRandomizes(BoolOf(entry.name), entry.chance,
                                entry.preventRepeats);
                        else
                            sb.DrivingRandomizes(Handle(entry.name, AnimatorControllerParameterType.Float),
                                entry.min, entry.max, entry.preventRepeats);
                        break;
                    case 3:
                        if (entry.convertRange)
                            sb.DrivingRemaps(
                                Handle(entry.source, AnimatorControllerParameterType.Float),
                                entry.sourceMin, entry.sourceMax,
                                Handle(entry.name, AnimatorControllerParameterType.Float),
                                entry.destMin, entry.destMax);
                        else
                            sb.DrivingCopies(
                                Handle(entry.source, AnimatorControllerParameterType.Float),
                                Handle(entry.name, AnimatorControllerParameterType.Float));
                        break;
                    default:
                        if (TypeOf(entry.name) == AnimatorControllerParameterType.Bool)
                            sb.Drives(BoolOf(entry.name), entry.value != 0f);
                        else
                            sb.Drives(Handle(entry.name, AnimatorControllerParameterType.Float), entry.value);
                        break;
                }
            if (spec.localOnly) sb.DrivingLocally();
        }

        static bool HasDriverAlready(StateBuilder sb)
        {
            foreach (var behaviour in sb.State.behaviours)
                if (behaviour.driver != null) return true;
            return false;
        }

        // ---- wiring ------------------------------------------------------------

        void WireScope(MachineScope scope, ControllerIR.Machine machine,
            Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines)
        {
            // .Default() only when the implicit first-state rule doesn't already cover it.
            if (machine.states.Count > 0 && machine.defaultState != null)
            {
                string firstPath = ControllerIR.Join(scope.Prefix, machine.states[0].name);
                if (machine.defaultState != firstPath
                    && states.TryGetValue(machine.defaultState, out var defaultBuilder))
                    defaultBuilder.Default();
            }

            foreach (var state in machine.states)
            {
                var sb = states[ControllerIR.Join(scope.Prefix, state.name)];
                foreach (var t in state.transitions)
                    EmitTransition(WireFrom(sb, t, states, machines), t);
            }
            foreach (var t in machine.anyStateTransitions)
            {
                var built = t.target == ControllerIR.Transition.Target.State
                    && states.TryGetValue(t.destination, out var ds) ? scope.AnyTransitionsTo(ds)
                    : t.target == ControllerIR.Transition.Target.Machine
                    && machines.TryGetValue(t.destination, out var dm) ? scope.AnyTransitionsTo(dm)
                    : null;
                if (built == null)
                    _warnings.Add(L.Tr("Any-State transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    EmitTransition(built, t, fromAnyState: true);
            }
            foreach (var t in machine.entryTransitions)
            {
                var built = t.target == ControllerIR.Transition.Target.State
                    && states.TryGetValue(t.destination, out var ds) ? scope.EntryTransitionsTo(ds)
                    : t.target == ControllerIR.Transition.Target.Machine
                    && machines.TryGetValue(t.destination, out var dm) ? scope.EntryTransitionsTo(dm)
                    : null;
                if (built == null)
                    _warnings.Add(L.Tr("Entry transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    EmitTransition(built, t);
            }

            foreach (var child in machine.machines)
            {
                var mb = machines[ControllerIR.Join(scope.Prefix, child.machine.name)];
                foreach (var t in child.transitions)
                {
                    var built = t.target == ControllerIR.Transition.Target.Exit ? mb.Exits()
                        : t.target == ControllerIR.Transition.Target.State
                        && states.TryGetValue(t.destination, out var ds) ? mb.TransitionsTo(ds)
                        : t.target == ControllerIR.Transition.Target.Machine
                        && machines.TryGetValue(t.destination, out var dm) ? mb.TransitionsTo(dm)
                        : null;
                    if (built == null)
                        _warnings.Add(L.Tr("Transition from machine '{0}' to '{1}' could not be resolved — skipped.",
                            child.machine.name, t.destination));
                    else
                        EmitTransition(built, t);
                }
                WireScope(mb, child.machine, states, machines);
            }
        }

        TransitionBuilder WireFrom(StateBuilder sb, ControllerIR.Transition t,
            Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines)
        {
            switch (t.target)
            {
                case ControllerIR.Transition.Target.Exit:
                    return sb.Exits();
                case ControllerIR.Transition.Target.State
                    when states.TryGetValue(t.destination, out var destination):
                    return sb.TransitionsTo(destination);
                case ControllerIR.Transition.Target.Machine
                    when machines.TryGetValue(t.destination, out var destination):
                    return sb.TransitionsTo(destination);
            }
            _warnings.Add(L.Tr("Transition from '{0}' to '{1}' could not be resolved — skipped.",
                sb.Path, t.destination));
            return null;
        }

        /// <summary>Conditions, then only the settings that differ from authoring defaults.</summary>
        void EmitTransition(TransitionBuilder tb, ControllerIR.Transition t,
            bool fromAnyState = false)
        {
            if (tb == null) return;
            for (int i = 0; i < t.conditions.Count; i++)
            {
                var condition = MakeCondition(t.conditions[i]);
                if (i == 0) tb.When(condition);
                else tb.And(condition);
            }

            if (!t.isStateTransition)
            {
                if (t.solo) tb.Solo();
                if (t.mute) tb.Mute();
                return;
            }
            if (t.hasExitTime)
            {
                if (t.exitTime == 1f) tb.AfterAnimationFinishes();
                else tb.AfterAnimationIsAtLeastAtNormalized(t.exitTime);
            }
            if (!t.hasFixedDuration) tb.WithTransitionDurationNormalized(t.duration);
            else if (t.duration != 0f) tb.WithTransitionDurationSeconds(t.duration);
            if (t.offset != 0f) tb.WithOffset(t.offset);
            if (t.interruptionSource != TransitionInterruptionSource.None)
                tb.WithInterruption(t.interruptionSource);
            if (!t.orderedInterruption) tb.WithNoOrderedInterruption();
            // Any State is the only place this flag does anything, and the authoring default
            // there is "do not re-trigger". Writing it whenever Unity's own true showed up
            // put the call on nearly every line in the file, including the state-to-state
            // transitions where the editor does not even offer the checkbox.
            if (fromAnyState && t.canTransitionToSelf) tb.WithTransitionToSelf();
            if (t.solo) tb.Solo();
            if (t.mute) tb.Mute();
        }

        /// <summary>Rebuilds a condition through the handle factory matching its mode —
        /// go.IsTrue(), blend.IsGreaterThan(0.5f) — exactly what the code will say.</summary>
        Condition MakeCondition(ControllerIR.Condition c)
        {
            switch (c.mode)
            {
                case AnimatorConditionMode.If:
                    return Handle(c.parameter, AnimatorControllerParameterType.Bool)
                        is TriggerParam trigger ? trigger.IsSet() : BoolOf(c.parameter).IsTrue();
                case AnimatorConditionMode.IfNot:
                    return BoolOf(c.parameter).IsFalse();
                case AnimatorConditionMode.Greater:
                    return Handle(c.parameter, AnimatorControllerParameterType.Float)
                        is IntParam gi ? gi.IsGreaterThan((int)c.threshold)
                        : FloatOf(c.parameter).IsGreaterThan(c.threshold);
                case AnimatorConditionMode.Less:
                    return Handle(c.parameter, AnimatorControllerParameterType.Float)
                        is IntParam li ? li.IsLessThan((int)c.threshold)
                        : FloatOf(c.parameter).IsLessThan(c.threshold);
                case AnimatorConditionMode.Equals:
                    return IntOf(c.parameter).IsEqualTo((int)c.threshold);
                default:
                    return IntOf(c.parameter).IsNotEqualTo((int)c.threshold);
            }
        }
    }
}
