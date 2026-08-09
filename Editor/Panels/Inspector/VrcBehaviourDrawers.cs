using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Drawers for the VRChat SDK's state machine behaviours, plus the generic fallback and the
    /// add / remove plumbing both behaviour inspectors share.
    /// </summary>
    class VrcBehaviourDrawers
    {
        // VRC Parameter Driver: remembered "selected row" per behaviour so the Add/Up/Down/Delete
        // buttons know which entry they act on. Keyed by StateMachineBehaviour instance ID; stale
        // entries are harmless (Unity domain reload clears them) so we don't bother pruning.
        readonly Dictionary<int, int> _vrcDriverSelectedIndex = new Dictionary<int, int>();

        readonly DaerDContext _context;
        readonly GraphSync _sync;
        // Repaints the inspector once the add menu's callback runs, long after the frame that
        // opened it.
        readonly System.Action _refresh;

        public VrcBehaviourDrawers(DaerDContext context, GraphSync sync, System.Action refresh)
        {
            _context = context;
            _sync = sync;
            _refresh = refresh;
        }

        /// <summary>Draws a behaviour's fields: its own VRC drawer when there is one, the generic
        /// renderer otherwise.</summary>
        public void DrawBehaviourBody(StateMachineBehaviour behaviour)
        {
            if (!TryDrawKnownVrcBehaviour(behaviour))
                DrawSerializedFields(behaviour);
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
            PropertyRow(so, "debugString", L.Tr("Debug String"));
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
            PropertyRow(so, "delayTime", L.Tr("Delay Time"));
            PropertyRow(so, "debugString", L.Tr("Debug String"));
            so.ApplyModifiedProperties();
        }

        void DrawVrcLayerControl(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            PropertyRow(so, "playable", L.Tr("Playable"));
            PropertyRow(so, "layer", L.Tr("Layer"));
            var goal = so.FindProperty("goalWeight");
            if (goal != null)
                goal.floatValue = EditorGUILayout.Slider(L.Tr("Goal Weight"), goal.floatValue, 0f, 1f);
            PropertyRow(so, "blendDuration", L.Tr("Blend Duration"));
            PropertyRow(so, "debugString", L.Tr("Debug String"));
            so.ApplyModifiedProperties();
        }

        void DrawVrcPlayableLayerControl(StateMachineBehaviour behaviour)
        {
            var so = new SerializedObject(behaviour);
            so.Update();
            PropertyRow(so, "layer", L.Tr("Layer"));
            var goal = so.FindProperty("goalWeight");
            if (goal != null)
                goal.floatValue = EditorGUILayout.Slider(L.Tr("Goal Weight"), goal.floatValue, 0f, 1f);
            PropertyRow(so, "blendDuration", L.Tr("Blend Duration"));
            PropertyRow(so, "debugString", L.Tr("Debug String"));
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
                EditorGUILayout.PropertyField(sourcePath, new GUIContent(L.Tr("Source Path")));
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
        // matching serialized property name on VRCAnimatorTrackingControl. The labels are the
        // English source strings (translated where they are drawn, not here — a static table
        // would freeze whatever language was current at domain load).
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

            EditorGUILayout.LabelField(L.Tr("Tracking Control"), EditorStyles.miniBoldLabel);

            // Column headers use the same subdivision as the rows so they stay aligned when
            // the inspector is resized.
            var headerRect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight,
                GUILayout.ExpandWidth(true));
            var headerCols = SubdivideTrackingRow(headerRect);
            for (int i = 0; i < VrcTrackingColumns.Length; i++)
                GUI.Label(headerCols[i + 1], L.Tr(VrcTrackingColumns[i]), TrackingColumnHeaderStyle);

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
            int allPicked = DrawVrcTrackingRow(L.Tr("All"), commonKnown ? commonValue : -1);
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
                int picked = DrawVrcTrackingRow(L.Tr(label), prop.intValue);
                if (picked >= 0 && picked != prop.intValue)
                    prop.intValue = picked;
            }

            var debug = so.FindProperty("debugString");
            if (debug != null)
            {
                EditorGUILayout.PropertyField(debug, new GUIContent(L.Tr("Debug String")));
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

            EditorGUILayout.HelpBox(L.Tr(VrcParameterDriverInfo), MessageType.Info);

            var localOnly = so.FindProperty("localOnly");
            if (localOnly != null)
                EditorGUILayout.PropertyField(localOnly, new GUIContent(L.Tr("Local Only")));

            var debugString = so.FindProperty("debugString");
            if (debugString != null)
                EditorGUILayout.PropertyField(debugString, new GUIContent(L.Tr("Debug String")));

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
            if (GUILayout.Button(L.Tr("Add")))
            {
                parameters.arraySize++;
                selected = parameters.arraySize - 1;
            }
            using (new EditorGUI.DisabledScope(selected <= 0))
            {
                if (GUILayout.Button(L.Tr("Up")))
                {
                    parameters.MoveArrayElement(selected, selected - 1);
                    selected--;
                }
            }
            using (new EditorGUI.DisabledScope(selected < 0 || selected >= parameters.arraySize - 1))
            {
                if (GUILayout.Button(L.Tr("Down")))
                {
                    parameters.MoveArrayElement(selected, selected + 1);
                    selected++;
                }
            }
            using (new EditorGUI.DisabledScope(selected < 0))
            {
                if (GUILayout.Button(L.Tr("Delete")))
                {
                    parameters.DeleteArrayElementAtIndex(selected);
                    if (parameters.arraySize == 0) selected = -1;
                    else selected = Mathf.Clamp(selected, 0, parameters.arraySize - 1);
                }
            }
            EditorGUILayout.EndHorizontal();

            var controllerParameters = _context.Controller?.parameters;
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
            EditorGUI.LabelField(headerRect, L.Tr("Parameter {0}", index), EditorStyles.miniBoldLabel);
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
                EditorGUILayout.PropertyField(typeProp, new GUIContent(L.Tr("Type")));

            int type = typeProp != null ? typeProp.intValue : 0;

            // For Set/Add/Random the destination is `name`; for Copy VRCSDK reuses `name` as the
            // Copy destination while `source` holds the parameter being read.
            if (type == 3 && sourceProp != null)
                EditorGUILayout.PropertyField(sourceProp, new GUIContent(L.Tr("Source")));
            if (nameProp != null)
            {
                EditorGUILayout.PropertyField(nameProp, new GUIContent(L.Tr("Destination")));
                if (controllerParameters != null
                    && !string.IsNullOrEmpty(nameProp.stringValue)
                    && !ControllerHasParameter(controllerParameters, nameProp.stringValue))
                {
                    EditorGUILayout.HelpBox(
                        L.Tr("Parameter '{0}' not found. Make sure you defined it in the Animator window's Parameter list.",
                            nameProp.stringValue),
                        MessageType.Warning);
                }
            }

            switch (type)
            {
                case 0: // Set
                case 1: // Add
                    if (valueProp != null)
                        EditorGUILayout.PropertyField(valueProp, new GUIContent(L.Tr("Value")));
                    break;
                case 2: // Random
                    if (valueMinProp != null)
                        EditorGUILayout.PropertyField(valueMinProp, new GUIContent(L.Tr("Value Min")));
                    if (valueMaxProp != null)
                        EditorGUILayout.PropertyField(valueMaxProp, new GUIContent(L.Tr("Value Max")));
                    if (chanceProp != null)
                        EditorGUILayout.PropertyField(chanceProp, new GUIContent(L.Tr("Chance")));
                    break;
                case 3: // Copy
                    if (convertRangeProp != null)
                    {
                        EditorGUILayout.PropertyField(convertRangeProp, new GUIContent(L.Tr("Convert Range")));
                        if (convertRangeProp.boolValue)
                        {
                            DrawMinMaxRow(L.Tr("Source"), sourceMinProp, sourceMaxProp);
                            DrawMinMaxRow(L.Tr("Destination"), destMinProp, destMaxProp);
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
            if (minProp != null) EditorGUILayout.PropertyField(minProp, new GUIContent(L.Tr("Min")));
            if (maxProp != null) EditorGUILayout.PropertyField(maxProp, new GUIContent(L.Tr("Max")));
            EditorGUIUtility.labelWidth = saved;
            EditorGUILayout.EndHorizontal();
        }

        public void ShowAddBehaviourMenu(AnimatorState state) =>
            ShowAddBehaviourMenu(new List<AnimatorState> { state });

        /// <summary>Add menu for one or many states; picking a type adds it to every target
        /// in a single undo step.</summary>
        public void ShowAddBehaviourMenu(List<AnimatorState> states)
        {
            // The menu callback runs long after this frame — snapshot the targets so a
            // rebuilt selection list can't change what gets added.
            var targets = new List<AnimatorState>();
            foreach (var s in states)
                if (s != null) targets.Add(s);
            if (targets.Count == 0) return;

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
                // Grayed out only when there is nothing left to add — a singleton missing from
                // even one target is still worth offering (it lands on that target alone).
                if (VrcBehaviours.IsSingleton(typeName) && AllStatesHave(targets, typeName))
                    menu.AddDisabledItem(label);
                else
                    menu.AddItem(label, false, () =>
                    {
                        using (new UndoScope("Add Behaviour"))
                            foreach (var s in targets)
                            {
                                if (s == null) continue;
                                if (VrcBehaviours.IsSingleton(captured) && VrcBehaviours.Has(s, captured))
                                    continue;
                                VrcBehaviours.Add(s, captured);
                                _sync.RefreshStateNode(s);   // B badge updates immediately
                            }
                        _refresh();
                    });
            }
            if (anyVrc)
                menu.AddSeparator(string.Empty);

            foreach (var type in TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>())
            {
                if (type.IsAbstract) continue;
                if (anyVrc && VrcBehaviours.IsVrcType(type.Name)) continue;   // already listed above
                var captured = type;
                var label = anyVrc ? L.Tr("Other") + "/" + type.Name : type.Name;
                menu.AddItem(new GUIContent(label), false, () =>
                {
                    using (new UndoScope("Add Behaviour"))
                        foreach (var s in targets)
                        {
                            if (s == null) continue;
                            Undo.RegisterCompleteObjectUndo(s, "Add Behaviour");
                            s.AddStateMachineBehaviour(captured);
                            EditorUtility.SetDirty(s);
                            _sync.RefreshStateNode(s);   // B badge updates immediately
                        }
                    _refresh();
                });
            }
            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent(L.Tr("No StateMachineBehaviour types found")));
            menu.ShowAsContext();
        }

        static bool AllStatesHave(List<AnimatorState> states, string typeName)
        {
            foreach (var s in states)
                if (s != null && !VrcBehaviours.Has(s, typeName)) return false;
            return true;
        }

        public static void RemoveBehaviour(AnimatorState state, StateMachineBehaviour behaviour)
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
    }
}
