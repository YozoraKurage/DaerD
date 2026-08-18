using System;
using System.Collections.Generic;
using UnityEditor.Animations;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// Name search across every layer of a controller: states (by state name or by the name of
    /// their motion), sub-state machines, and blend-tree states. Each hit carries the layer index
    /// and state-machine drill path needed for <see cref="DaerDContext.NavigateTo"/>.
    /// </summary>
    static class StateSearch
    {
        public class Result
        {
            /// <summary>List label, e.g. "Base / Locomotion / Walk".</summary>
            public string label;
            public int layerIndex;
            /// <summary>Drill path from the layer's root SM down to the SM whose view shows the hit.</summary>
            public List<AnimatorStateMachine> stateMachinePath;
            /// <summary>The state or sub-state machine to select and frame.</summary>
            public object target;
        }

        public static List<Result> Find(AnimatorController controller, string query, int maxResults = 40)
        {
            var results = new List<Result>();
            if (controller == null || string.IsNullOrWhiteSpace(query)) return results;
            query = query.Trim();

            var layers = controller.layers;
            for (int li = 0; li < layers.Length && results.Count < maxResults; li++)
            {
                var root = layers[li].stateMachine;
                if (root == null) continue;
                var path = new List<AnimatorStateMachine> { root };
                Walk(root, path, li, layers[li].name, query, maxResults, results);
            }
            return results;
        }

        static void Walk(AnimatorStateMachine sm, List<AnimatorStateMachine> path, int layerIndex,
            string layerName, string query, int maxResults, List<Result> results)
        {
            if (results.Count >= maxResults) return;
            string pathLabel = path.PathLabel(layerName);

            foreach (var cs in sm.states)
            {
                if (results.Count >= maxResults) return;
                var state = cs.state;
                if (state == null) continue;
                if (Matches(state.name, query))
                    results.Add(MakeResult(layerIndex, path, pathLabel + " / " + state.name, state));
                else if (state.motion != null && Matches(state.motion.name, query))
                    results.Add(MakeResult(layerIndex, path,
                        pathLabel + " / " + state.name + " — " + state.motion.name, state));
            }

            foreach (var child in sm.stateMachines)
            {
                if (results.Count >= maxResults) return;
                var childSm = child.stateMachine;
                if (childSm == null) continue;
                // The sub-state machine's node lives in the parent's view, so the drill path
                // for the hit itself is the current one.
                if (Matches(childSm.name, query))
                    results.Add(MakeResult(layerIndex, path, pathLabel + " / " + childSm.name, childSm));
            }

            foreach (var child in sm.stateMachines)
            {
                var childSm = child.stateMachine;
                if (childSm == null || path.Contains(childSm)) continue;
                path.Add(childSm);
                Walk(childSm, path, layerIndex, layerName, query, maxResults, results);
                path.RemoveAt(path.Count - 1);
            }
        }

        static bool Matches(string text, string query) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static Result MakeResult(int layerIndex, List<AnimatorStateMachine> path, string label, object target)
        {
            return new Result
            {
                label = label,
                layerIndex = layerIndex,
                // Snapshot — the walker mutates the list as it recurses.
                stateMachinePath = new List<AnimatorStateMachine>(path),
                target = target,
            };
        }
    }
}
