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
            DrawMultiStateBehaviours(alive);

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

            DrawSyncRequests(state);
            _behaviours.DrawBehaviours(state);
        }

        // ---- sync requests ---------------------------------------------------

        /// <summary>Setups the user opened with "+ Add Sync Request" but hasn't ticked a
        /// target in yet — nothing is stored until the first tick, so the open box only
        /// exists here. Keyed by state instance ID; drafts of other states are dropped on
        /// draw, so a selection change closes them.</summary>
        readonly List<(int stateId, string baseName)> _syncRequestDrafts =
            new List<(int stateId, string baseName)>();

        /// <summary>
        /// The per-state Sync Request "component": while the avatar sits in this state, the
        /// ticked parameters are requested from an async sync setup out of turn (see
        /// <see cref="SyncRequestBuilder"/>). Backed by a DaerD-managed Parameter Driver —
        /// visible under Behaviours below, rewritten wholesale on every edit here — plus a
        /// record in GraphFrameData.
        /// </summary>
        void DrawSyncRequests(AnimatorState state)
        {
            var controller = Context.Controller;
            var configs = GraphFrameData.GetAsyncSyncs(controller);
            var entries = GraphFrameData.GetSyncRequests(controller, state);
            _syncRequestDrafts.RemoveAll(draft => draft.stateId != state.GetInstanceID());
            // No setup and nothing stored: most states in most controllers — draw nothing.
            if (configs.Count == 0 && entries.Count == 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(L.Tr("Sync Request"), EditorStyles.boldLabel);

            if (!VrcParameterDriver.SdkAvailable)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("VRChat SDK not found — the Parameter Driver behaviour is required."),
                    MessageType.Warning);
                return;
            }

            var open = new List<GraphFrameData.AsyncSyncConfig>();
            var addable = new List<GraphFrameData.AsyncSyncConfig>();
            foreach (var config in configs)
            {
                bool shown = _syncRequestDrafts.Contains((state.GetInstanceID(), config.baseName));
                foreach (var entry in entries)
                    if (entry.baseName == config.baseName)
                        shown = true;
                (shown ? open : addable).Add(config);
            }

            foreach (var config in open)
                DrawSyncRequestBox(state, config, FindSyncRequest(entries, config.baseName));

            // Records whose setup is gone (renamed base, deleted layer): the driver on the
            // state still fires into nothing — surface it instead of silently keeping it.
            foreach (var entry in entries)
            {
                if (FindConfig(configs, entry.baseName) != null) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.HelpBox(
                    L.Tr("This state requests from async sync '{0}', which no longer exists.",
                        entry.baseName),
                    MessageType.Warning);
                if (GUILayout.Button(L.Tr("Remove"), EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    SyncRequestBuilder.Remove(controller, state, entry.baseName);
                    _graphView.Sync.RefreshStateNode(state);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndVertical();
            }

            if (addable.Count > 0
                && GUILayout.Button(L.Tr("+ Add Sync Request"), EditorStyles.miniButton))
            {
                if (addable.Count == 1)
                {
                    _syncRequestDrafts.Add((state.GetInstanceID(), addable[0].baseName));
                }
                else
                {
                    var menu = new GenericMenu();
                    int stateId = state.GetInstanceID();
                    foreach (var config in addable)
                    {
                        string baseName = config.baseName;
                        menu.AddItem(new GUIContent(baseName), false,
                            () => _syncRequestDrafts.Add((stateId, baseName)));
                    }
                    menu.ShowAsContext();
                }
            }
        }

        static GraphFrameData.SyncRequest FindSyncRequest(
            List<GraphFrameData.SyncRequest> entries, string baseName)
        {
            foreach (var entry in entries)
                if (entry.baseName == baseName)
                    return entry;
            return null;
        }

        static GraphFrameData.AsyncSyncConfig FindConfig(
            List<GraphFrameData.AsyncSyncConfig> configs, string baseName)
        {
            foreach (var config in configs)
                if (config.baseName == baseName)
                    return config;
            return null;
        }

        /// <summary>One setup's box: target ticks applied immediately — the first tick
        /// creates the driver and the record, unticking the last one removes both.</summary>
        void DrawSyncRequestBox(AnimatorState state, GraphFrameData.AsyncSyncConfig config,
            GraphFrameData.SyncRequest entry)
        {
            var controller = Context.Controller;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Async Sync '{0}'", config.baseName),
                EditorStyles.miniBoldLabel);
            if (GUILayout.Button(L.Tr("Remove"), EditorStyles.miniButton, GUILayout.Width(70)))
            {
                _syncRequestDrafts.Remove((state.GetInstanceID(), config.baseName));
                SyncRequestBuilder.Remove(controller, state, config.baseName);
                _graphView.Sync.RefreshStateNode(state);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                L.Tr("Ticked parameters are synced out of turn while this state plays."),
                EditorStyles.miniLabel);

            var selected = new List<string>();
            if (entry != null) selected.AddRange(entry.targets);

            EditorGUI.BeginChangeCheck();
            foreach (var target in config.targets)
            {
                bool was = selected.Contains(target);
                bool now = EditorGUILayout.ToggleLeft(target, was);
                if (now && !was) selected.Add(target);
                else if (!now && was) selected.Remove(target);
            }
            if (EditorGUI.EndChangeCheck())
            {
                if (selected.Count == 0)
                {
                    // Keep the box open as a draft so the user can tick something else.
                    if (!_syncRequestDrafts.Contains((state.GetInstanceID(), config.baseName)))
                        _syncRequestDrafts.Add((state.GetInstanceID(), config.baseName));
                    if (entry != null)
                        SyncRequestBuilder.Remove(controller, state, config.baseName);
                }
                else
                {
                    SyncRequestBuilder.Apply(controller, config, state, selected);
                }
                _graphView.Sync.RefreshStateNode(state);
            }

            // A recipe regenerates its layers by destroy-and-recreate, on both sides of this
            // feature: a request on a recipe-built state dies with the state, and a
            // recipe-built sync layer is rebuilt from the recipe's own Requestable list.
            var codeOwned = GraphFrameData.GetCodeOwned(controller);
            var currentRoot = Context.CurrentLayer?.stateMachine;
            if (currentRoot != null && codeOwned.ContainsKey(currentRoot))
                EditorGUILayout.HelpBox(
                    L.Tr("This layer is generated by a recipe — the next Generate rebuilds its states and this request is lost. Add the request in the recipe instead."),
                    MessageType.Warning);
            else if (config.layer != null && codeOwned.ContainsKey(config.layer))
                EditorGUILayout.HelpBox(
                    L.Tr("Async sync '{0}' is generated by a recipe — mark the targets with .Requestable(...) there, or the next Generate drops the request routes.",
                        config.baseName),
                    MessageType.Info);

            EditorGUILayout.EndVertical();
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

        // ---- behaviours across several states --------------------------------

        /// <summary>
        /// One behaviour "slot" shared by the selected states: same type, same instance name and
        /// same occurrence index within a state (so a state carrying two identically named
        /// drivers contributes to two slots). The first instance found is the one drawn; editing
        /// it mirrors onto the rest.
        /// </summary>
        class BehaviourSlot
        {
            public string typeName;
            public readonly List<StateMachineBehaviour> instances = new List<StateMachineBehaviour>();
            public readonly List<AnimatorState> owners = new List<AnimatorState>();
            public bool valuesDiffer;

            public StateMachineBehaviour Representative => instances[0];
        }

        /// <summary>
        /// Bulk editor for the behaviours of every selected state. Behaviours are matched across
        /// states by type + instance name; a slot present on all of them is editable and every
        /// edit is mirrored onto its peers.
        /// </summary>
        void DrawMultiStateBehaviours(List<AnimatorState> states)
        {
            var slots = BuildBehaviourSlots(states);
            var representatives = new StateMachineBehaviour[slots.Count];
            for (int i = 0; i < slots.Count; i++)
                representatives[i] = slots[i].Representative;

            _behaviours.PruneBehaviourSelection(representatives);
            HandleMultiStateBehaviourShortcuts(states, representatives);

            bool hasSelection = _selectedBehaviours.Count > 0;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(hasSelection
                    ? "Behaviours (" + _selectedBehaviours.Count + "/" + slots.Count + ")"
                    : "Behaviours (" + slots.Count + ")",
                EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(slots.Count == 0))
                if (GUILayout.Button(new GUIContent(L.Tr("Copy"), hasSelection
                        ? L.Tr("Copy the selected behaviours; paste from a state's right-click menu or here.")
                        : L.Tr("Copy one instance of every behaviour found on the selected states.")),
                        EditorStyles.miniButton, GUILayout.Width(50)))
                    _behaviours.CopyBehaviours(representatives);
            using (new EditorGUI.DisabledScope(VrcBehaviours.ClipboardCount == 0))
                if (GUILayout.Button(new GUIContent(L.Tr("Paste"),
                        L.Tr("Append the copied behaviours to every selected state.")),
                        EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    PasteBehavioursToAll(states);
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();

            if (slots.Count == 0)
            {
                EditorGUILayout.LabelField(L.Tr("None of the selected states have behaviours."),
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    L.Tr("Edits apply to every selected state that has the behaviour. Click a title to select (Ctrl / Shift for multi-select); Ctrl+C / Ctrl+V copies and pastes."),
                    EditorStyles.miniLabel);
            }

            for (int i = 0; i < slots.Count; i++)
                DrawBehaviourSlot(slots[i], states, representatives, i);

            if (GUILayout.Button("+ Add Behaviour to All " + states.Count))
                _vrcDrawers.ShowAddBehaviourMenu(states);
        }

        void DrawBehaviourSlot(BehaviourSlot slot, List<AnimatorState> states,
            StateMachineBehaviour[] representatives, int index)
        {
            var representative = slot.Representative;
            if (representative == null) return;
            bool selected = _selectedBehaviours.Contains(representative);
            int missing = states.Count - slot.instances.Count;

            var boxBackground = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = PanelGui.SelectionTint;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = boxBackground;

            EditorGUILayout.BeginHorizontal();
            var titleBackground = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = PanelGui.SelectionTint;
            if (GUILayout.Button(BehaviourInspector.BehaviourTitle(representative), BehaviourInspector.BehaviourTitleStyle))
                _behaviours.HandleBehaviourRowClick(representatives, index);
            GUI.backgroundColor = titleBackground;

            // Repeatable VRC types are matched by instance name, so renaming here has to reach
            // every peer or the slot would split apart on the next repaint.
            if (VrcBehaviours.IsVrcType(slot.typeName) && !VrcBehaviours.IsSingleton(slot.typeName))
            {
                string instanceName = EditorGUILayout.DelayedTextField(representative.name, GUILayout.Width(90));
                if (instanceName != representative.name)
                    RenameSlot(slot, instanceName);
            }

            EditorGUILayout.LabelField(slot.instances.Count + "/" + states.Count,
                EditorStyles.miniLabel, GUILayout.Width(38));

            using (new EditorGUI.DisabledScope(missing == 0))
                if (GUILayout.Button(new GUIContent("+ " + missing,
                        L.Tr("Copy this behaviour onto the selected states that don't have it.")),
                        EditorStyles.miniButton, GUILayout.Width(40)))
                {
                    AddSlotToMissingStates(slot, states);
                    GUIUtility.ExitGUI();
                }
            if (GUILayout.Button(new GUIContent(L.Tr("Remove All"),
                    L.Tr("Remove this behaviour from every selected state that has it.")),
                    EditorStyles.miniButton, GUILayout.Width(76)))
            {
                RemoveSlot(slot);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (slot.valuesDiffer)
                EditorGUILayout.LabelField(
                    L.Tr("Values differ between states — the first state's values are shown, and editing applies them to all."),
                    EditorStyles.miniLabel);

            // Draw the representative with the normal editor, then mirror whatever changed onto
            // its peers. The drawers write through their own SerializedObject, so an outer change
            // check is what tells us an edit happened — and since GUI.changed also fires for
            // things that touch no data (expanding a foldout, for one), the serialized content is
            // compared before mirroring. Overwriting peers on a foldout click would silently
            // flatten values that only differ between states.
            string before = slot.instances.Count > 1 ? EditorJsonUtility.ToJson(representative) : null;
            EditorGUI.BeginChangeCheck();
            if (!_vrcDrawers.TryDrawKnownVrcBehaviour(representative))
                VrcBehaviourDrawers.DrawSerializedFields(representative);
            if (EditorGUI.EndChangeCheck() && before != null && EditorJsonUtility.ToJson(representative) != before)
                PropagateSlot(slot);

            EditorGUILayout.EndVertical();
        }

        /// <summary>Groups the selected states' behaviours into slots, in the order the first
        /// state that owns them lists them.</summary>
        List<BehaviourSlot> BuildBehaviourSlots(List<AnimatorState> states)
        {
            var slots = new List<BehaviourSlot>();
            var byKey = new Dictionary<string, BehaviourSlot>();
            var occurrences = new Dictionary<string, int>();

            foreach (var state in states)
            {
                if (state == null) continue;
                occurrences.Clear();
                foreach (var behaviour in state.behaviours)
                {
                    if (behaviour == null) continue;
                    string typeName = behaviour.GetType().Name;
                    // Repeatable types are told apart by instance name; singletons and plain
                    // StateMachineBehaviours only ever have one meaningful identity per type.
                    string identity = VrcBehaviours.IsVrcType(typeName) && !VrcBehaviours.IsSingleton(typeName)
                        ? typeName + "\n" + behaviour.name
                        : typeName;
                    occurrences.TryGetValue(identity, out int occurrence);
                    occurrences[identity] = occurrence + 1;

                    string key = identity + "\n#" + occurrence;
                    if (!byKey.TryGetValue(key, out var slot))
                    {
                        slot = new BehaviourSlot { typeName = typeName };
                        byKey[key] = slot;
                        slots.Add(slot);
                    }
                    slot.instances.Add(behaviour);
                    slot.owners.Add(state);
                }
            }

            foreach (var slot in slots)
                slot.valuesDiffer = InstancesDiffer(slot.instances);
            return slots;
        }

        static bool InstancesDiffer(List<StateMachineBehaviour> instances)
        {
            if (instances.Count < 2 || instances[0] == null) return false;
            var first = new SerializedObject(instances[0]);
            for (int i = 1; i < instances.Count; i++)
            {
                if (instances[i] == null) return true;
                if (!SameVisibleData(first, new SerializedObject(instances[i]))) return true;
            }
            return false;
        }

        /// <summary>Compares the properties the inspector actually draws. The object name and
        /// hide flags are deliberately out of scope — they aren't what a bulk edit is about, and
        /// a differing name would otherwise make every slot look mixed.</summary>
        static bool SameVisibleData(SerializedObject a, SerializedObject b)
        {
            var left = a.GetIterator();
            var right = b.GetIterator();
            bool enterChildren = true;
            while (true)
            {
                bool hasLeft = left.NextVisible(enterChildren);
                bool hasRight = right.NextVisible(enterChildren);
                enterChildren = false;   // DataEquals already covers the children of each row
                if (hasLeft != hasRight) return false;
                if (!hasLeft) return true;
                if (left.propertyPath == "m_Script") continue;
                if (left.propertyPath != right.propertyPath) return false;
                if (!SerializedProperty.DataEquals(left, right)) return false;
            }
        }

        /// <summary>Copies the representative's contents onto every other instance of the slot.</summary>
        void PropagateSlot(BehaviourSlot slot)
        {
            var representative = slot.Representative;
            if (representative == null) return;
            // No UndoScope here: the drawer already recorded the representative's edit into the
            // current undo group, and starting a new one would split a single edit into two
            // Ctrl+Z steps. Joining that group also keeps slider drags collapsing as usual.
            Undo.SetCurrentGroupName("Edit Behaviours");
            for (int i = 1; i < slot.instances.Count; i++)
            {
                var peer = slot.instances[i];
                if (peer == null || ReferenceEquals(peer, representative)) continue;
                Undo.RegisterCompleteObjectUndo(peer, "Edit Behaviours");
                EditorUtility.CopySerialized(representative, peer);
                peer.name = representative.name;
                VrcBehaviours.MarkAsSubAsset(peer);
                EditorUtility.SetDirty(peer);
            }
        }

        void RenameSlot(BehaviourSlot slot, string instanceName)
        {
            using (new UndoScope("Rename Behaviour"))
                foreach (var instance in slot.instances)
                {
                    if (instance == null) continue;
                    Undo.RegisterCompleteObjectUndo(instance, "Rename Behaviour");
                    instance.name = instanceName;
                    EditorUtility.SetDirty(instance);
                }
        }

        /// <summary>Gives the states missing this slot a copy of the representative.</summary>
        void AddSlotToMissingStates(BehaviourSlot slot, List<AnimatorState> states)
        {
            var representative = slot.Representative;
            if (representative == null) return;
            var type = representative.GetType();

            using (new UndoScope("Add Behaviour"))
                foreach (var state in states)
                {
                    if (state == null || slot.owners.Contains(state)) continue;
                    // A singleton the state already carries under another name stays untouched —
                    // a second instance would be invalid.
                    if (VrcBehaviours.IsSingleton(slot.typeName) && VrcBehaviours.Has(state, slot.typeName))
                        continue;
                    Undo.RegisterCompleteObjectUndo(state, "Add Behaviour");
                    var added = state.AddStateMachineBehaviour(type);
                    if (added == null) continue;
                    EditorUtility.CopySerialized(representative, added);
                    added.name = representative.name;
                    VrcBehaviours.MarkAsSubAsset(added);
                    EditorUtility.SetDirty(state);
                    _graphView.Sync.RefreshStateNode(state);   // B badge updates immediately
                }
        }

        void RemoveSlot(BehaviourSlot slot)
        {
            _selectedBehaviours.Remove(slot.Representative);
            _behaviours.ResetAnchor();
            using (new UndoScope("Remove Behaviours"))
                for (int i = 0; i < slot.instances.Count; i++)
                {
                    var instance = slot.instances[i];
                    var owner = slot.owners[i];
                    if (instance == null || owner == null) continue;
                    VrcBehaviours.RemoveFrom(owner, instance);
                    _graphView.Sync.RefreshStateNode(owner);   // B badge updates immediately
                }
        }

        /// <summary>Ctrl+C / Ctrl+V over the multi-state behaviour list: copy takes the drawn
        /// (first-state) instances, paste appends to every selected state.</summary>
        void HandleMultiStateBehaviourShortcuts(List<AnimatorState> states, StateMachineBehaviour[] representatives)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || !(e.control || e.command))
                return;
            if (_selectedBehaviours.Count == 0) return;
            if (EditorGUIUtility.editingTextField) return;

            if (e.keyCode == KeyCode.C)
            {
                _behaviours.CopyBehaviours(representatives);
                e.Use();
            }
            else if (e.keyCode == KeyCode.V && VrcBehaviours.ClipboardCount > 0)
            {
                PasteBehavioursToAll(states);
                e.Use();
                GUIUtility.ExitGUI();
            }
        }

        void PasteBehavioursToAll(List<AnimatorState> states)
        {
            using (new UndoScope("Paste Behaviours"))
                foreach (var state in states)
                {
                    if (state == null) continue;
                    VrcBehaviours.Paste(state, replace: false);
                    _graphView.Sync.RefreshStateNode(state);   // B badge updates immediately
                }
            // The pasted rows regroup into new slots on the next repaint; the old selection
            // would point at whatever happened to sit at those indices.
            _selectedBehaviours.Clear();
            _behaviours.ResetAnchor();
        }
    }
}
