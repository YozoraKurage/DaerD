using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Persistent storage for graph frames (comment/group boxes drawn behind nodes) and memo
    /// notes. One hidden sub-asset per controller, created lazily on first use — controllers
    /// that never use frames or notes are left untouched.
    /// </summary>
    class GraphFrameData : ScriptableObject
    {
        [Serializable]
        public class Frame
        {
            public string title = "Frame";
            public Color color = new Color(0.32f, 0.45f, 0.60f, 1f);
            public Rect bounds;
            public bool moveNodesWithFrame = true;
            /// A locked frame cannot be moved, resized, renamed or deleted from the graph.
            public bool locked;
            public AnimatorStateMachine stateMachine;
        }

        /// <summary>A free-floating memo (sticky note) drawn among the nodes.</summary>
        [Serializable]
        public class Note
        {
            public string text = "Memo";
            public Color color = new Color(0.93f, 0.86f, 0.51f, 1f);
            public Rect bounds;
            public int fontSize = 12;
            public AnimatorStateMachine stateMachine;
        }

        public List<Frame> frames = new List<Frame>();
        public List<Note> notes = new List<Note>();

        /// <summary>The frame holder already stored on the controller, or null when none exists.</summary>
        public static GraphFrameData Find(AnimatorController controller)
        {
            if (controller == null) return null;
            var path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is GraphFrameData data)
                    return data;
            return null;
        }

        /// <summary>Finds or creates the frame holder. In-memory controllers get a non-persisted instance.</summary>
        public static GraphFrameData GetOrCreate(AnimatorController controller)
        {
            var existing = Find(controller);
            if (existing != null) return existing;

            var data = CreateInstance<GraphFrameData>();
            data.name = "DaerD Frames";
            data.hideFlags = HideFlags.HideInHierarchy;
            var path = controller != null ? AssetDatabase.GetAssetPath(controller) : null;
            if (!string.IsNullOrEmpty(path))
            {
                AssetDatabase.AddObjectToAsset(data, controller);
                EditorUtility.SetDirty(controller);
            }
            return data;
        }

        /// <summary>The frames belonging to one state machine view (frames are per-graph, not per-layer).</summary>
        public List<Frame> FramesIn(AnimatorStateMachine sm)
        {
            var result = new List<Frame>();
            if (sm == null) return result;
            foreach (var frame in frames)
                if (frame != null && frame.stateMachine == sm)
                    result.Add(frame);
            return result;
        }

        public Frame AddFrame(AnimatorStateMachine sm, Rect bounds, string title = "Frame")
        {
            Undo.RegisterCompleteObjectUndo(this, "Create Frame");
            var frame = new Frame { title = title, bounds = bounds, stateMachine = sm };
            frames.Add(frame);
            EditorUtility.SetDirty(this);
            return frame;
        }

        public void RemoveFrame(Frame frame)
        {
            if (frame == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Delete Frame");
            frames.Remove(frame);
            EditorUtility.SetDirty(this);
        }

        /// <summary>The notes belonging to one state machine view.</summary>
        public List<Note> NotesIn(AnimatorStateMachine sm)
        {
            var result = new List<Note>();
            if (sm == null) return result;
            foreach (var note in notes)
                if (note != null && note.stateMachine == sm)
                    result.Add(note);
            return result;
        }

        public Note AddNote(AnimatorStateMachine sm, Rect bounds)
        {
            Undo.RegisterCompleteObjectUndo(this, "Create Note");
            var note = new Note { bounds = bounds, stateMachine = sm };
            notes.Add(note);
            EditorUtility.SetDirty(this);
            return note;
        }

        public void RemoveNote(Note note)
        {
            if (note == null) return;
            Undo.RegisterCompleteObjectUndo(this, "Delete Note");
            notes.Remove(note);
            EditorUtility.SetDirty(this);
        }
    }
}
