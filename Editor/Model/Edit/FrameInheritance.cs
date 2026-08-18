using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;

namespace Yozolab.DaerD.Edit
{
    /// <summary>
    /// Carries graph frames and notes from one set of state machines to another. Used when a
    /// layer is duplicated: <see cref="StateMachineCloner"/> deep-copies the state machine
    /// hierarchy and returns a source→copy map, then this helper mirrors every frame / note
    /// whose owning state machine is in that map to a new entry pointing at the copy.
    /// </summary>
    static class FrameInheritance
    {
        public static void CarryOver(AnimatorController controller,
            IReadOnlyDictionary<AnimatorStateMachine, AnimatorStateMachine> machineMap)
        {
            if (controller == null) return;
            var data = GraphFrameData.Find(controller);
            if (data == null) return;
            CarryOver(data, machineMap);
        }

        public static void CarryOver(GraphFrameData data,
            IReadOnlyDictionary<AnimatorStateMachine, AnimatorStateMachine> machineMap)
        {
            if (data == null || machineMap == null || machineMap.Count == 0) return;
            if (data.frames.Count == 0 && data.notes.Count == 0) return;

            Undo.RegisterCompleteObjectUndo(data, "Duplicate Layer");

            // Snapshot the existing entries first — the lists are mutated during the loop, so
            // walking the live list would re-clone the freshly added copies.
            var existingFrames = new List<GraphFrameData.Frame>(data.frames);
            var existingNotes = new List<GraphFrameData.Note>(data.notes);

            foreach (var frame in existingFrames)
            {
                if (frame == null || frame.stateMachine == null) continue;
                if (!machineMap.TryGetValue(frame.stateMachine, out var copySm)) continue;
                data.frames.Add(new GraphFrameData.Frame
                {
                    title = frame.title,
                    color = frame.color,
                    bounds = frame.bounds,
                    moveNodesWithFrame = frame.moveNodesWithFrame,
                    locked = frame.locked,
                    stateMachine = copySm,
                });
            }

            foreach (var note in existingNotes)
            {
                if (note == null || note.stateMachine == null) continue;
                if (!machineMap.TryGetValue(note.stateMachine, out var copySm)) continue;
                data.notes.Add(new GraphFrameData.Note
                {
                    text = note.text,
                    color = note.color,
                    bounds = note.bounds,
                    fontSize = note.fontSize,
                    stateMachine = copySm,
                });
            }

            EditorUtility.SetDirty(data);
        }
    }
}
