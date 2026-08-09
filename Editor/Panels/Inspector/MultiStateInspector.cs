using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- multi-state -----------------------------------------------------

    /// <summary>Bulk editor for the states selected in the graph.</summary>
    class MultiStateInspector
    {
        readonly DaerDContext _context;
        readonly GraphSync _sync;
        // Held because a selection can thin out to one live state (or none) under the null
        // filter, and the draw is then handed back to the single-state form or the overview.
        readonly StateInspector _stateInspector;
        readonly OverviewInspector _overviewInspector;
        readonly MultiStateBehaviourInspector _multiBehaviours;

        public MultiStateInspector(DaerDContext context, GraphSync sync, StateInspector stateInspector,
            OverviewInspector overviewInspector, MultiStateBehaviourInspector multiBehaviours)
        {
            _context = context;
            _sync = sync;
            _stateInspector = stateInspector;
            _overviewInspector = overviewInspector;
            _multiBehaviours = multiBehaviours;
        }

        public static bool AnyStateAlive(List<AnimatorState> states)
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
        public void DrawMultiStateEditor(List<AnimatorState> states)
        {
            // Drop destroyed entries up front so the mixed-value detection and writes don't
            // walk a null reference mid-IMGUI.
            var alive = new List<AnimatorState>(states.Count);
            foreach (var s in states)
                if (s != null) alive.Add(s);
            if (alive.Count < 2)
            {
                if (alive.Count == 1) _stateInspector.DrawState(alive[0]);
                else _overviewInspector.DrawOverview();
                return;
            }

            var controller = _context.Controller;
            EditorGUILayout.LabelField(L.Tr("{0} states selected", alive.Count), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(L.Tr("Common Settings (applied to all selected)"), EditorStyles.boldLabel);

            MultiEditGui.ObjectField<AnimatorState, Motion>(L.Tr("Motion"), alive,
                x => x.motion, (x, v) => x.motion = v,
                undoName: "Edit States", postApply: s => _sync.RefreshStateNode(s));
            MultiEditGui.Float(L.Tr("Speed"), alive, x => x.speed, (x, v) => x.speed = v, undoName: "Edit States");
            MultiEditGui.Float(L.Tr("Cycle Offset"), alive, x => x.cycleOffset, (x, v) => x.cycleOffset = v, undoName: "Edit States");
            MultiEditGui.Bool(L.Tr("Mirror"), alive, x => x.mirror, (x, v) => x.mirror = v, undoName: "Edit States");
            MultiEditGui.Bool(L.Tr("Foot IK"), alive, x => x.iKOnFeet, (x, v) => x.iKOnFeet = v, undoName: "Edit States");
            MultiEditGui.Bool(L.Tr("Write Defaults"), alive, x => x.writeDefaultValues, (x, v) => x.writeDefaultValues = v,
                undoName: "Edit States", postApply: s => _sync.RefreshStateNode(s));
            MultiEditGui.Text(L.Tr("Tag"), alive, x => x.tag, (x, v) => x.tag = v, undoName: "Edit States");

            EditorGUILayout.Space(4);
            DrawMultiStateParameterOverrides(alive, controller);

            EditorGUILayout.Space(6);
            PanelGui.HorizontalLine();
            _multiBehaviours.DrawMultiStateBehaviours(alive);

            EditorGUILayout.Space(6);
            PanelGui.HorizontalLine();
            EditorGUILayout.LabelField(L.Tr("Bulk Actions"), EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(L.Tr("Set Default State to First")))
                _sync.SetDefaultState(alive[0]);
            if (GUILayout.Button(L.Tr("Delete All {0}", alive.Count)))
            {
                if (EditorUtility.DisplayDialog(L.Tr("Delete States"),
                    L.Tr("Delete the {0} selected states? Their transitions will be removed too.", alive.Count),
                    L.Tr("Delete"), L.Tr("Cancel")))
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

            EditorGUILayout.LabelField(L.Tr("Parameter Overrides (applied to all)"), EditorStyles.boldLabel);

            DrawMultiParameterOverride(states, L.Tr("Speed Multiplier"), floatParams,
                x => x.speedParameterActive, x => x.speedParameter,
                (s, a, p) => { s.speedParameterActive = a; s.speedParameter = p; });
            DrawMultiParameterOverride(states, L.Tr("Motion Time"), floatParams,
                x => x.timeParameterActive, x => x.timeParameter,
                (s, a, p) => { s.timeParameterActive = a; s.timeParameter = p; });
            DrawMultiParameterOverride(states, L.Tr("Mirror"), boolParams,
                x => x.mirrorParameterActive, x => x.mirrorParameter,
                (s, a, p) => { s.mirrorParameterActive = a; s.mirrorParameter = p; });
            DrawMultiParameterOverride(states, L.Tr("Cycle Offset"), floatParams,
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
            var sm = _context.CurrentStateMachine;
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
            _context.Select(null);
            _sync.RequestRebuild();
        }
    }
}
