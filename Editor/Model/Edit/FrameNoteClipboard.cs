using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Edit
{
    /// <summary>
    /// In-memory clipboard for graph frames and memo notes. Entries are plain data with no link
    /// back to the state machine they came from, so a copy taken in one layer pastes into any
    /// other layer — or into another controller entirely. Cleared on domain reload.
    /// </summary>
    static class FrameNoteClipboard
    {
        class FrameEntry
        {
            public string title;
            public Color color;
            public Rect bounds;
            public bool moveNodesWithFrame;
        }

        class NoteEntry
        {
            public string text;
            public Color color;
            public Rect bounds;
            public int fontSize;
        }

        static readonly List<FrameEntry> _frames = new List<FrameEntry>();
        static readonly List<NoteEntry> _notes = new List<NoteEntry>();
        // Top-left corner of the copied group. Paste puts that corner at the paste position and
        // keeps everything else at its original offset from it.
        static Vector2 _anchor;

        public static bool HasData => _frames.Count > 0 || _notes.Count > 0;
        public static int FrameCount => _frames.Count;
        public static int NoteCount => _notes.Count;

        /// <summary>Corner the copy was taken from — paste there to reproduce the original
        /// placement in whichever layer is open.</summary>
        public static Vector2 Anchor => _anchor;

        public static void Clear()
        {
            _frames.Clear();
            _notes.Clear();
        }

        /// <summary>
        /// Replaces the clipboard with the given frames and notes. <paramref name="anchorOverride"/>
        /// lets a caller that copies states in the same gesture share one anchor across both
        /// clipboards, so the group's internal layout survives the paste.
        /// </summary>
        public static void Copy(IList<GraphFrameData.Frame> frames, IList<GraphFrameData.Note> notes,
            Vector2? anchorOverride = null)
        {
            Clear();

            var anchor = new Vector2(float.MaxValue, float.MaxValue);
            if (frames != null)
                foreach (var frame in frames)
                {
                    if (frame == null) continue;
                    anchor = Vector2.Min(anchor, frame.bounds.position);
                    _frames.Add(new FrameEntry
                    {
                        title = frame.title,
                        color = frame.color,
                        bounds = frame.bounds,
                        moveNodesWithFrame = frame.moveNodesWithFrame,
                    });
                }

            if (notes != null)
                foreach (var note in notes)
                {
                    if (note == null) continue;
                    anchor = Vector2.Min(anchor, note.bounds.position);
                    _notes.Add(new NoteEntry
                    {
                        text = note.text,
                        color = note.color,
                        bounds = note.bounds,
                        fontSize = note.fontSize,
                    });
                }

            if (!HasData) return;
            _anchor = anchorOverride ?? anchor;
        }

        /// <summary>
        /// Pastes the clipboard into <paramref name="target"/> with the group's top-left corner at
        /// <paramref name="pasteAt"/>. Returns the created Frame / Note objects in paste order.
        /// </summary>
        public static List<object> Paste(GraphFrameData data, AnimatorStateMachine target, Vector2 pasteAt)
        {
            var created = new List<object>();
            if (data == null || target == null || !HasData) return created;

            var offset = pasteAt - _anchor;
            using (new UndoScope(UndoName))
            {
                Undo.RegisterCompleteObjectUndo(data, UndoName);

                foreach (var entry in _frames)
                {
                    var frame = new GraphFrameData.Frame
                    {
                        title = UniqueTitle(data, target, entry.title),
                        color = entry.color,
                        bounds = new Rect(entry.bounds.position + offset, entry.bounds.size),
                        moveNodesWithFrame = entry.moveNodesWithFrame,
                        // The lock belongs to the original — a fresh paste has to be draggable, or
                        // the user can't place it where they wanted it.
                        locked = false,
                        stateMachine = target,
                    };
                    data.frames.Add(frame);
                    created.Add(frame);
                }

                foreach (var entry in _notes)
                {
                    var note = new GraphFrameData.Note
                    {
                        text = entry.text,
                        color = entry.color,
                        bounds = new Rect(entry.bounds.position + offset, entry.bounds.size),
                        fontSize = entry.fontSize,
                        stateMachine = target,
                    };
                    data.notes.Add(note);
                    created.Add(note);
                }

                EditorUtility.SetDirty(data);
            }
            return created;
        }

        static string UndoName =>
            _frames.Count > 0 && _notes.Count > 0 ? "Paste Frames & Notes"
            : _frames.Count > 0 ? "Paste Frames" : "Paste Notes";

        /// <summary>
        /// Keeps titles unique within the target state machine only. Pasting into another layer
        /// keeps the original title (the point of the copy), while pasting next to the original
        /// gets a suffix so the two stay tellable apart.
        /// </summary>
        static string UniqueTitle(GraphFrameData data, AnimatorStateMachine target, string title)
        {
            var taken = new HashSet<string>();
            foreach (var frame in data.frames)
                if (frame != null && frame.stateMachine == target)
                    taken.Add(frame.title);

            if (!taken.Contains(title)) return title;
            int i = 1;
            while (taken.Contains(title + " " + i)) i++;
            return title + " " + i;
        }
    }
}
