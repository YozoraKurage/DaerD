using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Structural snapshot of an AnimatorController — the foundation of the C# conversion
    /// feature. The exporter walks one to emit recipe code, the authoring builders produce
    /// one from recipe code, <see cref="ControllerIRDiff"/> compares two, and
    /// <see cref="ControllerIRBuilder"/> turns one back into a live controller. Because the
    /// same IR sits on every path, "the generated code rebuilds the same controller" reduces
    /// to testable round-trips instead of trust.
    ///
    /// Asset references (clips, masks, blend trees living in other files) stay plain object
    /// references — never GUIDs or paths. Blend trees embedded in the .controller itself are
    /// decomposed into <see cref="Tree"/> data instead, since a rebuild must recreate them.
    ///
    /// States and machines are addressed by layer-local slash paths ("Combat/Idle"); the
    /// root machine contributes no segment. Duplicate names make paths ambiguous — the
    /// analyzer already flags those, and here the last one parsed wins.
    /// </summary>
    class ControllerIR
    {
        public readonly List<Param> parameters = new List<Param>();
        public readonly List<Layer> layers = new List<Layer>();

        public class Param
        {
            public string name;
            public AnimatorControllerParameterType type;
            public float defaultFloat;
            public int defaultInt;
            public bool defaultBool;
        }

        public class Layer
        {
            public string name;
            public float defaultWeight = 1f;
            public AnimatorLayerBlendingMode blending;
            public bool ikPass;
            public AvatarMask mask;
            /// <summary>Source layer index when this layer is synced; -1 otherwise.</summary>
            public int syncedLayerIndex = -1;
            public bool syncedLayerAffectsTiming;
            /// <summary>Root state machine; null for synced layers (they don't own one).</summary>
            public Machine machine;
            /// <summary>Synced-layer motion overrides, keyed by source-layer state path.
            /// (Behaviour overrides are not carried — a documented limitation.)</summary>
            public readonly List<MotionOverride> syncedMotions = new List<MotionOverride>();
        }

        public class MotionOverride
        {
            public string statePath;
            public Motion motion;
        }

        public class Machine
        {
            public string name;
            public Vector3 entryPosition;
            public Vector3 exitPosition;
            public Vector3 anyStatePosition;
            public Vector3 parentPosition;
            /// <summary>Layer-local path of this machine's default state; null when none.</summary>
            public string defaultState;
            public readonly List<State> states = new List<State>();
            public readonly List<ChildMachine> machines = new List<ChildMachine>();
            public readonly List<Transition> anyStateTransitions = new List<Transition>();
            public readonly List<Transition> entryTransitions = new List<Transition>();
        }

        public class ChildMachine
        {
            public Machine machine;
            public Vector3 position;
            /// <summary>Transitions drawn from this child machine's node in the parent view
            /// (AnimatorStateMachine.GetStateMachineTransitions).</summary>
            public readonly List<Transition> transitions = new List<Transition>();
        }

        public class State
        {
            public string name;
            public Vector3 position;
            /// <summary>Clip or externally stored blend tree; null when the state has an
            /// embedded tree or no motion at all.</summary>
            public Motion motionAsset;
            /// <summary>Blend tree embedded in the .controller, decomposed; null otherwise.</summary>
            public Tree tree;
            public float speed = 1f;
            public float cycleOffset;
            public bool mirror;
            public bool ikOnFeet;
            public bool writeDefaultValues = true;
            public string tag = string.Empty;
            public bool speedParameterActive;
            public string speedParameter = string.Empty;
            public bool mirrorParameterActive;
            public string mirrorParameter = string.Empty;
            public bool cycleOffsetParameterActive;
            public string cycleOffsetParameter = string.Empty;
            public bool timeParameterActive;
            public string timeParameter = string.Empty;
            public readonly List<Behaviour> behaviours = new List<Behaviour>();
            public readonly List<Transition> transitions = new List<Transition>();
        }

        public class Tree
        {
            public string name = "Blend Tree";
            public BlendTreeType type;
            public string blendParameter = string.Empty;
            public string blendParameterY = string.Empty;
            public bool useAutomaticThresholds = true;
            public float minThreshold;
            public float maxThreshold = 1f;
            public bool normalizedBlendValues;
            public readonly List<TreeChild> children = new List<TreeChild>();
        }

        public class TreeChild
        {
            public Motion motionAsset;
            public Tree tree;
            public float threshold;
            public Vector2 position;
            public float timeScale = 1f;
            public float cycleOffset;
            public bool mirror;
            public string directParameter = string.Empty;
        }

        /// <summary>
        /// One StateMachineBehaviour. Parsed controllers always fill <see cref="json"/>
        /// (an EditorJsonUtility snapshot — exact rebuild, comparable). Recipe-declared IR
        /// may instead carry a typed <see cref="driver"/> spec or a <see cref="configure"/>
        /// action; those never need to be diffed, because Verify re-parses the real
        /// controller after applying them.
        /// </summary>
        public class Behaviour
        {
            public string typeName;
            public string json;
            public DriverSpec driver;
            public System.Action<StateMachineBehaviour> configure;
            /// <summary>Object name; repeatable VRC types use it as the instance label.</summary>
            public string instanceName = string.Empty;
        }

        /// <summary>Typed VRC Avatar Parameter Driver contents (authoring + export).</summary>
        public class DriverSpec
        {
            public bool localOnly;
            public readonly List<DriverEntry> entries = new List<DriverEntry>();
        }

        public class DriverEntry
        {
            /// <summary>0 = Set, 1 = Add, 2 = Random, 3 = Copy (the SDK's ChangeType order).</summary>
            public int kind;
            public string name = string.Empty;
            public string source = string.Empty;
            public float value;
            public float min;
            public float max;
            public float chance = 1f;
            public bool convertRange;
            public float sourceMin;
            public float sourceMax;
            public float destMin;
            public float destMax;
        }

        public class Transition
        {
            public enum Target { State, Machine, Exit }

            public Target target;
            /// <summary>Layer-local path of the destination (empty for Exit).</summary>
            public string destination = string.Empty;
            /// <summary>True for AnimatorStateTransition (has the timing block below).</summary>
            public bool isStateTransition;
            public bool hasExitTime;
            public float exitTime = 0.75f;
            public bool hasFixedDuration = true;
            public float duration = 0.25f;
            public float offset;
            public TransitionInterruptionSource interruptionSource;
            public bool orderedInterruption = true;
            public bool canTransitionToSelf;
            public bool solo;
            public bool mute;
            public readonly List<Condition> conditions = new List<Condition>();
        }

        public class Condition
        {
            public AnimatorConditionMode mode;
            public string parameter = string.Empty;
            public float threshold;
        }

        // ---- parse -------------------------------------------------------------

        public static ControllerIR Parse(AnimatorController controller)
        {
            var ir = new ControllerIR();
            if (controller == null) return ir;

            foreach (var parameter in controller.parameters)
                ir.parameters.Add(new Param
                {
                    name = parameter.name,
                    type = parameter.type,
                    defaultFloat = parameter.defaultFloat,
                    defaultInt = parameter.defaultInt,
                    defaultBool = parameter.defaultBool,
                });

            string controllerPath = AssetDatabase.GetAssetPath(controller);
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var source = layers[i];
                var layer = new Layer
                {
                    name = source.name,
                    // The base layer always runs at weight 1 but serializes whatever it was
                    // created with; normalize so diffs don't flag a meaningless difference.
                    defaultWeight = i == 0 ? 1f : source.defaultWeight,
                    blending = source.blendingMode,
                    ikPass = source.iKPass,
                    mask = source.avatarMask,
                    syncedLayerIndex = source.syncedLayerIndex,
                    syncedLayerAffectsTiming = source.syncedLayerAffectsTiming,
                };
                ir.layers.Add(layer);

                if (source.syncedLayerIndex >= 0 && source.syncedLayerIndex < layers.Length)
                {
                    var syncSource = layers[source.syncedLayerIndex].stateMachine;
                    if (syncSource != null)
                    {
                        var paths = BuildPaths(syncSource);
                        foreach (var pair in paths.states)
                        {
                            var motion = source.GetOverrideMotion(pair.Key);
                            if (motion != null)
                                layer.syncedMotions.Add(new MotionOverride
                                { statePath = pair.Value, motion = motion });
                        }
                        // Deterministic order for diff and codegen.
                        layer.syncedMotions.Sort((a, b) =>
                            string.CompareOrdinal(a.statePath, b.statePath));
                    }
                    continue;
                }

                if (source.stateMachine == null) continue;
                var map = BuildPaths(source.stateMachine);
                layer.machine = ParseMachine(source.stateMachine, map, controllerPath);
            }
            return ir;
        }

        /// <summary>Object → layer-local path maps for one layer's machine hierarchy.</summary>
        internal class PathMap
        {
            public readonly Dictionary<AnimatorState, string> states =
                new Dictionary<AnimatorState, string>();
            public readonly Dictionary<AnimatorStateMachine, string> machines =
                new Dictionary<AnimatorStateMachine, string>();
        }

        internal static PathMap BuildPaths(AnimatorStateMachine root)
        {
            var map = new PathMap();
            CollectPaths(root, string.Empty, map, new HashSet<AnimatorStateMachine>());
            return map;
        }

        static void CollectPaths(AnimatorStateMachine sm, string prefix, PathMap map,
            HashSet<AnimatorStateMachine> visited)
        {
            if (sm == null || !visited.Add(sm)) return;
            map.machines[sm] = prefix;
            foreach (var child in sm.states)
                if (child.state != null)
                    map.states[child.state] = Join(prefix, child.state.name);
            foreach (var child in sm.stateMachines)
                if (child.stateMachine != null)
                    CollectPaths(child.stateMachine, Join(prefix, child.stateMachine.name),
                        map, visited);
        }

        internal static string Join(string prefix, string name) =>
            string.IsNullOrEmpty(prefix) ? name : prefix + "/" + name;

        static Machine ParseMachine(AnimatorStateMachine sm, PathMap map, string controllerPath)
        {
            var machine = new Machine
            {
                name = sm.name,
                entryPosition = sm.entryPosition,
                exitPosition = sm.exitPosition,
                anyStatePosition = sm.anyStatePosition,
                parentPosition = sm.parentStateMachinePosition,
                defaultState = sm.defaultState != null
                    && map.states.TryGetValue(sm.defaultState, out var dp) ? dp : null,
            };

            foreach (var child in sm.states)
            {
                if (child.state == null) continue;
                var state = ParseState(child.state, child.position, map, controllerPath);
                machine.states.Add(state);
            }

            foreach (var child in sm.stateMachines)
            {
                if (child.stateMachine == null) continue;
                var entry = new ChildMachine
                {
                    machine = ParseMachine(child.stateMachine, map, controllerPath),
                    position = child.position,
                };
                foreach (var t in sm.GetStateMachineTransitions(child.stateMachine))
                    if (t != null)
                        entry.transitions.Add(ParseTransition(t, map));
                machine.machines.Add(entry);
            }

            foreach (var t in sm.anyStateTransitions)
                if (t != null)
                    machine.anyStateTransitions.Add(ParseTransition(t, map));
            foreach (var t in sm.entryTransitions)
                if (t != null)
                    machine.entryTransitions.Add(ParseTransition(t, map));
            return machine;
        }

        static State ParseState(AnimatorState source, Vector3 position, PathMap map,
            string controllerPath)
        {
            var state = new State
            {
                name = source.name,
                position = position,
                speed = source.speed,
                cycleOffset = source.cycleOffset,
                mirror = source.mirror,
                ikOnFeet = source.iKOnFeet,
                writeDefaultValues = source.writeDefaultValues,
                tag = source.tag ?? string.Empty,
                speedParameterActive = source.speedParameterActive,
                speedParameter = source.speedParameter ?? string.Empty,
                mirrorParameterActive = source.mirrorParameterActive,
                mirrorParameter = source.mirrorParameter ?? string.Empty,
                cycleOffsetParameterActive = source.cycleOffsetParameterActive,
                cycleOffsetParameter = source.cycleOffsetParameter ?? string.Empty,
                timeParameterActive = source.timeParameterActive,
                timeParameter = source.timeParameter ?? string.Empty,
            };

            DecomposeMotion(source.motion, controllerPath, new HashSet<BlendTree>(),
                out state.motionAsset, out var tree);
            state.tree = tree;

            foreach (var behaviour in source.behaviours)
            {
                if (behaviour == null) continue;
                var entry = new Behaviour
                {
                    typeName = behaviour.GetType().Name,
                    json = EditorJsonUtility.ToJson(behaviour),
                    instanceName = behaviour.name ?? string.Empty,
                };
                if (VrcParameterDriver.Is(behaviour))
                    entry.driver = VrcParameterDriver.ReadSpec(behaviour);
                state.behaviours.Add(entry);
            }

            foreach (var t in source.transitions)
                if (t != null)
                    state.transitions.Add(ParseTransition(t, map));
            return state;
        }

        /// <summary>
        /// Splits a motion into (asset reference, embedded tree). A blend tree stored in the
        /// .controller file itself must be decomposed — the rebuild recreates it — while one
        /// living in another asset stays a reference. A tree revisited within its own
        /// hierarchy (cyclic nesting) falls back to a reference to break the cycle.
        /// </summary>
        static void DecomposeMotion(Motion motion, string controllerPath,
            HashSet<BlendTree> visited, out Motion asset, out Tree tree)
        {
            asset = null;
            tree = null;
            if (motion == null) return;
            if (!(motion is BlendTree blendTree))
            {
                asset = motion;
                return;
            }
            bool embedded = AssetDatabase.GetAssetPath(blendTree) == controllerPath;
            if (!embedded || !visited.Add(blendTree))
            {
                asset = blendTree;
                return;
            }

            tree = new Tree
            {
                name = blendTree.name,
                type = blendTree.blendType,
                blendParameter = blendTree.blendParameter ?? string.Empty,
                blendParameterY = blendTree.blendParameterY ?? string.Empty,
                useAutomaticThresholds = blendTree.useAutomaticThresholds,
                minThreshold = blendTree.minThreshold,
                maxThreshold = blendTree.maxThreshold,
                normalizedBlendValues = ReadNormalizedBlendValues(blendTree),
            };
            foreach (var child in blendTree.children)
            {
                var entry = new TreeChild
                {
                    threshold = child.threshold,
                    position = child.position,
                    timeScale = child.timeScale,
                    cycleOffset = child.cycleOffset,
                    mirror = child.mirror,
                    directParameter = child.directBlendParameter ?? string.Empty,
                };
                DecomposeMotion(child.motion, controllerPath, visited,
                    out entry.motionAsset, out var childTree);
                entry.tree = childTree;
                tree.children.Add(entry);
            }
            visited.Remove(blendTree);
        }

        /// <summary>The hidden "Normalized Blend Values" flag isn't exposed by the API.</summary>
        static bool ReadNormalizedBlendValues(BlendTree tree)
        {
            using (var serialized = new SerializedObject(tree))
            {
                var property = serialized.FindProperty("m_NormalizedBlendValues");
                return property != null && property.boolValue;
            }
        }

        static Transition ParseTransition(AnimatorTransitionBase source, PathMap map)
        {
            var transition = new Transition
            {
                solo = source.solo,
                mute = source.mute,
            };
            if (source.isExit)
                transition.target = Transition.Target.Exit;
            else if (source.destinationState != null)
            {
                transition.target = Transition.Target.State;
                map.states.TryGetValue(source.destinationState, out var path);
                transition.destination = path ?? source.destinationState.name;
            }
            else if (source.destinationStateMachine != null)
            {
                transition.target = Transition.Target.Machine;
                map.machines.TryGetValue(source.destinationStateMachine, out var path);
                transition.destination = path ?? source.destinationStateMachine.name;
            }
            else
            {
                // A dangling transition (destination deleted). Keep it as an exit so the
                // rebuild produces something valid; the analyzer reports the original.
                transition.target = Transition.Target.Exit;
            }

            if (source is AnimatorStateTransition state)
            {
                transition.isStateTransition = true;
                transition.hasExitTime = state.hasExitTime;
                transition.exitTime = state.exitTime;
                transition.hasFixedDuration = state.hasFixedDuration;
                transition.duration = state.duration;
                transition.offset = state.offset;
                transition.interruptionSource = state.interruptionSource;
                transition.orderedInterruption = state.orderedInterruption;
                transition.canTransitionToSelf = state.canTransitionToSelf;
            }

            foreach (var condition in source.conditions)
                transition.conditions.Add(new Condition
                {
                    mode = condition.mode,
                    parameter = condition.parameter ?? string.Empty,
                    threshold = condition.threshold,
                });
            return transition;
        }

        /// <summary>Subset view for verifying a partial (layer-owning) recipe: only the named
        /// layers and parameters remain, so untouched parts of the controller don't diff.</summary>
        public ControllerIR FilterTo(ICollection<string> layerNames, ICollection<string> parameterNames)
        {
            var filtered = new ControllerIR();
            foreach (var parameter in parameters)
                if (parameterNames == null || parameterNames.Contains(parameter.name))
                    filtered.parameters.Add(parameter);
            foreach (var layer in layers)
                if (layerNames == null || layerNames.Contains(layer.name))
                    filtered.layers.Add(layer);
            return filtered;
        }
    }
}
