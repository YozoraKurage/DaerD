using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- state machine ---------------------------------------------------

    /// <summary>Form for a selected sub-state machine node.</summary>
    class StateMachineInspector
    {
        readonly DaerDContext _context;

        public StateMachineInspector(DaerDContext context)
        {
            _context = context;
        }

        public void DrawStateMachine(AnimatorStateMachine stateMachine)
        {
            EditorGUILayout.LabelField(L.Tr("Sub-State Machine"), EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField(L.Tr("Name"), stateMachine.name);
            if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(name))
            {
                Undo.RegisterCompleteObjectUndo(stateMachine, "Rename State Machine");
                stateMachine.name = name;
                EditorUtility.SetDirty(stateMachine);
                _context.NotifyGraphStructureChanged();
            }

            EditorGUILayout.LabelField(L.Tr("States"), stateMachine.states.Length.ToString());
            EditorGUILayout.LabelField(L.Tr("Sub-State Machines"), stateMachine.stateMachines.Length.ToString());
            if (GUILayout.Button(L.Tr("Open")))
                _context.EnterStateMachine(stateMachine);
        }
    }
}
