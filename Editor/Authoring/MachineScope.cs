using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
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

        // ---- behaviours on the machine itself -----------------------------------

        /// <summary>
        /// A StateMachineBehaviour on this machine (the layer's root, or a sub-machine) rather
        /// than on a state — Unity allows both, and OnStateMachineEnter/Exit is the difference.
        /// From an EditorJsonUtility snapshot, like the state-level fallback.
        /// </summary>
        public MachineScope BehaviourJson(string typeName, string json, string instanceName = null)
        {
            Machine.behaviours.Add(new ControllerIR.Behaviour
            {
                typeName = typeName,
                json = json,
                instanceName = instanceName ?? string.Empty,
            });
            // Never chained into the declaration line: this returns the base scope, so
            // "var sub = main.NewSubStateMachine("Sub").BehaviourJson(…)" would type `sub` as
            // MachineScope and the layout block's sub.At(x, y) would stop compiling.
            Root.Script?.Call(this, instanceName == null
                ? $"BehaviourJson({RecipeScript.S(typeName)}, {RecipeScript.S(json)})"
                : $"BehaviourJson({RecipeScript.S(typeName)}, {RecipeScript.S(json)}, {RecipeScript.S(instanceName)})",
                chain: false);
            return this;
        }

        /// <summary>Escape hatch, as on a state: adds the behaviour and hands the live
        /// instance to <paramref name="configure"/> at apply time.</summary>
        public MachineScope Behaviour<T>(Action<T> configure) where T : StateMachineBehaviour
        {
            Machine.behaviours.Add(new ControllerIR.Behaviour
            {
                typeName = typeof(T).Name,
                configure = configure == null ? (Action<StateMachineBehaviour>)null
                    : b => configure((T)b),
            });
            return this;
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
}
