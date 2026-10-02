using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.IR
{
    /// <summary>
    /// Where a generation run writes, and how: which controller, whether the objects it makes
    /// are filed into the .controller as sub-assets, whether every change is recorded for Undo,
    /// and where the records the generators keep (GraphFrameData) live for the run.
    ///
    /// <para>WHY ONE OBJECT.</para>
    /// Every generator used to answer these for itself — "is the controller on disk?" asked by
    /// GetAssetPath in a dozen places, Undo registered unconditionally — which was right for the
    /// one place generation used to happen, the editor on a saved controller. A run that writes
    /// a controller nobody will ever save (a build, a preview) needs every one of those answers
    /// to change together: no Undo entries pointing at objects about to be thrown away, no
    /// sub-asset filing, records held in memory for exactly as long as the run. Asked in one
    /// place, that is one setting rather than a hunt for every branch.
    ///
    /// <para>AMBIENT, ON PURPOSE.</para>
    /// The post steps a recipe runs (async sync, gadgets) reach the engine through layers of
    /// helpers that take a controller and nothing else. Threading a target through all of them
    /// would rewrite their signatures for a setting that never varies within a run, so a run
    /// opens a scope (<see cref="Begin"/> / <see cref="BeginDetached"/>) and engine code asks
    /// <see cref="Of"/> for the controller in hand. Outside any scope — the wizards, the graph —
    /// the answer is what it always was: sub-assets when the controller is on disk, Undo on.
    /// </summary>
    sealed class GenerationTarget : IDisposable
    {
        [ThreadStatic] static GenerationTarget s_current;

        readonly GenerationTarget _outer;
        readonly IDisposable _records;
        bool _disposed;

        public AnimatorController Controller { get; }

        /// <summary>Objects the run creates are stored inside the .controller file.</summary>
        public bool Persisted { get; }

        /// <summary>Changes and created objects are recorded for Undo.</summary>
        public bool RecordsUndo { get; }

        GenerationTarget(AnimatorController controller, bool recordsUndo, bool detached, bool scoped)
        {
            Controller = controller;
            Persisted = !detached && controller != null
                && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller));
            RecordsUndo = recordsUndo;
            if (!scoped) return;
            _outer = s_current;
            s_current = this;
            if (detached) _records = GraphFrameData.HoldInMemory(controller);
        }

        /// <summary>The editor's own run on <paramref name="controller"/>: sub-assets when it is
        /// on disk, Undo on — what every generator did before targets existed, as a scope.</summary>
        public static GenerationTarget Begin(AnimatorController controller) =>
            new GenerationTarget(controller, recordsUndo: true, detached: false, scoped: true);

        /// <summary>
        /// A run on a controller that will never be saved: nothing is recorded for Undo, nothing
        /// is filed as a sub-asset, and the generators' records are held in memory until the
        /// scope ends. For a controller that is already an asset this still writes no Undo, but
        /// it cannot un-file what the controller already is — detached runs are meant for
        /// controllers made for the run.
        /// </summary>
        public static GenerationTarget BeginDetached(AnimatorController controller) =>
            new GenerationTarget(controller, recordsUndo: false, detached: true, scoped: true);

        /// <summary>The target in force for <paramref name="controller"/>: the open scope's when
        /// it is about this controller, otherwise the editor default.</summary>
        public static GenerationTarget Of(AnimatorController controller)
        {
            for (var t = s_current; t != null; t = t._outer)
                if (t.Controller == controller) return t;
            return new GenerationTarget(controller, recordsUndo: true, detached: false, scoped: false);
        }

        /// <summary>A freshly made object that belongs to the controller: recorded as created
        /// for Undo, and filed into the controller when it is on disk.</summary>
        public void Created(UnityEngine.Object made, string undoName)
        {
            if (made == null) return;
            if (RecordsUndo) Undo.RegisterCreatedObjectUndo(made, undoName);
            if (Persisted) AssetDatabase.AddObjectToAsset(made, Controller);
        }

        /// <summary>An existing object about to be changed, recorded for Undo when the run
        /// records it.</summary>
        public void Changing(UnityEngine.Object changed, string undoName)
        {
            if (changed != null && RecordsUndo) Undo.RegisterCompleteObjectUndo(changed, undoName);
        }

        /// <summary>Marks a changed object dirty. Skipped only for a detached run, whose
        /// objects nobody will save or inspect; an in-memory controller in the editor still
        /// gets it, because the windows showing it repaint on it.</summary>
        public void Dirty(UnityEngine.Object changed)
        {
            if (changed != null && (Persisted || RecordsUndo)) EditorUtility.SetDirty(changed);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _records?.Dispose();
            if (s_current == this) s_current = _outer;
        }
    }
}
