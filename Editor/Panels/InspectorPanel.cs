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
        bool _showBlendTree = true;
        readonly CleanupInspector _cleanup = new CleanupInspector();
        readonly OverviewInspector _overview;
        readonly StateMachineInspector _stateMachine;
        readonly VrcBehaviourDrawers _vrcDrawers;
        readonly BehaviourInspector _behaviours;
        readonly MultiStateBehaviourInspector _multiBehaviours;
        readonly SyncRequestInspector _syncRequests;

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
            if (selectedStates.Count >= 2 && AnyStateAlive(selectedStates))
            {
                DrawMultiStateEditor(selectedStates);
                return;
            }

            if (selection is AnimatorState state)
            {
                DrawState(state);
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

        // ---- multi-state -----------------------------------------------------

        static bool AnyStateAlive(List<AnimatorState> states)
        {
            foreach (var s in states)
                if (s != null) return true;
            return false;
        }

        /// <summary>
        /// Bulk editor for the selected states' common fields. Mirrors the multi-transition
        /// editor: every row shows the shared value (or a "mixed" placeholder) and writes back to
        /// every selected state with a single undo entry.
        /// </summary>
        void DrawMultiStateEditor(List<AnimatorState> states)
        {
            // Drop destroyed entries up front so the mixed-value detection and writes don't
            // walk a null reference mid-IMGUI.
            var alive = new List<AnimatorState>(states.Count);
            foreach (var s in states)
                if (s != null) alive.Add(s);
            if (alive.Count < 2)
            {
                if (alive.Count == 1) DrawState(alive[0]);
                else _overview.DrawOverview();
                return;
            }

            var controller = Context.Controller;
            EditorGUILayout.LabelField(alive.Count + " states selected", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Common Settings (applied to all selected)", EditorStyles.boldLabel);

            MultiEditGui.ObjectField<AnimatorState, Motion>("Motion", alive,
                x => x.motion, (x, v) => x.motion = v,
                undoName: "Edit States", postApply: s => _graphView.Sync.RefreshStateNode(s));
            MultiEditGui.Float("Speed", alive, x => x.speed, (x, v) => x.speed = v, undoName: "Edit States");
            MultiEditGui.Float("Cycle Offset", alive, x => x.cycleOffset, (x, v) => x.cycleOffset = v, undoName: "Edit States");
            MultiEditGui.Bool("Mirror", alive, x => x.mirror, (x, v) => x.mirror = v, undoName: "Edit States");
            MultiEditGui.Bool("Foot IK", alive, x => x.iKOnFeet, (x, v) => x.iKOnFeet = v, undoName: "Edit States");
            MultiEditGui.Bool("Write Defaults", alive, x => x.writeDefaultValues, (x, v) => x.writeDefaultValues = v,
                undoName: "Edit States", postApply: s => _graphView.Sync.RefreshStateNode(s));
            MultiEditGui.Text("Tag", alive, x => x.tag, (x, v) => x.tag = v, undoName: "Edit States");

            EditorGUILayout.Space(4);
            DrawMultiStateParameterOverrides(alive, controller);

            EditorGUILayout.Space(6);
            PanelGui.HorizontalLine();
            _multiBehaviours.DrawMultiStateBehaviours(alive);

            EditorGUILayout.Space(6);
            PanelGui.HorizontalLine();
            EditorGUILayout.LabelField("Bulk Actions", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Set Default State to First"))
                _graphView.Sync.SetDefaultState(alive[0]);
            if (GUILayout.Button("Delete All " + alive.Count))
            {
                if (EditorUtility.DisplayDialog("Delete States",
                    "Delete the " + alive.Count + " selected states? Their transitions will be removed too.",
                    "Delete", "Cancel"))
                {
                    DeleteStates(alive);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawMultiStateParameterOverrides(List<AnimatorState> states, AnimatorController controller)
        {
            var floatParams = PanelGui.ParameterNamesOfType(controller, AnimatorControllerParameterType.Float);
            var boolParams = PanelGui.ParameterNamesOfType(controller, AnimatorControllerParameterType.Bool);

            EditorGUILayout.LabelField("Parameter Overrides (applied to all)", EditorStyles.boldLabel);

            DrawMultiParameterOverride(states, "Speed Multiplier", floatParams,
                x => x.speedParameterActive, x => x.speedParameter,
                (s, a, p) => { s.speedParameterActive = a; s.speedParameter = p; });
            DrawMultiParameterOverride(states, "Motion Time", floatParams,
                x => x.timeParameterActive, x => x.timeParameter,
                (s, a, p) => { s.timeParameterActive = a; s.timeParameter = p; });
            DrawMultiParameterOverride(states, "Mirror", boolParams,
                x => x.mirrorParameterActive, x => x.mirrorParameter,
                (s, a, p) => { s.mirrorParameterActive = a; s.mirrorParameter = p; });
            DrawMultiParameterOverride(states, "Cycle Offset", floatParams,
                x => x.cycleOffsetParameterActive, x => x.cycleOffsetParameter,
                (s, a, p) => { s.cycleOffsetParameterActive = a; s.cycleOffsetParameter = p; });
        }

        void DrawMultiParameterOverride(List<AnimatorState> states, string label, string[] parameters,
            Func<AnimatorState, bool> activeGetter, Func<AnimatorState, string> paramGetter,
            Action<AnimatorState, bool, string> apply)
        {
            EditorGUILayout.BeginHorizontal();

            bool firstActive = activeGetter(states[0]);
            bool activeMixed = false;
            foreach (var s in states)
                if (activeGetter(s) != firstActive) { activeMixed = true; break; }

            string firstParam = paramGetter(states[0]) ?? string.Empty;
            bool paramMixed = false;
            foreach (var s in states)
                if ((paramGetter(s) ?? string.Empty) != firstParam) { paramMixed = true; break; }

            EditorGUI.showMixedValue = activeMixed;
            EditorGUI.BeginChangeCheck();
            bool active = EditorGUILayout.ToggleLeft(label, firstActive, GUILayout.Width(150));
            EditorGUI.showMixedValue = false;
            bool activeChanged = EditorGUI.EndChangeCheck();

            string param = firstParam;
            bool paramChanged = false;
            using (new EditorGUI.DisabledScope(!active && !activeMixed))
            {
                int idx = Mathf.Max(0, Array.IndexOf(parameters, param));
                EditorGUI.showMixedValue = paramMixed;
                EditorGUI.BeginChangeCheck();
                idx = EditorGUILayout.Popup(idx, parameters);
                EditorGUI.showMixedValue = false;
                paramChanged = EditorGUI.EndChangeCheck();
                if (idx >= 0 && idx < parameters.Length) param = parameters[idx];
            }

            if (activeChanged || paramChanged)
            {
                using (new UndoScope("Edit States"))
                    foreach (var s in states)
                    {
                        Undo.RegisterCompleteObjectUndo(s, "Edit States");
                        // Carry over whatever the user didn't touch so a single click on the
                        // toggle doesn't also rewrite the parameter name and vice versa.
                        bool newActive = activeChanged ? active : activeGetter(s);
                        string newParam = paramChanged ? param : paramGetter(s);
                        apply(s, newActive, newParam);
                        EditorUtility.SetDirty(s);
                    }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DeleteStates(List<AnimatorState> states)
        {
            var sm = Context.CurrentStateMachine;
            if (sm == null) return;
            using (new UndoScope("Delete States"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Delete States");
                foreach (var s in states)
                {
                    if (s == null) continue;
                    sm.RemoveState(s);
                }
                EditorUtility.SetDirty(sm);
            }
            Context.Select(null);
            _graphView.Sync.RequestRebuild();
        }

        // ---- state -----------------------------------------------------------

        void DrawState(AnimatorState state)
        {
            var controller = Context.Controller;
            EditorGUILayout.LabelField("State", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField("Name", state.name);
            var motion = (Motion)EditorGUILayout.ObjectField("Motion", state.motion, typeof(Motion), false);
            float speed = EditorGUILayout.FloatField("Speed", state.speed);
            float cycleOffset = EditorGUILayout.FloatField("Cycle Offset", state.cycleOffset);
            bool mirror = EditorGUILayout.Toggle("Mirror", state.mirror);
            bool ikOnFeet = EditorGUILayout.Toggle("Foot IK", state.iKOnFeet);
            bool writeDefaults = EditorGUILayout.Toggle("Write Defaults", state.writeDefaultValues);
            string tag = EditorGUILayout.TextField("Tag", state.tag);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(state, "Edit State");
                bool visualChange = state.name != name || state.motion != motion;
                bool badgeChange = state.writeDefaultValues != writeDefaults;
                if (!string.IsNullOrEmpty(name)) state.name = name;
                state.motion = motion;
                state.speed = speed;
                state.cycleOffset = cycleOffset;
                state.mirror = mirror;
                state.iKOnFeet = ikOnFeet;
                state.writeDefaultValues = writeDefaults;
                state.tag = tag;
                EditorUtility.SetDirty(state);
                if (visualChange) Context.NotifyGraphStructureChanged();
                // The WD badge lives on the graph node; repaint it right away rather than
                // waiting for the next full rebuild.
                else if (badgeChange) _graphView.Sync.RefreshStateNode(state);
            }

            DrawStateParameters(state, controller);

            EditorGUILayout.Space(4);
            var transitions = state.transitions;
            EditorGUILayout.LabelField("Transitions (" + transitions.Length + ")", EditorStyles.boldLabel);
            foreach (var t in transitions)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(ParameterConverter.DescribeTransition(t));
                if (GUILayout.Button("Select", EditorStyles.miniButton, GUILayout.Width(56)))
                {
                    var edge = _graphView.Sync.FindEdge(t);
                    Context.Select((object)edge ?? t);
                    // Also center the graph view on the edge so the user can see what they
                    // selected — clicking "Select" without a follow-up frame leaves the user
                    // hunting for the highlighted edge on a large state machine.
                    Context.RequestFrameOn((object)edge ?? t);
                }
                EditorGUILayout.EndHorizontal();
            }

            if (state.motion is BlendTree blendTree)
            {
                EditorGUILayout.Space(4);
                _showBlendTree = EditorGUILayout.Foldout(_showBlendTree, "Blend Tree", true);
                if (_showBlendTree)
                {
                    EditorGUI.indentLevel++;
                    BlendTreePanel.Draw(blendTree, Context);
                    EditorGUI.indentLevel--;
                }
            }

            _syncRequests.DrawSyncRequests(state);
            _behaviours.DrawBehaviours(state);
        }

        /// <summary>
        /// Optional parameter drivers for Speed / Motion Time / Mirror / Cycle Offset.
        /// Speed, Motion Time and Cycle Offset take a Float parameter; Mirror takes a Bool one.
        /// </summary>
        void DrawStateParameters(AnimatorState state, AnimatorController controller)
        {
            var floatParams = PanelGui.ParameterNamesOfType(controller, AnimatorControllerParameterType.Float);
            var boolParams = PanelGui.ParameterNamesOfType(controller, AnimatorControllerParameterType.Bool);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Parameter Overrides", EditorStyles.boldLabel);

            DrawParameterOverride(state, "Speed Multiplier", floatParams,
                state.speedParameterActive, state.speedParameter,
                (active, param) => { state.speedParameterActive = active; state.speedParameter = param; });
            DrawParameterOverride(state, "Motion Time", floatParams,
                state.timeParameterActive, state.timeParameter,
                (active, param) => { state.timeParameterActive = active; state.timeParameter = param; });
            DrawParameterOverride(state, "Mirror", boolParams,
                state.mirrorParameterActive, state.mirrorParameter,
                (active, param) => { state.mirrorParameterActive = active; state.mirrorParameter = param; });
            DrawParameterOverride(state, "Cycle Offset", floatParams,
                state.cycleOffsetParameterActive, state.cycleOffsetParameter,
                (active, param) => { state.cycleOffsetParameterActive = active; state.cycleOffsetParameter = param; });
        }

        /// <summary>One "drive this from a parameter" row: a toggle plus a parameter popup.</summary>
        void DrawParameterOverride(AnimatorState state, string label, string[] parameters,
            bool currentActive, string currentParam, Action<bool, string> apply)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool active = EditorGUILayout.ToggleLeft(label, currentActive, GUILayout.Width(150));
            string param = currentParam;
            using (new EditorGUI.DisabledScope(!active))
            {
                int idx = Mathf.Max(0, Array.IndexOf(parameters, param));
                idx = EditorGUILayout.Popup(idx, parameters);
                param = parameters[idx];
            }
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(state, "Edit State");
                apply(active, param);
                EditorUtility.SetDirty(state);
            }
            EditorGUILayout.EndHorizontal();
        }

    }
}
