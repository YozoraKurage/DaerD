using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The behaviour list of a single selected state, and the row selection both behaviour
    /// inspectors share — the multi-state one selects, prunes and copies through this class so
    /// there is one selection, not two.
    /// </summary>
    class BehaviourInspector
    {
        readonly DaerDContext _context;
        readonly List<StateMachineBehaviour> _selectedBehaviours;
        readonly VrcBehaviourDrawers _vrcDrawers;

        int _behaviourRangeAnchor = -1;

        public BehaviourInspector(DaerDContext context, List<StateMachineBehaviour> selectedBehaviours,
            VrcBehaviourDrawers vrcDrawers)
        {
            _context = context;
            _selectedBehaviours = selectedBehaviours;
            _vrcDrawers = vrcDrawers;
        }

        /// <summary>Forgets where a Shift-range selection would start from.</summary>
        public void ResetAnchor() => _behaviourRangeAnchor = -1;

        public void DrawBehaviours(AnimatorState state)
        {
            EditorGUILayout.Space(4);
            var behaviours = state.behaviours;
            PruneBehaviourSelection(behaviours);
            HandleBehaviourShortcuts(state, behaviours);

            bool hasSelection = _selectedBehaviours.Count > 0;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(hasSelection
                    ? L.Tr("Behaviours") + " (" + _selectedBehaviours.Count + "/" + behaviours.Length + ")"
                    : L.Tr("Behaviours") + " (" + behaviours.Length + ")",
                EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(behaviours.Length == 0))
                if (GUILayout.Button(new GUIContent(L.Tr("Copy"), hasSelection
                        ? L.Tr("Copy the selected behaviours; paste from a state's right-click menu or here.")
                        : L.Tr("Copy every behaviour on this state; paste from a state's right-click menu or here.")),
                        EditorStyles.miniButton, GUILayout.Width(50)))
                    CopyBehaviours(behaviours);
            using (new EditorGUI.DisabledScope(VrcBehaviours.ClipboardCount == 0))
                if (GUILayout.Button(new GUIContent(L.Tr("Paste"),
                        L.Tr("Append the copied behaviours to this state.")),
                        EditorStyles.miniButton, GUILayout.Width(50)))
                {
                    PasteBehaviours(state);
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();
            if (behaviours.Length > 0)
                EditorGUILayout.LabelField(
                    L.Tr("Click a title to select (Ctrl / Shift for multi-select); Ctrl+C / Ctrl+V copies and pastes the selected behaviours."),
                    EditorStyles.miniLabel);

            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour == null) continue;

                BeginBehaviourBox(behaviour, behaviours, i);

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
                    if (GUILayout.Button("↑", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    { VrcBehaviours.Move(state, i, -1); GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(i == behaviours.Length - 1))
                    if (GUILayout.Button("↓", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    { VrcBehaviours.Move(state, i, +1); GUIUtility.ExitGUI(); }
                if (GUILayout.Button(L.Tr("Remove"), EditorStyles.miniButton, GUILayout.Width(60)))
                {
                    _selectedBehaviours.Remove(behaviour);
                    _behaviourRangeAnchor = -1;
                    VrcBehaviourDrawers.RemoveBehaviour(state, behaviour);
                    _context.NotifyGraphVisualsChanged(state);   // B badge updates immediately
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
                _vrcDrawers.DrawBehaviourBody(behaviour);
                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button(L.Tr("+ Add Behaviour")))
                _vrcDrawers.ShowAddBehaviourMenu(state);
        }

        /// <summary>
        /// Opens one behaviour's box and draws its title row, the same way in the single- and
        /// multi-state lists: box and title are tinted while the row is selected, and the title
        /// doubles as the button that selects it. The caller closes both the horizontal row and
        /// the vertical box it leaves open.
        /// </summary>
        public void BeginBehaviourBox(StateMachineBehaviour behaviour, StateMachineBehaviour[] rows, int index)
        {
            bool selected = _selectedBehaviours.Contains(behaviour);

            var boxBackground = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = DaerDColors.SelectedRow;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUI.backgroundColor = boxBackground;

            EditorGUILayout.BeginHorizontal();
            var titleBackground = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = DaerDColors.SelectedRow;
            if (GUILayout.Button(BehaviourTitle(behaviour), BehaviourTitleStyle))
                HandleBehaviourRowClick(rows, index);
            GUI.backgroundColor = titleBackground;
        }

        static string BehaviourTitle(StateMachineBehaviour behaviour)
        {
            string typeName = behaviour.GetType().Name;
            // The VRC prefix is noise inside an already VRC-labeled box; keep titles short.
            return typeName.StartsWith("VRC") ? typeName.Substring(3) : typeName;
        }

        // ---- behaviour selection ---------------------------------------------

        static GUIStyle s_behaviourTitleStyle;

        /// <summary>Header of a behaviour box: reads as a title, behaves as a selectable row.</summary>
        static GUIStyle BehaviourTitleStyle => s_behaviourTitleStyle ??= new GUIStyle(EditorStyles.miniButton)
        {
            alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold,
        };

        /// <summary>Drops entries the state no longer holds (removed, moved to another state,
        /// destroyed by an undo) so the selection can't outlive what it points at.</summary>
        public void PruneBehaviourSelection(StateMachineBehaviour[] behaviours)
        {
            _selectedBehaviours.RemoveAll(b => b == null || Array.IndexOf(behaviours, b) < 0);
            if (_selectedBehaviours.Count == 0)
                _behaviourRangeAnchor = -1;
        }

        /// <summary>Plain click selects one row; Ctrl/Cmd toggles; Shift extends from the anchor.
        /// Clicking the already-single-selected row clears the selection, which hands Ctrl+C/V
        /// back to the state-level copy/paste.</summary>
        public void HandleBehaviourRowClick(StateMachineBehaviour[] behaviours, int index)
        {
            var behaviour = behaviours[index];
            var e = Event.current;
            bool additive = e != null && (e.control || e.command);
            bool range = e != null && e.shift;

            if (range && _behaviourRangeAnchor >= 0 && _behaviourRangeAnchor < behaviours.Length)
            {
                _selectedBehaviours.Clear();
                int lo = Mathf.Min(_behaviourRangeAnchor, index);
                int hi = Mathf.Max(_behaviourRangeAnchor, index);
                for (int i = lo; i <= hi; i++)
                    if (behaviours[i] != null)
                        _selectedBehaviours.Add(behaviours[i]);
            }
            else if (additive)
            {
                if (_selectedBehaviours.Contains(behaviour)) _selectedBehaviours.Remove(behaviour);
                else _selectedBehaviours.Add(behaviour);
                _behaviourRangeAnchor = index;
            }
            else if (_selectedBehaviours.Count == 1 && _selectedBehaviours[0] == behaviour)
            {
                _selectedBehaviours.Clear();
                _behaviourRangeAnchor = -1;
            }
            else
            {
                _selectedBehaviours.Clear();
                _selectedBehaviours.Add(behaviour);
                _behaviourRangeAnchor = index;
            }
        }

        /// <summary>
        /// Ctrl+C / Ctrl+V on the behaviour list. Only fires while at least one behaviour row is
        /// selected — otherwise the keys stay with the graph view's state copy/paste.
        /// </summary>
        void HandleBehaviourShortcuts(AnimatorState state, StateMachineBehaviour[] behaviours)
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown || !(e.control || e.command))
                return;
            if (_selectedBehaviours.Count == 0) return;
            // A behaviour field being typed in (driver parameter name, instance name) owns the
            // keys — Ctrl+C there means "copy the text", not "copy the behaviour".
            if (EditorGUIUtility.editingTextField) return;

            if (e.keyCode == KeyCode.C)
            {
                CopyBehaviours(behaviours);
                e.Use();
            }
            else if (e.keyCode == KeyCode.V && VrcBehaviours.ClipboardCount > 0)
            {
                PasteBehaviours(state);
                e.Use();
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>Copies the selected behaviours, or every behaviour when nothing is selected.</summary>
        public void CopyBehaviours(StateMachineBehaviour[] behaviours)
        {
            VrcBehaviours.Copy(_selectedBehaviours.Count > 0
                ? (IEnumerable<StateMachineBehaviour>)_selectedBehaviours
                : behaviours);
        }

        /// <summary>Appends the clipboard to the state and selects what was pasted, so a
        /// follow-up Ctrl+C / Remove acts on the new rows.</summary>
        void PasteBehaviours(AnimatorState state)
        {
            int before = state.behaviours.Length;
            VrcBehaviours.Paste(state, replace: false);
            var after = state.behaviours;
            _selectedBehaviours.Clear();
            for (int i = before; i < after.Length; i++)
                if (after[i] != null)
                    _selectedBehaviours.Add(after[i]);
            _behaviourRangeAnchor = _selectedBehaviours.Count > 0 ? before : -1;
            _context.NotifyGraphVisualsChanged(state);   // B badge updates immediately
        }
    }
}
