using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// The recipe-facing fluent API for describing an AnimatorController in C#. A recipe's
    /// Build method declares parameters, layers, states, transitions and blend trees through
    /// these builders; <see cref="ControllerRecipe.Generate"/> then applies the result to the
    /// target controller. Everything is data until Apply — nothing here touches a controller
    /// directly (use <see cref="Raw"/> for that).
    ///
    /// Quick reference (also emitted at the top of exported recipes):
    ///   c.Float("Blend", 0.5f);  c.Bool("Go");  c.Int("Step");  c.Trigger("Fire");
    ///   var fx = c.Layer("Hand").Weight(1f);
    ///   var idle = fx.State("Idle", idleClip).At(260, 60).Default();
    ///   idle.To(other).If("Go").IfGreater("Blend", 0.5f).Duration(0.15f);
    ///   other.ToExit().IfNot("Go");   fx.AnyTo(idle).IfIntEquals("Step", 2);
    ///   var tree = walk.Tree("Move").Blend2D("X", "Y");  tree.Add(runClip).Position(0, 1);
    ///   idle.Driver().LocalOnly().Set("Step", 1f).Copy("A", "B");
    /// </summary>
    public sealed class ControllerBuilder
    {
        internal readonly ControllerIR IR = new ControllerIR();
        internal readonly List<Action<AnimatorController>> PostOps =
            new List<Action<AnimatorController>>();
        internal readonly List<string> Notes = new List<string>();
        internal readonly List<Action> PostBakeSyncs = new List<Action>();
        internal RecipeScript Script;

        // ---- parameters --------------------------------------------------------

        public ControllerBuilder Float(string name, float defaultValue = 0f)
        {
            IR.parameters.Add(new ControllerIR.Param
            { name = name, type = AnimatorControllerParameterType.Float, defaultFloat = defaultValue });
            Script?.Call(this, defaultValue == 0f
                ? $"Float({RecipeScript.S(name)})"
                : $"Float({RecipeScript.S(name)}, {RecipeScript.F(defaultValue)})");
            return this;
        }

        public ControllerBuilder Int(string name, int defaultValue = 0)
        {
            IR.parameters.Add(new ControllerIR.Param
            { name = name, type = AnimatorControllerParameterType.Int, defaultInt = defaultValue });
            Script?.Call(this, defaultValue == 0
                ? $"Int({RecipeScript.S(name)})"
                : $"Int({RecipeScript.S(name)}, {defaultValue})");
            return this;
        }

        public ControllerBuilder Bool(string name, bool defaultValue = false)
        {
            IR.parameters.Add(new ControllerIR.Param
            { name = name, type = AnimatorControllerParameterType.Bool, defaultBool = defaultValue });
            Script?.Call(this, defaultValue
                ? $"Bool({RecipeScript.S(name)}, true)"
                : $"Bool({RecipeScript.S(name)})");
            return this;
        }

        public ControllerBuilder Trigger(string name)
        {
            IR.parameters.Add(new ControllerIR.Param
            { name = name, type = AnimatorControllerParameterType.Trigger });
            Script?.Call(this, $"Trigger({RecipeScript.S(name)})");
            return this;
        }

        // ---- layers ------------------------------------------------------------

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

        /// <summary>
        /// Escape hatch: runs after the declared layers are applied, with the live controller.
        /// Anything DaerD or Unity can do is available here — at the price that Verify can't
        /// see what it did. Exported code never contains Raw calls.
        /// </summary>
        public ControllerBuilder Raw(Action<AnimatorController> action)
        {
            if (action != null) PostOps.Add(action);
            return this;
        }

        // ---- bake --------------------------------------------------------------

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
            var parameterNames = new HashSet<string>();
            foreach (var parameter in IR.parameters)
                if (!parameterNames.Add(parameter.name))
                    problems.Add(L.Tr("Parameter '{0}' is declared more than once.", parameter.name));
            return problems;
        }

        internal int IndexOfLayer(string name)
        {
            for (int i = 0; i < IR.layers.Count; i++)
                if (IR.layers[i].name == name) return i;
            return -1;
        }
    }

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

        public StateBuilder State(string name, Motion motion = null)
        {
            var state = new ControllerIR.State { name = name, motionAsset = motion };
            Machine.states.Add(state);
            // Mirrors Unity: the first state of a machine is its default until told otherwise.
            string path = ControllerIR.Join(Prefix, name);
            if (Machine.states.Count == 1) Machine.defaultState = path;
            var builder = new StateBuilder(Root, this, state, path);
            Root.Script?.Declare(builder, name, this, motion == null
                ? $"State({RecipeScript.S(name)})"
                : $"State({RecipeScript.S(name)}, {Root.Script.AssetRef(motion)})");
            return builder;
        }

        /// <summary>A nested sub-state machine.</summary>
        public MachineBuilder AddMachine(string name)
        {
            var child = new ControllerIR.ChildMachine { machine = new ControllerIR.Machine { name = name } };
            Machine.machines.Add(child);
            var builder = new MachineBuilder(Root, child, ControllerIR.Join(Prefix, name));
            Root.Script?.Declare(builder, name, this, $"AddMachine({RecipeScript.S(name)})");
            return builder;
        }

        public TransitionBuilder AnyTo(StateBuilder destination) =>
            Wire("AnyTo", Machine.anyStateTransitions, destination.Path,
                ControllerIR.Transition.Target.State, isStateTransition: true, destination);

        public TransitionBuilder AnyTo(MachineBuilder destination) =>
            Wire("AnyTo", Machine.anyStateTransitions, destination.Prefix,
                ControllerIR.Transition.Target.Machine, isStateTransition: true, destination);

        public TransitionBuilder EntryTo(StateBuilder destination) =>
            Wire("EntryTo", Machine.entryTransitions, destination.Path,
                ControllerIR.Transition.Target.State, isStateTransition: false, destination);

        public TransitionBuilder EntryTo(MachineBuilder destination) =>
            Wire("EntryTo", Machine.entryTransitions, destination.Prefix,
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

        public void EntryAt(float x, float y)
        {
            Machine.entryPosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"EntryAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
        }

        public void ExitAt(float x, float y)
        {
            Machine.exitPosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"ExitAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
        }

        public void AnyStateAt(float x, float y)
        {
            Machine.anyStatePosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"AnyStateAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
        }

        public void ParentAt(float x, float y)
        {
            Machine.parentPosition = new Vector3(x, y, 0f);
            Root.Script?.Call(this, $"ParentAt({RecipeScript.F(x)}, {RecipeScript.F(y)})");
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

        public LayerBuilder Weight(float weight)
        {
            _layer.defaultWeight = weight;
            Root.Script?.Call(this, $"Weight({RecipeScript.F(weight)})");
            return this;
        }

        public LayerBuilder Additive()
        {
            _layer.blending = AnimatorLayerBlendingMode.Additive;
            Root.Script?.Call(this, "Additive()");
            return this;
        }

        public LayerBuilder IkPass(bool on = true)
        {
            _layer.ikPass = on;
            Root.Script?.Call(this, on ? "IkPass()" : "IkPass(false)");
            return this;
        }

        public LayerBuilder Mask(AvatarMask mask)
        {
            _layer.mask = mask;
            Root.Script?.Call(this, $"Mask({Root.Script.AssetRef(mask)})");
            return this;
        }
    }

    /// <summary>Synced layer: mirrors a source layer's states, overriding motions only.</summary>
    public sealed class SyncedLayerBuilder
    {
        readonly ControllerBuilder _root;
        readonly ControllerIR.Layer _layer;
        readonly string _sourceLayer;

        internal SyncedLayerBuilder(ControllerBuilder root, ControllerIR.Layer layer, string sourceLayer)
        {
            _root = root;
            _layer = layer;
            _sourceLayer = sourceLayer;
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

        public SyncedLayerBuilder Weight(float weight)
        {
            _layer.defaultWeight = weight;
            _root.Script?.Call(this, $"Weight({RecipeScript.F(weight)})");
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
        public TransitionBuilder To(StateBuilder destination) =>
            Source("To", destination.Path, ControllerIR.Transition.Target.State, destination);

        public TransitionBuilder To(MachineBuilder destination) =>
            Source("To", destination.Prefix, ControllerIR.Transition.Target.Machine, destination);

        public TransitionBuilder ToExit() =>
            Source("ToExit", string.Empty, ControllerIR.Transition.Target.Exit, null);

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

    public sealed class StateBuilder
    {
        readonly ControllerBuilder _root;
        readonly MachineScope _scope;
        internal readonly ControllerIR.State State;
        internal readonly string Path;

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

        public StateBuilder Motion(Motion motion)
        {
            State.motionAsset = motion;
            State.tree = null;
            _root.Script?.Call(this, $"Motion({_root.Script.AssetRef(motion)})");
            return this;
        }

        /// <summary>Starts an embedded blend tree as this state's motion.</summary>
        public TreeBuilder Tree(string name = "Blend Tree")
        {
            var tree = new ControllerIR.Tree { name = name };
            State.tree = tree;
            State.motionAsset = null;
            var builder = new TreeBuilder(_root, tree, null);
            _root.Script?.Declare(builder, name, this, $"Tree({RecipeScript.S(name)})");
            return builder;
        }

        public StateBuilder Speed(float speed)
        {
            State.speed = speed;
            _root.Script?.Call(this, $"Speed({RecipeScript.F(speed)})");
            return this;
        }

        public StateBuilder CycleOffset(float offset)
        {
            State.cycleOffset = offset;
            _root.Script?.Call(this, $"CycleOffset({RecipeScript.F(offset)})");
            return this;
        }

        public StateBuilder Mirror(bool on = true)
        {
            State.mirror = on;
            _root.Script?.Call(this, on ? "Mirror()" : "Mirror(false)");
            return this;
        }

        public StateBuilder FootIK(bool on = true)
        {
            State.ikOnFeet = on;
            _root.Script?.Call(this, on ? "FootIK()" : "FootIK(false)");
            return this;
        }

        public StateBuilder WriteDefaults(bool on)
        {
            State.writeDefaultValues = on;
            _root.Script?.Call(this, $"WriteDefaults({RecipeScript.B(on)})");
            return this;
        }

        public StateBuilder Tag(string tag)
        {
            State.tag = tag ?? string.Empty;
            _root.Script?.Call(this, $"Tag({RecipeScript.S(tag)})");
            return this;
        }

        public StateBuilder SpeedBy(string parameter) =>
            Drive(v => { State.speedParameterActive = true; State.speedParameter = v; }, "SpeedBy", parameter);

        public StateBuilder MirrorBy(string parameter) =>
            Drive(v => { State.mirrorParameterActive = true; State.mirrorParameter = v; }, "MirrorBy", parameter);

        public StateBuilder CycleOffsetBy(string parameter) =>
            Drive(v => { State.cycleOffsetParameterActive = true; State.cycleOffsetParameter = v; }, "CycleOffsetBy", parameter);

        public StateBuilder TimeBy(string parameter) =>
            Drive(v => { State.timeParameterActive = true; State.timeParameter = v; }, "TimeBy", parameter);

        StateBuilder Drive(Action<string> apply, string method, string parameter)
        {
            apply(parameter ?? string.Empty);
            _root.Script?.Call(this, $"{method}({RecipeScript.S(parameter)})");
            return this;
        }

        // ---- transitions -------------------------------------------------------

        public TransitionBuilder To(StateBuilder destination) =>
            Wire("To", destination.Path, ControllerIR.Transition.Target.State, destination);

        public TransitionBuilder To(MachineBuilder destination) =>
            Wire("To", destination.Prefix, ControllerIR.Transition.Target.Machine, destination);

        public TransitionBuilder ToExit() =>
            Wire("ToExit", string.Empty, ControllerIR.Transition.Target.Exit, null);

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

        // ---- behaviours --------------------------------------------------------

        /// <summary>A VRC Avatar Parameter Driver on this state.</summary>
        public DriverBuilder Driver(string instanceName = null)
        {
            var behaviour = new ControllerIR.Behaviour
            {
                typeName = VrcParameterDriver.TypeName,
                driver = new ControllerIR.DriverSpec(),
                instanceName = instanceName ?? string.Empty,
            };
            State.behaviours.Add(behaviour);
            var builder = new DriverBuilder(_root, behaviour.driver);
            _root.Script?.Declare(builder, "d", this, instanceName == null
                ? "Driver()"
                : $"Driver({RecipeScript.S(instanceName)})");
            return builder;
        }

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

    public sealed class TransitionBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.Transition Transition;

        internal TransitionBuilder(ControllerBuilder root, ControllerIR.Transition transition)
        {
            _root = root;
            Transition = transition;
        }

        public TransitionBuilder If(string parameter) =>
            Condition(AnimatorConditionMode.If, parameter, 0f, $"If({RecipeScript.S(parameter)})");

        public TransitionBuilder IfNot(string parameter) =>
            Condition(AnimatorConditionMode.IfNot, parameter, 0f, $"IfNot({RecipeScript.S(parameter)})");

        public TransitionBuilder IfGreater(string parameter, float threshold) =>
            Condition(AnimatorConditionMode.Greater, parameter, threshold,
                $"IfGreater({RecipeScript.S(parameter)}, {RecipeScript.F(threshold)})");

        public TransitionBuilder IfLess(string parameter, float threshold) =>
            Condition(AnimatorConditionMode.Less, parameter, threshold,
                $"IfLess({RecipeScript.S(parameter)}, {RecipeScript.F(threshold)})");

        public TransitionBuilder IfIntEquals(string parameter, int value) =>
            Condition(AnimatorConditionMode.Equals, parameter, value,
                $"IfIntEquals({RecipeScript.S(parameter)}, {value})");

        public TransitionBuilder IfIntNotEquals(string parameter, int value) =>
            Condition(AnimatorConditionMode.NotEqual, parameter, value,
                $"IfIntNotEquals({RecipeScript.S(parameter)}, {value})");

        TransitionBuilder Condition(AnimatorConditionMode mode, string parameter, float threshold,
            string call)
        {
            Transition.conditions.Add(new ControllerIR.Condition
            { mode = mode, parameter = parameter ?? string.Empty, threshold = threshold });
            _root.Script?.Call(this, call);
            return this;
        }

        /// <summary>Leave the current state at this normalized time (enables exit time).</summary>
        public TransitionBuilder ExitTime(float normalizedTime)
        {
            Transition.hasExitTime = true;
            Transition.exitTime = normalizedTime;
            _root.Script?.Call(this, $"ExitTime({RecipeScript.F(normalizedTime)})");
            return this;
        }

        /// <summary>Blend duration in seconds (fixed duration).</summary>
        public TransitionBuilder Duration(float seconds)
        {
            Transition.hasFixedDuration = true;
            Transition.duration = seconds;
            _root.Script?.Call(this, $"Duration({RecipeScript.F(seconds)})");
            return this;
        }

        /// <summary>Blend duration as a fraction of the source state.</summary>
        public TransitionBuilder DurationNormalized(float fraction)
        {
            Transition.hasFixedDuration = false;
            Transition.duration = fraction;
            _root.Script?.Call(this, $"DurationNormalized({RecipeScript.F(fraction)})");
            return this;
        }

        public TransitionBuilder Offset(float offset)
        {
            Transition.offset = offset;
            _root.Script?.Call(this, $"Offset({RecipeScript.F(offset)})");
            return this;
        }

        public TransitionBuilder Interruption(TransitionInterruptionSource source, bool ordered = true)
        {
            Transition.interruptionSource = source;
            Transition.orderedInterruption = ordered;
            _root.Script?.Call(this, ordered
                ? $"Interruption({RecipeScript.E(source)})"
                : $"Interruption({RecipeScript.E(source)}, false)");
            return this;
        }

        public TransitionBuilder CanTransitionToSelf(bool on = true)
        {
            Transition.canTransitionToSelf = on;
            _root.Script?.Call(this, on ? "CanTransitionToSelf()" : "CanTransitionToSelf(false)");
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

    public sealed class TreeBuilder
    {
        readonly ControllerBuilder _root;
        internal readonly ControllerIR.Tree Tree;

        /// <summary>The child slot this tree occupies in its parent (null for a state's root
        /// tree) — thresholds and 2D positions of a nested tree live on the slot.</summary>
        public TreeChildBuilder Slot { get; }

        internal TreeBuilder(ControllerBuilder root, ControllerIR.Tree tree, TreeChildBuilder slot)
        {
            _root = root;
            Tree = tree;
            Slot = slot;
        }

        public TreeBuilder Blend1D(string parameter)
        {
            Tree.type = BlendTreeType.Simple1D;
            Tree.blendParameter = parameter;
            _root.Script?.Call(this, $"Blend1D({RecipeScript.S(parameter)})");
            return this;
        }

        public TreeBuilder Blend2D(string parameterX, string parameterY,
            BlendTreeType type = BlendTreeType.FreeformDirectional2D)
        {
            Tree.type = type;
            Tree.blendParameter = parameterX;
            Tree.blendParameterY = parameterY;
            _root.Script?.Call(this, type == BlendTreeType.FreeformDirectional2D
                ? $"Blend2D({RecipeScript.S(parameterX)}, {RecipeScript.S(parameterY)})"
                : $"Blend2D({RecipeScript.S(parameterX)}, {RecipeScript.S(parameterY)}, {RecipeScript.E(type)})");
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

        public TreeChildBuilder Add(Motion motion)
        {
            var child = new ControllerIR.TreeChild { motionAsset = motion };
            Tree.children.Add(child);
            var builder = new TreeChildBuilder(_root, child);
            _root.Script?.Declare(builder, "slot", this, $"Add({_root.Script.AssetRef(motion)})");
            return builder;
        }

        /// <summary>A nested blend tree in the next child slot.</summary>
        public TreeBuilder AddTree(string name)
        {
            var child = new ControllerIR.TreeChild { tree = new ControllerIR.Tree { name = name } };
            Tree.children.Add(child);
            var slot = new TreeChildBuilder(_root, child);
            var builder = new TreeBuilder(_root, child.tree, slot);
            _root.Script?.Declare(builder, name, this, $"AddTree({RecipeScript.S(name)})");
            return builder;
        }
    }

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

        public TreeChildBuilder DirectParameter(string parameter)
        {
            Child.directParameter = parameter ?? string.Empty;
            _root.Script?.Call(this, $"DirectParameter({RecipeScript.S(parameter)})");
            return this;
        }
    }

    /// <summary>VRC Avatar Parameter Driver contents (entries run top to bottom).</summary>
    public sealed class DriverBuilder
    {
        readonly ControllerBuilder _root;
        readonly ControllerIR.DriverSpec _spec;

        internal DriverBuilder(ControllerBuilder root, ControllerIR.DriverSpec spec)
        {
            _root = root;
            _spec = spec;
        }

        public DriverBuilder LocalOnly(bool on = true)
        {
            _spec.localOnly = on;
            _root.Script?.Call(this, on ? "LocalOnly()" : "LocalOnly(false)");
            return this;
        }

        public DriverBuilder Set(string parameter, float value)
        {
            _spec.entries.Add(new ControllerIR.DriverEntry { kind = 0, name = parameter, value = value });
            _root.Script?.Call(this, $"Set({RecipeScript.S(parameter)}, {RecipeScript.F(value)})");
            return this;
        }

        public DriverBuilder Add(string parameter, float value)
        {
            _spec.entries.Add(new ControllerIR.DriverEntry { kind = 1, name = parameter, value = value });
            _root.Script?.Call(this, $"Add({RecipeScript.S(parameter)}, {RecipeScript.F(value)})");
            return this;
        }

        public DriverBuilder Random(string parameter, float min, float max, float chance = 1f)
        {
            _spec.entries.Add(new ControllerIR.DriverEntry
            { kind = 2, name = parameter, min = min, max = max, chance = chance });
            _root.Script?.Call(this, chance == 1f
                ? $"Random({RecipeScript.S(parameter)}, {RecipeScript.F(min)}, {RecipeScript.F(max)})"
                : $"Random({RecipeScript.S(parameter)}, {RecipeScript.F(min)}, {RecipeScript.F(max)}, {RecipeScript.F(chance)})");
            return this;
        }

        public DriverBuilder Copy(string source, string destination)
        {
            _spec.entries.Add(new ControllerIR.DriverEntry { kind = 3, name = destination, source = source });
            _root.Script?.Call(this, $"Copy({RecipeScript.S(source)}, {RecipeScript.S(destination)})");
            return this;
        }

        public DriverBuilder CopyRange(string source, string destination,
            float sourceMin, float sourceMax, float destMin, float destMax)
        {
            _spec.entries.Add(new ControllerIR.DriverEntry
            {
                kind = 3,
                name = destination,
                source = source,
                convertRange = true,
                sourceMin = sourceMin,
                sourceMax = sourceMax,
                destMin = destMin,
                destMax = destMax,
            });
            _root.Script?.Call(this,
                $"CopyRange({RecipeScript.S(source)}, {RecipeScript.S(destination)}, {RecipeScript.F(sourceMin)}, {RecipeScript.F(sourceMax)}, {RecipeScript.F(destMin)}, {RecipeScript.F(destMax)})");
            return this;
        }
    }
}
