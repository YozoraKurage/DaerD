using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    // ---- behaviours across several states --------------------------------

    /// <summary>Bulk behaviour editing for the states selected in the graph.</summary>
    class MultiStateBehaviourInspector
    {
        readonly GraphSync _sync;
        readonly List<StateMachineBehaviour> _selectedBehaviours;
        readonly BehaviourInspector _behaviours;
        readonly VrcBehaviourDrawers _vrcDrawers;

        public MultiStateBehaviourInspector(GraphSync sync, List<StateMachineBehaviour> selectedBehaviours,
            BehaviourInspector behaviours, VrcBehaviourDrawers vrcDrawers)
        {
            _sync = sync;
            _selectedBehaviours = selectedBehaviours;
            _behaviours = behaviours;
            _vrcDrawers = vrcDrawers;
        }

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
        public void DrawMultiStateBehaviours(List<AnimatorState> states)
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
            int missing = states.Count - slot.instances.Count;

            _behaviours.BeginBehaviourBox(representative, representatives, index);

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
            _vrcDrawers.DrawBehaviourBody(representative);
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
                    _sync.RefreshStateNode(state);   // B badge updates immediately
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
                    _sync.RefreshStateNode(owner);   // B badge updates immediately
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
                    _sync.RefreshStateNode(state);   // B badge updates immediately
                }
            // The pasted rows regroup into new slots on the next repaint; the old selection
            // would point at whatever happened to sit at those indices.
            _selectedBehaviours.Clear();
            _behaviours.ResetAnchor();
        }
    }
}
