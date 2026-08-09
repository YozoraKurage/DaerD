using System;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
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
}
