using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

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
        readonly Dictionary<string, ParamHandle> _handles =
            new Dictionary<string, ParamHandle>();
        readonly Dictionary<string, AnimatorControllerParameterType> _types =
            new Dictionary<string, AnimatorControllerParameterType>();

        public RecipeDriver(ControllerBuilder c, ControllerIR ir, List<string> warnings)
        {
            _c = c;
            _ir = ir;
            _warnings = warnings;
            _folds = new RecipeFoldPlanner(this, c);
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
                _handles[p.name] = Declare(p);

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
                _c.Script.Comment(RecipeExporter.Header("Layer: " + layer.name));
                // A gadget layer exports as the raw tree it is — accurate, and a wall of
                // children nobody edits by hand. The calls that build this kind of layer
                // are right there in the API, so point at them where the wall starts.
                if (IsGadgetShapedLayer(layer.machine))
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
}
