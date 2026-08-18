using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Locates an arbitrary controller object (state, sub-state machine, transition or blend
    /// tree) inside a controller: the layer index, the state-machine drill path whose view
    /// shows it, and the object to select there. Lets the analyzer's Ping jump inside the
    /// DaerD graph instead of just pinging the .controller asset in the Project window.
    /// </summary>
    static class ControllerLocator
    {
        public class Location
        {
            public int layerIndex;
            public List<AnimatorStateMachine> stateMachinePath;
            /// <summary>The object to select in that view — may differ from the query (the
            /// owning state for a blend tree, null for a layer's root state machine).</summary>
            public object target;
        }

        /// <summary>
        /// Where an analyzer issue points: the object it carries, or — when that is nothing a
        /// graph can show (an unused parameter, a whole layer) — the layer it was reported on.
        /// Null when neither, and the caller falls back to a Project-window ping.
        /// </summary>
        public static Location LocateIssue(AnimatorController controller, AnalyzerIssue issue)
        {
            if (controller == null || issue == null) return null;
            var location = Locate(controller, issue.context);
            if (location == null && issue.layerIndex >= 0
                && issue.layerIndex < controller.layers.Length)
                location = new Location { layerIndex = issue.layerIndex };
            return location;
        }

        public static Location Locate(AnimatorController controller, object obj)
        {
            if (controller == null || obj == null) return null;

            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var root = layers[li].stateMachine;
                if (root == null) continue;
                var path = new List<AnimatorStateMachine> { root };
                if (ReferenceEquals(root, obj))
                    return Make(li, path, null);   // just open the layer's root view
                var found = Walk(root, path, li, obj);
                if (found != null) return found;
            }
            return null;
        }

        static Location Walk(AnimatorStateMachine sm, List<AnimatorStateMachine> path, int layerIndex, object obj)
        {
            foreach (var cs in sm.states)
            {
                var state = cs.state;
                if (state == null) continue;
                if (ReferenceEquals(state, obj))
                    return Make(layerIndex, path, state);
                // A blend tree issue lands on the state that plays the tree.
                if (obj is BlendTree bt && state.motion is BlendTree stateTree && stateTree.ContainsTree(bt))
                    return Make(layerIndex, path, state);
                foreach (var t in state.transitions)
                    if (ReferenceEquals(t, obj))
                        return Make(layerIndex, path, t);
            }
            foreach (var t in sm.anyStateTransitions)
                if (ReferenceEquals(t, obj))
                    return Make(layerIndex, path, t);
            foreach (var t in sm.entryTransitions)
                if (ReferenceEquals(t, obj))
                    return Make(layerIndex, path, t);
            foreach (var child in sm.stateMachines)
            {
                var childSm = child.stateMachine;
                if (childSm == null) continue;
                // The sub-state machine's node lives in the parent's view, so the drill path
                // for the hit itself is the current one.
                if (ReferenceEquals(childSm, obj))
                    return Make(layerIndex, path, childSm);
                foreach (var t in sm.GetStateMachineTransitions(childSm))
                    if (ReferenceEquals(t, obj))
                        return Make(layerIndex, path, t);
            }
            foreach (var child in sm.stateMachines)
            {
                var childSm = child.stateMachine;
                if (childSm == null || path.Contains(childSm)) continue;
                path.Add(childSm);
                var found = Walk(childSm, path, layerIndex, obj);
                path.RemoveAt(path.Count - 1);
                if (found != null) return found;
            }
            return null;
        }

        static Location Make(int layerIndex, List<AnimatorStateMachine> path, object target)
        {
            return new Location
            {
                layerIndex = layerIndex,
                // Snapshot — the walker mutates the list as it recurses.
                stateMachinePath = new List<AnimatorStateMachine>(path),
                target = target,
            };
        }
    }
}
