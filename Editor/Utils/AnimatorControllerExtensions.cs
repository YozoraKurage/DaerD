using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Traversal helpers shared by the parameter converter, the cascade renamer and the analyzer.
    /// All iterators are lazy and skip null sub-assets defensively.
    /// </summary>
    static class AnimatorControllerExtensions
    {
        public static IEnumerable<AnimatorStateMachine> SelfAndDescendants(this AnimatorStateMachine sm)
        {
            if (sm == null) yield break;
            yield return sm;
            foreach (var child in sm.stateMachines)
                foreach (var nested in SelfAndDescendants(child.stateMachine))
                    yield return nested;
        }

        public static IEnumerable<AnimatorStateMachine> AllStateMachines(this AnimatorController controller)
        {
            if (controller == null) yield break;
            foreach (var layer in controller.layers)
                foreach (var sm in layer.stateMachine.SelfAndDescendants())
                    yield return sm;
        }

        public static IEnumerable<AnimatorState> AllStates(this AnimatorController controller)
        {
            foreach (var sm in controller.AllStateMachines())
                foreach (var child in sm.states)
                    if (child.state != null)
                        yield return child.state;
        }

        /// <summary>Every transition reachable from the controller (state, any-state, entry and state-machine).</summary>
        public static IEnumerable<AnimatorTransitionBase> AllTransitions(this AnimatorController controller)
        {
            foreach (var sm in controller.AllStateMachines())
            {
                foreach (var t in sm.anyStateTransitions)
                    if (t != null) yield return t;
                foreach (var t in sm.entryTransitions)
                    if (t != null) yield return t;
                foreach (var child in sm.stateMachines)
                    foreach (var t in sm.GetStateMachineTransitions(child.stateMachine))
                        if (t != null) yield return t;
            }
            foreach (var state in controller.AllStates())
                foreach (var t in state.transitions)
                    if (t != null) yield return t;
        }

        /// <summary>Every StateMachineBehaviour attached to any state or state machine, each
        /// instance yielded once even when shared.</summary>
        public static IEnumerable<UnityEngine.StateMachineBehaviour> AllBehaviours(this AnimatorController controller)
        {
            var seen = new HashSet<UnityEngine.StateMachineBehaviour>();
            foreach (var state in controller.AllStates())
                foreach (var behaviour in state.behaviours)
                    if (behaviour != null && seen.Add(behaviour)) yield return behaviour;
            foreach (var sm in controller.AllStateMachines())
                foreach (var behaviour in sm.behaviours)
                    if (behaviour != null && seen.Add(behaviour)) yield return behaviour;
        }

        public static IEnumerable<BlendTree> AllBlendTrees(this AnimatorController controller)
        {
            // The visited set guards against self-nested (cyclic) blend trees, which would
            // otherwise recurse forever; it also yields a tree shared by several states only once.
            var visited = new HashSet<BlendTree>();
            foreach (var state in controller.AllStates())
                foreach (var bt in BlendTreesIn(state.motion, visited))
                    yield return bt;

            // A synced layer replays another layer's states with its own per-state override
            // motions; those trees appear nowhere in the state walk above.
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                int source = layers[i].syncedLayerIndex;
                if (source < 0 || source >= layers.Length || layers[source].stateMachine == null) continue;
                foreach (var sm in layers[source].stateMachine.SelfAndDescendants())
                    foreach (var child in sm.states)
                        if (child.state != null)
                            foreach (var bt in BlendTreesIn(layers[i].GetOverrideMotion(child.state), visited))
                                yield return bt;
            }
        }

        static IEnumerable<BlendTree> BlendTreesIn(UnityEngine.Motion motion, HashSet<BlendTree> visited)
        {
            if (motion is BlendTree tree && visited.Add(tree))
            {
                yield return tree;
                foreach (var child in tree.children)
                    foreach (var nested in BlendTreesIn(child.motion, visited))
                        yield return nested;
            }
        }

        /// <summary>True when the state is a DIRECT child of the state machine (not nested deeper).</summary>
        public static bool ContainsState(this AnimatorStateMachine sm, AnimatorState target)
        {
            if (target == null || sm == null) return false;
            foreach (var cs in sm.states)
                if (cs.state == target) return true;
            return false;
        }

        /// <summary>True when the machine is a DIRECT child of the state machine (not nested deeper).</summary>
        public static bool ContainsStateMachine(this AnimatorStateMachine sm, AnimatorStateMachine target)
        {
            if (target == null || sm == null) return false;
            foreach (var cm in sm.stateMachines)
                if (cm.stateMachine == target) return true;
            return false;
        }

        /// <summary>
        /// True when <paramref name="root"/> is, or nests (at any depth), <paramref name="target"/>.
        /// Safe to call on trees that already contain reference cycles.
        /// </summary>
        public static bool ContainsTree(this BlendTree root, BlendTree target)
        {
            if (root == null || target == null) return false;
            var visited = new HashSet<BlendTree>();
            var stack = new Stack<BlendTree>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var tree = stack.Pop();
                if (tree == target) return true;
                if (!visited.Add(tree)) continue;
                foreach (var child in tree.children)
                    if (child.motion is BlendTree nested)
                        stack.Push(nested);
            }
            return false;
        }
    }
}
