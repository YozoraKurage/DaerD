using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Wizard for <see cref="ToggleBuilder"/>: pick target GameObjects (or type their
    /// hierarchy paths), a driving parameter and a wiring style, and generate the ON/OFF
    /// clips plus the layer or Direct-blend-tree machinery. The parameter name follows the
    /// toggle name until the user edits it by hand.
    /// </summary>
    class ToggleBuilderWindow : EditorWindow
    {
        class ShapeRow
        {
            public string name;
            public float off;
            public float on = 100f;
        }

        class Row
        {
            public string path = string.Empty;
            public bool activeWhenOn = true;
            /// <summary>Scene object the row was created from; null for hand-typed paths.
            /// Component chips need it to know what exists on the target.</summary>
            public GameObject source;
            public bool toggleActive = true;
            public bool renderer;
            public bool particle;
            public bool audio;
            public bool light;
            public bool physBone;
            public bool shapesExpanded;
            public readonly List<ShapeRow> shapes = new List<ShapeRow>();
        }

        /// <summary>VRCPhysBone type when the VRChat SDK is present; resolved once per window.</summary>
        Type _physBoneType;
        bool _physBoneTypeResolved;

        Type PhysBoneType
        {
            get
            {
                if (!_physBoneTypeResolved)
                {
                    _physBoneType = ToggleBuilder.FindComponentType("VRCPhysBone");
                    _physBoneTypeResolved = true;
                }
                return _physBoneType;
            }
        }

        AnimatorController _controller;
        Action<int> _onApplied;

        string _toggleName = "Toggle";
        string _parameter = "Toggle";
        ToggleBuilder.Mode _mode = ToggleBuilder.Mode.Layer;
        bool _defaultOn;
        GameObject _root;
        readonly List<Row> _rows = new List<Row>();
        Vector2 _scroll;
        // 0 = create a new layer; 1.. = _layerCandidates[index - 1]. DBT mode only.
        int _layerChoice;
        string _newLayerName = "DBT";
        readonly List<int> _layerCandidates = new List<int>();

        /// <summary>onApplied receives the layer index the toggle landed in.</summary>
        public static void Open(AnimatorController controller, Action<int> onApplied)
        {
            var window = CreateInstance<ToggleBuilderWindow>();
            window.titleContent = new GUIContent(L.Tr("Object Toggle"));
            window.minSize = new Vector2(440, 380);
            window._controller = controller;
            window._onApplied = onApplied;
            window.RefreshChoices();
            window.ShowUtility();
        }

        void RefreshChoices()
        {
            _root = FindAnimatorRoot();

            _layerCandidates.Clear();
            if (_controller != null)
            {
                var layers = _controller.layers;
                for (int i = 0; i < layers.Length; i++)
                    if (DbtBuilder.CanHostGadget(layers[i]))
                        _layerCandidates.Add(i);
            }
            _layerChoice = _layerCandidates.Count > 0 ? 1 : 0;
        }

        /// <summary>Best-guess path root: a scene Animator running this controller.</summary>
        GameObject FindAnimatorRoot()
        {
            if (_controller == null) return null;
            foreach (var animator in UnityEngine.Object.FindObjectsOfType<Animator>(true))
                if (animator.runtimeAnimatorController == _controller)
                    return animator.gameObject;
            return null;
        }

        static readonly string[] ModeLabels = { "New Layer (Bool)", "Direct Blend Tree (Float)" };

        void OnGUI()
        {
            // Utility windows outlive domain reloads but their fields don't; bail out.
            if (_controller == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Object Toggle"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                L.Tr("Creates ON/OFF clips that toggle the listed GameObjects and wires them to a parameter. The clips are saved next to the controller asset."),
                MessageType.Info);

            _toggleName = DrawToggleName();
            _mode = (ToggleBuilder.Mode)EditorGUILayout.Popup(L.Tr("Wiring"), (int)_mode, ModeLabels);
            EditorGUILayout.HelpBox(_mode == ToggleBuilder.Mode.Layer
                    ? L.Tr("Adds a layer with OFF/ON states and instant transitions driven by a Bool parameter.")
                    : L.Tr("Adds a 1D tree (0 = OFF, 1 = ON) driven by a Float parameter to a Direct blend tree layer — many toggles can share one layer."),
                MessageType.None);
            _parameter = EditorGUILayout.TextField(L.Tr("Parameter"), _parameter);
            DrawParameterNote();
            _defaultOn = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Default ON"),
                    L.Tr("Stored as the parameter's default value; the layer also starts on the ON state.")),
                _defaultOn);

            if (_mode == ToggleBuilder.Mode.DirectBlendTree)
                DrawLayerChoice();

            EditorGUILayout.Space(6);
            DrawTargets();

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            if (GUILayout.Button(L.Tr("Create"), GUILayout.Width(DaerDLayout.DialogButton)))
                TryApply();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The parameter tracks the toggle name until it was edited to something else.</summary>
        string DrawToggleName()
        {
            string name = EditorGUILayout.TextField(L.Tr("Toggle Name"), _toggleName);
            if (name != _toggleName && _parameter == _toggleName)
                _parameter = name;
            return name;
        }

        void DrawParameterNote()
        {
            var existing = DbtBuilder.FindParameter(_controller, _parameter);
            if (existing == null) return;
            var wanted = _mode == ToggleBuilder.Mode.Layer
                ? AnimatorControllerParameterType.Bool
                : AnimatorControllerParameterType.Float;
            EditorGUILayout.HelpBox(existing.type == wanted
                    ? L.Tr("Uses the existing '{0}' parameter.", _parameter)
                    : L.Tr("Parameter '{0}' exists but is a {1} — pick another name or wiring.", _parameter, existing.type),
                existing.type == wanted ? MessageType.None : MessageType.Warning);
        }

        void DrawLayerChoice()
        {
            var labels = new string[_layerCandidates.Count + 1];
            labels[0] = L.Tr("Create new layer");
            var layers = _controller.layers;
            for (int i = 0; i < _layerCandidates.Count; i++)
            {
                int index = _layerCandidates[i];
                labels[i + 1] = index < layers.Length ? layers[index].name : "?";
            }
            _layerChoice = EditorGUILayout.Popup(L.Tr("Target Layer"), Mathf.Clamp(_layerChoice, 0, labels.Length - 1), labels);
            if (_layerChoice == 0)
                _newLayerName = EditorGUILayout.TextField(L.Tr("New Layer Name"), _newLayerName);
        }

        void DrawTargets()
        {
            EditorGUILayout.LabelField(L.Tr("Target Objects"), EditorStyles.boldLabel);
            _root = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent(L.Tr("Path Root"),
                    L.Tr("The GameObject holding the Animator; dropped objects get their path relative to it.")),
                _root, typeof(GameObject), true);

            EditorGUILayout.BeginHorizontal();
            // Action slot: dropping / picking an object appends a row — the field always
            // displays None, it is not a stored value.
            var dropped = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent(L.Tr("Add Object"),
                    L.Tr("Drop a scene GameObject to add it as a target.")),
                null, typeof(GameObject), true);
            if (dropped != null)
                AddTarget(dropped);
            if (GUILayout.Button(new GUIContent(L.Tr("Add Selection"),
                    L.Tr("Add every GameObject selected in the Hierarchy.")), GUILayout.Width(DaerDLayout.DialogButton)))
                foreach (var picked in Selection.gameObjects)
                    AddTarget(picked);
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90));
            int remove = -1;
            for (int i = 0; i < _rows.Count; i++)
                if (!DrawRow(_rows[i]))
                    remove = i;
            if (remove >= 0)
                _rows.RemoveAt(remove);
            if (_rows.Count == 0)
                EditorGUILayout.HelpBox(
                    L.Tr("No targets yet. Drop GameObjects above or type hierarchy paths (relative to the Animator root)."),
                    MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>One target row: path line, then component chips for scene-backed rows.
        /// Returns false when the row's remove button was pressed.</summary>
        bool DrawRow(Row row)
        {
            EditorGUILayout.BeginHorizontal();
            row.path = EditorGUILayout.TextField(row.path);
            row.activeWhenOn = GUILayout.Toggle(row.activeWhenOn,
                new GUIContent(L.Tr("Active"),
                    L.Tr("Checked: the object is active while the toggle is ON. Unchecked inverts it.")),
                GUILayout.Width(60));
            bool removed = GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton));
            EditorGUILayout.EndHorizontal();
            if (removed) return false;

            if (row.source == null) return true;   // hand-typed path: m_IsActive only

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            row.toggleActive = GUILayout.Toggle(row.toggleActive,
                new GUIContent(L.Tr("Object"), L.Tr("Animate GameObject.m_IsActive")),
                EditorStyles.miniButton);
            DrawChip(ref row.renderer, "Renderer", row.source.GetComponent<Renderer>() != null,
                L.Tr("Animate the Renderer's enabled flag"));
            DrawChip(ref row.particle, "Particle", row.source.GetComponent<ParticleSystem>() != null,
                L.Tr("Animate the ParticleSystem's enabled flag"));
            DrawChip(ref row.audio, "Audio", row.source.GetComponent<AudioSource>() != null,
                L.Tr("Animate the AudioSource's enabled flag"));
            DrawChip(ref row.light, "Light", row.source.GetComponent<Light>() != null,
                L.Tr("Animate the Light's enabled flag"));
            if (PhysBoneType != null)
                DrawChip(ref row.physBone, "PhysBone", row.source.GetComponent(PhysBoneType) != null,
                    L.Tr("Animate the VRCPhysBone's enabled flag"));

            var skinned = row.source.GetComponent<SkinnedMeshRenderer>();
            bool hasShapes = skinned != null && skinned.sharedMesh != null
                && skinned.sharedMesh.blendShapeCount > 0;
            if (hasShapes)
                row.shapesExpanded = GUILayout.Toggle(row.shapesExpanded,
                    new GUIContent(L.Tr("BlendShapes"), L.Tr("Animate blendshape weights (OFF/ON values per shape)")),
                    EditorStyles.miniButton);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (hasShapes && row.shapesExpanded)
                DrawShapeRows(row, skinned);
            return true;
        }

        static void DrawChip(ref bool value, string label, bool available, string tooltip)
        {
            if (!available)
            {
                value = false;
                return;
            }
            value = GUILayout.Toggle(value, new GUIContent(label, tooltip), EditorStyles.miniButton);
        }

        void DrawShapeRows(Row row, SkinnedMeshRenderer skinned)
        {
            int removeShape = -1;
            for (int i = 0; i < row.shapes.Count; i++)
            {
                var shape = row.shapes[i];
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(24);
                EditorGUILayout.LabelField(shape.name, GUILayout.MinWidth(60));
                float saved = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 26f;
                shape.off = EditorGUILayout.FloatField(L.Tr("Off"), shape.off, GUILayout.Width(70));
                shape.on = EditorGUILayout.FloatField(L.Tr("On"), shape.on, GUILayout.Width(70));
                EditorGUIUtility.labelWidth = saved;
                if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    removeShape = i;
                EditorGUILayout.EndHorizontal();
            }
            if (removeShape >= 0)
                row.shapes.RemoveAt(removeShape);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(24);
            if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                ShowAddShapeMenu(row, skinned);
            EditorGUILayout.EndHorizontal();
        }

        void ShowAddShapeMenu(Row row, SkinnedMeshRenderer skinned)
        {
            var taken = new HashSet<string>();
            foreach (var shape in row.shapes) taken.Add(shape.name);
            var menu = new GenericMenu();
            var mesh = skinned.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                if (taken.Contains(name))
                {
                    menu.AddDisabledItem(new GUIContent(name));
                    continue;
                }
                var captured = name;
                menu.AddItem(new GUIContent(name), false,
                    () => row.shapes.Add(new ShapeRow { name = captured }));
            }
            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent(L.Tr("No blendshapes")));
            menu.ShowAsContext();
        }

        void AddTarget(GameObject picked)
        {
            if (picked == null) return;
            string path = PathFor(picked);
            if (path != null && path.Length == 0)
            {
                EditorUtility.DisplayDialog(L.Tr("Object Toggle"),
                    L.Tr("The path root itself can't be toggled — animations can't re-enable the object that hosts the Animator."), "OK");
                return;
            }
            if (path == null)
            {
                EditorUtility.DisplayDialog(L.Tr("Object Toggle"),
                    L.Tr("'{0}' is not a child of the path root '{1}'.", picked.name, _root.name), "OK");
                return;
            }
            foreach (var row in _rows)
                if (row.path == path)
                    return;   // already listed
            _rows.Add(new Row { path = path, source = picked });
        }

        /// <summary>Hierarchy path relative to the root, or null when the object is outside it.
        /// With no root set, the dropped object's own root is assumed.</summary>
        string PathFor(GameObject picked)
        {
            var root = _root != null ? _root.transform : picked.transform.root;
            if (!picked.transform.IsChildOf(root)) return null;
            return AnimationUtility.CalculateTransformPath(picked.transform, root);
        }

        /// <summary>Chips become concrete-type m_Enabled bindings (curves must bind the actual
        /// component type, e.g. SkinnedMeshRenderer rather than Renderer).</summary>
        ToggleBuilder.Target BuildTarget(Row row)
        {
            var target = new ToggleBuilder.Target
            {
                path = row.path,
                activeWhenOn = row.activeWhenOn,
                toggleActive = row.source == null || row.toggleActive,
            };
            if (row.source == null) return target;

            if (row.renderer && row.source.GetComponent<Renderer>() is Renderer renderer)
                target.bindings.Add(ToggleBuilder.Binding.Enabled(renderer.GetType()));
            if (row.particle && row.source.GetComponent<ParticleSystem>() != null)
                target.bindings.Add(ToggleBuilder.Binding.Enabled(typeof(ParticleSystem)));
            if (row.audio && row.source.GetComponent<AudioSource>() != null)
                target.bindings.Add(ToggleBuilder.Binding.Enabled(typeof(AudioSource)));
            if (row.light && row.source.GetComponent<Light>() != null)
                target.bindings.Add(ToggleBuilder.Binding.Enabled(typeof(Light)));
            if (row.physBone && PhysBoneType != null && row.source.GetComponent(PhysBoneType) != null)
                target.bindings.Add(ToggleBuilder.Binding.Enabled(PhysBoneType));
            foreach (var shape in row.shapes)
                target.bindings.Add(ToggleBuilder.Binding.BlendShape(shape.name, shape.off, shape.on));
            return target;
        }

        void TryApply()
        {
            var request = new ToggleBuilder.Request
            {
                controller = _controller,
                mode = _mode,
                toggleName = _toggleName != null ? _toggleName.Trim() : string.Empty,
                parameter = _parameter != null ? _parameter.Trim() : string.Empty,
                defaultOn = _defaultOn,
                layerIndex = _mode == ToggleBuilder.Mode.DirectBlendTree
                    && _layerChoice > 0 && _layerChoice - 1 < _layerCandidates.Count
                    ? _layerCandidates[_layerChoice - 1] : -1,
                newLayerName = _newLayerName != null ? _newLayerName.Trim() : string.Empty,
            };
            foreach (var row in _rows)
                request.targets.Add(BuildTarget(row));

            var error = ToggleBuilder.Validate(request);
            if (error != null)
            {
                EditorUtility.DisplayDialog(L.Tr("Object Toggle"), error, "OK");
                return;
            }
            ToggleBuilder.Apply(request);
            // New layers (both modes) land at the end; only a DBT toggle added to an
            // existing layer stays where that layer already was.
            _onApplied?.Invoke(request.layerIndex >= 0
                ? request.layerIndex : _controller.layers.Length - 1);
            Close();
        }
    }
}
