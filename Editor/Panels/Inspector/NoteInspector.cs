using System;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- note ------------------------------------------------------------

    /// <summary>Form for a selected sticky note, and the clipboard row the frame form reuses.</summary>
    class NoteInspector
    {
        static readonly int[] NoteFontSizes = { 10, 12, 16 };
        static readonly string[] NoteFontSizeLabels = { "Small", "Medium", "Large" };

        /// <summary>The font-size popup's options, translated per draw the way
        /// <see cref="PanelGui.ModeLabels"/> handles the condition modes — a static array built
        /// once would keep whatever language was current at domain load.</summary>
        static string[] TranslatedFontSizeLabels()
        {
            var labels = new string[NoteFontSizeLabels.Length];
            for (int i = 0; i < labels.Length; i++)
                labels[i] = L.Tr(NoteFontSizeLabels[i]);
            return labels;
        }

        readonly DaerDContext _context;
        readonly GraphSync _sync;

        public NoteInspector(DaerDContext context, GraphSync sync)
        {
            _context = context;
            _sync = sync;
        }

        public void DrawNote(GraphFrameData.Note note)
        {
            var frameData = GraphFrameData.Find(_context.Controller);
            if (frameData == null || !frameData.notes.Contains(note))
            {
                EditorGUILayout.LabelField(L.Tr("This note no longer exists."));
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Note"), EditorStyles.boldLabel);

            // Text is edited in place on the note itself — the inspector column is too narrow
            // for sticky-note content (long lines were getting cut off in the old TextArea).
            // Show a read-only preview here and an "Edit Text" button that opens the in-graph
            // editor, the same one double-click / F2 triggers.
            EditorGUILayout.LabelField(L.Tr("Text"), EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextArea(string.IsNullOrEmpty(note.text) ? L.Tr("(empty)") : note.text,
                    EditorStyles.textArea, GUILayout.MinHeight(40));
            if (GUILayout.Button(L.Tr("Edit Text in Graph")))
            {
                _context.NotifyNoteEditRequested(note);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.LabelField(L.Tr("Tip: double-click the note (or press F2) to edit text in place."),
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            var color = EditorGUILayout.ColorField(L.Tr("Color"), note.color);
            int sizeIndex = Array.IndexOf(NoteFontSizes, note.fontSize);
            if (sizeIndex < 0) sizeIndex = 1;
            sizeIndex = EditorGUILayout.Popup(L.Tr("Font Size"), sizeIndex, TranslatedFontSizeLabels());
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(frameData, "Edit Note");
                note.color = color;
                note.fontSize = NoteFontSizes[sizeIndex];
                EditorUtility.SetDirty(frameData);
                _context.NotifyGraphVisualsChanged(note);
            }

            EditorGUILayout.Space(6);
            DrawFrameNoteClipboardRow(_sync, L.Tr("Copy Note"),
                L.Tr("Copy this note. Open another layer and paste to reuse it there."),
                () => _sync.CopyNote(note));

            if (GUILayout.Button(L.Tr("Delete Note")))
            {
                _sync.DeleteNote(note);
                _context.Select(null);
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// Copy / paste row shared by the frame and note inspectors. Paste targets the layer
        /// currently open in the graph — that's what makes these copies cross-layer — and drops
        /// the copy at the position it was taken from.
        /// </summary>
        public static void DrawFrameNoteClipboardRow(GraphSync sync, string copyLabel, string copyTooltip, Action copy)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(copyLabel, copyTooltip)))
                copy();
            using (new EditorGUI.DisabledScope(!FrameNoteClipboard.HasData))
                if (GUILayout.Button(new GUIContent(L.Tr("Paste Into This Layer"),
                        L.Tr("Paste the copied frames / notes into the layer currently open in the graph."))))
                {
                    sync.PasteFramesAndNotesAtOrigin();
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();
        }
    }
}
