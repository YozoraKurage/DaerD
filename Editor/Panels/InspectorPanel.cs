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
        static readonly AnimatorConditionMode[] IntModes =
        {
            AnimatorConditionMode.Greater, AnimatorConditionMode.Less,
            AnimatorConditionMode.Equals, AnimatorConditionMode.NotEqual,
        };
        static readonly AnimatorConditionMode[] FloatModes = { AnimatorConditionMode.Greater, AnimatorConditionMode.Less };
        static readonly string[] BoolValueLabels = { "true", "false" };
        static readonly string[] GestureValueLabels = BuildGestureValueLabels();

        static string[] BuildGestureValueLabels()
        {
            var names = VrcParameters.GestureNames;
            var labels = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
                labels[i] = i + ": " + names[i];
            return labels;
        }

        readonly AnimatorGraphView _graphView;
        readonly List<AnimatorTransitionBase> _selectedTransitions = new List<AnimatorTransitionBase>();
        readonly TransitionClipboard.ConditionData _newCondition =
            new TransitionClipboard.ConditionData { mode = AnimatorConditionMode.If, parameter = string.Empty };

        int _rangeAnchor = -1;
        object _lastSelection;
        bool _showBlendTree = true;
        List<UnityEngine.Object> _leftovers;
        // VRC Parameter Driver: remembered "selected row" per behaviour so the Add/Up/Down/Delete
        // buttons know which entry they act on. Keyed by StateMachineBehaviour instance ID; stale
        // entries are harmless (Unity domain reload clears them) so we don't bother pruning.
        readonly Dictionary<int, int> _vrcDriverSelectedIndex = new Dictionary<int, int>();

        public InspectorPanel(DaerDContext context, AnimatorGraphView graphView)
            : base(context, "Inspector")
        {
            _graphView = graphView;
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
                _rangeAnchor = -1;
                if (Context.Selection is AnimatorTransitionBase transition)
                    _selectedTransitions.Add(transition);
                _lastSelection = Context.Selection;
                // Any in-flight condition edit was for the old selection — drop the focus so the
                // next IMGUI repaint redraws the controls fresh against the new transition(s).
                EndConditionInput();
            }
            Refresh();
        }

        void ClearControllerCaches()
        {
            _leftovers = null;
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
                DrawTransitionContext();
            }
            else if (selection is AnimatorStateMachine stateMachine)
            {
                DrawStateMachine(stateMachine);
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
                DrawOverview();
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
            if (GUILayout.Button("Delete Note"))
            {
                _graphView.Sync.DeleteNote(note);
                Context.Select(null);
                GUIUtility.ExitGUI();
            }
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
                else DrawOverview();
                return;
            }

            var controller = Context.Controller;
            EditorGUILayout.LabelField(alive.Count + " states selected", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Common Settings (applied to all selected)", EditorStyles.boldLabel);

            MultiObjectField<AnimatorState, Motion>("Motion", alive,
                x => x.motion, (x, v) => x.motion = v,
                undoName: "Edit States", postApply: s => _graphView.Sync.RefreshStateNode(s));
            MultiFloat("Speed", alive, x => x.speed, (x, v) => x.speed = v, undoName: "Edit States");
            MultiFloat("Cycle Offset", alive, x => x.cycleOffset, (x, v) => x.cycleOffset = v, undoName: "Edit States");
            MultiBool("Mirror", alive, x => x.mirror, (x, v) => x.mirror = v, undoName: "Edit States");
            MultiBool("Foot IK", alive, x => x.iKOnFeet, (x, v) => x.iKOnFeet = v, undoName: "Edit States");
            MultiBool("Write Defaults", alive, x => x.writeDefaultValues, (x, v) => x.writeDefaultValues = v,
                undoName: "Edit States", postApply: s => _graphView.Sync.RefreshStateNode(s));
            MultiString("Tag", alive, x => x.tag, (x, v) => x.tag = v, undoName: "Edit States");

            EditorGUILayout.Space(4);
            DrawMultiStateParameterOverrides(alive, controller);

            EditorGUILayout.Space(6);
            HorizontalLine();
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
            var floatParams = ParameterNamesOfType(controller, AnimatorControllerParameterType.Float);
            var boolParams = ParameterNamesOfType(controller, AnimatorControllerParameterType.Bool);

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

            DrawBehaviours(state);
        }

        /// <summary>
        /// Optional parameter drivers for Speed / Motion Time / Mirror / Cycle Offset.
        /// Speed, Motion Time and Cycle Offset take a Float parameter; Mirror takes a Bool one.
        /// </summary>
        void DrawStateParameters(AnimatorState state, AnimatorController controller)
        {
            var floatParams = ParameterNamesOfType(controller, AnimatorControllerParameterType.Float);
            var boolParams = ParameterNamesOfType(controller, AnimatorControllerParameterType.Bool);

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

        void DrawBehaviours(AnimatorState state)
        {
            EditorGUILayout.Space(4);
            var behaviours = state.behaviours;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Behaviours (" + behaviours.Length + ")", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(behaviours.Length == 0))
                if (GUILayout.Button(new GUIContent(L.Tr("Copy"),
                        L.Tr("Copy every behaviour on this state; paste from a state's right-click menu or here.")),
                        EditorStyles.miniButton, GUILayout.Width(50)))
                    VrcBehaviours.Copy(behaviours);
            using (new EditorGUI.DisabledScope(VrcBehaviours.ClipboardCount == 0))
                if (GUILayout.Button(new GUIContent(L.Tr("Paste"),
                        L.Tr("Append the copied behaviours to this state.")),
                        EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    VrcBehaviours.Paste(state, replace: false);
                    _graphView.Sync.RefreshStateNode(state);
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(BehaviourTitle(behaviour), EditorStyles.boldLabel);

                // Repeatable VRC types get a per-instance name so multiple rows stay
                // distinguishable (drivers named "Network" by the sync generator, etc.).
                string typeName = behaviour.GetType().Name;
                if (VrcBehaviours.IsVrcType(typeName) && !VrcBehaviours.IsSingleton(typeName))
                {
                    string instanceName = EditorGUILayout.DelayedTextField(behaviour.name, GUILayout.Width(90));
                    if (instanceName != behaviour.name)
                    {
                        Undo.RegisterCompleteObjectUndo(behaviour, "Rename Behaviour");
                        behaviour.name = instanceName;
                        EditorUtility.SetDirty(behaviour);
                    }
                }

                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("↑", EditorStyles.miniButton, GUILayout.Width(22)))
                    { VrcBehaviours.Move(state, i, -1); GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(i == behaviours.Length - 1))
                    if (GUILayout.Button("↓", EditorStyles.miniButton, GUILayout.Width(22)))
                    { VrcBehaviours.Move(state, i, +1); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    RemoveBehaviour(state, behaviour);
                    _graphView.Sync.RefreshStateNode(state);   // B badge updates immediately
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
                if (!TryDrawKnownVrcBehaviour(behaviour))
                    DrawSerializedFields(behaviour);
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Behaviour"))
                ShowAddBehaviourMenu(state);
        }

        static string BehaviourTitle(StateMachineBehaviour behaviour)
        {
            string typeName = behaviour.GetType().Name;
            // The VRC prefix is noise inside an already VRC-labeled box; keep titles short.
            return typeName.StartsWith("VRC") ? typeName.Substring(3) : typeName;
        }

        /// <summary>
        /// Render VRC SDK behaviours (Tracking Control, Parameter Driver) with a UI matching
        /// their native inspector. Detected by type name, so we don't need to reference VRCSDK.
        /// Returns true if the behaviour was drawn — caller should skip the generic renderer.
        /// </summary>
        bool TryDrawKnownVrcBehaviour(StateMachineBehaviour behaviour)
        {
            switch (behaviour.GetType().Name)
            {
                case "VRCAnimatorTrackingControl": DrawVrcTrackingControl(behaviour); return true;
                case "VRCAvatarParameterDriver": DrawVrcParameterDriver(behaviour); return true;
                case "VRCAnimatorPlayAudio": DrawVrcPlayAudio(behaviour); return true;
                case "VRCAnimatorLocomotionControl": DrawVrcLocomotionControl(behaviour); return true;
                case "VRCAnimatorLayerControl": DrawVrcLayerControl(behaviour); return true;
                case "VRCPlayableLayerControl": DrawVrcPlayableLayerControl(behaviour); return true;
                case "VRCAnimatorTemporaryPoseSpace": DrawVrcPoseSpace(behaviour); return true;
                default: return false;
            }
        }

        /// <summary>Two-button exclusive toggle; returns the (possibly new) value.</summary>
        static bool DrawTwoWayToggle(bool value, string whenTrue, string whenFalse)
        {
            EditorGUILayout.BeginHorizontal();
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = value ? new Color(0.55f, 0.85f, 0.55f) : prev;
            if (GUILayout.Button(whenTrue, EditorStyles.miniButtonLeft) && !value) value = true;
            GUI.backgroundColor = !value ? new Color(0.55f, 0.85f, 0.55f) : prev;
            if (GUILayout.Button(whenFalse, EditorStyles.miniButtonRight) && value) value = false;
            GUI.backgroundColor = prev;
            EditorGUILayout.EndHorizontal();
            return value;
        }

        /// <summary>PropertyField for a named property when it exists (SDK layouts vary).</summary>
        static void PropertyRow(SerializedObject so, string property, string label)
        {
            var prop = so.FindProperty(property);
            if (prop != null)
                EditorGUILayout.PropertyField(prop, new GUIContent(label), true);
        }

        void DrawVrcLocomotionControl(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            var disable = so.FindProperty("disableLocomotion");
            if (disable != null)
                disable.boolValue = !DrawTwoWayToggle(!disable.boolValue, L.Tr("Enable"), L.Tr("Disable"));
            PropertyRow(so, "debugString", "Debug String");
            so.ApplyModifiedProperties();
        }

        void DrawVrcPoseSpace(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            var enter = so.FindProperty("enterPoseSpace");
            if (enter != null)
                enter.boolValue = DrawTwoWayToggle(enter.boolValue, L.Tr("Enter"), L.Tr("Exit"));
            var fixedDelay = so.FindProperty("fixedDelay");
            if (fixedDelay != null)
                EditorGUILayout.PropertyField(fixedDelay,
                    new GUIContent(L.Tr("Fixed Delay"),
                        L.Tr("On: the delay is in seconds. Off: normalized time of the state.")));
            PropertyRow(so, "delayTime", "Delay Time");
            PropertyRow(so, "debugString", "Debug String");
            so.ApplyModifiedProperties();
        }

        void DrawVrcLayerControl(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            PropertyRow(so, "playable", "Playable");
            PropertyRow(so, "layer", "Layer");
            var goal = so.FindProperty("goalWeight");
            if (goal != null)
                goal.floatValue = EditorGUILayout.Slider(L.Tr("Goal Weight"), goal.floatValue, 0f, 1f);
            PropertyRow(so, "blendDuration", "Blend Duration");
            PropertyRow(so, "debugString", "Debug String");
            so.ApplyModifiedProperties();
        }

        void DrawVrcPlayableLayerControl(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            PropertyRow(so, "layer", "Layer");
            var goal = so.FindProperty("goalWeight");
            if (goal != null)
                goal.floatValue = EditorGUILayout.Slider(L.Tr("Goal Weight"), goal.floatValue, 0f, 1f);
            PropertyRow(so, "blendDuration", "Blend Duration");
            PropertyRow(so, "debugString", "Debug String");
            so.ApplyModifiedProperties();
        }

        /// <summary>Play Audio has a large, SDK-version-dependent field set: a drag slot
        /// resolves the source path, everything else renders generically.</summary>
        void DrawVrcPlayAudio(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            var sourcePath = so.FindProperty("SourcePath");
            if (sourcePath != null)
            {
                EditorGUILayout.PropertyField(sourcePath, new GUIContent("Source Path"));
                // Action slot: dropping an AudioSource fills the path (relative to its root).
                var dropped = (AudioSource)EditorGUILayout.ObjectField(
                    new GUIContent(L.Tr("Resolve From AudioSource"),
                        L.Tr("Drop the avatar's AudioSource to fill the source path.")),
                    null, typeof(AudioSource), true);
                if (dropped != null)
                    sourcePath.stringValue = AnimationUtility.CalculateTransformPath(
                        dropped.transform, dropped.transform.root);
            }
            var iterator = so.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script" || iterator.propertyPath == "SourcePath")
                    continue;
                EditorGUILayout.PropertyField(iterator, true);
            }
            so.ApplyModifiedProperties();
        }

        // Body part rows in the order the VRCSDK inspector shows them: display label + the
        // matching serialized property name on VRCAnimatorTrackingControl.
        static readonly (string label, string property)[] VrcTrackingTargets =
        {
            ("Head", "trackingHead"),
            ("Left Hand", "trackingLeftHand"),
            ("Right Hand", "trackingRightHand"),
            ("Hip", "trackingHip"),
            ("Left Foot", "trackingLeftFoot"),
            ("Right Foot", "trackingRightFoot"),
            ("Left Fingers", "trackingLeftFingers"),
            ("Right Fingers", "trackingRightFingers"),
            ("Eyes & Eyelids", "trackingEyes"),
            ("Mouth & Jaw", "trackingMouth"),
        };
        static readonly string[] VrcTrackingColumns = { "No Change", "Tracking", "Animation" };

        void DrawVrcTrackingControl(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();

            EditorGUILayout.LabelField("Tracking Control", EditorStyles.miniBoldLabel);

            // Column headers use the same subdivision as the rows so they stay aligned when
            // the inspector is resized.
            var headerRect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight,
                GUILayout.ExpandWidth(true));
            var headerCols = SubdivideTrackingRow(headerRect);
            for (int i = 0; i < VrcTrackingColumns.Length; i++)
                GUI.Label(headerCols[i + 1], VrcTrackingColumns[i], TrackingColumnHeaderStyle);

            // "All" row acts as a bulk selector: shows the common value across every body part
            // (or nothing when they diverge) and, when clicked, forces that column onto them all.
            int commonValue = -1;
            bool commonKnown = true;
            foreach (var (_, prop) in VrcTrackingTargets)
            {
                var p = so.FindProperty(prop);
                if (p == null) continue;
                if (commonValue == -1) commonValue = p.intValue;
                else if (commonValue != p.intValue) { commonKnown = false; break; }
            }
            int allPicked = DrawVrcTrackingRow("All", commonKnown ? commonValue : -1);
            if (allPicked >= 0 && (!commonKnown || allPicked != commonValue))
            {
                foreach (var (_, prop) in VrcTrackingTargets)
                {
                    var p = so.FindProperty(prop);
                    if (p != null) p.intValue = allPicked;
                }
            }

            foreach (var (label, propPath) in VrcTrackingTargets)
            {
                var prop = so.FindProperty(propPath);
                if (prop == null) continue;
                int picked = DrawVrcTrackingRow(label, prop.intValue);
                if (picked >= 0 && picked != prop.intValue)
                    prop.intValue = picked;
            }

            var debug = so.FindProperty("debugString");
            if (debug != null)
            {
                EditorGUILayout.PropertyField(debug, new GUIContent("Debug String"));
            }

            so.ApplyModifiedProperties();
        }

        static GUIStyle _trackingColumnHeaderStyle;
        static GUIStyle TrackingColumnHeaderStyle
        {
            get
            {
                if (_trackingColumnHeaderStyle == null)
                    _trackingColumnHeaderStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                    };
                return _trackingColumnHeaderStyle;
            }
        }

        /// <summary>
        /// Splits a full-width row rect into [label, col0, col1, col2] with widths that scale
        /// with the available space. Cells shrink before the label does, and both keep a floor
        /// so the checkboxes stay clickable in a narrow inspector.
        /// </summary>
        static Rect[] SubdivideTrackingRow(Rect row)
        {
            const float preferredLabelFraction = 0.34f;
            const float minLabel = 44f;
            const float minCell = 28f;

            float labelWidth = Mathf.Max(minLabel, row.width * preferredLabelFraction);
            float cellWidth = (row.width - labelWidth) / 3f;
            if (cellWidth < minCell)
            {
                cellWidth = minCell;
                labelWidth = Mathf.Max(0f, row.width - cellWidth * 3f);
            }
            return new[]
            {
                new Rect(row.x, row.y, labelWidth, row.height),
                new Rect(row.x + labelWidth, row.y, cellWidth, row.height),
                new Rect(row.x + labelWidth + cellWidth, row.y, cellWidth, row.height),
                new Rect(row.x + labelWidth + cellWidth * 2f, row.y, cellWidth, row.height),
            };
        }

        /// <summary>
        /// Row of three exclusive tri-state checkboxes. Returns the column the user just clicked
        /// on (0/1/2), or -1 if nothing changed this frame.
        /// </summary>
        static int DrawVrcTrackingRow(string label, int currentValue)
        {
            var rowRect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight,
                GUILayout.ExpandWidth(true));
            var cols = SubdivideTrackingRow(rowRect);
            GUI.Label(cols[0], label);
            int picked = -1;
            for (int i = 0; i < 3; i++)
            {
                bool wasOn = currentValue == i;
                // Centre the checkbox inside its column so the grid reads as neatly aligned as
                // the reference — a bare GUI.Toggle would hug the left edge of its cell.
                var cell = cols[i + 1];
                var box = new Rect(cell.x + (cell.width - 16f) * 0.5f, cell.y, 16f, cell.height);
                bool nowOn = GUI.Toggle(box, wasOn, GUIContent.none);
                if (nowOn && !wasOn) picked = i;
            }
            return picked;
        }

        // Text lifted from the VRCSDK inspector so users get the same guidance.
        const string VrcParameterDriverInfo =
            "This behaviour modifies parameters on this and all other animation controllers referenced on the avatar descriptor.\n" +
            "\n" +
            "Keep in mind only parameters defined in your VRCExpressionParameter object will be synced across the network.\n" +
            "\n" +
            "Additionally, synced parameters are clamped between Int [0,255] and Float [-1,1]. Operations that modify these parameters will be clipped inside those bounds.";

        void DrawVrcParameterDriver(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();

            EditorGUILayout.HelpBox(VrcParameterDriverInfo, MessageType.Info);

            var localOnly = so.FindProperty("localOnly");
            if (localOnly != null)
                EditorGUILayout.PropertyField(localOnly, new GUIContent("Local Only"));

            var debugString = so.FindProperty("debugString");
            if (debugString != null)
                EditorGUILayout.PropertyField(debugString, new GUIContent("Debug String"));

            var parameters = so.FindProperty("parameters");
            if (parameters == null || !parameters.isArray)
            {
                so.ApplyModifiedProperties();
                return;
            }

            int instanceId = behaviour.GetInstanceID();
            int selected = _vrcDriverSelectedIndex.TryGetValue(instanceId, out var stored) ? stored : 0;
            if (parameters.arraySize == 0) selected = -1;
            else selected = Mathf.Clamp(selected, 0, parameters.arraySize - 1);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add"))
            {
                parameters.arraySize++;
                selected = parameters.arraySize - 1;
            }
            using (new EditorGUI.DisabledScope(selected <= 0))
            {
                if (GUILayout.Button("Up"))
                {
                    parameters.MoveArrayElement(selected, selected - 1);
                    selected--;
                }
            }
            using (new EditorGUI.DisabledScope(selected < 0 || selected >= parameters.arraySize - 1))
            {
                if (GUILayout.Button("Down"))
                {
                    parameters.MoveArrayElement(selected, selected + 1);
                    selected++;
                }
            }
            using (new EditorGUI.DisabledScope(selected < 0))
            {
                if (GUILayout.Button("Delete"))
                {
                    parameters.DeleteArrayElementAtIndex(selected);
                    if (parameters.arraySize == 0) selected = -1;
                    else selected = Mathf.Clamp(selected, 0, parameters.arraySize - 1);
                }
            }
            EditorGUILayout.EndHorizontal();

            var controllerParameters = Context.Controller?.parameters;
            for (int i = 0; i < parameters.arraySize; i++)
            {
                DrawVrcDriverEntry(parameters.GetArrayElementAtIndex(i), i, ref selected, controllerParameters);
            }

            _vrcDriverSelectedIndex[instanceId] = selected;
            so.ApplyModifiedProperties();
        }

        /// <summary>Renders one parameter entry, laying out the fields that apply to its Type.</summary>
        void DrawVrcDriverEntry(SerializedProperty entry, int index, ref int selected,
            AnimatorControllerParameter[] controllerParameters)
        {
            bool isSelected = index == selected;
            // Highlight the active entry with a coloured background so it stands out like the
            // native inspector's blue rows. GUI.backgroundColor tints EditorStyles.helpBox.
            var savedBg = GUI.backgroundColor;
            if (isSelected) GUI.backgroundColor = new Color(0.35f, 0.55f, 0.85f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = savedBg;

            // Clicking anywhere on this entry's box makes it the selected one.
            var headerRect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(headerRect, "Parameter " + index, EditorStyles.miniBoldLabel);
            if (Event.current.type == EventType.MouseDown && headerRect.Contains(Event.current.mousePosition))
            {
                selected = index;
                Event.current.Use();
            }

            var typeProp = entry.FindPropertyRelative("type");
            var nameProp = entry.FindPropertyRelative("name");
            var valueProp = entry.FindPropertyRelative("value");
            var valueMinProp = entry.FindPropertyRelative("valueMin");
            var valueMaxProp = entry.FindPropertyRelative("valueMax");
            var chanceProp = entry.FindPropertyRelative("chance");
            var sourceProp = entry.FindPropertyRelative("source");
            var convertRangeProp = entry.FindPropertyRelative("convertRange");
            var sourceMinProp = entry.FindPropertyRelative("sourceMin");
            var sourceMaxProp = entry.FindPropertyRelative("sourceMax");
            var destMinProp = entry.FindPropertyRelative("destMin");
            var destMaxProp = entry.FindPropertyRelative("destMax");

            if (typeProp != null)
                EditorGUILayout.PropertyField(typeProp, new GUIContent("Type"));

            int type = typeProp != null ? typeProp.intValue : 0;

            // For Set/Add/Random the destination is `name`; for Copy VRCSDK reuses `name` as the
            // Copy destination while `source` holds the parameter being read.
            if (type == 3 && sourceProp != null)
                EditorGUILayout.PropertyField(sourceProp, new GUIContent("Source"));
            if (nameProp != null)
            {
                EditorGUILayout.PropertyField(nameProp, new GUIContent("Destination"));
                if (controllerParameters != null
                    && !string.IsNullOrEmpty(nameProp.stringValue)
                    && !ControllerHasParameter(controllerParameters, nameProp.stringValue))
                {
                    EditorGUILayout.HelpBox(
                        "Parameter '" + nameProp.stringValue + "' not found. Make sure you defined it in the Animator window's Parameter list.",
                        MessageType.Warning);
                }
            }

            switch (type)
            {
                case 0: // Set
                case 1: // Add
                    if (valueProp != null)
                        EditorGUILayout.PropertyField(valueProp, new GUIContent("Value"));
                    break;
                case 2: // Random
                    if (valueMinProp != null)
                        EditorGUILayout.PropertyField(valueMinProp, new GUIContent("Value Min"));
                    if (valueMaxProp != null)
                        EditorGUILayout.PropertyField(valueMaxProp, new GUIContent("Value Max"));
                    if (chanceProp != null)
                        EditorGUILayout.PropertyField(chanceProp, new GUIContent("Chance"));
                    break;
                case 3: // Copy
                    if (convertRangeProp != null)
                    {
                        EditorGUILayout.PropertyField(convertRangeProp, new GUIContent("Convert Range"));
                        if (convertRangeProp.boolValue)
                        {
                            DrawMinMaxRow("Source", sourceMinProp, sourceMaxProp);
                            DrawMinMaxRow("Destination", destMinProp, destMaxProp);
                        }
                    }
                    break;
            }

            EditorGUILayout.EndVertical();
        }

        static bool ControllerHasParameter(AnimatorControllerParameter[] parameters, string name)
        {
            foreach (var p in parameters)
                if (p != null && p.name == name) return true;
            return false;
        }

        /// <summary>
        /// Draws Min and Max side by side on one row (single vertical slot instead of two) —
        /// used for the Copy driver's Source / Destination ranges so the block stays compact.
        /// </summary>
        static void DrawMinMaxRow(string label, SerializedProperty minProp, SerializedProperty maxProp)
        {
            if (minProp == null && maxProp == null) return;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
            float saved = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 30f;
            if (minProp != null) EditorGUILayout.PropertyField(minProp, new GUIContent("Min"));
            if (maxProp != null) EditorGUILayout.PropertyField(maxProp, new GUIContent("Max"));
            EditorGUIUtility.labelWidth = saved;
            EditorGUILayout.EndHorizontal();
        }

        void ShowAddBehaviourMenu(AnimatorState state)
        {
            var menu = new GenericMenu();

            // VRC types first (the common case on this kind of controller). Singletons gray
            // out once present; repeatable types always add another instance.
            bool anyVrc = false;
            foreach (var typeName in VrcBehaviours.All)
            {
                if (VrcBehaviours.Find(typeName) == null) continue;
                anyVrc = true;
                var captured = typeName;
                var label = new GUIContent(typeName);
                if (VrcBehaviours.IsSingleton(typeName) && VrcBehaviours.Has(state, typeName))
                    menu.AddDisabledItem(label);
                else
                    menu.AddItem(label, false, () =>
                    {
                        VrcBehaviours.Add(state, captured);
                        _graphView.Sync.RefreshStateNode(state);   // B badge updates immediately
                        Refresh();
                    });
            }
            if (anyVrc)
                menu.AddSeparator(string.Empty);

            foreach (var type in TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>())
            {
                if (type.IsAbstract) continue;
                if (anyVrc && VrcBehaviours.IsVrcType(type.Name)) continue;   // already listed above
                var captured = type;
                var label = anyVrc ? "Other/" + type.Name : type.Name;
                menu.AddItem(new GUIContent(label), false, () =>
                {
                    Undo.RegisterCompleteObjectUndo(state, "Add Behaviour");
                    state.AddStateMachineBehaviour(captured);
                    EditorUtility.SetDirty(state);
                    _graphView.Sync.RefreshStateNode(state);   // B badge updates immediately
                    Refresh();
                });
            }
            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No StateMachineBehaviour types found"));
            menu.ShowAsContext();
        }

        static void RemoveBehaviour(AnimatorState state, StateMachineBehaviour behaviour)
        {
            var serialized = new SerializedObject(state);
            var array = serialized.FindProperty("m_StateMachineBehaviours");
            if (array != null && array.isArray)
            {
                for (int i = 0; i < array.arraySize; i++)
                {
                    if (array.GetArrayElementAtIndex(i).objectReferenceValue != behaviour) continue;
                    array.DeleteArrayElementAtIndex(i);
                    if (i < array.arraySize && array.GetArrayElementAtIndex(i).objectReferenceValue == behaviour)
                        array.DeleteArrayElementAtIndex(i);
                    break;
                }
                serialized.ApplyModifiedProperties();
            }
            Undo.DestroyObjectImmediate(behaviour);
        }

        static void DrawSerializedFields(UnityEngine.Object target)
        {
            var serialized = new SerializedObject(target);
            serialized.Update();
            var iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "m_Script") continue;
                EditorGUILayout.PropertyField(iterator, true);
            }
            serialized.ApplyModifiedProperties();
        }

        // ---- transition ------------------------------------------------------

        void DrawTransitionContext()
        {
            var controller = Context.Controller;
            var pool = GatherTransitionPool();
            if (pool.Count == 0)
            {
                if (Context.Selection is TransitionEdge edge && edge.IsDefaultEdge)
                    EditorGUILayout.HelpBox("Default-state link. Set a different default state from a state's context menu.",
                        MessageType.Info);
                else
                    EditorGUILayout.LabelField("No transitions to edit.");
                return;
            }

            PruneSelection(pool);
            HandleCopyPasteShortcuts();
            DrawTransitionList(pool);
            HorizontalLine();

            if (_selectedTransitions.Count >= 2)
                DrawMultiTransitionEditor(controller);
            else
                DrawSingleTransition(_selectedTransitions[0], controller);
        }

        /// <summary>All transitions of every currently selected (non-default) edge.</summary>
        List<AnimatorTransitionBase> GatherTransitionPool()
        {
            var pool = new List<AnimatorTransitionBase>();
            var edges = _graphView.GetSelectedEdges();
            if (edges.Count == 0)
            {
                var fallback = Context.Selection as TransitionEdge
                    ?? (Context.Selection is AnimatorTransitionBase tb ? _graphView.Sync.FindEdge(tb) : null);
                if (fallback != null) edges.Add(fallback);
            }
            foreach (var edge in edges)
            {
                if (edge.IsDefaultEdge) continue;
                foreach (var t in edge.Transitions)
                    if (t != null && !pool.Contains(t)) pool.Add(t);
            }
            return pool;
        }

        void PruneSelection(List<AnimatorTransitionBase> pool)
        {
            _selectedTransitions.RemoveAll(t => t == null || !pool.Contains(t));
            if (_selectedTransitions.Count == 0)
                _selectedTransitions.Add(pool[0]);
        }

        /// <summary>Unity-style vertical transition list with Solo / Mute columns and multi-select.</summary>
        void DrawTransitionList(List<AnimatorTransitionBase> pool)
        {
            EditorGUILayout.LabelField("Transitions (" + pool.Count + ")", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Solo", EditorStyles.miniLabel, GUILayout.Width(34));
            EditorGUILayout.LabelField("Mute", EditorStyles.miniLabel, GUILayout.Width(36));
            EditorGUILayout.LabelField("Transition", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < pool.Count; i++)
            {
                var t = pool[i];
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginChangeCheck();
                bool solo = EditorGUILayout.Toggle(t.solo, GUILayout.Width(34));
                bool mute = EditorGUILayout.Toggle(t.mute, GUILayout.Width(36));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RegisterCompleteObjectUndo(t, "Edit Transition");
                    t.solo = solo;
                    t.mute = mute;
                    EditorUtility.SetDirty(t);
                    RefreshEdges();
                }

                bool selected = _selectedTransitions.Contains(t);
                var prevBackground = GUI.backgroundColor;
                if (selected) GUI.backgroundColor = new Color(0.40f, 0.60f, 0.90f);
                if (GUILayout.Button((i + 1) + ".  " + ParameterConverter.DescribeTransition(t), EditorStyles.miniButton))
                    HandleRowClick(pool, i);
                GUI.backgroundColor = prevBackground;

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(24)))
                {
                    DeleteTransitionRow(t, pool);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Transition"))
            {
                AddTransitionToAnchorEdge(pool);
                GUIUtility.ExitGUI();
            }
            if (pool.Count > 1 && GUILayout.Button("Select All", GUILayout.Width(80)))
            {
                _selectedTransitions.Clear();
                _selectedTransitions.AddRange(pool);
            }
            EditorGUILayout.EndHorizontal();
        }

        void HandleRowClick(List<AnimatorTransitionBase> pool, int index)
        {
            var t = pool[index];
            var e = Event.current;
            bool additive = e != null && (e.control || e.command);
            bool range = e != null && e.shift;

            var previous = new List<AnimatorTransitionBase>(_selectedTransitions);

            if (range && _rangeAnchor >= 0 && _rangeAnchor < pool.Count)
            {
                _selectedTransitions.Clear();
                int lo = Mathf.Min(_rangeAnchor, index);
                int hi = Mathf.Max(_rangeAnchor, index);
                for (int i = lo; i <= hi; i++)
                    _selectedTransitions.Add(pool[i]);
            }
            else if (additive)
            {
                if (_selectedTransitions.Contains(t)) _selectedTransitions.Remove(t);
                else _selectedTransitions.Add(t);
                _rangeAnchor = index;
            }
            else
            {
                _selectedTransitions.Clear();
                _selectedTransitions.Add(t);
                _rangeAnchor = index;
            }

            // If the selection actually changed, force the in-flight condition input (FloatField,
            // delayed text field, popup) to end before its value gets attributed to the newly
            // selected transition. Without this, a value typed for transition X leaks into Y.
            if (!SameSet(previous, _selectedTransitions))
                EndConditionInput();
        }

        static bool SameSet(List<AnimatorTransitionBase> a, List<AnimatorTransitionBase> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>
        /// Drops keyboard focus and resets the editor's internal hot/keyboard control so any
        /// FloatField / DelayedFloatField currently being typed in stops being the active
        /// control — the next layout pass redraws it fresh for the new selected transition.
        /// </summary>
        static void EndConditionInput()
        {
            GUI.FocusControl(null);
            GUIUtility.keyboardControl = 0;
            EditorGUIUtility.editingTextField = false;
        }

        void DeleteTransitionRow(AnimatorTransitionBase transition, List<AnimatorTransitionBase> pool)
        {
            var edge = _graphView.Sync.FindEdge(transition);
            if (edge == null) return;

            AnimatorTransitionBase remaining = null;
            foreach (var t in pool)
                if (!ReferenceEquals(t, transition)) { remaining = t; break; }

            _selectedTransitions.Remove(transition);
            _graphView.Sync.DeleteTransition(edge, transition);
            Context.Select(remaining);
        }

        void AddTransitionToAnchorEdge(List<AnimatorTransitionBase> pool)
        {
            var anchor = _selectedTransitions.Count > 0 ? _selectedTransitions[0] : pool[0];
            var edge = _graphView.Sync.FindEdge(anchor);
            if (edge == null) return;
            var created = _graphView.Sync.CreateTransition(
                edge.output?.node as GraphNodeBase, edge.input?.node as GraphNodeBase);
            _graphView.Sync.Rebuild();
            if (created != null) Context.Select(created);
        }

        /// <summary>
        /// Ctrl+C copies the selected transition(s); Ctrl+V pastes the copy onto every selected one;
        /// Ctrl+Shift+V pastes it as a new transition alongside each selected one.
        /// </summary>
        void HandleCopyPasteShortcuts()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || !(e.control || e.command))
                return;

            if (e.keyCode == KeyCode.C && _selectedTransitions.Count >= 1)
            {
                TransitionClipboard.Copy(_selectedTransitions);
                e.Use();
            }
            else if (e.keyCode == KeyCode.V && TransitionClipboard.HasData && _selectedTransitions.Count >= 1)
            {
                if (e.shift) PasteSelectedAsNew();
                else PasteOntoSelected();
                e.Use();
                GUIUtility.ExitGUI();
            }
        }

        void PasteOntoSelected()
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            using (new UndoScope("Paste Transition"))
            {
                foreach (var transition in _selectedTransitions)
                    TransitionClipboard.Apply(transition, snapshot);
            }
            RefreshEdges();
        }

        // ---- single transition ----------------------------------------------

        void DrawSingleTransition(AnimatorTransitionBase transition, AnimatorController controller)
        {
            EditorGUILayout.LabelField("Transition  " + ParameterConverter.DescribeTransition(transition),
                EditorStyles.boldLabel);
            DrawTransitionSettings(transition);

            HorizontalLine();
            DrawConditions(transition, controller);
        }

        void DrawTransitionSettings(AnimatorTransitionBase transition)
        {
            var stateTransition = transition as AnimatorStateTransition;
            if (stateTransition == null)
            {
                EditorGUILayout.LabelField("(Entry / state-machine transition — no timing settings.)",
                    EditorStyles.miniLabel);
                return;
            }

            EditorGUI.BeginChangeCheck();
            bool hasExitTime = EditorGUILayout.Toggle("Has Exit Time", stateTransition.hasExitTime);
            float exitTime;
            using (new EditorGUI.DisabledScope(!hasExitTime))
                exitTime = EditorGUILayout.FloatField("Exit Time", stateTransition.exitTime);
            bool fixedDuration = EditorGUILayout.Toggle("Fixed Duration", stateTransition.hasFixedDuration);
            float duration = EditorGUILayout.FloatField("Duration", stateTransition.duration);
            float offset = EditorGUILayout.FloatField("Offset", stateTransition.offset);
            var interruption = (TransitionInterruptionSource)EditorGUILayout.EnumPopup("Interruption", stateTransition.interruptionSource);
            bool ordered = EditorGUILayout.Toggle("Ordered Interruption", stateTransition.orderedInterruption);
            bool toSelf = EditorGUILayout.Toggle("Can Transition To Self", stateTransition.canTransitionToSelf);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(stateTransition, "Edit Transition");
                stateTransition.hasExitTime = hasExitTime;
                stateTransition.exitTime = exitTime;
                stateTransition.hasFixedDuration = fixedDuration;
                stateTransition.duration = duration;
                stateTransition.offset = offset;
                stateTransition.interruptionSource = interruption;
                stateTransition.orderedInterruption = ordered;
                stateTransition.canTransitionToSelf = toSelf;
                EditorUtility.SetDirty(stateTransition);
            }
        }

        void DrawConditions(AnimatorTransitionBase transition, AnimatorController controller)
        {
            EditorGUILayout.LabelField("Conditions", EditorStyles.boldLabel);

            var paramNames = AllParameterNames(controller);
            if (paramNames.Length == 0)
            {
                EditorGUILayout.HelpBox("Add parameters before building conditions.", MessageType.Info);
                return;
            }
            var typeByName = ParameterTypeMap(controller);

            var working = ToDataList(transition);
            bool changed = false;
            int removeIndex = -1;
            EditorGUI.BeginChangeCheck();

            for (int i = 0; i < working.Count; i++)
            {
                var condition = working[i];
                EditorGUILayout.BeginHorizontal();

                int paramIndex = Mathf.Max(0, Array.IndexOf(paramNames, condition.parameter));
                paramIndex = EditorGUILayout.Popup(paramIndex, paramNames);
                condition.parameter = paramNames[paramIndex];

                var type = typeByName.TryGetValue(condition.parameter, out var t) ? t : AnimatorControllerParameterType.Float;
                DrawConditionValue(condition, type);

                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22)))
                    removeIndex = i;

                EditorGUILayout.EndHorizontal();
            }

            if (EditorGUI.EndChangeCheck())
                changed = true;
            if (removeIndex >= 0)
            {
                working.RemoveAt(removeIndex);
                changed = true;
            }
            if (GUILayout.Button("+ Add Condition"))
            {
                var type = typeByName.TryGetValue(paramNames[0], out var t) ? t : AnimatorControllerParameterType.Float;
                working.Add(new TransitionClipboard.ConditionData { parameter = paramNames[0], mode = ModesFor(type)[0] });
                changed = true;
            }

            if (changed)
            {
                Undo.RegisterCompleteObjectUndo(transition, "Edit Conditions");
                TransitionClipboard.SetConditions(transition, working);
                EditorUtility.SetDirty(transition);
            }
        }

        // ---- multi-transition editing ----------------------------------------

        void DrawMultiTransitionEditor(AnimatorController controller)
        {
            EditorGUILayout.LabelField(_selectedTransitions.Count + " transitions selected", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(!TransitionClipboard.HasData))
            {
                if (GUILayout.Button("Paste Copied Transition Onto All " + _selectedTransitions.Count + " Selected"))
                    PasteOntoSelected();
            }

            DrawMultiSettings();
            HorizontalLine();
            DrawSharedConditions(controller);
            EditorGUILayout.Space(4);
            DrawAddConditionToAll(controller);
        }

        void DrawMultiSettings()
        {
            EditorGUILayout.LabelField("Common Settings (applied to all selected)", EditorStyles.boldLabel);

            MultiBool("Mute", _selectedTransitions, x => x.mute, (x, v) => x.mute = v, refreshEdges: true);
            MultiBool("Solo", _selectedTransitions, x => x.solo, (x, v) => x.solo = v, refreshEdges: true);

            var stateTransitions = new List<AnimatorStateTransition>();
            foreach (var t in _selectedTransitions)
                if (t is AnimatorStateTransition st) stateTransitions.Add(st);
            if (stateTransitions.Count == 0) return;

            MultiBool("Has Exit Time", stateTransitions, x => x.hasExitTime, (x, v) => x.hasExitTime = v);
            MultiFloat("Exit Time", stateTransitions, x => x.exitTime, (x, v) => x.exitTime = v);
            MultiBool("Fixed Duration", stateTransitions, x => x.hasFixedDuration, (x, v) => x.hasFixedDuration = v);
            MultiFloat("Duration", stateTransitions, x => x.duration, (x, v) => x.duration = v);
            MultiFloat("Offset", stateTransitions, x => x.offset, (x, v) => x.offset = v);
            MultiInterruption(stateTransitions);
            MultiBool("Ordered Interruption", stateTransitions, x => x.orderedInterruption, (x, v) => x.orderedInterruption = v);
            MultiBool("Can Transition To Self", stateTransitions, x => x.canTransitionToSelf, (x, v) => x.canTransitionToSelf = v);
        }

        void MultiBool<T>(string label, List<T> items, Func<T, bool> getter, Action<T, bool> setter,
            bool refreshEdges = false, string undoName = "Edit Transitions",
            Action<T> postApply = null) where T : UnityEngine.Object
        {
            if (items.Count == 0) return;
            bool first = getter(items[0]);
            bool mixed = false;
            foreach (var item in items)
                if (getter(item) != first) { mixed = true; break; }

            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            bool value = EditorGUILayout.Toggle(label, first);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                using (new UndoScope(undoName))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, undoName);
                        setter(item, value);
                        EditorUtility.SetDirty(item);
                        postApply?.Invoke(item);
                    }
                if (refreshEdges) RefreshEdges();
            }
        }

        void MultiFloat<T>(string label, List<T> items, Func<T, float> getter, Action<T, float> setter,
            string undoName = "Edit Transitions") where T : UnityEngine.Object
        {
            if (items.Count == 0) return;
            float first = getter(items[0]);
            bool mixed = false;
            foreach (var item in items)
                if (!Mathf.Approximately(getter(item), first)) { mixed = true; break; }

            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            float value = EditorGUILayout.FloatField(label, first);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                using (new UndoScope(undoName))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, undoName);
                        setter(item, value);
                        EditorUtility.SetDirty(item);
                    }
            }
        }

        void MultiString<T>(string label, List<T> items, Func<T, string> getter, Action<T, string> setter,
            string undoName = "Edit States") where T : UnityEngine.Object
        {
            if (items.Count == 0) return;
            string first = getter(items[0]) ?? string.Empty;
            bool mixed = false;
            foreach (var item in items)
                if ((getter(item) ?? string.Empty) != first) { mixed = true; break; }

            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            string value = EditorGUILayout.DelayedTextField(label, first);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                using (new UndoScope(undoName))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, undoName);
                        setter(item, value);
                        EditorUtility.SetDirty(item);
                    }
            }
        }

        void MultiObjectField<TOwner, TObject>(string label, List<TOwner> items,
            Func<TOwner, TObject> getter, Action<TOwner, TObject> setter,
            string undoName = "Edit States", Action<TOwner> postApply = null)
            where TOwner : UnityEngine.Object
            where TObject : UnityEngine.Object
        {
            if (items.Count == 0) return;
            TObject first = getter(items[0]);
            bool mixed = false;
            foreach (var item in items)
                if (!ReferenceEquals(getter(item), first)) { mixed = true; break; }

            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            var value = (TObject)EditorGUILayout.ObjectField(label, first, typeof(TObject), false);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                using (new UndoScope(undoName))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, undoName);
                        setter(item, value);
                        EditorUtility.SetDirty(item);
                        postApply?.Invoke(item);
                    }
            }
        }

        void MultiInterruption(List<AnimatorStateTransition> items)
        {
            if (items.Count == 0) return;
            var first = items[0].interruptionSource;
            bool mixed = false;
            foreach (var item in items)
                if (item.interruptionSource != first) { mixed = true; break; }

            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            var value = (TransitionInterruptionSource)EditorGUILayout.EnumPopup("Interruption", first);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
            {
                using (new UndoScope("Edit Transitions"))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, "Edit Transition");
                        item.interruptionSource = value;
                        EditorUtility.SetDirty(item);
                    }
            }
        }

        void DrawSharedConditions(AnimatorController controller)
        {
            EditorGUILayout.LabelField("Shared Conditions", EditorStyles.boldLabel);

            int total = _selectedTransitions.Count;
            var shared = SharedConditions(_selectedTransitions);
            if (shared.Count == 0)
            {
                EditorGUILayout.LabelField("(the selected transitions have no conditions)", EditorStyles.miniLabel);
                return;
            }

            var paramNames = AllParameterNames(controller);
            if (paramNames.Length == 0)
            {
                EditorGUILayout.HelpBox("Add parameters before editing conditions.", MessageType.Info);
                return;
            }
            var typeByName = ParameterTypeMap(controller);

            foreach (var entry in shared)
            {
                var original = entry.data;
                var working = new TransitionClipboard.ConditionData
                {
                    mode = original.mode,
                    parameter = original.parameter,
                    threshold = original.threshold,
                };
                bool sharedByAll = entry.count == total;

                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                if (!sharedByAll) GUI.color = new Color(1f, 0.85f, 0.4f);   // amber marks partial coverage
                EditorGUILayout.LabelField(entry.count + "/" + total, EditorStyles.miniLabel, GUILayout.Width(32));
                GUI.color = prevColor;

                EditorGUI.BeginChangeCheck();
                int paramIndex = Mathf.Max(0, Array.IndexOf(paramNames, working.parameter));
                paramIndex = EditorGUILayout.Popup(paramIndex, paramNames);
                working.parameter = paramNames[paramIndex];
                var type = typeByName.TryGetValue(working.parameter, out var ty) ? ty : AnimatorControllerParameterType.Float;
                DrawConditionValue(working, type, delayed: true);
                bool edited = EditorGUI.EndChangeCheck();

                bool remove = GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22));
                EditorGUILayout.EndHorizontal();

                if (remove)
                {
                    RemoveCommonCondition(original);
                    GUIUtility.ExitGUI();   // a row disappears, so restart layout
                }
                else if (edited)
                {
                    ReplaceCommonCondition(original, working);
                    // Restart layout: changing the parameter can change the value control's shape,
                    // and a recompute may merge/reorder rows. The threshold uses a delayed field, so
                    // this only fires on commit (Enter / focus-out) — typing stays smooth.
                    GUIUtility.ExitGUI();
                }
            }
        }

        void DrawAddConditionToAll(AnimatorController controller)
        {
            EditorGUILayout.LabelField("Add The Same Condition To Every Selected Transition", EditorStyles.boldLabel);

            var paramNames = AllParameterNames(controller);
            if (paramNames.Length == 0)
            {
                EditorGUILayout.HelpBox("Add parameters before building conditions.", MessageType.Info);
                return;
            }
            var typeByName = ParameterTypeMap(controller);

            EditorGUILayout.BeginHorizontal();
            int paramIndex = Mathf.Max(0, Array.IndexOf(paramNames, _newCondition.parameter));
            paramIndex = EditorGUILayout.Popup(paramIndex, paramNames);
            _newCondition.parameter = paramNames[paramIndex];
            var type = typeByName.TryGetValue(_newCondition.parameter, out var ty) ? ty : AnimatorControllerParameterType.Float;
            DrawConditionValue(_newCondition, type);
            if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(46)))
            {
                AddConditionToAll(_newCondition);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }

        void ReplaceCommonCondition(TransitionClipboard.ConditionData oldData, TransitionClipboard.ConditionData newData)
        {
            using (new UndoScope("Edit Common Condition"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var list = ToDataList(transition);
                    bool changed = false;
                    foreach (var data in list)
                        if (Same(data, oldData))
                        {
                            data.mode = newData.mode;
                            data.parameter = newData.parameter;
                            data.threshold = newData.threshold;
                            changed = true;
                        }
                    if (changed)
                    {
                        Undo.RegisterCompleteObjectUndo(transition, "Edit Common Condition");
                        TransitionClipboard.SetConditions(transition, list);
                        EditorUtility.SetDirty(transition);
                    }
                }
            }
        }

        void RemoveCommonCondition(TransitionClipboard.ConditionData data)
        {
            using (new UndoScope("Remove Common Condition"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var list = ToDataList(transition);
                    if (list.RemoveAll(d => Same(d, data)) > 0)
                    {
                        Undo.RegisterCompleteObjectUndo(transition, "Remove Common Condition");
                        TransitionClipboard.SetConditions(transition, list);
                        EditorUtility.SetDirty(transition);
                    }
                }
            }
        }

        void AddConditionToAll(TransitionClipboard.ConditionData data)
        {
            using (new UndoScope("Add Condition To All"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var list = ToDataList(transition);
                    list.Add(new TransitionClipboard.ConditionData
                    {
                        mode = data.mode,
                        parameter = data.parameter,
                        threshold = data.threshold,
                    });
                    Undo.RegisterCompleteObjectUndo(transition, "Add Condition To All");
                    TransitionClipboard.SetConditions(transition, list);
                    EditorUtility.SetDirty(transition);
                }
            }
        }

        /// <summary>Adds a new transition (with the copied settings) alongside each selected one.</summary>
        void PasteSelectedAsNew()
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            AnimatorTransitionBase last = null;
            using (new UndoScope("Paste Transition As New"))
            {
                foreach (var transition in _selectedTransitions)
                {
                    var edge = _graphView.Sync.FindEdge(transition);
                    if (edge == null) continue;
                    var created = _graphView.Sync.CreateTransition(
                        edge.output?.node as GraphNodeBase, edge.input?.node as GraphNodeBase);
                    if (created != null) { TransitionClipboard.Apply(created, snapshot); last = created; }
                }
            }
            _graphView.Sync.Rebuild();
            if (last != null) Context.Select(last);
        }

        void RefreshEdges()
        {
            foreach (var edge in _graphView.Sync.Edges)
                edge.Refresh();
        }

        // ---- condition / value helpers ---------------------------------------

        /// <summary>Draws the value control for one condition. Bool shows true/false; Trigger shows nothing.</summary>
        static void DrawConditionValue(TransitionClipboard.ConditionData condition, AnimatorControllerParameterType type,
            bool delayed = false)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                {
                    int index = condition.mode == AnimatorConditionMode.IfNot ? 1 : 0;
                    index = EditorGUILayout.Popup(index, BoolValueLabels, GUILayout.Width(80));
                    condition.mode = index == 1 ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If;
                    GUILayout.Space(56);
                    break;
                }
                case AnimatorControllerParameterType.Trigger:
                {
                    condition.mode = AnimatorConditionMode.If;
                    EditorGUILayout.LabelField("(set)", EditorStyles.miniLabel, GUILayout.Width(80));
                    GUILayout.Space(56);
                    break;
                }
                default:
                {
                    var modes = ModesFor(type);
                    // GestureLeft / GestureRight thresholds read as an enum ("1: Fist"…), so
                    // give the value popup the wider slot and shrink the mode popup — the
                    // row's total width stays aligned with the plain numeric layout.
                    bool gesture = type == AnimatorControllerParameterType.Int
                        && VrcParameters.IsGestureParameter(condition.parameter)
                        && VrcParameters.GestureLabel(condition.threshold) != null;
                    int modeIndex = Mathf.Max(0, Array.IndexOf(modes, condition.mode));
                    modeIndex = EditorGUILayout.Popup(modeIndex, ModeLabels(modes), GUILayout.Width(gesture ? 56 : 80));
                    condition.mode = modes[modeIndex];
                    if (gesture)
                    {
                        int current = (int)Math.Round(condition.threshold);
                        condition.threshold = EditorGUILayout.Popup(current, GestureValueLabels, GUILayout.Width(80));
                    }
                    else
                    {
                        condition.threshold = delayed
                            ? EditorGUILayout.DelayedFloatField(condition.threshold, GUILayout.Width(56))
                            : EditorGUILayout.FloatField(condition.threshold, GUILayout.Width(56));
                    }
                    break;
                }
            }
        }

        static List<TransitionClipboard.ConditionData> ToDataList(AnimatorTransitionBase transition)
        {
            var list = new List<TransitionClipboard.ConditionData>();
            foreach (var c in transition.conditions)
                list.Add(new TransitionClipboard.ConditionData { mode = c.mode, parameter = c.parameter, threshold = c.threshold });
            return list;
        }

        struct SharedConditionEntry
        {
            public TransitionClipboard.ConditionData data;
            public int count;
            public int order;
        }

        /// <summary>
        /// Every distinct condition across the selected transitions, with how many of them contain
        /// it. Conditions present in every transition are listed first; ties keep first-seen order.
        /// </summary>
        static List<SharedConditionEntry> SharedConditions(List<AnimatorTransitionBase> transitions)
        {
            var result = new List<SharedConditionEntry>();
            foreach (var t in transitions)
            {
                if (t == null) continue;
                foreach (var c in t.conditions)
                {
                    var data = new TransitionClipboard.ConditionData { mode = c.mode, parameter = c.parameter, threshold = c.threshold };
                    int idx = result.FindIndex(e => Same(e.data, data));
                    if (idx >= 0)
                    {
                        var e = result[idx];
                        e.count++;
                        result[idx] = e;
                    }
                    else
                    {
                        result.Add(new SharedConditionEntry { data = data, count = 1, order = result.Count });
                    }
                }
            }
            result.Sort((a, b) =>
            {
                int byCount = b.count.CompareTo(a.count);
                return byCount != 0 ? byCount : a.order.CompareTo(b.order);
            });
            return result;
        }

        static bool Same(TransitionClipboard.ConditionData a, TransitionClipboard.ConditionData b)
        {
            return a.parameter == b.parameter && a.mode == b.mode && Mathf.Approximately(a.threshold, b.threshold);
        }

        // ---- state machine ---------------------------------------------------

        void DrawStateMachine(AnimatorStateMachine stateMachine)
        {
            EditorGUILayout.LabelField("Sub-State Machine", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField("Name", stateMachine.name);
            if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(name))
            {
                Undo.RegisterCompleteObjectUndo(stateMachine, "Rename State Machine");
                stateMachine.name = name;
                EditorUtility.SetDirty(stateMachine);
                Context.NotifyGraphStructureChanged();
            }

            EditorGUILayout.LabelField("States", stateMachine.states.Length.ToString());
            EditorGUILayout.LabelField("Sub-State Machines", stateMachine.stateMachines.Length.ToString());
            if (GUILayout.Button("Open"))
                Context.EnterStateMachine(stateMachine);
        }

        // ---- overview ---------------------------------------------------------

        // Deliberately sparse: identity rows, the two per-controller settings and three
        // action buttons. Explanations live in tooltips (and confirm dialogs), not in
        // always-visible labels — the reports themselves open in their own windows.
        void DrawOverview()
        {
            var controller = Context.Controller;
            EditorGUILayout.LabelField(L.Tr("Controller"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.Tr("Name"), controller.name);
            EditorGUILayout.LabelField(L.Tr("Layers"), controller.layers.Length.ToString());
            EditorGUILayout.LabelField(L.Tr("Parameters"), controller.parameters.Length.ToString());

            EditorGUILayout.Space(6);
            var currentEmpty = GraphFrameData.GetEmptyClip(controller);
            var pickedEmpty = (AnimationClip)EditorGUILayout.ObjectField(
                new GUIContent(L.Tr("Empty Clip"),
                    L.Tr("Stored with this controller. New states are created with it, and the analyzer's Fill fix assigns it to states with no motion.")),
                currentEmpty, typeof(AnimationClip), false);
            if (pickedEmpty != currentEmpty)
                GraphFrameData.SetEmptyClip(controller, pickedEmpty);

            var wdTooltip = L.Tr("Bulk-set every state. Layers containing only Direct blend trees stay ON.");
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent(L.Tr("Write Defaults"), wdTooltip));
            if (GUILayout.Button(new GUIContent(L.Tr("Set All ON"), wdTooltip)))
                BulkSetWriteDefaults(controller, true);
            if (GUILayout.Button(new GUIContent(L.Tr("Set All OFF"), wdTooltip)))
                BulkSetWriteDefaults(controller, false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            if (GUILayout.Button(new GUIContent(L.Tr("Analyze Controller"),
                    L.Tr("Audit this controller for unused parameters, broken conditions, unreachable states and more."))))
            {
                AnalyzerWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("List Clips"),
                    L.Tr("List every AnimationClip this controller references and the states that use it."))))
            {
                ClipsWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Object Toggle…"),
                    L.Tr("Generate ON/OFF clips for picked GameObjects and the layer or Direct blend tree machinery that plays them."))))
            {
                ToggleBuilderWindow.Open(controller, OnToggleApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Round-Robin Sync…"),
                    L.Tr("Time-multiplex several parameters over a few synced ones (index + value channels) — parameter compression."))))
            {
                RoundRobinSyncWindow.Open(controller, OnToggleApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Network Sync (Beta)…"),
                    L.Tr("Generate the local-driver + remote-mirror structure that syncs this layer to other VRChat players. Beta: the generated structure may still change."))))
            {
                NetworkSyncWindow.Open(controller, Context.LayerIndex, OnToggleApplied);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Expressions Menu…"),
                    L.Tr("Edit the avatar's VRC Expressions Menu (auto-detected from the scene)."))))
            {
                VrcMenuWindow.Open(controller);
                GUIUtility.ExitGUI();   // the focus moved to another window under this layout pass
            }
            DrawCleanupSection(controller);
        }

        /// <summary>The toggle wizard added a parameter, clips and possibly a layer — let
        /// every panel and the graph pick that up, and show the layer it landed in.</summary>
        void OnToggleApplied(int layerIndex)
        {
            var controller = Context.Controller;
            Context.NotifyParametersChanged();
            Context.NotifyLayersChanged();
            Context.NotifyGraphStructureChanged();
            if (controller != null && layerIndex >= 0 && layerIndex < controller.layers.Length)
                Context.SetLayer(layerIndex);
        }

        void BulkSetWriteDefaults(AnimatorController controller, bool value)
        {
            string message = value
                ? L.Tr("Set Write Defaults ON for every state in this controller?")
                : L.Tr("Set Write Defaults OFF for every state?\n\nLayers that contain only Direct blend trees are kept ON.");
            if (!EditorUtility.DisplayDialog(L.Tr("Write Defaults"), message,
                    value ? L.Tr("Set ON") : L.Tr("Set OFF"), L.Tr("Cancel")))
                return;
            ControllerAnalyzer.SetAllWriteDefaults(controller, value);
            _graphView.Sync.RefreshAllStateNodes();   // WD badges update immediately
        }

        // ---- cleanup ----------------------------------------------------------

        void DrawCleanupSection(AnimatorController controller)
        {
            bool isAsset = !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller));
            using (new EditorGUI.DisabledScope(!isAsset))
                if (GUILayout.Button(new GUIContent(L.Tr("Scan For Leftovers"),
                        L.Tr("Find sub-assets stored in the .controller file that nothing references any more.") + "\n"
                        + L.Tr("Blend trees, clips and states deleted from the graph can survive as invisible sub-assets; find them."))))
                    _leftovers = ControllerCleanup.FindLeftoverSubAssets(controller);
            if (!isAsset)
                EditorGUILayout.LabelField(L.Tr("(unsaved controller — nothing to scan)"), EditorStyles.miniLabel);
            if (_leftovers == null) return;

            // Entries deleted (or restored by Undo) since the scan linger as fake nulls.
            int live = 0;
            foreach (var asset in _leftovers)
                if (asset != null) live++;
            if (live == 0)
            {
                EditorGUILayout.HelpBox(L.Tr("No leftover sub-assets found."), MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox(L.Tr("{0} leftover sub-asset(s) in this .controller file.", live),
                MessageType.Warning);
            foreach (var asset in _leftovers)
            {
                if (asset == null) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(ControllerCleanup.Describe(asset), EditorStyles.miniLabel);
                if (GUILayout.Button(new GUIContent(L.Tr("Ping"), L.Tr("Highlight this object in the Project / graph")),
                        EditorStyles.miniButton, GUILayout.Width(46)))
                    EditorGUIUtility.PingObject(asset);
                if (GUILayout.Button(new GUIContent(L.Tr("Delete"),
                        L.Tr("Delete this leftover sub-asset from the .controller file")),
                        EditorStyles.miniButton, GUILayout.Width(46)))
                {
                    // Undoable single delete — no dialog, matching the analyzer's one-click fixes.
                    ControllerCleanup.DeleteSubAssets(controller, new[] { asset });
                    _leftovers = ControllerCleanup.FindLeftoverSubAssets(controller);
                    EditorGUILayout.EndHorizontal();
                    GUIUtility.ExitGUI();   // the leftover list was rebuilt under this layout pass
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button(L.Tr("Delete All")))
            {
                DeleteAllLeftovers(controller, live);
                GUIUtility.ExitGUI();
            }
        }

        void DeleteAllLeftovers(AnimatorController controller, int count)
        {
            if (!EditorUtility.DisplayDialog(L.Tr("Cleanup"),
                    L.Tr("Delete {0} leftover sub-asset(s) from '{1}'?\n\nNothing in this file references them. This can be undone.",
                        count, controller.name),
                    L.Tr("Delete"), L.Tr("Cancel")))
                return;
            ControllerCleanup.DeleteSubAssets(controller, _leftovers);
            _leftovers = ControllerCleanup.FindLeftoverSubAssets(controller);
        }

        // ---- helpers ---------------------------------------------------------

        static void HorizontalLine()
        {
            EditorGUILayout.Space(5);
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.35f));
            EditorGUILayout.Space(5);
        }

        static AnimatorConditionMode[] ModesFor(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    return new[] { AnimatorConditionMode.If, AnimatorConditionMode.IfNot };
                case AnimatorControllerParameterType.Int:
                    return IntModes;
                default:
                    return FloatModes;
            }
        }

        static string[] ModeLabels(AnimatorConditionMode[] modes)
        {
            var labels = new string[modes.Length];
            for (int i = 0; i < modes.Length; i++)
                labels[i] = modes[i].ToString();
            return labels;
        }

        static string[] AllParameterNames(AnimatorController controller)
        {
            var parameters = controller.parameters;
            var names = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                names[i] = parameters[i].name;
            return names;
        }

        static string[] ParameterNamesOfType(AnimatorController controller, AnimatorControllerParameterType type)
        {
            var names = new List<string>();
            foreach (var p in controller.parameters)
                if (p.type == type)
                    names.Add(p.name);
            if (names.Count == 0) names.Add(string.Empty);
            return names.ToArray();
        }

        static Dictionary<string, AnimatorControllerParameterType> ParameterTypeMap(AnimatorController controller)
        {
            var map = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var p in controller.parameters)
                map[p.name] = p.type;
            return map;
        }
    }
}
