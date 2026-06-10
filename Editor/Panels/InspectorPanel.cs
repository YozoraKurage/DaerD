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

        readonly AnimatorGraphView _graphView;
        readonly List<AnimatorTransitionBase> _selectedTransitions = new List<AnimatorTransitionBase>();
        readonly TransitionClipboard.ConditionData _newCondition =
            new TransitionClipboard.ConditionData { mode = AnimatorConditionMode.If, parameter = string.Empty };

        int _rangeAnchor = -1;
        object _lastSelection;
        bool _showBlendTree = true;
        List<ControllerAnalyzer.Issue> _issues;

        public InspectorPanel(DaerDContext context, AnimatorGraphView graphView)
            : base(context, "Inspector")
        {
            _graphView = graphView;
            context.SelectionChanged += OnSelectionChanged;
            context.ControllerChanged += Refresh;
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
            }
            Refresh();
        }

        /// <summary>Runs the analyzer and shows the result in the overview view.</summary>
        public void ShowAnalysis()
        {
            if (!Context.HasController) return;
            _issues = ControllerAnalyzer.Analyze(Context.Controller);
            Context.Select(null);
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
            using (new EditorGUI.DisabledScope(frame.locked))
            {
                if (GUILayout.Button("Delete Frame"))
                {
                    _graphView.Sync.DeleteFrame(frame);
                    Context.Select(null);
                    GUIUtility.ExitGUI();
                }
            }
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

            EditorGUI.BeginChangeCheck();
            string text = EditorGUILayout.TextArea(note.text, GUILayout.MinHeight(60));
            var color = EditorGUILayout.ColorField("Color", note.color);
            int sizeIndex = Array.IndexOf(NoteFontSizes, note.fontSize);
            if (sizeIndex < 0) sizeIndex = 1;
            sizeIndex = EditorGUILayout.Popup("Font Size", sizeIndex, NoteFontSizeLabels);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(frameData, "Edit Note");
                note.text = text;
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
            EditorGUILayout.LabelField("Behaviours (" + behaviours.Length + ")", EditorStyles.boldLabel);

            foreach (var behaviour in behaviours)
            {
                if (behaviour == null) continue;
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(behaviour.GetType().Name, EditorStyles.boldLabel);
                if (GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    RemoveBehaviour(state, behaviour);
                    _graphView.Sync.RefreshStateNode(state);   // B badge updates immediately
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
                DrawSerializedFields(behaviour);
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Behaviour"))
                ShowAddBehaviourMenu(state);
        }

        void ShowAddBehaviourMenu(AnimatorState state)
        {
            var menu = new GenericMenu();
            foreach (var type in TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>())
            {
                if (type.IsAbstract) continue;
                var captured = type;
                menu.AddItem(new GUIContent(type.Name), false, () =>
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
            bool refreshEdges = false) where T : UnityEngine.Object
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
                using (new UndoScope("Edit Transitions"))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, "Edit Transition");
                        setter(item, value);
                        EditorUtility.SetDirty(item);
                    }
                if (refreshEdges) RefreshEdges();
            }
        }

        void MultiFloat<T>(string label, List<T> items, Func<T, float> getter, Action<T, float> setter)
            where T : UnityEngine.Object
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
                using (new UndoScope("Edit Transitions"))
                    foreach (var item in items)
                    {
                        Undo.RegisterCompleteObjectUndo(item, "Edit Transition");
                        setter(item, value);
                        EditorUtility.SetDirty(item);
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
                    int modeIndex = Mathf.Max(0, Array.IndexOf(modes, condition.mode));
                    modeIndex = EditorGUILayout.Popup(modeIndex, ModeLabels(modes), GUILayout.Width(80));
                    condition.mode = modes[modeIndex];
                    condition.threshold = delayed
                        ? EditorGUILayout.DelayedFloatField(condition.threshold, GUILayout.Width(56))
                        : EditorGUILayout.FloatField(condition.threshold, GUILayout.Width(56));
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

        // ---- overview / analysis --------------------------------------------

        void DrawOverview()
        {
            var controller = Context.Controller;
            EditorGUILayout.LabelField("Controller", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Name", controller.name);
            EditorGUILayout.LabelField("Layers", controller.layers.Length.ToString());
            EditorGUILayout.LabelField("Parameters", controller.parameters.Length.ToString());

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Write Defaults", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Bulk-set every state. Layers containing only Direct blend trees stay ON.",
                EditorStyles.miniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Set All ON"))
                BulkSetWriteDefaults(controller, true);
            if (GUILayout.Button("Set All OFF"))
                BulkSetWriteDefaults(controller, false);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Analyze Controller"))
                _issues = ControllerAnalyzer.Analyze(controller);

            if (_issues == null) return;

            EditorGUILayout.Space(2);
            if (_issues.Count == 0)
            {
                EditorGUILayout.HelpBox("No issues found.", MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField(_issues.Count + " issue(s)", EditorStyles.boldLabel);
            foreach (var issue in _issues)
            {
                var messageType = issue.severity == ControllerAnalyzer.Severity.Error ? MessageType.Error
                    : issue.severity == ControllerAnalyzer.Severity.Warning ? MessageType.Warning
                    : MessageType.None;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("[" + issue.category + "] " + issue.message, messageType);
                if (issue.context != null && GUILayout.Button("Ping", GUILayout.Width(46), GUILayout.Height(38)))
                    EditorGUIUtility.PingObject(issue.context);
                EditorGUILayout.EndHorizontal();
            }
        }

        void BulkSetWriteDefaults(AnimatorController controller, bool value)
        {
            string message = value
                ? "Set Write Defaults ON for every state in this controller?"
                : "Set Write Defaults OFF for every state?\n\nLayers that contain only Direct blend trees are kept ON.";
            if (!EditorUtility.DisplayDialog("Write Defaults", message, value ? "Set ON" : "Set OFF", "Cancel"))
                return;
            ControllerAnalyzer.SetAllWriteDefaults(controller, value);
            _issues = ControllerAnalyzer.Analyze(controller);
            _graphView.Sync.RefreshAllStateNodes();   // WD badges update immediately
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
