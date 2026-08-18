using System.Collections.Generic;
using UnityEditor.Animations;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Deep-copies the content of one state machine into another (used for layer duplication).
    /// Motions, including blend trees, are shared by reference — editing a copied blend tree
    /// affects the original.
    /// </summary>
    static class StateMachineCloner
    {
        public static void Clone(AnimatorStateMachine source, AnimatorStateMachine destination)
        {
            Clone(source, destination, out _, out _);
        }

        /// <summary>
        /// Same as <see cref="Clone"/> but exposes the source-to-copy maps so callers (e.g. layer
        /// duplication) can mirror data hanging off the original objects, such as frames/notes
        /// stored in <see cref="GraphFrameData"/>, onto the new copies.
        /// </summary>
        public static void Clone(AnimatorStateMachine source, AnimatorStateMachine destination,
            out Dictionary<AnimatorState, AnimatorState> stateMap,
            out Dictionary<AnimatorStateMachine, AnimatorStateMachine> machineMap)
        {
            stateMap = new Dictionary<AnimatorState, AnimatorState>();
            machineMap = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            if (source == null || destination == null) return;

            machineMap[source] = destination;
            CloneNodes(source, destination, stateMap, machineMap);
            CloneTransitions(source, stateMap, machineMap);
        }

        static void CloneNodes(AnimatorStateMachine src, AnimatorStateMachine dst,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> machineMap)
        {
            dst.entryPosition = src.entryPosition;
            dst.exitPosition = src.exitPosition;
            dst.anyStatePosition = src.anyStatePosition;
            dst.parentStateMachinePosition = src.parentStateMachinePosition;

            foreach (var child in src.states)
            {
                if (child.state == null) continue;
                var copy = dst.AddState(child.state.name, child.position);
                CopyStateFields(child.state, copy);
                stateMap[child.state] = copy;
            }

            foreach (var child in src.stateMachines)
            {
                if (child.stateMachine == null) continue;
                var copy = dst.AddStateMachine(child.stateMachine.name, child.position);
                machineMap[child.stateMachine] = copy;
                CloneNodes(child.stateMachine, copy, stateMap, machineMap);
            }
        }

        static void CloneTransitions(AnimatorStateMachine src,
            Dictionary<AnimatorState, AnimatorState> stateMap,
            Dictionary<AnimatorStateMachine, AnimatorStateMachine> machineMap)
        {
            var dst = machineMap[src];

            if (src.defaultState != null && stateMap.TryGetValue(src.defaultState, out var defaultCopy))
                dst.defaultState = defaultCopy;

            foreach (var child in src.states)
            {
                if (child.state == null || !stateMap.TryGetValue(child.state, out var fromCopy)) continue;
                foreach (var t in child.state.transitions)
                {
                    AnimatorStateTransition created = null;
                    if (t.isExit) created = fromCopy.AddExitTransition();
                    else if (t.destinationState != null && stateMap.TryGetValue(t.destinationState, out var ds))
                        created = fromCopy.AddTransition(ds);
                    else if (t.destinationStateMachine != null && machineMap.TryGetValue(t.destinationStateMachine, out var dm))
                        created = fromCopy.AddTransition(dm);
                    if (created != null)
                        TransitionClipboard.Apply(created, TransitionClipboard.Capture(t));
                }
            }

            foreach (var t in src.anyStateTransitions)
            {
                AnimatorStateTransition created = null;
                if (t.destinationState != null && stateMap.TryGetValue(t.destinationState, out var ds))
                    created = dst.AddAnyStateTransition(ds);
                else if (t.destinationStateMachine != null && machineMap.TryGetValue(t.destinationStateMachine, out var dm))
                    created = dst.AddAnyStateTransition(dm);
                if (created != null)
                    TransitionClipboard.Apply(created, TransitionClipboard.Capture(t));
            }

            foreach (var t in src.entryTransitions)
            {
                AnimatorTransition created = null;
                if (t.destinationState != null && stateMap.TryGetValue(t.destinationState, out var ds))
                    created = dst.AddEntryTransition(ds);
                else if (t.destinationStateMachine != null && machineMap.TryGetValue(t.destinationStateMachine, out var dm))
                    created = dst.AddEntryTransition(dm);
                if (created != null)
                    TransitionClipboard.Apply(created, TransitionClipboard.Capture(t));
            }

            foreach (var child in src.stateMachines)
            {
                if (child.stateMachine == null || !machineMap.TryGetValue(child.stateMachine, out var fromCopy)) continue;
                foreach (var t in src.GetStateMachineTransitions(child.stateMachine))
                {
                    AnimatorTransition created = null;
                    if (t.isExit) created = dst.AddStateMachineExitTransition(fromCopy);
                    else if (t.destinationState != null && stateMap.TryGetValue(t.destinationState, out var ds))
                        created = dst.AddStateMachineTransition(fromCopy, ds);
                    else if (t.destinationStateMachine != null && machineMap.TryGetValue(t.destinationStateMachine, out var dm))
                        created = dst.AddStateMachineTransition(fromCopy, dm);
                    if (created != null)
                        TransitionClipboard.Apply(created, TransitionClipboard.Capture(t));
                }
            }

            foreach (var child in src.stateMachines)
                if (child.stateMachine != null && machineMap.ContainsKey(child.stateMachine))
                    CloneTransitions(child.stateMachine, stateMap, machineMap);
        }

        /// <summary>Copies every serialized state field except transitions and behaviours. Motions are shared.</summary>
        public static void CopyStateFields(AnimatorState src, AnimatorState dst)
        {
            dst.motion = src.motion;
            dst.speed = src.speed;
            dst.cycleOffset = src.cycleOffset;
            dst.mirror = src.mirror;
            dst.iKOnFeet = src.iKOnFeet;
            dst.writeDefaultValues = src.writeDefaultValues;
            dst.tag = src.tag;
            dst.speedParameterActive = src.speedParameterActive;
            dst.speedParameter = src.speedParameter;
            dst.mirrorParameterActive = src.mirrorParameterActive;
            dst.mirrorParameter = src.mirrorParameter;
            dst.cycleOffsetParameterActive = src.cycleOffsetParameterActive;
            dst.cycleOffsetParameter = src.cycleOffsetParameter;
            dst.timeParameterActive = src.timeParameterActive;
            dst.timeParameter = src.timeParameter;
        }
    }
}
