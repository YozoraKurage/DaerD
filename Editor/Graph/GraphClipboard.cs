using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Drives the three clipboards (states, frames / notes, transitions) from explicit model
    /// lists, so the copy and paste commands run without a graph view: GraphSync turns the
    /// selection into those lists and does the rebuild and selection afterwards.
    /// </summary>
    class GraphClipboard
    {
        readonly DaerDContext _context;
        readonly EdgeCommands _transitions;
        readonly FrameCommands _frames;

        public GraphClipboard(DaerDContext context, EdgeCommands transitions, FrameCommands frames)
        {
            _context = context;
            _transitions = transitions;
            _frames = frames;
        }

        // ---- states ----------------------------------------------------------

        public void CopyStates(List<AnimatorState> states, Func<AnimatorState, Vector2> positionOf)
        {
            if (states.Count == 0) return;
            StateClipboard.Copy(states, positionOf, null, _context.Controller, _context.CurrentStateMachine);
            // States and frames/notes paste together, so a fresh copy of one kind has to drop the
            // other — otherwise the next paste would also drop whatever was copied before it.
            FrameNoteClipboard.Clear();
        }

        /// <summary>
        /// Pastes into the state machine currently on screen, so switching layers between the
        /// copy and the paste is what moves states from one layer to another. Parameters the
        /// states reference are recreated when the destination controller lacks them.
        /// Returns false when the clipboard was empty and nothing changed.
        /// </summary>
        public bool PasteStates(Vector2 position)
        {
            if (!StateClipboard.HasData) return false;
            var controller = _context.Controller;
            int parametersBefore = controller != null ? controller.parameters.Length : 0;
            StateClipboard.Paste(_context.CurrentStateMachine, position, controller);
            if (controller != null && controller.parameters.Length != parametersBefore)
                _context.NotifyParametersChanged();
            return true;
        }

        // ---- frames / notes --------------------------------------------------

        /// <summary>
        /// Ctrl+C over the canvas: the states, frames and notes in the selection all go to their
        /// clipboards in one gesture, sharing a single anchor so a mixed selection keeps its
        /// relative layout when it is pasted — including into a different layer.
        /// </summary>
        public void CopyElements(List<AnimatorState> states, List<GraphFrameData.Frame> frames,
            List<GraphFrameData.Note> notes, int subStateMachines, Func<AnimatorState, Vector2> positionOf)
        {
            // Sub-state machines aren't part of the state clipboard. Say so instead of copying a
            // silently incomplete selection — "select all" in a layer that has them looks like it
            // worked until the paste comes up short.
            if (subStateMachines > 0)
                Debug.Log("DaerD: " + subStateMachines + " sub-state machine(s) were left out of the copy"
                    + " — copy the whole layer (layer settings > Copy Layer) to move those too.");

            if (states.Count == 0 && frames.Count == 0 && notes.Count == 0) return;

            var anchor = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var state in states) anchor = Vector2.Min(anchor, positionOf(state));
            foreach (var frame in frames) anchor = Vector2.Min(anchor, frame.bounds.position);
            foreach (var note in notes) anchor = Vector2.Min(anchor, note.bounds.position);

            StateClipboard.Copy(states, positionOf, anchor, _context.Controller, _context.CurrentStateMachine);
            FrameNoteClipboard.Copy(frames, notes, anchor);
        }

        /// <summary>Copies one frame (from its context menu), dropping any copied states.</summary>
        public void CopyFrame(GraphFrameData.Frame frame)
        {
            if (frame == null) return;
            StateClipboard.Clear();
            FrameNoteClipboard.Copy(new List<GraphFrameData.Frame> { frame }, null);
        }

        /// <summary>Copies one note (from its context menu), dropping any copied states.</summary>
        public void CopyNote(GraphFrameData.Note note)
        {
            if (note == null) return;
            StateClipboard.Clear();
            FrameNoteClipboard.Copy(null, new List<GraphFrameData.Note> { note });
        }

        /// <summary>
        /// Pastes the copied frames and notes into the state machine currently on screen — the
        /// clipboard holds no state machine reference, so this is what makes the copy land in
        /// whichever layer the user has open. Null means nothing was pasted.
        /// </summary>
        public List<object> PasteFramesAndNotes(Vector2 position)
        {
            if (!FrameNoteClipboard.HasData || _context.Controller == null) return null;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;

            return FrameNoteClipboard.Paste(_frames.Ensure(), sm, position);
        }

        // ---- transitions -----------------------------------------------------

        /// <summary>
        /// Copies every transition of the given edges, recording each transition's source kind /
        /// source node and its destination so the snapshots can later be pasted onto a different
        /// state either as the new source or as the new destination.
        /// </summary>
        public void CopyTransitions(IEnumerable<(TransitionEnd source, IList<AnimatorTransitionBase> transitions)> edges)
        {
            var snapshots = new List<TransitionClipboard.Snapshot>();
            foreach (var edge in edges)
            {
                ResolveSourceContext(edge.source,
                    out var kind, out var sourceState, out var sourceSm);
                foreach (var t in edge.transitions)
                {
                    if (t == null) continue;
                    snapshots.Add(TransitionClipboard.CaptureWithContext(t, kind, sourceState, sourceSm));
                }
            }
            if (snapshots.Count > 0)
                TransitionClipboard.CopySnapshots(snapshots);
        }

        /// <summary>
        /// Applies the first copied transition's settings (timing, conditions, mute/solo) onto every
        /// given transition — the "paste onto" behaviour, driven from Ctrl+V. Returns whether
        /// anything was touched.
        /// </summary>
        public bool PasteTransitionSettingsOnto(IEnumerable<AnimatorTransitionBase> transitions)
        {
            if (!TransitionClipboard.HasData) return false;
            var snapshot = TransitionClipboard.Snapshots[0];
            bool any = false;
            using (new UndoScope("Paste Transition Settings"))
            {
                foreach (var t in transitions)
                    if (t != null) { TransitionClipboard.Apply(t, snapshot); any = true; }
            }
            return any;
        }

        /// <summary>
        /// Adds a new transition alongside the existing ones between each given pair of ends, for
        /// every copied snapshot — the "paste as new" behaviour, driven from Ctrl+Shift+V. Returns
        /// false when the clipboard was empty; <paramref name="last"/> is the transition to select.
        /// </summary>
        public bool PasteTransitionsAsNewOn(IEnumerable<(TransitionEnd source, TransitionEnd destination)> pairs,
            out AnimatorTransitionBase last)
        {
            last = null;
            if (!TransitionClipboard.HasData) return false;
            var snapshots = TransitionClipboard.Snapshots;
            using (new UndoScope("Paste Transition As New"))
            {
                foreach (var pair in pairs)
                {
                    var created = _transitions.Recreate(snapshots, pair.source, pair.destination);
                    if (created.Count > 0) last = created[created.Count - 1];
                }
            }
            return true;
        }

        static void ResolveSourceContext(TransitionEnd source,
            out TransitionClipboard.SourceKind kind,
            out AnimatorState state,
            out AnimatorStateMachine stateMachine)
        {
            kind = TransitionClipboard.SourceKind.None;
            state = null;
            stateMachine = null;
            switch (source.Kind)
            {
                case TransitionEndKind.State:
                    kind = TransitionClipboard.SourceKind.State;
                    state = source.State;
                    break;
                case TransitionEndKind.SubStateMachine:
                    kind = TransitionClipboard.SourceKind.SubStateMachine;
                    stateMachine = source.StateMachine;
                    break;
                case TransitionEndKind.AnyState:
                    kind = TransitionClipboard.SourceKind.AnyState;
                    break;
                case TransitionEndKind.Entry:
                    kind = TransitionClipboard.SourceKind.Entry;
                    break;
            }
        }

        /// <summary>
        /// Pastes the clipboard transitions onto <paramref name="state"/>, using it as the source
        /// for every new transition. Each new transition's destination is the snapshot's recorded
        /// destination (state, sub-state machine, or Exit). Snapshots whose destination cannot be
        /// resolved inside the current state machine are skipped. Null means nothing ran.
        /// </summary>
        public List<AnimatorTransitionBase> PasteTransitionsWithStateAsSource(AnimatorState state)
        {
            if (state == null) return null;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;
            var snapshots = TransitionClipboard.Snapshots;
            if (snapshots.Count == 0) return null;

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Paste Transition (As Source)"))
            {
                Undo.RegisterCompleteObjectUndo(state, "Paste Transition");
                foreach (var snap in snapshots)
                {
                    AnimatorTransitionBase t = null;
                    if (snap.isExit)
                    {
                        t = state.AddExitTransition();
                    }
                    else if (snap.destinationState != null && sm.ContainsState(snap.destinationState))
                    {
                        if (snap.destinationState == state) continue;
                        t = state.AddTransition(snap.destinationState);
                    }
                    else if (snap.destinationStateMachine != null && sm.ContainsStateMachine(snap.destinationStateMachine))
                    {
                        t = state.AddTransition(snap.destinationStateMachine);
                    }
                    if (t == null) continue;
                    TransitionClipboard.Apply(t, snap);
                    created.Add(t);
                }
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }
            return created;
        }

        /// <summary>
        /// Pastes the clipboard transitions onto <paramref name="state"/>, using it as the
        /// destination for every new transition. Each new transition is added at the snapshot's
        /// original source (state, sub-state machine, AnyState, or Entry of the current state
        /// machine). Snapshots whose source cannot be resolved are skipped. Null means nothing ran.
        /// </summary>
        public List<AnimatorTransitionBase> PasteTransitionsWithStateAsDestination(AnimatorState state)
        {
            if (state == null) return null;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;
            var snapshots = TransitionClipboard.Snapshots;
            if (snapshots.Count == 0) return null;

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Paste Transition (As Destination)"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Paste Transition");
                foreach (var snap in snapshots)
                {
                    AnimatorTransitionBase t = null;
                    switch (snap.sourceKind)
                    {
                        case TransitionClipboard.SourceKind.State:
                            if (snap.sourceState == null || snap.sourceState == state) break;
                            if (!sm.ContainsState(snap.sourceState)) break;
                            Undo.RegisterCompleteObjectUndo(snap.sourceState, "Paste Transition");
                            t = snap.sourceState.AddTransition(state);
                            break;
                        case TransitionClipboard.SourceKind.SubStateMachine:
                            if (snap.sourceStateMachine == null) break;
                            if (!sm.ContainsStateMachine(snap.sourceStateMachine)) break;
                            t = sm.AddStateMachineTransition(snap.sourceStateMachine, state);
                            break;
                        case TransitionClipboard.SourceKind.AnyState:
                            t = sm.AddAnyStateTransition(state);
                            break;
                        case TransitionClipboard.SourceKind.Entry:
                            t = sm.AddEntryTransition(state);
                            break;
                    }
                    if (t == null) continue;
                    TransitionClipboard.Apply(t, snap);
                    created.Add(t);
                }
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }
            return created;
        }
    }
}
