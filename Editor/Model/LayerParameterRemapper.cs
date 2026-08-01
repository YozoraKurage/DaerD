using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Renames parameter references inside ONE state machine hierarchy (conditions, state
    /// parameter overrides, blend trees, driver behaviours) without touching the rest of the
    /// controller. Used by template import, where the freshly cloned layer must be re-pointed
    /// at the user's chosen parameters. Only call this on hierarchies whose blend trees are
    /// owned by the layer (deep copies) — shared trees would leak the rename outside.
    /// </summary>
    static class LayerParameterRemapper
    {
        public static void Remap(AnimatorStateMachine root, IReadOnlyDictionary<string, string> map)
        {
            if (root == null || map == null || map.Count == 0) return;
            var seenTrees = new HashSet<BlendTree>();

            foreach (var sm in root.SelfAndDescendants())
            {
                Undo.RegisterCompleteObjectUndo(sm, "Remap Parameters");
                foreach (var child in sm.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    Undo.RegisterCompleteObjectUndo(state, "Remap Parameters");
                    foreach (var transition in state.transitions)
                        RemapConditions(transition, map);
                    state.speedParameter = Mapped(map, state.speedParameter);
                    state.mirrorParameter = Mapped(map, state.mirrorParameter);
                    state.cycleOffsetParameter = Mapped(map, state.cycleOffsetParameter);
                    state.timeParameter = Mapped(map, state.timeParameter);
                    if (state.motion is BlendTree tree)
                        RemapBlendTree(tree, map, seenTrees);
                    foreach (var behaviour in state.behaviours)
                        foreach (var pair in map)
                            VrcParameterDriver.RenameReferences(behaviour, pair.Key, pair.Value);
                    EditorUtility.SetDirty(state);
                }
                foreach (var transition in sm.anyStateTransitions)
                    RemapConditions(transition, map);
                foreach (var transition in sm.entryTransitions)
                    RemapConditions(transition, map);
                foreach (var child in sm.stateMachines)
                    if (child.stateMachine != null)
                        foreach (var transition in sm.GetStateMachineTransitions(child.stateMachine))
                            RemapConditions(transition, map);
                EditorUtility.SetDirty(sm);
            }
        }

        static void RemapConditions(AnimatorTransitionBase transition,
            IReadOnlyDictionary<string, string> map)
        {
            if (transition == null) return;
            var conditions = transition.conditions;
            bool changed = false;
            for (int i = 0; i < conditions.Length; i++)
            {
                var mapped = Mapped(map, conditions[i].parameter);
                if (mapped == conditions[i].parameter) continue;
                conditions[i].parameter = mapped;
                changed = true;
            }
            if (!changed) return;
            Undo.RegisterCompleteObjectUndo(transition, "Remap Parameters");
            transition.conditions = conditions;
            EditorUtility.SetDirty(transition);
        }

        static void RemapBlendTree(BlendTree tree, IReadOnlyDictionary<string, string> map,
            HashSet<BlendTree> seen)
        {
            if (tree == null || !seen.Add(tree)) return;
            Undo.RegisterCompleteObjectUndo(tree, "Remap Parameters");
            tree.blendParameter = Mapped(map, tree.blendParameter);
            if (tree.blendType != BlendTreeType.Direct && tree.blendType != BlendTreeType.Simple1D)
                tree.blendParameterY = Mapped(map, tree.blendParameterY);
            var children = tree.children;
            bool childrenChanged = false;
            for (int i = 0; i < children.Length; i++)
            {
                if (tree.blendType == BlendTreeType.Direct)
                {
                    var mapped = Mapped(map, children[i].directBlendParameter);
                    if (mapped != children[i].directBlendParameter)
                    {
                        children[i].directBlendParameter = mapped;
                        childrenChanged = true;
                    }
                }
                if (children[i].motion is BlendTree nested)
                    RemapBlendTree(nested, map, seen);
            }
            if (childrenChanged)
                tree.children = children;
            EditorUtility.SetDirty(tree);
        }

        static string Mapped(IReadOnlyDictionary<string, string> map, string name) =>
            !string.IsNullOrEmpty(name) && map.TryGetValue(name, out var mapped) ? mapped : name;
    }
}
