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

        public static IEnumerable<BlendTree> AllBlendTrees(this AnimatorController controller)
        {
            // The visited set guards against self-nested (cyclic) blend trees, which would
            // otherwise recurse forever; it also yields a tree shared by several states only once.
            var visited = new HashSet<BlendTree>();
            foreach (var state in controller.AllStates())
                foreach (var bt in BlendTreesIn(state.motion, visited))
                    yield return bt;
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
