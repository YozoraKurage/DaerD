using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- state -----------------------------------------------------------

    /// <summary>Form for the single selected state: its fields, parameter overrides, outgoing
    /// transitions, an inline blend tree editor, sync requests and behaviours.</summary>
    class StateInspector
    {
        readonly DaerDContext _context;
        readonly GraphSync _sync;
        readonly SyncRequestInspector _syncRequests;
        readonly BehaviourInspector _behaviours;

        bool _showBlendTree = true;

        public StateInspector(DaerDContext context, GraphSync sync, SyncRequestInspector syncRequests,
            BehaviourInspector behaviours)
        {
            _context = context;
            _sync = sync;
            _syncRequests = syncRequests;
            _behaviours = behaviours;
        }

        public void DrawState(AnimatorState state)
        {
            var controller = _context.Controller;
            EditorGUILayout.LabelField(L.Tr("State"), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField(L.Tr("Name"), state.name);
            var motion = (Motion)EditorGUILayout.ObjectField(L.Tr("Motion"), state.motion, typeof(Motion), false);
            float speed = EditorGUILayout.FloatField(L.Tr("Speed"), state.speed);
            float cycleOffset = EditorGUILayout.FloatField(L.Tr("Cycle Offset"), state.cycleOffset);
            bool mirror = EditorGUILayout.Toggle(L.Tr("Mirror"), state.mirror);
            bool ikOnFeet = EditorGUILayout.Toggle(L.Tr("Foot IK"), state.iKOnFeet);
            bool writeDefaults = EditorGUILayout.Toggle(L.Tr("Write Defaults"), state.writeDefaultValues);
            string tag = EditorGUILayout.TextField(L.Tr("Tag"), state.tag);
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
                if (visualChange) _context.NotifyGraphStructureChanged();
                // The WD badge lives on the graph node; repaint it right away rather than
                // waiting for the next full rebuild.
                else if (badgeChange) _context.NotifyGraphVisualsChanged(state);
            }

            DrawStateParameters(state, controller);

            EditorGUILayout.Space(4);
            var transitions = state.transitions;
            EditorGUILayout.LabelField(L.Tr("Transitions") + " (" + transitions.Length + ")", EditorStyles.boldLabel);
            foreach (var t in transitions)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(ParameterConverter.DescribeTransition(t));
                if (GUILayout.Button(L.Tr("Select"), EditorStyles.miniButton, GUILayout.Width(56)))
                {
                    var edge = _sync.FindEdge(t);
                    _context.Select((object)edge ?? t);
                    // Also center the graph view on the edge so the user can see what they
                    // selected — clicking "Select" without a follow-up frame leaves the user
                    // hunting for the highlighted edge on a large state machine.
                    _context.RequestFrameOn((object)edge ?? t);
                }
                EditorGUILayout.EndHorizontal();
            }

            if (state.motion is BlendTree blendTree)
            {
                EditorGUILayout.Space(4);
                _showBlendTree = EditorGUILayout.Foldout(_showBlendTree, L.Tr("Blend Tree"), true);
                if (_showBlendTree)
                {
                    EditorGUI.indentLevel++;
                    BlendTreePanel.Draw(blendTree, _context);
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
            EditorGUILayout.LabelField(L.Tr("Parameter Overrides"), EditorStyles.boldLabel);

            DrawParameterOverride(state, L.Tr("Speed Multiplier"), floatParams,
                state.speedParameterActive, state.speedParameter,
                (active, param) => { state.speedParameterActive = active; state.speedParameter = param; });
            DrawParameterOverride(state, L.Tr("Motion Time"), floatParams,
                state.timeParameterActive, state.timeParameter,
                (active, param) => { state.timeParameterActive = active; state.timeParameter = param; });
            DrawParameterOverride(state, L.Tr("Mirror"), boolParams,
                state.mirrorParameterActive, state.mirrorParameter,
                (active, param) => { state.mirrorParameterActive = active; state.mirrorParameter = param; });
            DrawParameterOverride(state, L.Tr("Cycle Offset"), floatParams,
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
