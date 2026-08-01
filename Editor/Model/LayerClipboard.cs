using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Session clipboard for whole layers. Copy deep-clones the layer's state machine into a
    /// detached hierarchy (with behaviours, frames and notes), so the paste target doesn't
    /// depend on the source controller staying alive. Paste re-clones into the destination
    /// controller, deep-copies blend trees (so no sub-assets are shared across .controller
    /// files) and adds any referenced parameters the destination is missing.
    /// </summary>
    static class LayerClipboard
    {
        [System.Serializable]
        public class ParameterSnapshot
        {
            public string name;
            public AnimatorControllerParameterType type;
            public float defaultFloat;
            public int defaultInt;
            public bool defaultBool;
        }

        class Snapshot
        {
            public string name;
            public float defaultWeight;
            public AnimatorLayerBlendingMode blendingMode;
            public AvatarMask avatarMask;
            public bool ikPass;
            public AnimatorStateMachine stateMachine;
            public readonly List<ParameterSnapshot> parameters = new List<ParameterSnapshot>();
            public readonly List<GraphFrameData.Frame> frames = new List<GraphFrameData.Frame>();
            public readonly List<GraphFrameData.Note> notes = new List<GraphFrameData.Note>();
        }

        static Snapshot _data;

        public static bool HasData => _data != null && _data.stateMachine != null;
        public static string CopiedLayerName => HasData ? _data.name : null;

        public static void Copy(AnimatorController controller, int layerIndex)
        {
            if (controller == null || layerIndex < 0 || layerIndex >= controller.layers.Length)
                return;
            var layer = controller.layers[layerIndex];
            if (layer.stateMachine == null) return;

            var snapshot = new Snapshot
            {
                name = layer.name,
                defaultWeight = layerIndex == 0 ? 1f : layer.defaultWeight,
                blendingMode = layer.blendingMode,
                avatarMask = layer.avatarMask,
                ikPass = layer.iKPass,
                stateMachine = new AnimatorStateMachine
                {
                    name = layer.name,
                    hideFlags = HideFlags.HideAndDontSave,
                },
            };
            StateMachineCloner.Clone(layer.stateMachine, snapshot.stateMachine,
                out var stateMap, out var machineMap);
            CopyBehaviours(stateMap);

            var referenced = CollectParameterNames(layer.stateMachine);
            foreach (var parameter in controller.parameters)
                if (referenced.Contains(parameter.name))
                    snapshot.parameters.Add(new ParameterSnapshot
                    {
                        name = parameter.name,
                        type = parameter.type,
                        defaultFloat = parameter.defaultFloat,
                        defaultInt = parameter.defaultInt,
                        defaultBool = parameter.defaultBool,
                    });

            var frameData = GraphFrameData.Find(controller);
            if (frameData != null)
            {
                foreach (var frame in frameData.frames)
                    if (frame?.stateMachine != null && machineMap.TryGetValue(frame.stateMachine, out var copySm))
                        snapshot.frames.Add(new GraphFrameData.Frame
                        {
                            title = frame.title,
                            color = frame.color,
                            bounds = frame.bounds,
                            moveNodesWithFrame = frame.moveNodesWithFrame,
                            locked = frame.locked,
                            stateMachine = copySm,
                        });
                foreach (var note in frameData.notes)
                    if (note?.stateMachine != null && machineMap.TryGetValue(note.stateMachine, out var copySm))
                        snapshot.notes.Add(new GraphFrameData.Note
                        {
                            text = note.text,
                            color = note.color,
                            bounds = note.bounds,
                            fontSize = note.fontSize,
                            stateMachine = copySm,
                        });
            }
            _data = snapshot;
        }

        /// <summary>Pastes the copied layer at the end; returns its index, or -1.</summary>
        public static int Paste(AnimatorController controller)
        {
            if (controller == null || !HasData) return -1;
            using (new UndoScope("Paste Layer"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Paste Layer");
                controller.AddLayer(DbtBuilder.UniqueLayerName(controller, _data.name));
                var layers = controller.layers;
                int index = layers.Length - 1;
                layers[index].defaultWeight = _data.defaultWeight;
                layers[index].blendingMode = _data.blendingMode;
                layers[index].avatarMask = _data.avatarMask;
                layers[index].iKPass = _data.ikPass;
                controller.layers = layers;

                var target = layers[index].stateMachine;
                StateMachineCloner.Clone(_data.stateMachine, target,
                    out var stateMap, out var machineMap);
                CopyBehaviours(stateMap);
                DeepCopyBlendTrees(controller, stateMap.Values);

                foreach (var parameter in _data.parameters)
                    if (DbtBuilder.FindParameter(controller, parameter.name) == null)
                        controller.AddParameter(new AnimatorControllerParameter
                        {
                            name = parameter.name,
                            type = parameter.type,
                            defaultFloat = parameter.defaultFloat,
                            defaultInt = parameter.defaultInt,
                            defaultBool = parameter.defaultBool,
                        });

                PasteFrames(controller, machineMap);
                EditorUtility.SetDirty(controller);
                return index;
            }
        }

        /// <summary>Applies only the copied layer's settings (weight, blending, mask, IK) —
        /// the layer's states are untouched. The base layer keeps weight 1.</summary>
        public static bool PasteSettings(AnimatorController controller, int layerIndex)
        {
            if (controller == null || !HasData
                || layerIndex < 0 || layerIndex >= controller.layers.Length)
                return false;
            Undo.RegisterCompleteObjectUndo(controller, "Paste Layer Settings");
            var layers = controller.layers;
            layers[layerIndex].defaultWeight = layerIndex == 0 ? 1f : _data.defaultWeight;
            layers[layerIndex].blendingMode = _data.blendingMode;
            layers[layerIndex].avatarMask = _data.avatarMask;
            layers[layerIndex].iKPass = _data.ikPass;
            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            return true;
        }

        // ---- shared helpers (also used by layer templates) --------------------

        /// <summary>Clones every behaviour from each source state onto its copy.</summary>
        public static void CopyBehaviours(IReadOnlyDictionary<AnimatorState, AnimatorState> stateMap)
        {
            foreach (var pair in stateMap)
            {
                foreach (var behaviour in pair.Key.behaviours)
                {
                    if (behaviour == null) continue;
                    var copy = pair.Value.AddStateMachineBehaviour(behaviour.GetType());
                    if (copy == null) continue;
                    EditorUtility.CopySerialized(behaviour, copy);
                    copy.name = behaviour.name;
                    copy.hideFlags = HideFlags.None;
                }
                EditorUtility.SetDirty(pair.Value);
            }
        }

        /// <summary>Replaces every blend-tree motion on the given states with a deep copy
        /// owned by <paramref name="host"/>, so nothing references another .controller's
        /// sub-assets. Clip leaves stay shared.</summary>
        public static void DeepCopyBlendTrees(Object host, IEnumerable<AnimatorState> states)
        {
            var cache = new Dictionary<BlendTree, BlendTree>();
            foreach (var state in states)
                if (state.motion is BlendTree tree)
                {
                    state.motion = DeepCopyTree(host, tree, cache);
                    EditorUtility.SetDirty(state);
                }
        }

        /// <summary>Deep copy of a motion when it's a blend tree (attached to the host asset);
        /// clips and null pass through unchanged.</summary>
        public static Motion DeepCopyMotion(Object host, Motion motion) =>
            motion is BlendTree tree
                ? DeepCopyTree(host, tree, new Dictionary<BlendTree, BlendTree>())
                : motion;

        static BlendTree DeepCopyTree(Object host, BlendTree tree, Dictionary<BlendTree, BlendTree> cache)
        {
            if (cache.TryGetValue(tree, out var existing)) return existing;
            var copy = new BlendTree
            {
                name = tree.name,
                blendType = tree.blendType,
                blendParameter = tree.blendParameter,
                blendParameterY = tree.blendParameterY,
                useAutomaticThresholds = tree.useAutomaticThresholds,
                minThreshold = tree.minThreshold,
                maxThreshold = tree.maxThreshold,
                hideFlags = HideFlags.HideInHierarchy,
            };
            cache[tree] = copy;
            Attach(host, copy);

            var children = tree.children;
            var copied = new ChildMotion[children.Length];
            for (int i = 0; i < children.Length; i++)
            {
                copied[i] = children[i];
                if (children[i].motion is BlendTree nested)
                    copied[i].motion = DeepCopyTree(host, nested, cache);
            }
            copy.children = copied;

            // The hidden "Normalized Blend Values" flag isn't exposed by the API.
            using (var source = new SerializedObject(tree))
            {
                var normalized = source.FindProperty("m_NormalizedBlendValues");
                if (normalized != null)
                    DbtBuilder.SetNormalizedBlendValues(copy, normalized.boolValue);
            }
            EditorUtility.SetDirty(copy);
            return copy;
        }

        static void Attach(Object host, Object created)
        {
            Undo.RegisterCreatedObjectUndo(created, "Paste Layer");
            if (host != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(host)))
                AssetDatabase.AddObjectToAsset(created, host);
        }

        /// <summary>Every parameter name the layer references: transition conditions, state
        /// parameter overrides, blend-tree blend/weight parameters and driver behaviours.</summary>
        public static HashSet<string> CollectParameterNames(AnimatorStateMachine root)
        {
            var names = new HashSet<string>();
            if (root == null) return names;
            foreach (var sm in root.SelfAndDescendants())
            {
                foreach (var child in sm.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    foreach (var transition in state.transitions)
                        AddConditions(transition, names);
                    if (state.speedParameterActive) AddName(names, state.speedParameter);
                    if (state.mirrorParameterActive) AddName(names, state.mirrorParameter);
                    if (state.cycleOffsetParameterActive) AddName(names, state.cycleOffsetParameter);
                    if (state.timeParameterActive) AddName(names, state.timeParameter);
                    if (state.motion is BlendTree tree)
                        AddBlendTreeParameters(tree, names, new HashSet<BlendTree>());
                    foreach (var behaviour in state.behaviours)
                        if (behaviour != null)
                            VrcParameterDriver.CollectReferencedParameters(behaviour, names);
                }
                foreach (var transition in sm.anyStateTransitions)
                    AddConditions(transition, names);
                foreach (var transition in sm.entryTransitions)
                    AddConditions(transition, names);
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null)
                        foreach (var transition in sm.GetStateMachineTransitions(child.stateMachine))
                            AddConditions(transition, names);
            }
            return names;
        }

        static void AddConditions(AnimatorTransitionBase transition, HashSet<string> names)
        {
            if (transition == null) return;
            foreach (var condition in transition.conditions)
                AddName(names, condition.parameter);
        }

        /// <summary>Every parameter name a blend tree subtree references.</summary>
        public static HashSet<string> CollectBlendTreeParameterNames(BlendTree tree)
        {
            var names = new HashSet<string>();
            AddBlendTreeParameters(tree, names, new HashSet<BlendTree>());
            return names;
        }

        static void AddBlendTreeParameters(BlendTree tree, HashSet<string> names, HashSet<BlendTree> seen)
        {
            if (tree == null || !seen.Add(tree)) return;
            AddName(names, tree.blendParameter);
            if (tree.blendType != BlendTreeType.Direct && tree.blendType != BlendTreeType.Simple1D)
                AddName(names, tree.blendParameterY);
            foreach (var child in tree.children)
            {
                if (tree.blendType == BlendTreeType.Direct)
                    AddName(names, child.directBlendParameter);
                if (child.motion is BlendTree nested)
                    AddBlendTreeParameters(nested, names, seen);
            }
        }

        static void AddName(HashSet<string> names, string name)
        {
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }

        static void PasteFrames(AnimatorController controller,
            IReadOnlyDictionary<AnimatorStateMachine, AnimatorStateMachine> machineMap)
        {
            if (_data.frames.Count == 0 && _data.notes.Count == 0) return;
            var data = GraphFrameData.GetOrCreate(controller);
            if (data == null) return;
            Undo.RegisterCompleteObjectUndo(data, "Paste Layer");
            foreach (var frame in _data.frames)
                if (frame.stateMachine != null && machineMap.TryGetValue(frame.stateMachine, out var target))
                    data.frames.Add(new GraphFrameData.Frame
                    {
                        title = frame.title,
                        color = frame.color,
                        bounds = frame.bounds,
                        moveNodesWithFrame = frame.moveNodesWithFrame,
                        locked = frame.locked,
                        stateMachine = target,
                    });
            foreach (var note in _data.notes)
                if (note.stateMachine != null && machineMap.TryGetValue(note.stateMachine, out var target))
                    data.notes.Add(new GraphFrameData.Note
                    {
                        text = note.text,
                        color = note.color,
                        bounds = note.bounds,
                        fontSize = note.fontSize,
                        stateMachine = target,
                    });
            EditorUtility.SetDirty(data);
        }
    }
}
