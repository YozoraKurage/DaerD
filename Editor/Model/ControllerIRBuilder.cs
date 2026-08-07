using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
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
    /// </summary>
    static class ControllerIRBuilder
    {
        const string UndoName = "Apply Recipe";

        class Context
        {
            public AnimatorController controller;
            public bool persisted;
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

            using (new UndoScope(UndoName))
            {
                Undo.RegisterCompleteObjectUndo(controller, UndoName);
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
                    built.Add(BuildLayer(layer, controller, exclusive, oldMachineIds, warnings));

                for (int i = 0; i < ir.layers.Count; i++)
                    if (ir.layers[i].syncedLayerIndex >= 0)
                        ApplySyncedMotions(ir.layers[i], built[i], controller, warnings);

                EditorUtility.SetDirty(controller);
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
                // The recipe is the declared source of truth for defaults it names.
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
        static int BuildLayer(ControllerIR.Layer ir, AnimatorController controller,
            bool exclusive, Dictionary<string, int> oldMachineIds, List<string> warnings)
        {
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

            var context = new Context
            {
                controller = controller,
                persisted = !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller)),
            };

            if (ir.machine != null)
            {
                var root = new AnimatorStateMachine
                {
                    name = ir.machine.name ?? ir.name,
                    hideFlags = HideFlags.HideInHierarchy,
                };
                Undo.RegisterCreatedObjectUndo(root, UndoName);
                if (context.persisted)
                    AssetDatabase.AddObjectToAsset(root, controller);
                layer.stateMachine = root;

                CreateMachine(ir.machine, root, string.Empty, context);
                WireMachine(ir.machine, root, string.Empty, context);
            }

            controller.AddLayer(layer);
            int index = controller.layers.Length - 1;
            if (existing >= 0)
                index = MoveLayer(controller, index, existing);

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

        /// <summary>Moves the layer at <paramref name="from"/> to <paramref name="to"/>,
        /// shifting the ones between; returns the final index.</summary>
        static int MoveLayer(AnimatorController controller, int from, int to)
        {
            var layers = controller.layers;
            if (from == to || from < 0 || to < 0 || from >= layers.Length || to >= layers.Length)
                return from;
            var moved = layers[from];
            int step = from < to ? 1 : -1;
            for (int i = from; i != to; i += step)
                layers[i] = layers[i + step];
            layers[to] = moved;
            controller.layers = layers;
            return to;
        }

        // ---- machines ----------------------------------------------------------

        /// <summary>First pass: creates states and sub-machines, registering their paths.</summary>
        static void CreateMachine(ControllerIR.Machine ir, AnimatorStateMachine sm,
            string prefix, Context context)
        {
            context.machines[prefix] = sm;
            sm.entryPosition = ir.entryPosition;
            sm.exitPosition = ir.exitPosition;
            sm.anyStatePosition = ir.anyStatePosition;
            sm.parentStateMachinePosition = ir.parentPosition;

            foreach (var stateIr in ir.states)
            {
                var state = sm.AddState(stateIr.name, stateIr.position);
                context.states[ControllerIR.Join(prefix, stateIr.name)] = state;
                ConfigureState(state, stateIr, context);
            }

            foreach (var child in ir.machines)
            {
                var childSm = sm.AddStateMachine(child.machine.name, child.position);
                CreateMachine(child.machine, childSm,
                    ControllerIR.Join(prefix, child.machine.name), context);
            }
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

            foreach (var behaviour in ir.behaviours)
                BuildBehaviour(state, behaviour, context);
            EditorUtility.SetDirty(state);
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
            Undo.RegisterCreatedObjectUndo(tree, UndoName);
            if (context.persisted)
                AssetDatabase.AddObjectToAsset(tree, context.controller);

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
            EditorUtility.SetDirty(tree);
            return tree;
        }

        static void Write(SerializedObject serialized, string property, bool value)
        {
            var found = serialized.FindProperty(property);
            if (found != null) found.boolValue = value;
        }

        static void BuildBehaviour(AnimatorState state, ControllerIR.Behaviour ir, Context context)
        {
            var type = VrcBehaviours.Find(ir.typeName);
            if (type == null)
            {
                context.warnings.Add(L.Tr("Behaviour type '{0}' was not found (SDK missing?) — skipped on state '{1}'.",
                    ir.typeName, state.name));
                return;
            }
            var behaviour = state.AddStateMachineBehaviour(type);
            if (behaviour == null) return;

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
        /// path resolves.</summary>
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
                foreach (var t in stateIr.transitions)
                {
                    AnimatorStateTransition built = null;
                    switch (Resolve(t, context, out var destState, out var destMachine))
                    {
                        case ControllerIR.Transition.Target.Exit:
                            built = state.AddExitTransition();
                            break;
                        case ControllerIR.Transition.Target.State:
                            if (destState != null) built = state.AddTransition(destState);
                            break;
                        case ControllerIR.Transition.Target.Machine:
                            if (destMachine != null) built = state.AddTransition(destMachine);
                            break;
                    }
                    if (built == null)
                        context.warnings.Add(L.Tr("Transition from '{0}' to '{1}' could not be resolved — skipped.",
                            stateIr.name, t.destination));
                    else
                        ApplyTransition(built, t);
                }
            }

            foreach (var t in ir.anyStateTransitions)
            {
                AnimatorStateTransition built = null;
                switch (Resolve(t, context, out var destState, out var destMachine))
                {
                    case ControllerIR.Transition.Target.State:
                        if (destState != null) built = sm.AddAnyStateTransition(destState);
                        break;
                    case ControllerIR.Transition.Target.Machine:
                        if (destMachine != null) built = sm.AddAnyStateTransition(destMachine);
                        break;
                }
                if (built == null)
                    context.warnings.Add(L.Tr("Any-State transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    ApplyTransition(built, t);
            }

            foreach (var t in ir.entryTransitions)
            {
                AnimatorTransition built = null;
                switch (Resolve(t, context, out var destState, out var destMachine))
                {
                    case ControllerIR.Transition.Target.State:
                        if (destState != null) built = sm.AddEntryTransition(destState);
                        break;
                    case ControllerIR.Transition.Target.Machine:
                        if (destMachine != null) built = sm.AddEntryTransition(destMachine);
                        break;
                }
                if (built == null)
                    context.warnings.Add(L.Tr("Entry transition to '{0}' could not be resolved — skipped.", t.destination));
                else
                    ApplyTransition(built, t);
            }

            foreach (var child in ir.machines)
            {
                string childPrefix = ControllerIR.Join(prefix, child.machine.name);
                if (!context.machines.TryGetValue(childPrefix, out var childSm)) continue;

                foreach (var t in child.transitions)
                {
                    AnimatorTransition built = null;
                    switch (Resolve(t, context, out var destState, out var destMachine))
                    {
                        case ControllerIR.Transition.Target.Exit:
                            built = sm.AddStateMachineExitTransition(childSm);
                            break;
                        case ControllerIR.Transition.Target.State:
                            if (destState != null) built = sm.AddStateMachineTransition(childSm, destState);
                            break;
                        case ControllerIR.Transition.Target.Machine:
                            if (destMachine != null) built = sm.AddStateMachineTransition(childSm, destMachine);
                            break;
                    }
                    if (built == null)
                        context.warnings.Add(L.Tr("Transition from machine '{0}' to '{1}' could not be resolved — skipped.",
                            child.machine.name, t.destination));
                    else
                        ApplyTransition(built, t);
                }

                WireMachine(child.machine, childSm, childPrefix, context);
            }
            EditorUtility.SetDirty(sm);
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

        static void ApplyTransition(AnimatorTransitionBase built, ControllerIR.Transition ir)
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
            EditorUtility.SetDirty(built);
        }

        // ---- synced layers -----------------------------------------------------

        static void ApplySyncedMotions(ControllerIR.Layer ir, int layerIndex,
            AnimatorController controller, List<string> warnings)
        {
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
            if (changed)
                controller.layers = layers;
        }
    }
}
