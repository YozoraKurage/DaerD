using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- frame -----------------------------------------------------------

    /// <summary>Form for a selected frame box.</summary>
    class FrameInspector
    {
        readonly DaerDContext _context;
        readonly GraphSync _sync;

        public FrameInspector(DaerDContext context, GraphSync sync)
        {
            _context = context;
            _sync = sync;
        }

        public void DrawFrame(GraphFrameData.Frame frame)
        {
            var frameData = GraphFrameData.Find(_context.Controller);
            if (frameData == null || !frameData.frames.Contains(frame))
            {
                EditorGUILayout.LabelField(L.Tr("This frame no longer exists."));
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Frame"), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string frameTitle;
            Color color;
            using (new EditorGUI.DisabledScope(frame.locked))
            {
                frameTitle = EditorGUILayout.DelayedTextField(L.Tr("Title"), frame.title);
                color = EditorGUILayout.ColorField(L.Tr("Color"), frame.color);
            }
            bool moveNodes = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Move Nodes With Frame"), L.Tr("Dragging the title bar also moves the nodes inside the frame.")),
                frame.moveNodesWithFrame);
            bool locked = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Locked"), L.Tr("A locked frame cannot be moved, resized, renamed or deleted.")),
                frame.locked);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(frameData, "Edit Frame");
                frame.title = string.IsNullOrEmpty(frameTitle) ? frame.title : frameTitle;
                frame.color = color;
                frame.moveNodesWithFrame = moveNodes;
                frame.locked = locked;
                EditorUtility.SetDirty(frameData);
                _context.NotifyGraphVisualsChanged(frame);
            }

            EditorGUILayout.Space(6);
            NoteInspector.DrawFrameNoteClipboardRow(_sync, L.Tr("Copy Frame"),
                L.Tr("Copy this frame's box. Open another layer and paste to reuse it there."),
                () => _sync.CopyFrame(frame));

            EditorGUILayout.BeginHorizontal();
            // Duplicates this frame, the states inside, and the transitions among them — works
            // even when the frame is locked since the copy is independent.
            if (GUILayout.Button(L.Tr("Duplicate Frame")))
            {
                _sync.DuplicateFrame(frame);
                GUIUtility.ExitGUI();
            }
            using (new EditorGUI.DisabledScope(frame.locked))
            {
                if (GUILayout.Button(L.Tr("Delete Frame")))
                {
                    _sync.DeleteFrame(frame);
                    _context.Select(null);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
