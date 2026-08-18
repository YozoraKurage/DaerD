using System.Collections.Generic;
using UnityEditor.Animations;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// Locates every place that references a parameter by name inside an AnimatorController:
    /// transition conditions, state-level parameter overrides (Speed / Motion Time / Mirror /
    /// Cycle Offset), blend-tree blend parameters (X, Y, Direct per-child) and VRC Parameter
    /// Driver entries. Each result carries the layer index and state-machine drill path
    /// needed to navigate to it.
    /// </summary>
    static class ParameterUsageFinder
    {
        public class Usage
        {
            /// <summary>Menu/list label, e.g. "L0 / SM Locomotion / Idle → Walk".</summary>
            public string label;
            /// <summary>Index of the owning layer in <c>controller.layers</c>.</summary>
            public int layerIndex;
            /// <summary>Drill path from the layer's root state machine down to the owner SM.</summary>
            public List<AnimatorStateMachine> stateMachinePath;
            /// <summary>What to feed into <see cref="DaerDContext.Select"/> once we arrive — the
            /// transition, state or blend tree the usage lives on.</summary>
            public object selection;
        }

        public static List<Usage> Find(AnimatorController controller, string parameterName)
        {
            var usages = new List<Usage>();
            if (controller == null || string.IsNullOrEmpty(parameterName)) return usages;
            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var root = layers[li].stateMachine;
                if (root == null) continue;
                var path = new List<AnimatorStateMachine> { root };
                WalkStateMachine(root, path, li, layers[li].name, parameterName, usages);
            }
            return usages;
        }

        static void WalkStateMachine(AnimatorStateMachine sm, List<AnimatorStateMachine> path,
            int layerIndex, string layerName, string param, List<Usage> usages)
        {
            string pathLabel = path.PathLabel(layerName);

            foreach (var t in sm.anyStateTransitions)
                if (TransitionUses(t, param))
                    usages.Add(MakeUsage(layerIndex, path,
                        $"{pathLabel} / AnyState {ParameterConverter.DescribeTransition(t)}", t));

            foreach (var t in sm.entryTransitions)
                if (TransitionUses(t, param))
                    usages.Add(MakeUsage(layerIndex, path,
                        $"{pathLabel} / Entry {ParameterConverter.DescribeTransition(t)}", t));

            foreach (var child in sm.stateMachines)
            {
                var childSm = child.stateMachine;
                if (childSm == null) continue;
                foreach (var t in sm.GetStateMachineTransitions(childSm))
                    if (TransitionUses(t, param))
                        usages.Add(MakeUsage(layerIndex, path,
                            $"{pathLabel} / {childSm.name} {ParameterConverter.DescribeTransition(t)}", t));
            }

            foreach (var behaviour in sm.behaviours)
                if (VrcParameterDriver.References(behaviour, param))
                    usages.Add(MakeUsage(layerIndex, path, $"{pathLabel} (Parameter Driver)", sm));

            foreach (var cs in sm.states)
            {
                var s = cs.state;
                if (s == null) continue;

                foreach (var t in s.transitions)
                    if (TransitionUses(t, param))
                        usages.Add(MakeUsage(layerIndex, path,
                            $"{pathLabel} / {s.name} {ParameterConverter.DescribeTransition(t)}", t));

                if (s.speedParameterActive && s.speedParameter == param)
                    usages.Add(MakeUsage(layerIndex, path, $"{pathLabel} / {s.name} (Speed)", s));
                if (s.timeParameterActive && s.timeParameter == param)
                    usages.Add(MakeUsage(layerIndex, path, $"{pathLabel} / {s.name} (Motion Time)", s));
                if (s.mirrorParameterActive && s.mirrorParameter == param)
                    usages.Add(MakeUsage(layerIndex, path, $"{pathLabel} / {s.name} (Mirror)", s));
                if (s.cycleOffsetParameterActive && s.cycleOffsetParameter == param)
                    usages.Add(MakeUsage(layerIndex, path, $"{pathLabel} / {s.name} (Cycle Offset)", s));

                foreach (var behaviour in s.behaviours)
                    if (VrcParameterDriver.References(behaviour, param))
                        usages.Add(MakeUsage(layerIndex, path, $"{pathLabel} / {s.name} (Parameter Driver)", s));

                if (s.motion is BlendTree bt)
                    WalkBlendTree(bt, layerIndex, path, $"{pathLabel} / {s.name}", param, usages, new HashSet<BlendTree>());
            }

            foreach (var child in sm.stateMachines)
            {
                var childSm = child.stateMachine;
                if (childSm == null) continue;
                path.Add(childSm);
                WalkStateMachine(childSm, path, layerIndex, layerName, param, usages);
                path.RemoveAt(path.Count - 1);
            }
        }

        static void WalkBlendTree(BlendTree tree, int layerIndex, List<AnimatorStateMachine> path,
            string prefix, string param, List<Usage> usages, HashSet<BlendTree> visited)
        {
            // Self-nested blend trees are legal in Unity; visited guards against infinite recursion.
            if (tree == null || !visited.Add(tree)) return;

            if (tree.blendParameter == param)
                usages.Add(MakeUsage(layerIndex, path, $"{prefix} / BlendTree '{tree.name}' (Blend X)", tree));
            if (tree.blendParameterY == param)
                usages.Add(MakeUsage(layerIndex, path, $"{prefix} / BlendTree '{tree.name}' (Blend Y)", tree));

            foreach (var child in tree.children)
            {
                if (child.directBlendParameter == param)
                {
                    string childName = child.motion != null ? child.motion.name : "(empty)";
                    usages.Add(MakeUsage(layerIndex, path,
                        $"{prefix} / BlendTree '{tree.name}' → {childName} (Direct)", tree));
                }
                if (child.motion is BlendTree nested)
                    WalkBlendTree(nested, layerIndex, path, prefix, param, usages, visited);
            }
        }

        static bool TransitionUses(AnimatorTransitionBase t, string param)
        {
            if (t == null) return false;
            foreach (var c in t.conditions)
                if (c.parameter == param) return true;
            return false;
        }

        static Usage MakeUsage(int layerIndex, List<AnimatorStateMachine> path, string label, object selection)
        {
            return new Usage
            {
                label = label,
                layerIndex = layerIndex,
                // Snapshot the path — the caller mutates it as it recurses up/down.
                stateMachinePath = new List<AnimatorStateMachine>(path),
                selection = selection,
            };
        }
    }
}
