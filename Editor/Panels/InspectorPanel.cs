using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>Context-sensitive inspector for the currently selected graph element.</summary>
    class InspectorPanel : PanelBase
    {
        readonly List<AnimatorTransitionBase> _selectedTransitions = new List<AnimatorTransitionBase>();
        readonly MultiTransitionInspector _multiTransition;
        readonly TransitionInspector _transitions;

        // Behaviour rows selected in the state inspector. Selecting is what tells Ctrl+C/V to
        // act on behaviours instead of the state itself (the graph view owns state copy/paste).
        readonly List<StateMachineBehaviour> _selectedBehaviours = new List<StateMachineBehaviour>();
        object _lastSelection;
        readonly OverviewInspector _overview;
        readonly StateMachineInspector _stateMachine;
        readonly VrcBehaviourDrawers _vrcDrawers;
        readonly BehaviourInspector _behaviours;
        readonly MultiStateBehaviourInspector _multiBehaviours;
        readonly SyncRequestInspector _syncRequests;
        readonly StateInspector _state;
        readonly MultiStateInspector _multiStates;
        readonly NoteInspector _notes;
        readonly FrameInspector _frames;

        public InspectorPanel(DaerDContext context, GraphSync sync)
            : base(context, "Inspector")
        {
            _overview = new OverviewInspector(context);
            _stateMachine = new StateMachineInspector(context);
            _multiTransition = new MultiTransitionInspector(context, sync, _selectedTransitions);
            _transitions = new TransitionInspector(context, sync, _selectedTransitions, _multiTransition);
            _vrcDrawers = new VrcBehaviourDrawers(context, Refresh);
            _behaviours = new BehaviourInspector(context, _selectedBehaviours, _vrcDrawers);
            _multiBehaviours = new MultiStateBehaviourInspector(context, _selectedBehaviours, _behaviours, _vrcDrawers);
            _syncRequests = new SyncRequestInspector(context);
            _state = new StateInspector(context, sync, _syncRequests, _behaviours);
            _multiStates = new MultiStateInspector(context, sync, _state, _overview, _multiBehaviours);
            _notes = new NoteInspector(context, sync);
            _frames = new FrameInspector(context, sync);
            context.SelectionChanged += OnSelectionChanged;
            context.ControllerChanged += Refresh;
            context.GraphStructureChanged += Refresh;
            context.GraphRebuilt += Refresh;
            context.ParametersChanged += Refresh;

            // The rows highlight their edge while the pointer is over them; a pointer that
            // leaves the panel altogether produces no further row repaints, so the last
            // highlight would stay lit on the graph until something else redrew it.
            RegisterCallback<MouseLeaveEvent>(_ => sync.SetHoveredTransition(null));
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
            var selectedStates = Context.GetSelectedStates();
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
                _frames.DrawFrame(frame);
            }
            else if (selection is GraphFrameData.Note note)
            {
                _notes.DrawNote(note);
            }
            else if (selection is SpecialNodeKind kind)
            {
                // Entry and Any State own transition lists of their own — and lists with a
                // priority order, which is only visible if something draws them. Exit owns
                // none: it is where transitions end.
                if (kind == SpecialNodeKind.Exit)
                    EditorGUILayout.HelpBox(L.Tr("{0} node. Drag from its port to create transitions.", kind), MessageType.Info);
                else
                    _transitions.DrawSourceContext(kind == SpecialNodeKind.Entry
                        ? TransitionEnd.Entry : TransitionEnd.AnyState);
            }
            else
            {
                _overview.DrawOverview();
            }
        }

        void DrawBlendTreeSelection(BlendTree blendTree)
        {
            EditorGUILayout.LabelField(L.Tr("Blend Tree"), EditorStyles.boldLabel);
            BlendTreePanel.Draw(blendTree, Context);
        }

        void DrawClipSelection(AnimationClip clip)
        {
            EditorGUILayout.LabelField(L.Tr("Animation Clip"), EditorStyles.boldLabel);
            EditorGUILayout.ObjectField(L.Tr("Clip"), clip, typeof(AnimationClip), false);
            EditorGUILayout.LabelField(L.Tr("Length"), clip.length.ToString("0.###") + "s");
            EditorGUILayout.LabelField(L.Tr("Frame Rate"), clip.frameRate.ToString("0.#") + " fps");
            EditorGUILayout.LabelField(L.Tr("Looping"), clip.isLooping ? L.Tr("Yes") : L.Tr("No"));
            if (GUILayout.Button(L.Tr("Ping in Project")))
                EditorGUIUtility.PingObject(clip);
        }
    }
}
