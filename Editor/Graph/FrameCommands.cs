using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Every write to the controller's <see cref="GraphFrameData"/> holder: creating, deleting
    /// and editing frames and memo notes. View-free — <see cref="GraphSync"/> keeps the wrappers
    /// that read node layout, refresh the visuals and rebuild the graph.
    ///
    /// This class owns the holder reference. It used to be a GraphSync field written from six
    /// different places; keeping the single field here means every reader still sees exactly
    /// the instance the last <see cref="Find"/> / <see cref="Ensure"/> produced.
    /// </summary>
    class FrameCommands
    {
        readonly DaerDContext _context;
        GraphFrameData _data;

        public FrameCommands(DaerDContext context)
        {
            _context = context;
        }

        /// <summary>The holder as last looked up, or null when the controller has none yet.</summary>
        public GraphFrameData Data => _data;

        /// <summary>Re-reads the holder already stored on the controller. Null when there is none.</summary>
        public GraphFrameData Find() => _data = GraphFrameData.Find(_context.Controller);

        /// <summary>Finds or creates the holder — every command that adds a frame or note goes through it.</summary>
        public GraphFrameData Ensure() => _data = GraphFrameData.GetOrCreate(_context.Controller);

        /// <summary>
        /// The shared body of every frame / note field edit: one undo step recorded on the holder,
        /// the change, and the dirty flag. <paramref name="undoLabel"/> stays per-command so the
        /// undo history keeps reading "Rename Frame", "Lock Frame", "Note Color" and so on.
        /// Returns false when there is no holder to write to, so the caller can skip the visual
        /// refresh that normally follows.
        /// </summary>
        bool Mutate(string undoLabel, Action apply)
        {
            if (_data == null) return false;
            Undo.RecordObject(_data, undoLabel);
            apply();
            EditorUtility.SetDirty(_data);
            return true;
        }

        // ---- frames ------------------------------------------------------------

        public GraphFrameData.Frame CreateFrame(Rect bounds)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null) return null;
            Ensure();
            return _data.AddFrame(sm, bounds);
        }

        public bool DeleteFrame(GraphFrameData.Frame frame)
        {
            if (frame == null || _data == null || frame.locked) return false;
            _data.RemoveFrame(frame);
            return true;
        }

        /// <summary>
        /// Duplicates the frame's box together with the states and notes the caller found inside
        /// it. Transitions whose source and destination are both in the duplicated set are
        /// reproduced; transitions crossing the frame's edge are dropped intentionally so the
        /// copy is self-contained.
        /// </summary>
        public GraphFrameData.Frame DuplicateFrame(GraphFrameData.Frame frame,
            IList<AnimatorState> statesInside, IList<GraphFrameData.Note> notesInside)
        {
            if (frame == null) return null;
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null) return null;

            Ensure();
            return FrameDuplicator.Duplicate(_data, _context.Controller, sm, frame, statesInside, notesInside);
        }

        public bool ToggleFrameMoveNodes(GraphFrameData.Frame frame)
        {
            if (frame == null) return false;
            return Mutate("Edit Frame", () => frame.moveNodesWithFrame = !frame.moveNodesWithFrame);
        }

        public bool RenameFrame(GraphFrameData.Frame frame, string title)
        {
            if (frame == null || string.IsNullOrEmpty(title)) return false;
            return Mutate("Rename Frame", () => frame.title = title);
        }

        public bool ToggleFrameLock(GraphFrameData.Frame frame)
        {
            if (frame == null) return false;
            // Read before the flip, so the step is named for what the user asked for.
            return Mutate(frame.locked ? "Unlock Frame" : "Lock Frame", () => frame.locked = !frame.locked);
        }

        public bool SetFrameColor(GraphFrameData.Frame frame, Color color)
        {
            if (frame == null) return false;
            return Mutate("Frame Color", () => frame.color = color);
        }

        /// <summary>Writes the snug bounds the caller measured from the frame's contents.</summary>
        public bool FitFrame(GraphFrameData.Frame frame, Rect bounds)
        {
            if (frame == null) return false;
            return Mutate("Fit Frame To Contents", () => frame.bounds = bounds);
        }

        /// <summary>Writes a frame's new box back to the asset after the resize handle changed it.</summary>
        public bool ResizeFrame(GraphFrameData.Frame frame, Rect bounds)
        {
            if (frame == null) return false;
            return Mutate("Resize Frame", () => frame.bounds = bounds);
        }

        // ---- notes -------------------------------------------------------------

        public GraphFrameData.Note CreateNote(Rect bounds)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null) return null;
            Ensure();
            return _data.AddNote(sm, bounds);
        }

        public bool DeleteNote(GraphFrameData.Note note)
        {
            if (note == null || _data == null) return false;
            _data.RemoveNote(note);
            return true;
        }

        public bool SetNoteText(GraphFrameData.Note note, string text)
        {
            if (note == null) return false;
            return Mutate("Edit Note", () => note.text = text ?? string.Empty);
        }

        public bool SetNoteColor(GraphFrameData.Note note, Color color)
        {
            if (note == null) return false;
            return Mutate("Note Color", () => note.color = color);
        }

        public bool SetNoteFontSize(GraphFrameData.Note note, int fontSize)
        {
            if (note == null) return false;
            return Mutate("Note Font Size", () => note.fontSize = fontSize);
        }

        /// <summary>Writes a note's new box back to the asset after the resize handle changed it.</summary>
        public bool ResizeNote(GraphFrameData.Note note, Rect bounds)
        {
            if (note == null) return false;
            return Mutate("Resize Note", () => note.bounds = bounds);
        }
    }
}
