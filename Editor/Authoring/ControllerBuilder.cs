using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// The recipe-facing fluent API, deliberately shaped like AnimatorAsCode V1 — the
    /// dialect AI models and gimmick authors already know: NewState / WithAnimation /
    /// TransitionsTo(x).When(param.IsTrue()) / Drives / DrivingRemaps. Parameters are typed
    /// handles, conditions are objects those handles produce. Everything is data until
    /// Generate applies it; nothing here touches a controller directly (use <see cref="Raw"/>).
    ///
    ///   var go = c.BoolParameter("Go");
    ///   var fx = c.Layer("Hand");
    ///   var idle = fx.NewState("Idle").WithAnimation(idleClip).At(260, 60);
    ///   idle.TransitionsTo(wave).When(go.IsTrue()).WithTransitionDurationSeconds(0.15f);
    ///   wave.Exits().When(go.IsFalse());
    ///   idle.Drives(step, 1).DrivingLocally();
    /// </summary>
    public sealed class ControllerBuilder
    {
        internal readonly ControllerIR IR = new ControllerIR();
        internal readonly List<Func<AnimatorController, List<string>>> PostOps =
            new List<Func<AnimatorController, List<string>>>();
        internal readonly List<string> Notes = new List<string>();
        internal readonly List<Action> PostBakeSyncs = new List<Action>();
        internal RecipeScript Script;

        // ---- parameters ----------------------------------------------------------

        public FloatParam FloatParameter(string name) =>
            Handle(new FloatParam(this, name), AnimatorControllerParameterType.Float,
                false, 0f, 0, false, $"FloatParameter({RecipeScript.S(name)})");

        public FloatParam FloatParameter(string name, float defaultValue) =>
            Handle(new FloatParam(this, name), AnimatorControllerParameterType.Float,
                true, defaultValue, 0, false,
                $"FloatParameter({RecipeScript.S(name)}, {RecipeScript.F(defaultValue)})");

        public IntParam IntParameter(string name) =>
            Handle(new IntParam(this, name), AnimatorControllerParameterType.Int,
                false, 0f, 0, false, $"IntParameter({RecipeScript.S(name)})");

        public IntParam IntParameter(string name, int defaultValue) =>
            Handle(new IntParam(this, name), AnimatorControllerParameterType.Int,
                true, 0f, defaultValue, false,
                $"IntParameter({RecipeScript.S(name)}, {defaultValue})");

        public BoolParam BoolParameter(string name) =>
            Handle(new BoolParam(this, name), AnimatorControllerParameterType.Bool,
                false, 0f, 0, false, $"BoolParameter({RecipeScript.S(name)})");

        public BoolParam BoolParameter(string name, bool defaultValue) =>
            Handle(new BoolParam(this, name), AnimatorControllerParameterType.Bool,
                true, 0f, 0, defaultValue,
                $"BoolParameter({RecipeScript.S(name)}, {RecipeScript.B(defaultValue)})");

        public TriggerParam TriggerParameter(string name) =>
            Handle(new TriggerParam(this, name), AnimatorControllerParameterType.Trigger,
                false, 0f, 0, false, $"TriggerParameter({RecipeScript.S(name)})");

        /// <summary>Registration is idempotent: naming a parameter twice refers to the same
        /// one; only a call that states a default value overwrites the default.</summary>
        T Handle<T>(T handle, AnimatorControllerParameterType type, bool hasDefault,
            float f, int i, bool b, string call) where T : ParamHandle
        {
            var existing = IR.parameters.Find(p => p.name == handle.Name);
            if (existing == null)
            {
                IR.parameters.Add(new ControllerIR.Param
                {
                    name = handle.Name,
                    type = type,
                    hasDefault = hasDefault,
                    defaultFloat = f,
                    defaultInt = i,
                    defaultBool = b,
                });
            }
            else if (existing.type != type)
                Notes.Add(L.Tr("Parameter '{0}' is declared with conflicting types ({1} and {2}).",
                    handle.Name, existing.type, type));
            else if (hasDefault)
            {
                existing.hasDefault = true;
                existing.defaultFloat = f;
                existing.defaultInt = i;
                existing.defaultBool = b;
            }
            Script?.Declare(handle, handle.Name, this, call);
            return handle;
        }

        // ---- layers ----------------------------------------------------------------

        public LayerBuilder Layer(string name)
        {
            var layer = new ControllerIR.Layer { name = name, machine = new ControllerIR.Machine { name = name } };
            IR.layers.Add(layer);
            var builder = new LayerBuilder(this, layer);
            Script?.Declare(builder, name, this, $"Layer({RecipeScript.S(name)})");
            return builder;
        }

        /// <summary>A synced layer mirroring <paramref name="sourceLayer"/> (a layer declared
        /// in this recipe, resolved by name at apply time).</summary>
        public SyncedLayerBuilder SyncedLayer(string name, string sourceLayer)
        {
            var layer = new ControllerIR.Layer { name = name };
            IR.layers.Add(layer);
            var builder = new SyncedLayerBuilder(this, layer, sourceLayer);
            Script?.Declare(builder, name, this,
                $"SyncedLayer({RecipeScript.S(name)}, {RecipeScript.S(sourceLayer)})");
            return builder;
        }

        /// <summary>An embedded blend tree; assign it with
        /// <see cref="StateBuilder.WithAnimation(TreeBuilder)"/> (AAC's aac.NewBlendTree flow).</summary>
        public TreeBuilder NewBlendTree(string name = "Blend Tree")
        {
            var builder = new TreeBuilder(this, new ControllerIR.Tree { name = name });
            Script?.Declare(builder, name, this, $"NewBlendTree({RecipeScript.S(name)})");
            return builder;
        }

        /// <summary>
        /// Escape hatch: runs after the declared layers are applied, with the live controller.
        /// Anything DaerD or Unity can do is available here — at the price that Verify can't
        /// see what it did. Exported code never contains Raw calls.
        /// </summary>
        public ControllerBuilder Raw(Action<AnimatorController> action)
        {
            if (action != null)
                PostOps.Add(controller =>
                {
                    action(controller);
                    return new List<string>();
                });
            return this;
        }

        /// <summary>
        /// Async Sync (parameter compression) as a post step: full wizard configuration plus
        /// the explicit per-step schedule the wizard doesn't expose. The generated layer is
        /// regenerated in place on every Generate, matched by base name. Left unnamed, the
        /// base name is derived from the target controller so two distributions that both
        /// multiplex don't fight over the same synced parameters.
        /// </summary>
        public AsyncSyncRecipeBuilder AsyncSync(string baseName = null) =>
            new AsyncSyncRecipeBuilder(this, baseName);

        // ---- bake ---------------------------------------------------------------

        /// <summary>Resolves deferred references (synced source names) and returns problems
        /// worth surfacing before the declaration is applied.</summary>
        internal List<string> Bake()
        {
            foreach (var resolve in PostBakeSyncs) resolve();

            var problems = new List<string>(Notes);
            var layerNames = new HashSet<string>();
            foreach (var layer in IR.layers)
                if (!layerNames.Add(layer.name))
                    problems.Add(L.Tr("Layer '{0}' is declared more than once.", layer.name));
            return problems;
        }

        internal int IndexOfLayer(string name)
        {
            for (int i = 0; i < IR.layers.Count; i++)
                if (IR.layers[i].name == name) return i;
            return -1;
        }
    }

    // ---- parameters and conditions -----------------------------------------------

    /// <summary>A declared (or referenced) controller parameter. Handles are how recipes
    /// name parameters everywhere — conditions, drivers, per-state parameter slots.</summary>
    public abstract class ParamHandle
    {
        internal readonly ControllerBuilder Root;
        public string Name { get; }

        internal ParamHandle(ControllerBuilder root, string name)
        {
            Root = root;
            Name = name ?? string.Empty;
        }

        internal Condition Make(AnimatorConditionMode mode, float threshold, string call) =>
            new Condition
            {
                Mode = mode,
                Parameter = Name,
                Threshold = threshold,
                Source = Root.Script != null ? Root.Script.NameArg(this) + "." + call : null,
            };
    }

    public sealed class FloatParam : ParamHandle
    {
        internal FloatParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsGreaterThan(float value) =>
            Make(AnimatorConditionMode.Greater, value, $"IsGreaterThan({RecipeScript.F(value)})");

        public Condition IsLessThan(float value) =>
            Make(AnimatorConditionMode.Less, value, $"IsLessThan({RecipeScript.F(value)})");
    }

    public sealed class IntParam : ParamHandle
    {
        internal IntParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsGreaterThan(int value) =>
            Make(AnimatorConditionMode.Greater, value, $"IsGreaterThan({value})");

        public Condition IsLessThan(int value) =>
            Make(AnimatorConditionMode.Less, value, $"IsLessThan({value})");

        public Condition IsEqualTo(int value) =>
            Make(AnimatorConditionMode.Equals, value, $"IsEqualTo({value})");

        public Condition IsNotEqualTo(int value) =>
            Make(AnimatorConditionMode.NotEqual, value, $"IsNotEqualTo({value})");
    }

    public sealed class BoolParam : ParamHandle
    {
        internal BoolParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsTrue() => Make(AnimatorConditionMode.If, 0f, "IsTrue()");

        public Condition IsFalse() => Make(AnimatorConditionMode.IfNot, 0f, "IsFalse()");

        public Condition IsEqualTo(bool value) =>
            Make(value ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f,
                $"IsEqualTo({RecipeScript.B(value)})");
    }

    public sealed class TriggerParam : ParamHandle
    {
        internal TriggerParam(ControllerBuilder root, string name) : base(root, name) { }

        public Condition IsSet() => Make(AnimatorConditionMode.If, 0f, "IsSet()");
    }

    /// <summary>One transition condition, produced by a parameter handle
    /// (go.IsTrue(), blend.IsGreaterThan(0.5f)) and consumed by When / And.</summary>
    public sealed class Condition
    {
        internal AnimatorConditionMode Mode;
        internal string Parameter;
        internal float Threshold;
        internal string Source;
    }

    // ---- machines ------------------------------------------------------------------

    /// <summary>Shared surface of a layer's root and a sub-state machine.</summary>
    public abstract class MachineScope
    {
        internal readonly ControllerBuilder Root;
        internal readonly ControllerIR.Machine Machine;
        internal readonly string Prefix;

        internal MachineScope(ControllerBuilder root, ControllerIR.Machine machine, string prefix)
        {
            Root = root;
            Machine = machine;
            Prefix = prefix;
        }

        public StateBuilder NewState(string name)
        {
            var state = new ControllerIR.State { name = name };
            Machine.states.Add(state);
            // Mirrors Unity: the first state of a machine is its default until told otherwise.
            string path = ControllerIR.Join(Prefix, name);
            if (Machine.states.Count == 1) Machine.defaultState = path;
            var builder = new StateBuilder(Root, this, state, path);
            Root.Script?.Declare(builder, name, this, $"NewState({RecipeScript.S(name)})");
            return builder;
        }

        public MachineBuilder NewSubStateMachine(string name)
        {
            var child = new ControllerIR.ChildMachine { machine = new ControllerIR.Machine { name = name } };
            Machine.machines.Add(child);
            var builder = new MachineBuilder(Root, child, ControllerIR.Join(Prefix, name));
            Root.Script?.Declare(builder, name, this, $"NewSubStateMachine({RecipeScript.S(name)})");
            return builder;
        }

        public TransitionBuilder AnyTransitionsTo(StateBuilder destination) =>
            Wire("AnyTransitionsTo", Machine.anyStateTransitions, destination.Path,
                ControllerIR.Transition.Target.State, isStateTransition: true, destination);

        public TransitionBuilder AnyTransitionsTo(MachineBuilder destination) =>
            Wire("AnyTransitionsTo", Machine.anyStateTransitions, destination.Prefix,
                ControllerIR.Transition.Target.Machine, isStateTransition: true, destination);

        public TransitionBuilder EntryTransitionsTo(StateBuilder destination) =>
            Wire("EntryTransitionsTo", Machine.entryTransitions, destination.Path,
                ControllerIR.Transition.Target.State, isStateTransition: false, destination);

        public TransitionBuilder EntryTransitionsTo(MachineBuilder destination) =>
            Wire("EntryTransitionsTo", Machine.entryTransitions, destination.Prefix,
                ControllerIR.Transition.Target.Machine, isStateTransition: false, destination);

        TransitionBuilder Wire(string method, List<ControllerIR.Transition> list, string path,
            ControllerIR.Transition.Target target, bool isStateTransition, object destination)
        {
            var transition = NewTransition(target, path, isStateTransition);
            list.Add(transition);
            var builder = new TransitionBuilder(Root, transition);
            Root.Script?.Declare(builder, "t", this, $"{method}({Root.Script.NameArg(destination)})");
            return builder;
        }

        internal static ControllerIR.Transition NewTransition(ControllerIR.Transition.Target target,
            string path, bool isStateTransition)
        {
            var transition = new ControllerIR.Transition
            {
                target = target,
                destination = path ?? string.Empty,
                isStateTransition = isStateTransition,
            };
            if (isStateTransition)
            {
                // Authoring defaults favour gimmick wiring: no exit time, instant switch.
                transition.hasExitTime = false;
                transition.exitTime = 0.75f;
                transition.hasFixedDuration = true;
                transition.duration = 0f;
            }
            return transition;
        }

        public MachineScope EntryAt(float x, float y)
        {
            Machine.entryPosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"EntryAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        public MachineScope ExitAt(float x, float y)
        {
            Machine.exitPosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"ExitAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        public MachineScope AnyStateAt(float x, float y)
        {
            Machine.anyStatePosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"AnyStateAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        public MachineScope ParentAt(float x, float y)
        {
            Machine.parentPosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"ParentAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }
    }

    public sealed class LayerBuilder : MachineScope
    {
        readonly ControllerIR.Layer _layer;

        internal LayerBuilder(ControllerBuilder root, ControllerIR.Layer layer)
            : base(root, layer.machine, string.Empty)
        {
            _layer = layer;
        }

        public LayerBuilder WithWeight(float weight)
        {
            _layer.defaultWeight = weight;
            Root.Script?.Call(this, $"WithWeight({RecipeScript.F(weight)})");
            return this;
        }

        public LayerBuilder Additive()
        {
            _layer.blending = AnimatorLayerBlendingMode.Additive;
            Root.Script?.Call(this, "Additive()");
            return this;
        }

        public LayerBuilder WithIkPass(bool on = true)
        {
            _layer.ikPass = on;
            Root.Script?.Call(this, on ? "WithIkPass()" : "WithIkPass(false)");
            return this;
        }

        public LayerBuilder WithAvatarMask(AvatarMask mask)
        {
            _layer.mask = mask;
            Root.Script?.Call(this, $"WithAvatarMask({Root.Script.AssetRef(mask)})");
            return this;
        }
    }

    /// <summary>Synced layer: mirrors a source layer's states, overriding motions only.</summary>
    public sealed class SyncedLayerBuilder
    {
        readonly ControllerBuilder _root;
        readonly ControllerIR.Layer _layer;

        internal SyncedLayerBuilder(ControllerBuilder root, ControllerIR.Layer layer, string sourceLayer)
        {
            _root = root;
            _layer = layer;
            // Resolved lazily so declaration order doesn't matter; a bad name surfaces as -1
            // plus a note at bake time.
            root.PostBakeSyncs.Add(() =>
            {
                int index = root.IndexOfLayer(sourceLayer);
                if (index < 0)
                    root.Notes.Add(L.Tr("Synced layer '{0}': source layer '{1}' is not declared in this recipe.",
                        layer.name, sourceLayer));
                layer.syncedLayerIndex = index;
            });
        }

        public SyncedLayerBuilder WithWeight(float weight)
        {
            _layer.defaultWeight = weight;
            _root.Script?.Call(this, $"WithWeight({RecipeScript.F(weight)})");
            return this;
        }

        public SyncedLayerBuilder Additive()
        {
            _layer.blending = AnimatorLayerBlendingMode.Additive;
            _root.Script?.Call(this, "Additive()");
            return this;
        }

        public SyncedLayerBuilder WithIkPass(bool on = true)
        {
            _layer.ikPass = on;
            _root.Script?.Call(this, on ? "WithIkPass()" : "WithIkPass(false)");
            return this;
        }

        public SyncedLayerBuilder WithAvatarMask(AvatarMask mask)
        {
            _layer.mask = mask;
            _root.Script?.Call(this, $"WithAvatarMask({_root.Script.AssetRef(mask)})");
            return this;
        }

        public SyncedLayerBuilder AffectsTiming(bool on = true)
        {
            _layer.syncedLayerAffectsTiming = on;
            _root.Script?.Call(this, on ? "AffectsTiming()" : "AffectsTiming(false)");
            return this;
        }

        /// <summary>Overrides the motion of a source-layer state ("Sub/State" path form).</summary>
        public SyncedLayerBuilder Override(string statePath, Motion motion)
        {
            _layer.syncedMotions.Add(new ControllerIR.MotionOverride
            { statePath = statePath, motion = motion });
            _root.Script?.Call(this,
                $"Override({RecipeScript.S(statePath)}, {_root.Script.AssetRef(motion)})");
            return this;
        }
    }

    public sealed class MachineBuilder : MachineScope
    {
        readonly ControllerIR.ChildMachine _child;

        internal MachineBuilder(ControllerBuilder root, ControllerIR.ChildMachine child, string prefix)
            : base(root, child.machine, prefix)
        {
            _child = child;
        }

        public MachineBuilder At(float x, float y)
        {
            _child.position = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"At({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        /// <summary>Transition drawn from this machine's node in the parent view.</summary>
        public TransitionBuilder TransitionsTo(StateBuilder destination) =>
            Source("TransitionsTo", destination.Path, ControllerIR.Transition.Target.State, destination);

        public TransitionBuilder TransitionsTo(MachineBuilder destination) =>
            Source("TransitionsTo", destination.Prefix, ControllerIR.Transition.Target.Machine, destination);

        public TransitionBuilder Exits() =>
            Source("Exits", string.Empty, ControllerIR.Transition.Target.Exit, null);

        TransitionBuilder Source(string method, string path, ControllerIR.Transition.Target target,
            object destination)
        {
            var transition = NewTransition(target, path, isStateTransition: false);
            _child.transitions.Add(transition);
            var builder = new TransitionBuilder(Root, transition);
            Root.Script?.Declare(builder, "t", this, destination == null
                ? $"{method}()"
                : $"{method}({Root.Script.NameArg(destination)})");
            return builder;
        }
    }

    // ---- states -------------------------------------------------------------------

    public sealed class StateBuilder
    {
        readonly ControllerBuilder _root;
        readonly MachineScope _scope;
        internal readonly ControllerIR.State State;
        internal readonly string Path;
        ControllerIR.Behaviour _driver;

        internal StateBuilder(ControllerBuilder root, MachineScope scope,
            ControllerIR.State state, string path)
        {
            _root = root;
            _scope = scope;
            State = state;
            Path = path;
        }

        public StateBuilder At(float x, float y)
        {
            State.position = new Vector3(x, y, 0f);
            _root.Script?.Call(this, $"At({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        public StateBuilder Default()
        {
            _scope.Machine.defaultState = Path;
            _root.Script?.Call(this, "Default()");
            return this;
        }

        public StateBuilder WithAnimation(Motion motion)
        {
            State.motionAsset = motion;
            State.tree = null;
            _root.Script?.Call(this, $"WithAnimation({_root.Script.AssetRef(motion)})");
            return this;
        }

        public StateBuilder WithAnimation(TreeBuilder blendTree)
        {
            State.tree = blendTree.Tree;
            State.motionAsset = null;
            _root.Script?.Call(this, $"WithAnimation({_root.Script.NameArg(blendTree)})");
            return this;
        }

        public StateBuilder WithSpeedSetTo(float speed)
        {
            State.speed = speed;
            _root.Script?.Call(this, $"WithSpeedSetTo({RecipeScript.F(speed)})");
            return this;
        }

        public StateBuilder WithSpeed(FloatParam parameter)
        {
            State.speedParameterActive = true;
            State.speedParameter = parameter.Name;
            _root.Script?.Call(this, $"WithSpeed({_root.Script.NameArg(parameter)})");
            return this;
        }

        public StateBuilder WithCycleOffsetSetTo(float offset)
        {
            State.cycleOffset = offset;
            _root.Script?.Call(this, $"WithCycleOffsetSetTo({RecipeScript.F(offset)})");
            return this;
        }

        public StateBuilder WithCycleOffset(FloatParam parameter)
        {
            State.cycleOffsetParameterActive = true;
            State.cycleOffsetParameter = parameter.Name;
            _root.Script?.Call(this, $"WithCycleOffset({_root.Script.NameArg(parameter)})");
            return this;
        }

        public StateBuilder WithMotionTime(FloatParam parameter)
        {
            State.timeParameterActive = true;
            State.timeParameter = parameter.Name;
            _root.Script?.Call(this, $"WithMotionTime({_root.Script.NameArg(parameter)})");
            return this;
        }

        public StateBuilder WithMirrorSetTo(bool mirror)
        {
            State.mirror = mirror;
            _root.Script?.Call(this, $"WithMirrorSetTo({RecipeScript.B(mirror)})");
            return this;
        }

        public StateBuilder WithMirror(BoolParam parameter)
        {
            State.mirrorParameterActive = true;
            State.mirrorParameter = parameter.Name;
            _root.Script?.Call(this, $"WithMirror({_root.Script.NameArg(parameter)})");
            return this;
        }

        public StateBuilder WithWriteDefaultsSetTo(bool writeDefaults)
        {
            State.writeDefaultValues = writeDefaults;
            _root.Script?.Call(this, $"WithWriteDefaultsSetTo({RecipeScript.B(writeDefaults)})");
            return this;
        }

        public StateBuilder WithFootIkSetTo(bool footIk)
        {
            State.ikOnFeet = footIk;
            _root.Script?.Call(this, $"WithFootIkSetTo({RecipeScript.B(footIk)})");
            return this;
        }

        public StateBuilder WithTag(string tag)
        {
            State.tag = tag ?? string.Empty;
            _root.Script?.Call(this, $"WithTag({RecipeScript.S(tag)})");
            return this;
        }

        // ---- transitions -----------------------------------------------------------

        public TransitionBuilder TransitionsTo(StateBuilder destination) =>
            Wire("TransitionsTo", destination.Path, ControllerIR.Transition.Target.State, destination);

        public TransitionBuilder TransitionsTo(MachineBuilder destination) =>
            Wire("TransitionsTo", destination.Prefix, ControllerIR.Transition.Target.Machine, destination);

        public TransitionBuilder Exits() =>
            Wire("Exits", string.Empty, ControllerIR.Transition.Target.Exit, null);

        TransitionBuilder Wire(string method, string path, ControllerIR.Transition.Target target,
            object destination)
        {
            var transition = MachineScope.NewTransition(target, path, isStateTransition: true);
            State.transitions.Add(transition);
            var builder = new TransitionBuilder(_root, transition);
            _root.Script?.Declare(builder, "t", this, destination == null
                ? $"{method}()"
                : $"{method}({_root.Script.NameArg(destination)})");
            return builder;
        }

        // ---- VRC Parameter Driver (AAC's Drives family) ------------------------------

        /// <summary>Starts another driver behaviour on this state; the Driving* calls that
        /// follow write into it. Only needed for a second driver — the first one appears on
        /// demand.</summary>
        public StateBuilder NewDriver(string instanceName = null)
        {
            _driver = MakeDriver(instanceName);
            _root.Script?.Call(this, instanceName == null
                ? "NewDriver()"
                : $"NewDriver({RecipeScript.S(instanceName)})");
            return this;
        }

        ControllerIR.Behaviour MakeDriver(string instanceName)
        {
            var behaviour = new ControllerIR.Behaviour
            {
                typeName = VrcParameterDriver.TypeName,
                driver = new ControllerIR.DriverSpec(),
                instanceName = instanceName ?? string.Empty,
            };
            State.behaviours.Add(behaviour);
            return behaviour;
        }

        ControllerIR.DriverSpec Driver()
        {
            if (_driver == null) _driver = MakeDriver(null);
            return _driver.driver;
        }

        public StateBuilder Drives(ParamHandle parameter, float value) =>
            Drive(new ControllerIR.DriverEntry { kind = 0, name = parameter.Name, value = value },
                parameter, $"Drives({{0}}, {RecipeScript.F(value)})");

        public StateBuilder Drives(BoolParam parameter, bool value) =>
            Drive(new ControllerIR.DriverEntry { kind = 0, name = parameter.Name, value = value ? 1f : 0f },
                parameter, $"Drives({{0}}, {RecipeScript.B(value)})");

        public StateBuilder DrivingIncreases(ParamHandle parameter, float amount) =>
            Drive(new ControllerIR.DriverEntry { kind = 1, name = parameter.Name, value = amount },
                parameter, $"DrivingIncreases({{0}}, {RecipeScript.F(amount)})");

        public StateBuilder DrivingDecreases(ParamHandle parameter, float amount) =>
            Drive(new ControllerIR.DriverEntry { kind = 1, name = parameter.Name, value = -amount },
                parameter, $"DrivingDecreases({{0}}, {RecipeScript.F(amount)})");

        public StateBuilder DrivingRandomizes(ParamHandle parameter, float min, float max) =>
            Drive(new ControllerIR.DriverEntry { kind = 2, name = parameter.Name, min = min, max = max },
                parameter, $"DrivingRandomizes({{0}}, {RecipeScript.F(min)}, {RecipeScript.F(max)})");

        /// <summary>Bool randomization: the chance of landing true.</summary>
        public StateBuilder DrivingRandomizes(BoolParam parameter, float chance) =>
            Drive(new ControllerIR.DriverEntry { kind = 2, name = parameter.Name, chance = chance },
                parameter, $"DrivingRandomizes({{0}}, {RecipeScript.F(chance)})");

        public StateBuilder DrivingCopies(ParamHandle source, ParamHandle destination)
        {
            Driver().entries.Add(new ControllerIR.DriverEntry
            { kind = 3, name = destination.Name, source = source.Name });
            _root.Script?.Call(this,
                $"DrivingCopies({_root.Script.NameArg(source)}, {_root.Script.NameArg(destination)})");
            return this;
        }

        public StateBuilder DrivingRemaps(ParamHandle source, float sourceMin, float sourceMax,
            ParamHandle destination, float destMin, float destMax)
        {
            Driver().entries.Add(new ControllerIR.DriverEntry
            {
                kind = 3,
                name = destination.Name,
                source = source.Name,
                convertRange = true,
                sourceMin = sourceMin,
                sourceMax = sourceMax,
                destMin = destMin,
                destMax = destMax,
            });
            _root.Script?.Call(this,
                $"DrivingRemaps({_root.Script.NameArg(source)}, {RecipeScript.F(sourceMin)}, {RecipeScript.F(sourceMax)}, "
                + $"{_root.Script.NameArg(destination)}, {RecipeScript.F(destMin)}, {RecipeScript.F(destMax)})");
            return this;
        }

        public StateBuilder DrivingLocally(bool on = true)
        {
            Driver().localOnly = on;
            _root.Script?.Call(this, on ? "DrivingLocally()" : "DrivingLocally(false)");
            return this;
        }

        StateBuilder Drive(ControllerIR.DriverEntry entry, ParamHandle parameter, string callFormat)
        {
            Driver().entries.Add(entry);
            _root.Script?.Call(this, string.Format(callFormat, _root.Script.NameArg(parameter)));
            return this;
        }

        // ---- other behaviours --------------------------------------------------------

        /// <summary>Any StateMachineBehaviour from an EditorJsonUtility snapshot — the
        /// fallback for SDK types without a typed builder.</summary>
        public StateBuilder BehaviourJson(string typeName, string json, string instanceName = null)
        {
            State.behaviours.Add(new ControllerIR.Behaviour
            {
                typeName = typeName,
                json = json,
                instanceName = instanceName ?? string.Empty,
            });
            _root.Script?.Call(this, instanceName == null
                ? $"BehaviourJson({RecipeScript.S(typeName)}, {RecipeScript.S(json)})"
                : $"BehaviourJson({RecipeScript.S(typeName)}, {RecipeScript.S(json)}, {RecipeScript.S(instanceName)})");
            return this;
        }

        /// <summary>Escape hatch: adds the behaviour and hands the live instance to
        /// <paramref name="configure"/> at apply time. Exported code never contains this.</summary>
        public StateBuilder Behaviour<T>(Action<T> configure) where T : StateMachineBehaviour
        {
            State.behaviours.Add(new ControllerIR.Behaviour
            {
                typeName = typeof(T).Name,
                configure = configure == null ? (Action<StateMachineBehaviour>)null
                    : b => configure((T)b),
            });
            return this;
        }
    }

    // ---- transitions ----------------------------------------------------------------

    public sealed class TransitionBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.Transition Transition;

        internal TransitionBuilder(ControllerBuilder root, ControllerIR.Transition transition)
        {
            _root = root;
            Transition = transition;
        }

        /// <summary>Adds a condition. Conditions AND together; When and And are synonyms so
        /// chains read naturally (.When(a.IsTrue()).And(b.IsGreaterThan(0.5f))).</summary>
        public TransitionBuilder When(Condition condition) => Append(condition, "When");

        public TransitionBuilder And(Condition condition) => Append(condition, "And");

        TransitionBuilder Append(Condition condition, string method)
        {
            if (condition == null) return this;
            Transition.conditions.Add(new ControllerIR.Condition
            {
                mode = condition.Mode,
                parameter = condition.Parameter,
                threshold = condition.Threshold,
            });
            if (condition.Source != null)
                _root.Script?.Call(this, $"{method}({condition.Source})");
            return this;
        }

        /// <summary>Exit time 1: leave when the animation completes a loop.</summary>
        public TransitionBuilder AfterAnimationFinishes()
        {
            Transition.hasExitTime = true;
            Transition.exitTime = 1f;
            _root.Script?.Call(this, "AfterAnimationFinishes()");
            return this;
        }

        public TransitionBuilder AfterAnimationIsAtLeastAtNormalized(float exitTimeNormalized)
        {
            Transition.hasExitTime = true;
            Transition.exitTime = exitTimeNormalized;
            _root.Script?.Call(this,
                $"AfterAnimationIsAtLeastAtNormalized({RecipeScript.F(exitTimeNormalized)})");
            return this;
        }

        public TransitionBuilder WithTransitionDurationSeconds(float seconds)
        {
            Transition.hasFixedDuration = true;
            Transition.duration = seconds;
            _root.Script?.Call(this, $"WithTransitionDurationSeconds({RecipeScript.F(seconds)})");
            return this;
        }

        public TransitionBuilder WithTransitionDurationNormalized(float fraction)
        {
            Transition.hasFixedDuration = false;
            Transition.duration = fraction;
            _root.Script?.Call(this, $"WithTransitionDurationNormalized({RecipeScript.F(fraction)})");
            return this;
        }

        public TransitionBuilder WithOffset(float offset)
        {
            Transition.offset = offset;
            _root.Script?.Call(this, $"WithOffset({RecipeScript.F(offset)})");
            return this;
        }

        public TransitionBuilder WithInterruption(TransitionInterruptionSource source)
        {
            Transition.interruptionSource = source;
            _root.Script?.Call(this, $"WithInterruption({RecipeScript.E(source)})");
            return this;
        }

        public TransitionBuilder WithOrderedInterruption()
        {
            Transition.orderedInterruption = true;
            _root.Script?.Call(this, "WithOrderedInterruption()");
            return this;
        }

        public TransitionBuilder WithNoOrderedInterruption()
        {
            Transition.orderedInterruption = false;
            _root.Script?.Call(this, "WithNoOrderedInterruption()");
            return this;
        }

        public TransitionBuilder WithTransitionToSelf()
        {
            Transition.canTransitionToSelf = true;
            _root.Script?.Call(this, "WithTransitionToSelf()");
            return this;
        }

        public TransitionBuilder WithNoTransitionToSelf()
        {
            Transition.canTransitionToSelf = false;
            _root.Script?.Call(this, "WithNoTransitionToSelf()");
            return this;
        }

        public TransitionBuilder Solo(bool on = true)
        {
            Transition.solo = on;
            _root.Script?.Call(this, on ? "Solo()" : "Solo(false)");
            return this;
        }

        public TransitionBuilder Mute(bool on = true)
        {
            Transition.mute = on;
            _root.Script?.Call(this, on ? "Mute()" : "Mute(false)");
            return this;
        }
    }

    // ---- blend trees ------------------------------------------------------------------

    /// <summary>An embedded blend tree (create with <see cref="ControllerBuilder.NewBlendTree"/>,
    /// attach with WithAnimation) — AAC's NewBlendTree flow, including chained children.</summary>
    public sealed class TreeBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.Tree Tree;
        TreeChildBuilder _lastChild;

        /// <summary>Slot options of the most recently added child (time scale, mirror…),
        /// for the rare settings the WithAnimation signatures don't carry.</summary>
        public TreeChildBuilder LastChild => _lastChild;

        internal TreeBuilder(ControllerBuilder root, ControllerIR.Tree tree)
        {
            _root = root;
            Tree = tree;
        }

        public TreeBuilder Simple1D(FloatParam parameter)
        {
            Tree.type = BlendTreeType.Simple1D;
            Tree.blendParameter = parameter.Name;
            _root.Script?.Call(this, $"Simple1D({_root.Script.NameArg(parameter)})");
            return this;
        }

        public TreeBuilder SimpleDirectional2D(FloatParam parameterX, FloatParam parameterY) =>
            TwoD(BlendTreeType.SimpleDirectional2D, "SimpleDirectional2D", parameterX, parameterY);

        public TreeBuilder FreeformDirectional2D(FloatParam parameterX, FloatParam parameterY) =>
            TwoD(BlendTreeType.FreeformDirectional2D, "FreeformDirectional2D", parameterX, parameterY);

        public TreeBuilder FreeformCartesian2D(FloatParam parameterX, FloatParam parameterY) =>
            TwoD(BlendTreeType.FreeformCartesian2D, "FreeformCartesian2D", parameterX, parameterY);

        TreeBuilder TwoD(BlendTreeType type, string method, FloatParam x, FloatParam y)
        {
            Tree.type = type;
            Tree.blendParameter = x.Name;
            Tree.blendParameterY = y.Name;
            _root.Script?.Call(this,
                $"{method}({_root.Script.NameArg(x)}, {_root.Script.NameArg(y)})");
            return this;
        }

        public TreeBuilder Direct()
        {
            Tree.type = BlendTreeType.Direct;
            _root.Script?.Call(this, "Direct()");
            return this;
        }

        public TreeBuilder AutoThresholds(bool on)
        {
            Tree.useAutomaticThresholds = on;
            _root.Script?.Call(this, $"AutoThresholds({RecipeScript.B(on)})");
            return this;
        }

        public TreeBuilder ThresholdRange(float min, float max)
        {
            Tree.minThreshold = min;
            Tree.maxThreshold = max;
            _root.Script?.Call(this, $"ThresholdRange({RecipeScript.F(min)}, {RecipeScript.F(max)})");
            return this;
        }

        public TreeBuilder NormalizedBlendValues(bool on = true)
        {
            Tree.normalizedBlendValues = on;
            _root.Script?.Call(this, on ? "NormalizedBlendValues()" : "NormalizedBlendValues(false)");
            return this;
        }

        // ---- children (AAC WithAnimation overloads) -----------------------------------

        public TreeBuilder WithAnimation(Motion motion) =>
            Child(motion, null, $"WithAnimation({_root.Script?.AssetRef(motion)})", null);

        /// <summary>1D child at an explicit threshold.</summary>
        public TreeBuilder WithAnimation(Motion motion, float threshold) =>
            Child(motion, null,
                $"WithAnimation({_root.Script?.AssetRef(motion)}, {RecipeScript.F(threshold)})",
                child => child.threshold = threshold);

        /// <summary>2D child at a blend-space position.</summary>
        public TreeBuilder WithAnimation(Motion motion, float x, float y) =>
            Child(motion, null,
                $"WithAnimation({_root.Script?.AssetRef(motion)}, {RecipeScript.F(x)}, {RecipeScript.F(y)})",
                child => child.position = new Vector2(x, y));

        /// <summary>Direct child weighted by a Float parameter.</summary>
        public TreeBuilder WithAnimation(Motion motion, FloatParam directParameter) =>
            Child(motion, null,
                $"WithAnimation({_root.Script?.AssetRef(motion)}, {_root.Script?.NameArg(directParameter)})",
                child => child.directParameter = directParameter.Name);

        public TreeBuilder WithAnimation(TreeBuilder blendTree) =>
            Child(null, blendTree.Tree, $"WithAnimation({_root.Script?.NameArg(blendTree)})", null);

        public TreeBuilder WithAnimation(TreeBuilder blendTree, float threshold) =>
            Child(null, blendTree.Tree,
                $"WithAnimation({_root.Script?.NameArg(blendTree)}, {RecipeScript.F(threshold)})",
                child => child.threshold = threshold);

        public TreeBuilder WithAnimation(TreeBuilder blendTree, float x, float y) =>
            Child(null, blendTree.Tree,
                $"WithAnimation({_root.Script?.NameArg(blendTree)}, {RecipeScript.F(x)}, {RecipeScript.F(y)})",
                child => child.position = new Vector2(x, y));

        public TreeBuilder WithAnimation(TreeBuilder blendTree, FloatParam directParameter) =>
            Child(null, blendTree.Tree,
                $"WithAnimation({_root.Script?.NameArg(blendTree)}, {_root.Script?.NameArg(directParameter)})",
                child => child.directParameter = directParameter.Name);

        TreeBuilder Child(Motion motion, ControllerIR.Tree nested, string call,
            Action<ControllerIR.TreeChild> configure)
        {
            var child = new ControllerIR.TreeChild { motionAsset = motion, tree = nested };
            configure?.Invoke(child);
            Tree.children.Add(child);
            _lastChild = new TreeChildBuilder(_root, child);
            if (_root.Script != null)
            {
                _root.Script.Call(this, call);
                _root.Script.RegisterAlias(_lastChild, _root.Script.NameArg(this) + ".LastChild");
            }
            return this;
        }
    }

    /// <summary>Slot options of one blend-tree child.</summary>
    public sealed class TreeChildBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.TreeChild Child;

        internal TreeChildBuilder(ControllerBuilder root, ControllerIR.TreeChild child)
        {
            _root = root;
            Child = child;
        }

        public TreeChildBuilder Threshold(float threshold)
        {
            Child.threshold = threshold;
            _root.Script?.Call(this, $"Threshold({RecipeScript.F(threshold)})");
            return this;
        }

        public TreeChildBuilder Position(float x, float y)
        {
            Child.position = new Vector2(x, y);
            _root.Script?.Call(this, $"Position({RecipeScript.F(x)}, {RecipeScript.F(y)})");
            return this;
        }

        public TreeChildBuilder TimeScale(float scale)
        {
            Child.timeScale = scale;
            _root.Script?.Call(this, $"TimeScale({RecipeScript.F(scale)})");
            return this;
        }

        public TreeChildBuilder CycleOffset(float offset)
        {
            Child.cycleOffset = offset;
            _root.Script?.Call(this, $"CycleOffset({RecipeScript.F(offset)})");
            return this;
        }

        public TreeChildBuilder Mirror(bool on = true)
        {
            Child.mirror = on;
            _root.Script?.Call(this, on ? "Mirror()" : "Mirror(false)");
            return this;
        }
    }
}
