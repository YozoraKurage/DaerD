using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>Context-sensitive inspector for the currently selected graph element.</summary>
    class InspectorPanel : PanelBase
    {
        readonly AnimatorGraphView _graphView;
        readonly List<AnimatorTransitionBase> _selectedTransitions = new List<AnimatorTransitionBase>();
        readonly MultiTransitionInspector _multiTransition;
        readonly TransitionInspector _transitions;

        // Behaviour rows selected in the state inspector. Selecting is what tells Ctrl+C/V to
        // act on behaviours instead of the state itself (the graph view owns state copy/paste).
        readonly List<StateMachineBehaviour> _selectedBehaviours = new List<StateMachineBehaviour>();
        object _lastSelection;
        readonly CleanupInspector _cleanup = new CleanupInspector();
        readonly OverviewInspector _overview;
        readonly StateMachineInspector _stateMachine;
        readonly VrcBehaviourDrawers _vrcDrawers;
        readonly BehaviourInspector _behaviours;
        readonly MultiStateBehaviourInspector _multiBehaviours;
        readonly SyncRequestInspector _syncRequests;
        readonly StateInspector _state;
        readonly MultiStateInspector _multiStates;

        public InspectorPanel(DaerDContext context, AnimatorGraphView graphView)
            : base(context, "Inspector")
        {
            _graphView = graphView;
            _overview = new OverviewInspector(context, graphView.Sync, _cleanup);
            _stateMachine = new StateMachineInspector(context);
            _multiTransition = new MultiTransitionInspector(context, graphView.Sync, _selectedTransitions);
            _transitions = new TransitionInspector(context, graphView, _selectedTransitions, _multiTransition);
            _vrcDrawers = new VrcBehaviourDrawers(context, graphView.Sync, Refresh);
            _behaviours = new BehaviourInspector(graphView.Sync, _selectedBehaviours, _vrcDrawers);
            _multiBehaviours = new MultiStateBehaviourInspector(graphView.Sync, _selectedBehaviours, _behaviours, _vrcDrawers);
            _syncRequests = new SyncRequestInspector(context, graphView.Sync);
            _state = new StateInspector(context, graphView.Sync, _syncRequests, _behaviours);
            _multiStates = new MultiStateInspector(context, graphView.Sync, _state, _overview, _multiBehaviours);
            context.SelectionChanged += OnSelectionChanged;
            // The leftover scan (and the object references captured in it) belongs to the
            // outgoing controller — drop it on a tab switch.
            context.ControllerChanged += ClearControllerCaches;
            context.GraphStructureChanged += Refresh;
            context.GraphRebuilt += Refresh;
            context.ParametersChanged += Refresh;
        }

        void OnSelectionChanged()
        {
            if (!ReferenceEquals(Context.Selection, _lastSelection))
            {
                _selectedTransitions.Clear();
                _transitions.ResetAnchor();
                _selectedBehaviours.Clear();
                _behaviours.ResetAnchor();
                if (Context.Selection is AnimatorTransitionBase transition)
                    _selectedTransitions.Add(transition);
                _lastSelection = Context.Selection;
                // Any in-flight condition edit was for the old selection — drop the focus so the
                // next IMGUI repaint redraws the controls fresh against the new transition(s).
                TransitionInspector.EndConditionInput();
            }
            Refresh();
        }

        void ClearControllerCaches()
        {
            _cleanup.Clear();
            Refresh();
        }

        protected override void DrawContent()
        {
            var selection = Context.Selection;
            // A deleted state / transition / blend tree lingers as a destroyed ("fake null")
            // Unity object until the graph rebuild catches up; touching its fields would throw
            // MissingReferenceException mid-IMGUI, so fall back to the overview instead.
            if (selection is UnityEngine.Object unityObject && unityObject == null)
                selection = null;

            // Multi-state editing takes precedence when the graph has more than one state
            // selected — mirrors the multi-transition editor behaviour.
            var selectedStates = _graphView.GetSelectedStates();
            if (selectedStates.Count >= 2 && MultiStateInspector.AnyStateAlive(selectedStates))
            {
                _multiStates.DrawMultiStateEditor(selectedStates);
                return;
            }

            if (selection is AnimatorState state)
            {
                _state.DrawState(state);
            }
            else if (selection is TransitionEdge || selection is AnimatorTransitionBase)
            {
                _transitions.DrawTransitionContext();
            }
            else if (selection is AnimatorStateMachine stateMachine)
            {
                _stateMachine.DrawStateMachine(stateMachine);
            }
            else if (selection is BlendTree blendTree)
            {
                DrawBlendTreeSelection(blendTree);
            }
            else if (selection is AnimationClip clip)
            {
                DrawClipSelection(clip);
            }
            else if (selection is GraphFrameData.Frame frame)
            {
                DrawFrame(frame);
            }
            else if (selection is GraphFrameData.Note note)
            {
                DrawNote(note);
            }
            else if (selection is SpecialNodeKind kind)
            {
                EditorGUILayout.HelpBox(kind + " node. Drag from its port to create transitions.", MessageType.Info);
            }
            else
            {
                _overview.DrawOverview();
            }
        }

        void DrawBlendTreeSelection(BlendTree blendTree)
        {
            EditorGUILayout.LabelField("Blend Tree", EditorStyles.boldLabel);
            BlendTreePanel.Draw(blendTree, Context);
        }

        void DrawClipSelection(AnimationClip clip)
        {
            EditorGUILayout.LabelField("Animation Clip", EditorStyles.boldLabel);
            EditorGUILayout.ObjectField("Clip", clip, typeof(AnimationClip), false);
            EditorGUILayout.LabelField("Length", clip.length.ToString("0.###") + "s");
            EditorGUILayout.LabelField("Frame Rate", clip.frameRate.ToString("0.#") + " fps");
            EditorGUILayout.LabelField("Looping", clip.isLooping ? "Yes" : "No");
            if (GUILayout.Button("Ping in Project"))
                EditorGUIUtility.PingObject(clip);
        }

        // ---- frame -----------------------------------------------------------

        void DrawFrame(GraphFrameData.Frame frame)
        {
            var frameData = GraphFrameData.Find(Context.Controller);
            if (frameData == null || !frameData.frames.Contains(frame))
            {
                EditorGUILayout.LabelField("This frame no longer exists.");
                return;
            }

            EditorGUILayout.LabelField("Frame", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string frameTitle;
            Color color;
            using (new EditorGUI.DisabledScope(frame.locked))
            {
                frameTitle = EditorGUILayout.DelayedTextField("Title", frame.title);
                color = EditorGUILayout.ColorField("Color", frame.color);
            }
            bool moveNodes = EditorGUILayout.Toggle(
                new GUIContent("Move Nodes With Frame", "Dragging the title bar also moves the nodes inside the frame."),
                frame.moveNodesWithFrame);
            bool locked = EditorGUILayout.Toggle(
                new GUIContent("Locked", "A locked frame cannot be moved, resized, renamed or deleted."),
                frame.locked);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(frameData, "Edit Frame");
                frame.title = string.IsNullOrEmpty(frameTitle) ? frame.title : frameTitle;
                frame.color = color;
                frame.moveNodesWithFrame = moveNodes;
                frame.locked = locked;
                EditorUtility.SetDirty(frameData);
                _graphView.Sync.RefreshFrameVisuals(frame);
            }

            EditorGUILayout.Space(6);
            DrawFrameNoteClipboardRow(L.Tr("Copy Frame"),
                L.Tr("Copy this frame's box. Open another layer and paste to reuse it there."),
                () => _graphView.Sync.CopyFrame(frame));

            EditorGUILayout.BeginHorizontal();
            // Duplicates this frame, the states inside, and the transitions among them — works
            // even when the frame is locked since the copy is independent.
            if (GUILayout.Button("Duplicate Frame"))
            {
                _graphView.Sync.DuplicateFrame(frame);
                GUIUtility.ExitGUI();
            }
            using (new EditorGUI.DisabledScope(frame.locked))
            {
                if (GUILayout.Button("Delete Frame"))
                {
                    _graphView.Sync.DeleteFrame(frame);
                    Context.Select(null);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---- note ------------------------------------------------------------

        static readonly int[] NoteFontSizes = { 10, 12, 16 };
        static readonly string[] NoteFontSizeLabels = { "Small", "Medium", "Large" };

        void DrawNote(GraphFrameData.Note note)
        {
            var frameData = GraphFrameData.Find(Context.Controller);
            if (frameData == null || !frameData.notes.Contains(note))
            {
                EditorGUILayout.LabelField("This note no longer exists.");
                return;
            }

            EditorGUILayout.LabelField("Note", EditorStyles.boldLabel);

            // Text is edited in place on the note itself — the inspector column is too narrow
            // for sticky-note content (long lines were getting cut off in the old TextArea).
            // Show a read-only preview here and an "Edit Text" button that opens the in-graph
            // editor, the same one double-click / F2 triggers.
            EditorGUILayout.LabelField("Text", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextArea(string.IsNullOrEmpty(note.text) ? "(empty)" : note.text,
                    EditorStyles.textArea, GUILayout.MinHeight(40));
            if (GUILayout.Button("Edit Text in Graph"))
            {
                _graphView.Sync.FindNoteNode(note)?.BeginEdit();
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.LabelField("Tip: double-click the note (or press F2) to edit text in place.",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            var color = EditorGUILayout.ColorField("Color", note.color);
            int sizeIndex = Array.IndexOf(NoteFontSizes, note.fontSize);
            if (sizeIndex < 0) sizeIndex = 1;
            sizeIndex = EditorGUILayout.Popup("Font Size", sizeIndex, NoteFontSizeLabels);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(frameData, "Edit Note");
                note.color = color;
                note.fontSize = NoteFontSizes[sizeIndex];
                EditorUtility.SetDirty(frameData);
                _graphView.Sync.RefreshNoteVisuals(note);
            }

            EditorGUILayout.Space(6);
            DrawFrameNoteClipboardRow(L.Tr("Copy Note"),
                L.Tr("Copy this note. Open another layer and paste to reuse it there."),
                () => _graphView.Sync.CopyNote(note));

            if (GUILayout.Button("Delete Note"))
            {
                _graphView.Sync.DeleteNote(note);
                Context.Select(null);
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// Copy / paste row shared by the frame and note inspectors. Paste targets the layer
        /// currently open in the graph — that's what makes these copies cross-layer — and drops
        /// the copy at the position it was taken from.
        /// </summary>
        void DrawFrameNoteClipboardRow(string copyLabel, string copyTooltip, Action copy)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent(copyLabel, copyTooltip)))
                copy();
            using (new EditorGUI.DisabledScope(!FrameNoteClipboard.HasData))
                if (GUILayout.Button(new GUIContent(L.Tr("Paste Into This Layer"),
                        L.Tr("Paste the copied frames / notes into the layer currently open in the graph."))))
                {
                    _graphView.Sync.PasteFramesAndNotesAtOrigin();
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();
        }
    }
}
