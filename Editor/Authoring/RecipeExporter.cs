using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Converts a controller (or a subset of its layers) into recipe source code. The
    /// exporter never writes C# by hand: it drives a real <see cref="ControllerBuilder"/>
    /// with a <see cref="RecipeScript"/> recorder attached, so the emitted text is the exact
    /// call sequence whose result can be diffed against the original — that replayed builder
    /// comes back in the result for the tests to verify. Assets become [SerializeField]
    /// fields (pre-assigned on the generated .asset), never GUIDs in code.
    /// </summary>
    static class RecipeExporter
    {
        public class Result
        {
            public string code;
            public string className;
            public readonly List<FieldRef> fields = new List<FieldRef>();
            public readonly List<string> warnings = new List<string>();
            /// <summary>The builder the recording run drove — its IR is what the code builds.</summary>
            internal ControllerBuilder replayed;
        }

        public class FieldRef
        {
            public string fieldName;
            public string fieldType;
            public Object asset;
        }

        /// <summary>
        /// <paramref name="layerNames"/> null exports the whole controller (an exclusive
        /// recipe); a subset exports those layers plus only the parameters they reference.
        /// </summary>
        public static Result Export(AnimatorController controller, ICollection<string> layerNames,
            string className, string namespaceName)
        {
            var result = new Result { className = className };
            if (controller == null) return result;

            var full = ControllerIR.Parse(controller);
            var ir = full;
            if (layerNames != null)
            {
                ir = full.FilterTo(layerNames, ReferencedParameters(controller, layerNames));
                // Synced indices refer to the FULL layer list; remap them into the subset
                // (or to -1, which the driver reports as an unexportable sync source).
                foreach (var layer in ir.layers)
                {
                    if (layer.syncedLayerIndex < 0) continue;
                    string sourceName = layer.syncedLayerIndex < full.layers.Count
                        ? full.layers[layer.syncedLayerIndex].name : null;
                    layer.syncedLayerIndex = -1;
                    for (int i = 0; i < ir.layers.Count; i++)
                        if (ir.layers[i].name == sourceName && ir.layers[i].machine != null)
                            layer.syncedLayerIndex = i;
                }
            }

            var script = new RecipeScript();
            var builder = new ControllerBuilder { Script = script };
            script.RegisterRoot(builder);
            result.replayed = builder;

            RegisterAssets(ir, script, result);
            new Driver(builder, ir, result.warnings).Run();
            result.warnings.AddRange(builder.Bake());

            result.code = Compose(script, className, namespaceName, controller, result);
            return result;
        }

        /// <summary>Only the parameters the exported layers actually use travel with a
        /// partial export.</summary>
        static HashSet<string> ReferencedParameters(AnimatorController controller,
            ICollection<string> layerNames)
        {
            var referenced = new HashSet<string>();
            foreach (var layer in controller.layers)
                if (layerNames.Contains(layer.name) && layer.stateMachine != null)
                    referenced.UnionWith(LayerClipboard.CollectParameterNames(layer.stateMachine));
            return referenced;
        }

        // ---- asset fields ------------------------------------------------------

        /// <summary>Walks the IR in emission order so field declarations come out in a
        /// stable, readable order.</summary>
        static void RegisterAssets(ControllerIR ir, RecipeScript script, Result result)
        {
            void Register(Object asset)
            {
                if (asset == null || script.Assets.ContainsKey(asset)) return;
                string name = script.RegisterAsset(asset, asset.name);
                result.fields.Add(new FieldRef
                {
                    fieldName = name,
                    fieldType = asset is AnimationClip ? "AnimationClip"
                        : asset is AvatarMask ? "AvatarMask" : "Motion",
                    asset = asset,
                });
            }

            void Tree(ControllerIR.Tree tree)
            {
                if (tree == null) return;
                foreach (var child in tree.children)
                {
                    Register(child.motionAsset);
                    Tree(child.tree);
                }
            }

            void Machine(ControllerIR.Machine machine)
            {
                if (machine == null) return;
                foreach (var state in machine.states)
                {
                    Register(state.motionAsset);
                    Tree(state.tree);
                }
                foreach (var child in machine.machines)
                    Machine(child.machine);
            }

            foreach (var layer in ir.layers)
            {
                Register(layer.mask);
                Machine(layer.machine);
                foreach (var entry in layer.syncedMotions)
                    Register(entry.motion);
            }
        }

        // ---- driving the builder ------------------------------------------------

        /// <summary>
        /// One export pass over the IR. Stateful because parameter handles are the recipe's
        /// vocabulary: every condition, driver entry and blend parameter goes through the
        /// typed handle declared up top, shared across layers.
        /// </summary>
        class Driver
        {
            readonly ControllerBuilder _c;
            readonly ControllerIR _ir;
            readonly List<string> _warnings;
            readonly Dictionary<string, ParamHandle> _handles =
                new Dictionary<string, ParamHandle>();
            readonly Dictionary<string, AnimatorControllerParameterType> _types =
                new Dictionary<string, AnimatorControllerParameterType>();

            public Driver(ControllerBuilder c, ControllerIR ir, List<string> warnings)
            {
                _c = c;
                _ir = ir;
                _warnings = warnings;
                foreach (var p in ir.parameters)
                    _types[p.name] = p.type;
            }

            public void Run()
            {
                // Parameters first, whatever the layer layout — they're the controller-wide
                // vocabulary, and one handle per line so a long list stays scannable.
                if (_ir.parameters.Count > 0)
                    _c.Script.Comment(Header("Parameters"));
                foreach (var p in _ir.parameters)
                    _handles[p.name] = Declare(p);

                foreach (var layer in _ir.layers)
                {
                    _c.Script.Blank();
                    if (layer.machine == null)
                    {
                        string source = layer.syncedLayerIndex >= 0
                            && layer.syncedLayerIndex < _ir.layers.Count
                            ? _ir.layers[layer.syncedLayerIndex].name : "?";
                        _c.Script.Comment(Header("Synced Layer: " + layer.name + " (mirrors " + source + ")"));
                        SyncedLayer(layer);
                        continue;
                    }
                    _c.Script.Comment(Header("Layer: " + layer.name));

                    var lb = _c.Layer(layer.name);
                    if (layer.defaultWeight != 1f) lb.WithWeight(layer.defaultWeight);
                    if (layer.blending == AnimatorLayerBlendingMode.Additive) lb.Additive();
                    if (layer.ikPass) lb.WithIkPass();
                    if (layer.mask != null) lb.WithAvatarMask(layer.mask);

                    var states = new Dictionary<string, StateBuilder>();
                    var machines = new Dictionary<string, MachineBuilder>();
                    CreateScope(lb, layer.machine, states, machines);
                    // States above, wiring below — the gap is what makes a layer readable.
                    _c.Script.Blank();
                    WireScope(lb, layer.machine, states, machines);
                }
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
            }

            void CreateScope(MachineScope scope, ControllerIR.Machine machine,
                Dictionary<string, StateBuilder> states, Dictionary<string, MachineBuilder> machines)
            {
                foreach (var state in machine.states)
                {
                    // A blend tree must exist as a variable before the state can reference it.
                    TreeBuilder tree = state.tree != null ? EmitTree(state.tree) : null;

                    var sb = scope.NewState(state.name);
                    states[sb.Path] = sb;
                    if (tree != null) sb.WithAnimation(tree);
                    else if (state.motionAsset != null) sb.WithAnimation(state.motionAsset);
                    sb.At(state.position.x, state.position.y);

                    if (state.speed != 1f) sb.WithSpeedSetTo(state.speed);
                    if (state.cycleOffset != 0f) sb.WithCycleOffsetSetTo(state.cycleOffset);
                    if (state.mirror) sb.WithMirrorSetTo(true);
                    if (state.ikOnFeet) sb.WithFootIkSetTo(true);
                    if (!state.writeDefaultValues) sb.WithWriteDefaultsSetTo(false);
                    if (!string.IsNullOrEmpty(state.tag)) sb.WithTag(state.tag);
                    if (state.speedParameterActive) sb.WithSpeed(FloatOf(state.speedParameter));
                    if (state.mirrorParameterActive) sb.WithMirror(BoolOf(state.mirrorParameter));
                    if (state.cycleOffsetParameterActive)
                        sb.WithCycleOffset(FloatOf(state.cycleOffsetParameter));
                    if (state.timeParameterActive) sb.WithMotionTime(FloatOf(state.timeParameter));

                    foreach (var behaviour in state.behaviours)
                        EmitBehaviour(sb, behaviour);
                }

                // Node positions last — bookkeeping, not structure; they chain onto one line.
                scope.EntryAt(machine.entryPosition.x, machine.entryPosition.y);
                scope.ExitAt(machine.exitPosition.x, machine.exitPosition.y);
                scope.AnyStateAt(machine.anyStatePosition.x, machine.anyStatePosition.y);
                if (machine.parentPosition != Vector3.zero)
                    scope.ParentAt(machine.parentPosition.x, machine.parentPosition.y);

                foreach (var child in machine.machines)
                {
                    var mb = scope.NewSubStateMachine(child.machine.name)
                        .At(child.position.x, child.position.y);
                    machines[mb.Prefix] = mb;
                    CreateScope(mb, child.machine, states, machines);
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

            void EmitBehaviour(StateBuilder sb, ControllerIR.Behaviour behaviour)
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
                                sb.DrivingRandomizes(BoolOf(entry.name), entry.chance);
                            else
                                sb.DrivingRandomizes(Handle(entry.name, AnimatorControllerParameterType.Float), entry.min, entry.max);
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
                        EmitTransition(built, t);
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
            void EmitTransition(TransitionBuilder tb, ControllerIR.Transition t)
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
                if (t.canTransitionToSelf) tb.WithTransitionToSelf();
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

        /// <summary>"---- text ----…" divider padded to a steady width.</summary>
        static string Header(string text)
        {
            const int width = 72;
            string lead = "---- " + text + " ";
            return lead.Length >= width ? lead.TrimEnd() : lead + new string('-', width - lead.Length);
        }

        // ---- composing the file --------------------------------------------------

        const string CheatSheet =
@"// DaerD recipe — edit this file, then press Generate on the recipe asset.
// AnimatorAsCode-style API (Yozolab.DaerD.Authoring), quick reference:
//   Parameters   var go = c.BoolParameter(""Go"");   var x = c.FloatParameter(""X"", 0.5f);
//                c.IntParameter(""N"");   c.TriggerParameter(""Fire"");
//   Layers       var fx = c.Layer(""Name"").WithWeight(1).Additive().WithAvatarMask(mask);
//                c.SyncedLayer(""Mirror"", ""Name"").Override(""StatePath"", clip);
//   States       var s = fx.NewState(""Idle"").WithAnimation(clip).At(260, 60)
//                    .WithWriteDefaultsSetTo(false).WithSpeedSetTo(2).WithMotionTime(x)
//                    .WithTag(""t"").Default();
//   Sub-machines var sub = fx.NewSubStateMachine(""Sub"").At(500, 50);  sub.NewState(...);
//   Transitions  s.TransitionsTo(other) / s.Exits() / fx.AnyTransitionsTo(s)
//                    / fx.EntryTransitionsTo(s) / sub.TransitionsTo(s), then chain:
//                .When(go.IsTrue()).And(x.IsGreaterThan(0.5f))      // conditions AND together
//                .AfterAnimationFinishes() .AfterAnimationIsAtLeastAtNormalized(0.9f)
//                .WithTransitionDurationSeconds(0.15f) .WithTransitionToSelf()
//                .WithInterruption(TransitionInterruptionSource.Destination)
//   Blend trees  var t = c.NewBlendTree(""Move"").Simple1D(x)
//                    .WithAnimation(idleClip, 0).WithAnimation(runClip, 1);
//                s.WithAnimation(t);   2D: .FreeformDirectional2D(x, y) + .WithAnimation(clip, 0, 1)
//                Direct: .Direct() + .WithAnimation(clip, weightParam);  extras: t.LastChild.TimeScale(2)
//   Drivers      s.Drives(n, 1).DrivingIncreases(x, 0.1f).DrivingCopies(a, b).DrivingLocally()
//                    .DrivingRemaps(a, 0, 1, b, -1, 1).DrivingRandomizes(x, 0, 1);
//   Fallbacks    s.BehaviourJson(typeName, json);   c.Raw(controller => { /* full API */ });
// Assets are the [SerializeField] fields below — assign them on the recipe asset.
// This Build method is ordinary C#: loops, helpers and interpolation all work here.";

        static string Compose(RecipeScript script, string className, string namespaceName,
            AnimatorController controller, Result result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> Exported from \"" + controller.name
                + "\" by DaerD. Safe to edit — this file is yours now. </auto-generated>");
            sb.AppendLine(CheatSheet);
            sb.AppendLine();
            sb.AppendLine("using UnityEditor.Animations;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using Yozolab.DaerD.Authoring;");
            sb.AppendLine();

            bool hasNamespace = !string.IsNullOrEmpty(namespaceName);
            string indent = hasNamespace ? "    " : string.Empty;
            if (hasNamespace)
            {
                sb.AppendLine("namespace " + namespaceName);
                sb.AppendLine("{");
            }

            sb.AppendLine(indent + "public class " + className + " : ControllerRecipe");
            sb.AppendLine(indent + "{");
            foreach (var field in result.fields)
                sb.AppendLine(indent + "    [SerializeField] " + field.fieldType + " "
                    + field.fieldName + ";");
            if (result.fields.Count > 0) sb.AppendLine();

            sb.AppendLine(indent + "    protected override void Build(ControllerBuilder c)");
            sb.AppendLine(indent + "    {");
            var body = StripUnusedVariables(script.Lines);
            while (body.Count > 0 && body[0].Length == 0) body.RemoveAt(0);
            while (body.Count > 0 && body[body.Count - 1].Length == 0) body.RemoveAt(body.Count - 1);
            foreach (var line in body)
                sb.AppendLine(line.Length == 0 ? string.Empty : indent + "        " + line);
            sb.AppendLine(indent + "    }");
            sb.AppendLine(indent + "}");
            if (hasNamespace) sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Drops "var t = " from declarations nothing refers back to (one-shot
        /// transitions), leaving plain fluent statements.</summary>
        internal static List<string> StripUnusedVariables(IReadOnlyList<string> lines)
        {
            var counts = new Dictionary<string, int>();
            foreach (var line in lines)
                foreach (Match token in Regex.Matches(line, @"[A-Za-z_][A-Za-z0-9_]*"))
                    counts[token.Value] = counts.TryGetValue(token.Value, out var n) ? n + 1 : 1;

            var output = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                var declaration = Regex.Match(line, @"^var ([A-Za-z_][A-Za-z0-9_]*) = (.*)$");
                output.Add(declaration.Success && counts[declaration.Groups[1].Value] == 1
                    ? declaration.Groups[2].Value
                    : line);
            }
            return output;
        }
    }
}
