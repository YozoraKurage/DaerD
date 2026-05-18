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
            foreach (var state in controller.AllStates())
                foreach (var bt in BlendTreesIn(state.motion))
                    yield return bt;
        }

        static IEnumerable<BlendTree> BlendTreesIn(UnityEngine.Motion motion)
        {
            if (motion is BlendTree tree)
            {
                yield return tree;
                foreach (var child in tree.children)
                    foreach (var nested in BlendTreesIn(child.motion))
                        yield return nested;
            }
        }
    }
}
