using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.IR
{
    /// <summary>
    /// Turns a <see cref="ControllerIR"/> back into a live AnimatorController. Two modes:
    /// exclusive replaces the whole controller (parameters and layers become exactly the IR),
    /// partial replaces only the IR's layers by name (existing position kept, otherwise
    /// appended) and merges parameters without ever retyping an existing one — parameter
    /// mismatches are the user's business, not ours.
    ///
    /// Returns warnings for whatever couldn't be reproduced (a behaviour type missing with
    /// the SDK absent, an unresolvable transition destination) instead of failing halfway.
    ///
    /// <para>BUILT IN BATCHES.</para>
    /// States, sub-machines, transitions and behaviours are made here and handed to their owner
    /// as one array per owner, instead of through AddState / AddTransition one at a time. Each
    /// of those copies the owner's whole array, registers an Undo step and asks the asset
    /// database where the controller lives — so a machine built through them costs more per
    /// object the bigger it already is. Measured (Unity 2022.3, 600 states, 2400 transitions):
    /// about 1.6 s through Add* against 0.1 s batched in memory, 2.3 s against 0.18 s on disk.
    /// What the Add* calls did besides appending is done here explicitly: unique names, the
    /// first state becoming the default, HideInHierarchy, and filing and Undo through the
    /// run's <see cref="GenerationTarget"/>.
    /// </summary>
    static class ControllerIRBuilder
    {
        const string UndoName = "Apply Recipe";

        class Context
        {
            public GenerationTarget target;
            public AnimatorController controller => target.Controller;
            public readonly List<string> warnings = new List<string>();
            public readonly Dictionary<string, AnimatorState> states =
                new Dictionary<string, AnimatorState>();
            public readonly Dictionary<string, AnimatorStateMachine> machines =
                new Dictionary<string, AnimatorStateMachine>();
        }

        public static List<string> Rebuild(ControllerIR ir, AnimatorController controller,
            bool exclusive)
        {
            var warnings = new List<string>();
            if (ir == null || controller == null) return warnings;

            var target = GenerationTarget.Of(controller);
            using (target.RecordsUndo ? new UndoScope(UndoName) : (IDisposable)null)
            {
                target.Changing(controller, UndoName);
                ApplyParameters(ir, controller, exclusive, warnings);

                // Records in GraphFrameData (async-sync setups, badges, frames, notes) hang
                // off the OLD state machines about to be destroyed; remember who was who so
                // the rebuilt layers can inherit them.
                var oldMachineIds = new Dictionary<string, int>();
                foreach (var layer in controller.layers)
                    if (layer.stateMachine != null && !oldMachineIds.ContainsKey(layer.name))
                        oldMachineIds[layer.name] = layer.stateMachine.GetInstanceID();

                if (exclusive)
                    for (int i = controller.layers.Length - 1; i >= 0; i--)
                        controller.RemoveLayer(i);
                else if (AnyForeignSyncedLayer(ir, controller))
                    warnings.Add(L.Tr("Other layers in this controller are synced layers; replacing layers by name can shift the indices they point at."));

                // Two passes over the layers: create everything first (synced layers and
                // cross-layer anything need the final list), then wire the synced overrides.
                var built = new List<int>();
                foreach (var layer in ir.layers)
                    built.Add(BuildLayer(layer, target, exclusive, oldMachineIds, warnings));

                for (int i = 0; i < ir.layers.Count; i++)
                    if (ir.layers[i].syncedLayerIndex >= 0)
                        ApplySyncedOverrides(ir.layers[i], built[i], target, warnings);

                target.Dirty(controller);
            }
            return warnings;
        }

        static bool AnyForeignSyncedLayer(ControllerIR ir, AnimatorController controller)
        {
            var replaced = new HashSet<string>();
            foreach (var layer in ir.layers) replaced.Add(layer.name);
            foreach (var layer in controller.layers)
                if (layer.syncedLayerIndex >= 0 && !replaced.Contains(layer.name))
                    return true;
            return false;
        }

        // ---- parameters --------------------------------------------------------

        static void ApplyParameters(ControllerIR ir, AnimatorController controller,
            bool exclusive, List<string> warnings)
        {
            if (exclusive)
            {
                var parameters = new AnimatorControllerParameter[ir.parameters.Count];
                for (int i = 0; i < ir.parameters.Count; i++)
                    parameters[i] = ToParameter(ir.parameters[i]);
                controller.parameters = parameters;
                return;
            }

            foreach (var declared in ir.parameters)
            {
                var existing = DbtBuilder.FindParameter(controller, declared.name);
                if (existing == null)
                {
                    controller.AddParameter(ToParameter(declared));
                    continue;
                }
                if (existing.type != declared.type)
                {
                    // Never retype in place — an intentional expression/animator mismatch is
                    // a supported VRChat pattern, and silently "fixing" it breaks avatars.
                    warnings.Add(L.Tr("Parameter '{0}' already exists as {1} (recipe declares {2}); left unchanged.",
                        declared.name, existing.type, declared.type));
                    continue;
                }
                // The recipe is the source of truth for defaults it explicitly states; a
                // reference-only handle leaves the existing default alone.
                if (!declared.hasDefault) continue;
                var parameters = controller.parameters;
                for (int i = 0; i < parameters.Length; i++)
                    if (parameters[i].name == declared.name)
                    {
                        parameters[i].defaultFloat = declared.defaultFloat;
                        parameters[i].defaultInt = declared.defaultInt;
                        parameters[i].defaultBool = declared.defaultBool;
                    }
                controller.parameters = parameters;
            }
        }

        static AnimatorControllerParameter ToParameter(ControllerIR.Param declared) =>
            new AnimatorControllerParameter
            {
                name = declared.name,
                type = declared.type,
                defaultFloat = declared.defaultFloat,
                defaultInt = declared.defaultInt,
                defaultBool = declared.defaultBool,
            };

        // ---- layers ------------------------------------------------------------

        /// <summary>Builds one layer; returns its index in the controller.</summary>
        static int BuildLayer(ControllerIR.Layer ir, GenerationTarget target,
            bool exclusive, Dictionary<string, int> oldMachineIds, List<string> warnings)
        {
            var controller = target.Controller;
            int existing = exclusive ? -1 : FindLayerIndex(controller, ir.name);
            if (existing >= 0)
                controller.RemoveLayer(existing);

            var layer = new AnimatorControllerLayer
            {
                name = ir.name,
                defaultWeight = ir.defaultWeight,
                blendingMode = ir.blending,
                iKPass = ir.ikPass,
                avatarMask = ir.mask,
                syncedLayerIndex = ir.syncedLayerIndex,
                syncedLayerAffectsTiming = ir.syncedLayerAffectsTiming,
            };

            var context = new Context { target = target };

            if (ir.machine != null)
            {
                var root = new AnimatorStateMachine
                {
                    name = ir.machine.name ?? ir.name,
                    hideFlags = HideFlags.HideInHierarchy,
                };
                target.Created(root, UndoName);
                layer.stateMachine = root;

                CreateMachine(ir.machine, root, string.Empty, context);
                WireMachine(ir.machine, root, string.Empty, context);
            }

            controller.AddLayer(layer);
            int index = controller.layers.Length - 1;
            if (existing >= 0)
                index = controller.MoveLayer(index, existing);

            // The rebuilt machine inherits the records of the one it replaced (same name):
            // async-sync setups keep their SYNC badge and wizard entry, frames and notes
            // stay on the layer, recipe ownership marks survive.
            if (layer.stateMachine != null
                && oldMachineIds.TryGetValue(ir.name, out int oldMachineId))
                GraphFrameData.RemapStateMachine(controller, oldMachineId, layer.stateMachine);

            warnings.AddRange(context.warnings);
            return index;
        }

        static int FindLayerIndex(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == name) return i;
            return -1;
        }

        // ---- machines ----------------------------------------------------------

        /// <summary>First pass: creates states and sub-machines, registering their paths. Each
        /// machine receives its states and its sub-machines as one array apiece.</summary>
        static void CreateMachine(ControllerIR.Machine ir, AnimatorStateMachine sm,
            string prefix, Context context)
        {
            context.machines[prefix] = sm;
            sm.entryPosition = ir.entryPosition;
            sm.exitPosition = ir.exitPosition;
            sm.anyStatePosition = ir.anyStatePosition;
            sm.parentStateMachinePosition = ir.parentPosition;

            var machineBehaviours = new List<StateMachineBehaviour>();
            foreach (var behaviourIr in ir.behaviours)
                BuildBehaviour(behaviourIr, string.IsNullOrEmpty(prefix) ? ir.name : prefix,
                    type => CreateLooseBehaviour(type, context, machineBehaviours), context);
            if (machineBehaviours.Count > 0) sm.behaviours = machineBehaviours.ToArray();

            // AddState made names unique among the machine's states, and AddStateMachine among
            // its sub-machines; an IR read from a controller with duplicate names (the analyzer
            // reports them, Unity allows them) has to come out the same way.
            var stateNames = new HashSet<string>();
            var states = new ChildAnimatorState[ir.states.Count];
            for (int i = 0; i < ir.states.Count; i++)
            {
                var stateIr = ir.states[i];
                var state = new AnimatorState
                {
                    name = Unique(stateNames, stateIr.name),
                    hideFlags = HideFlags.HideInHierarchy,
                };
                context.target.Created(state, UndoName);
                context.states[ControllerIR.Join(prefix, stateIr.name)] = state;
                ConfigureState(state, stateIr, context);
                states[i] = new ChildAnimatorState { state = state, position = stateIr.position };
            }
            sm.states = states;
            // AddState's other side effect: the first state of a machine without a default
            // becomes it. WireMachine sets the IR's own default over this when it names one.
            if (sm.defaultState == null && states.Length > 0) sm.defaultState = states[0].state;

            var machineNames = new HashSet<string>();
            var children = new ChildAnimatorStateMachine[ir.machines.Count];
            for (int i = 0; i < ir.machines.Count; i++)
            {
                var child = ir.machines[i];
                var childSm = new AnimatorStateMachine
                {
                    name = Unique(machineNames, child.machine.name),
                    hideFlags = HideFlags.HideInHierarchy,
                };
                context.target.Created(childSm, UndoName);
                children[i] = new ChildAnimatorStateMachine { stateMachine = childSm, position = child.position };
            }
            sm.stateMachines = children;
            for (int i = 0; i < ir.machines.Count; i++)
                CreateMachine(ir.machines[i].machine, children[i].stateMachine,
                    ControllerIR.Join(prefix, ir.machines[i].machine.name), context);
        }

        /// <summary>The name Unity's AddState / AddStateMachine would have given: the wanted one,
        /// or "wanted 0", "wanted 1"… — Unity's own numbering, which is not the "wanted (1)" of
        /// ObjectNames.GetUniqueName (measured against AddState, BatchedRebuildTests).</summary>
        static string Unique(HashSet<string> taken, string wanted)
        {
            string name = wanted ?? string.Empty;
            for (int i = 0; taken.Contains(name); i++)
                name = (wanted ?? string.Empty) + " " + i;
            taken.Add(name);
            return name;
        }

        static void ConfigureState(AnimatorState state, ControllerIR.State ir, Context context)
        {
            state.speed = ir.speed;
            state.cycleOffset = ir.cycleOffset;
            state.mirror = ir.mirror;
            state.iKOnFeet = ir.ikOnFeet;
            state.writeDefaultValues = ir.writeDefaultValues;
            state.tag = ir.tag;
            state.speedParameterActive = ir.speedParameterActive;
            state.speedParameter = ir.speedParameter;
            state.mirrorParameterActive = ir.mirrorParameterActive;
            state.mirrorParameter = ir.mirrorParameter;
            state.cycleOffsetParameterActive = ir.cycleOffsetParameterActive;
            state.cycleOffsetParameter = ir.cycleOffsetParameter;
            state.timeParameterActive = ir.timeParameterActive;
            state.timeParameter = ir.timeParameter;

            if (ir.tree != null)
                state.motion = BuildTree(ir.tree, context);
            else
                state.motion = ir.motionAsset;

            var behaviours = new List<StateMachineBehaviour>();
            foreach (var behaviour in ir.behaviours)
                BuildBehaviour(behaviour, state.name,
                    type => CreateLooseBehaviour(type, context, behaviours), context);
            if (behaviours.Count > 0) state.behaviours = behaviours.ToArray();
            context.target.Dirty(state);
        }

        static BlendTree BuildTree(ControllerIR.Tree ir, Context context)
        {
            // Built with automatic thresholds OFF so assigning children can't recompute the
            // authored values; the real flags land afterwards through the serialized object,
            // whose writes have no API side effects.
            var tree = new BlendTree
            {
                name = ir.name,
                blendType = ir.type,
                blendParameter = ir.blendParameter,
                blendParameterY = ir.blendParameterY,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            context.target.Created(tree, UndoName);

            var children = new ChildMotion[ir.children.Count];
            for (int i = 0; i < ir.children.Count; i++)
            {
                var child = ir.children[i];
                children[i] = new ChildMotion
                {
                    motion = child.tree != null ? BuildTree(child.tree, context) : child.motionAsset,
                    threshold = child.threshold,
                    position = child.position,
                    timeScale = child.timeScale,
                    cycleOffset = child.cycleOffset,
                    mirror = child.mirror,
                    directBlendParameter = child.directParameter,
                };
            }
            tree.children = children;

            using (var serialized = new SerializedObject(tree))
            {
                Write(serialized, "m_UseAutomaticThresholds", ir.useAutomaticThresholds);
                var min = serialized.FindProperty("m_MinThreshold");
                if (min != null) min.floatValue = ir.minThreshold;
                var max = serialized.FindProperty("m_MaxThreshold");
                if (max != null) max.floatValue = ir.maxThreshold;
                Write(serialized, "m_NormalizedBlendValues", ir.normalizedBlendValues);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            context.target.Dirty(tree);
            return tree;
        }

        static void Write(SerializedObject serialized, string property, bool value)
        {
            var found = serialized.FindProperty(property);
            if (found != null) found.boolValue = value;
        }

        /// <summary>
        /// Adds one behaviour to whatever owns it. States and state machines both take
        /// behaviours (and a synced layer takes them per overridden state), and only the
        /// "how do I get an instance" step differs — hence <paramref name="add"/>;
        /// <paramref name="owner"/> is only there to name the skipped one in a warning.
        /// </summary>
        static void BuildBehaviour(ControllerIR.Behaviour ir, string owner,
            Func<Type, StateMachineBehaviour> add, Context context)
        {
            var type = VrcBehaviours.Find(ir.typeName);
            if (type == null)
            {
                context.warnings.Add(L.Tr("Behaviour type '{0}' was not found (SDK missing?) — skipped on '{1}'.",
                    ir.typeName, owner));
                return;
            }
            var behaviour = add(type);
            if (behaviour == null) return;
            FillBehaviour(behaviour, ir);
        }

        /// <summary>Pours the snapshot (or the typed driver spec) into a fresh instance.</summary>
        static void FillBehaviour(StateMachineBehaviour behaviour, ControllerIR.Behaviour ir)
        {
            if (!string.IsNullOrEmpty(ir.json))
                EditorJsonUtility.FromJsonOverwrite(ir.json, behaviour);
            else if (ir.driver != null)
                VrcParameterDriver.ApplySpec(behaviour, ir.driver);
            ir.configure?.Invoke(behaviour);

            if (!string.IsNullOrEmpty(ir.instanceName))
                behaviour.name = ir.instanceName;
            VrcBehaviours.MarkAsSubAsset(behaviour);
            EditorUtility.SetDirty(behaviour);
        }

        /// <summary>Second pass: transitions and default states, now that every destination
        /// path resolves. Every owner — a state, the Any State, Entry, a sub-machine's exits —
        /// is handed its transitions as one array.</summary>
        static void WireMachine(ControllerIR.Machine ir, AnimatorStateMachine sm,
            string prefix, Context context)
        {
            if (ir.defaultState != null
                && context.states.TryGetValue(ir.defaultState, out var defaultState))
                sm.defaultState = defaultState;

            foreach (var stateIr in ir.states)
            {
                if (!context.states.TryGetValue(ControllerIR.Join(prefix, stateIr.name), out var state))
                    continue;
                var built = new List<AnimatorStateTransition>();
                foreach (var t in stateIr.transitions)
                {
                    var made = StateTransition(t, context, allowExit: true);
                    if (made == null)
                        context.warnings.Add(L.Tr("Transition from '{0}' to '{1}' could not be resolved — skipped.",
                            stateIr.name, t.destination));
                    else
                        built.Add(made);
                }
                if (built.Count > 0) state.transitions = built.ToArray();
            }

            var any = new List<AnimatorStateTransition>();
            foreach (var t in ir.anyStateTransitions)
            {
                var made = StateTransition(t, context, allowExit: false);
                if (made == null)
                    context.warnings.Add(L.Tr("Any-State transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    any.Add(made);
            }
            if (any.Count > 0) sm.anyStateTransitions = any.ToArray();

            var entry = new List<AnimatorTransition>();
            foreach (var t in ir.entryTransitions)
            {
                var made = MachineTransition(t, context, allowExit: false);
                if (made == null)
                    context.warnings.Add(L.Tr("Entry transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    entry.Add(made);
            }
            if (entry.Count > 0) sm.entryTransitions = entry.ToArray();

            foreach (var child in ir.machines)
            {
                string childPrefix = ControllerIR.Join(prefix, child.machine.name);
                if (!context.machines.TryGetValue(childPrefix, out var childSm)) continue;

                var exits = new List<AnimatorTransition>();
                foreach (var t in child.transitions)
                {
                    var made = MachineTransition(t, context, allowExit: true);
                    if (made == null)
                        context.warnings.Add(L.Tr("Transition from machine '{0}' to '{1}' could not be resolved — skipped.",
                            child.machine.name, t.destination));
                    else
                        exits.Add(made);
                }
                if (exits.Count > 0) sm.SetStateMachineTransitions(childSm, exits.ToArray());

                WireMachine(child.machine, childSm, childPrefix, context);
            }
            context.target.Dirty(sm);
        }

        /// <summary>
        /// A state (or Any State) transition, made and filed, or null when its destination does
        /// not resolve. Starts from what AddTransition / AddExitTransition gave — no exit time,
        /// fixed duration — though <see cref="ApplyTransition"/> overwrites both from the IR.
        /// </summary>
        static AnimatorStateTransition StateTransition(ControllerIR.Transition t, Context context,
            bool allowExit)
        {
            var target = Resolve(t, context, out var destState, out var destMachine);
            if (target == ControllerIR.Transition.Target.Exit && !allowExit) return null;
            if (target == ControllerIR.Transition.Target.State && destState == null) return null;
            if (target == ControllerIR.Transition.Target.Machine && destMachine == null) return null;
            var made = new AnimatorStateTransition
            {
                hasExitTime = false,
                hasFixedDuration = true,
                hideFlags = HideFlags.HideInHierarchy,
            };
            if (target == ControllerIR.Transition.Target.Exit) made.isExit = true;
            else if (target == ControllerIR.Transition.Target.State) made.destinationState = destState;
            else made.destinationStateMachine = destMachine;
            context.target.Created(made, UndoName);
            ApplyTransition(made, t, context);
            return made;
        }

        /// <summary>An Entry or sub-machine transition, made and filed, or null when its
        /// destination does not resolve.</summary>
        static AnimatorTransition MachineTransition(ControllerIR.Transition t, Context context,
            bool allowExit)
        {
            var target = Resolve(t, context, out var destState, out var destMachine);
            if (target == ControllerIR.Transition.Target.Exit && !allowExit) return null;
            if (target == ControllerIR.Transition.Target.State && destState == null) return null;
            if (target == ControllerIR.Transition.Target.Machine && destMachine == null) return null;
            var made = new AnimatorTransition { hideFlags = HideFlags.HideInHierarchy };
            if (target == ControllerIR.Transition.Target.Exit) made.isExit = true;
            else if (target == ControllerIR.Transition.Target.State) made.destinationState = destState;
            else made.destinationStateMachine = destMachine;
            context.target.Created(made, UndoName);
            ApplyTransition(made, t, context);
            return made;
        }

        static ControllerIR.Transition.Target Resolve(ControllerIR.Transition t, Context context,
            out AnimatorState state, out AnimatorStateMachine machine)
        {
            state = null;
            machine = null;
            if (t.target == ControllerIR.Transition.Target.State)
                context.states.TryGetValue(t.destination, out state);
            else if (t.target == ControllerIR.Transition.Target.Machine)
                context.machines.TryGetValue(t.destination, out machine);
            return t.target;
        }

        static void ApplyTransition(AnimatorTransitionBase built, ControllerIR.Transition ir,
            Context context)
        {
            built.solo = ir.solo;
            built.mute = ir.mute;
            if (built is AnimatorStateTransition state)
            {
                state.hasExitTime = ir.hasExitTime;
                state.exitTime = ir.exitTime;
                state.hasFixedDuration = ir.hasFixedDuration;
                state.duration = ir.duration;
                state.offset = ir.offset;
                state.interruptionSource = ir.interruptionSource;
                state.orderedInterruption = ir.orderedInterruption;
                state.canTransitionToSelf = ir.canTransitionToSelf;
            }
            var conditions = new AnimatorCondition[ir.conditions.Count];
            for (int i = 0; i < ir.conditions.Count; i++)
                conditions[i] = new AnimatorCondition
                {
                    mode = ir.conditions[i].mode,
                    parameter = ir.conditions[i].parameter,
                    threshold = ir.conditions[i].threshold,
                };
            built.conditions = conditions;
            context.target.Dirty(built);
        }

        // ---- synced layers -----------------------------------------------------

        static void ApplySyncedOverrides(ControllerIR.Layer ir, int layerIndex,
            GenerationTarget target, List<string> warnings)
        {
            var controller = target.Controller;
            var layers = controller.layers;
            if (layerIndex < 0 || layerIndex >= layers.Length) return;
            int sourceIndex = layers[layerIndex].syncedLayerIndex;
            if (sourceIndex < 0 || sourceIndex >= layers.Length) return;
            var source = layers[sourceIndex].stateMachine;
            if (source == null) return;

            // The override API is keyed by the SOURCE layer's live states; resolve the saved
            // paths against whatever that layer holds now.
            var paths = ControllerIR.BuildPaths(source);
            var byPath = new Dictionary<string, AnimatorState>();
            foreach (var pair in paths.states)
                byPath[pair.Value] = pair.Key;

            bool changed = false;
            foreach (var entry in ir.syncedMotions)
            {
                if (byPath.TryGetValue(entry.statePath, out var state))
                {
                    layers[layerIndex].SetOverrideMotion(state, entry.motion);
                    changed = true;
                }
                else
                    warnings.Add(L.Tr("Synced-layer override target '{0}' was not found in the source layer.", entry.statePath));
            }

            // Behaviour overrides are instances of their own — a synced layer runs these
            // instead of the source state's, so they have to be created here rather than
            // borrowed from the source.
            var context = new Context { target = target };
            foreach (var entry in ir.syncedBehaviours)
            {
                if (!byPath.TryGetValue(entry.statePath, out var state))
                {
                    warnings.Add(L.Tr("Synced-layer override target '{0}' was not found in the source layer.", entry.statePath));
                    continue;
                }
                var made = new List<StateMachineBehaviour>();
                foreach (var behaviourIr in entry.behaviours)
                    BuildBehaviour(behaviourIr, ir.name + " / " + entry.statePath,
                        type => CreateLooseBehaviour(type, context, made), context);
                warnings.AddRange(context.warnings);
                context.warnings.Clear();
                if (made.Count == 0) continue;
                layers[layerIndex].SetOverrideBehaviours(state, made.ToArray());
                changed = true;
            }

            if (changed)
                controller.layers = layers;
        }

        /// <summary>A behaviour created by hand and filed in the controller, the way
        /// AddStateMachineBehaviour would have — for every owner now, so a state's behaviours
        /// arrive as one array like everything else.</summary>
        static StateMachineBehaviour CreateLooseBehaviour(Type type, Context context,
            List<StateMachineBehaviour> into)
        {
            var behaviour = ScriptableObject.CreateInstance(type) as StateMachineBehaviour;
            if (behaviour == null) return null;
            behaviour.name = type.Name;
            context.target.Created(behaviour, UndoName);
            into.Add(behaviour);
            return behaviour;
        }
    }
}
