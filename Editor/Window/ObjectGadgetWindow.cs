using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The editor for one object gadget: which objects inside the linked gimmick prefab it
    /// animates, what it animates about them, and how it is wired.
    ///
    /// <para>THE PREFAB IS THE ONLY PLACE TARGETS COME FROM.</para>
    /// Its predecessor let a person type hierarchy paths against whatever scene happened to be
    /// open, which is how a toggle came to be unrecordable: a scene path describes nothing a
    /// gimmick can be rebuilt from. Here the pinned merge is the frame of reference, the picker
    /// offers exactly the objects under it, and what is stored is the reference (ADR 0044). A
    /// pin that is not healthy leaves the window with nothing to offer, so it says why and stops
    /// rather than falling back to something it could half do.
    ///
    /// <para>WHY A GENERIC MENU FOR THE PICKER.</para>
    /// A gimmick prefab is small and its shape is the thing being read, so a menu that mirrors
    /// the hierarchy is the picker: the derived path already contains '/', and GenericMenu reads
    /// that as "open a submenu", which is for once exactly right. (The prefab link's candidate
    /// menu replaces its slashes for the opposite reason — there the '/' is inside an asset path
    /// that has to be read as one string.)
    /// </summary>
    class ObjectGadgetWindow : EditorWindow
    {
        /// <summary>One target as the form holds it. The bindings are the saved records
        /// themselves rather than a set of checkboxes, so a binding this window has no chip for
        /// — a PhysBone in a project without the SDK, or one a later version added — is carried
        /// through an edit instead of being quietly dropped on the way out.</summary>
        class Row
        {
            public GameObject target;
            public bool activeWhenOn = true;
            public bool toggleActive = true;
            public bool shapesExpanded;
            public readonly List<GraphFrameData.BindingRecord> bindings =
                new List<GraphFrameData.BindingRecord>();
        }

        AnimatorController _controller;
        Action _onApplied;

        string _name = "Toggle";
        string _parameter = "Toggle";
        ToggleBuilder.Mode _mode = ToggleBuilder.Mode.Layer;
        bool _defaultOn;
        bool _declare = true;
        readonly List<Row> _rows = new List<Row>();
        Vector2 _scroll;
        // 0 = create a new layer; 1.. = _layerCandidates[index - 1]. DBT wiring only.
        int _layerChoice;
        readonly List<int> _layerCandidates = new List<int>();
        /// <summary>The saved record being edited, or null for a new gadget. What makes applying
        /// a regenerate: its own pieces are swept first and its own parameter is not a collision
        /// with itself.</summary>
        GraphFrameData.ObjectGadgetConfig _replaces;

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

        public static void Open(AnimatorController controller, Action onApplied) =>
            Create(controller, onApplied);

        /// <summary>Opens the window already loaded with a saved gadget — the home screen lists
        /// them and edits one from there, so the subject is picked outside the window.</summary>
        public static void Open(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config, Action onApplied)
        {
            var window = Create(controller, onApplied);
            if (config != null) window.LoadConfig(config);
        }

        static ObjectGadgetWindow Create(AnimatorController controller, Action onApplied)
        {
            var window = CreateInstance<ObjectGadgetWindow>();
            window.titleContent = new GUIContent(L.Tr("Object Gadget"));
            window.minSize = new Vector2(440, 380);
            window.Bind(controller, onApplied);
            window.ShowUtility();
            return window;
        }

        /// <summary>
        /// The window's subject, set before it is shown. Internal rather than private because
        /// this and <see cref="LoadConfig"/> / <see cref="BuildConfig"/> are the path an Apply
        /// runs through — a record in, a record out — and that is the part worth a test: a field
        /// the form quietly drops on the way back out becomes a regenerate that forgets a
        /// binding. The drawing needs an IMGUI event loop and is not tested.
        /// </summary>
        internal void Bind(AnimatorController controller, Action onApplied)
        {
            _controller = controller;
            _onApplied = onApplied;
            RefreshChoices();
        }

        void RefreshChoices()
        {
            _layerCandidates.Clear();
            if (_controller == null) return;
            var layers = _controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (DbtBuilder.CanHostGadget(layers[i]))
                    _layerCandidates.Add(i);
            _layerChoice = _layerCandidates.Count > 0 ? 1 : 0;
        }

        /// <summary>Fills the form from a saved record, so Regenerate starts from what was built
        /// rather than from the defaults. A target whose object is gone comes back as an empty
        /// row: dropping it here would let a regenerate quietly forget an object, which is the
        /// one thing a missing reference must not do.</summary>
        internal void LoadConfig(GraphFrameData.ObjectGadgetConfig config)
        {
            _replaces = config;
            _name = config.name;
            _parameter = config.parameter;
            _mode = (ToggleBuilder.Mode)config.mode;
            _defaultOn = config.defaultOn;
            _declare = config.declare;

            _rows.Clear();
            foreach (var record in config.targets)
            {
                if (record == null) continue;
                var row = new Row
                {
                    target = record.target,
                    activeWhenOn = record.activeWhenOn,
                    toggleActive = record.toggleActive,
                };
                if (record.bindings != null)
                    foreach (var binding in record.bindings)
                        if (binding != null)
                            row.bindings.Add(binding);
                _rows.Add(row);
            }

            int candidate = _layerCandidates.IndexOf(LayerIndexOf(config.layer));
            _layerChoice = candidate >= 0 ? candidate + 1 : 0;
        }

        int LayerIndexOf(AnimatorStateMachine machine)
        {
            if (_controller == null || machine == null) return -1;
            var layers = _controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == machine) return i;
            return -1;
        }

        void OnGUI()
        {
            // Utility windows outlive domain reloads but their fields don't; bail out.
            if (_controller == null)
            {
                Close();
                return;
            }

            EditorGUILayout.LabelField(L.Tr("Object Gadget"), EditorStyles.boldLabel);

            var root = ObjectGadgets.Root(_controller);
            if (root == null)
            {
                // The pin is the frame of reference for everything below, so there is nothing
                // to offer half of: say which state the link is in and stop.
                EditorGUILayout.HelpBox(ObjectGadgets.LinkRefusal(_controller)
                    ?? L.Tr("The linked Merge Animator could not be resolved."), MessageType.Warning);
                if (GUILayout.Button(L.Tr("Close"))) Close();
                return;
            }

            EditorGUILayout.HelpBox(
                L.Tr("Animates objects inside the linked prefab. Paths are worked out from '{0}' every time the gadget is generated, so renaming an object in the prefab does not break it.",
                    root.name),
                MessageType.Info);

            _name = DrawGadgetName();
            _mode = (ToggleBuilder.Mode)EditorGUILayout.Popup(L.Tr("Wiring"), (int)_mode,
                ObjectGadgets.ModeLabels);
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
            _declare = EditorGUILayout.Toggle(
                new GUIContent(L.Tr("Declare"),
                    L.Tr("Add the parameter to this controller's parameter store, so the built avatar knows about it. Off for a parameter driven from inside the controller.")),
                _declare);

            if (_mode == ToggleBuilder.Mode.DirectBlendTree)
                DrawLayerChoice();

            EditorGUILayout.Space(6);
            DrawTargets(root);

            string note = ObjectGadgets.Note(_controller, BuildConfig());
            if (note != null)
                EditorGUILayout.HelpBox(note, MessageType.Info);

            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(L.Tr("Cancel"), GUILayout.Width(DaerDLayout.DialogButton)))
                Close();
            if (GUILayout.Button(_replaces != null ? L.Tr("Regenerate") : L.Tr("Create"),
                GUILayout.Width(DaerDLayout.DialogButton)))
                TryApply();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The parameter tracks the gadget name until it was edited to something else.</summary>
        string DrawGadgetName()
        {
            string name = EditorGUILayout.TextField(L.Tr("Gadget Name"), _name);
            if (name != _name && _parameter == _name)
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
            _layerChoice = EditorGUILayout.Popup(L.Tr("Target Layer"),
                Mathf.Clamp(_layerChoice, 0, labels.Length - 1), labels);
        }

        // ---- targets -----------------------------------------------------------

        void DrawTargets(Transform root)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Target Objects"), EditorStyles.boldLabel);
            if (GUILayout.Button(new GUIContent(L.Tr("Add Object"),
                    L.Tr("Pick an object from the linked prefab. Only objects under the merge can be animated.")),
                GUILayout.Width(DaerDLayout.DialogButton)))
                ShowTargetMenu(root);
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90));
            int remove = -1;
            for (int i = 0; i < _rows.Count; i++)
                if (!DrawRow(_rows[i], root))
                    remove = i;
            if (remove >= 0)
                _rows.RemoveAt(remove);
            if (_rows.Count == 0)
                EditorGUILayout.HelpBox(L.Tr("No targets yet. Add objects from the linked prefab."),
                    MessageType.None);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>The hierarchy under the merge as a menu. Objects already listed are shown
        /// disabled rather than left out, so the menu keeps describing the prefab.</summary>
        void ShowTargetMenu(Transform root)
        {
            var taken = new HashSet<GameObject>();
            foreach (var row in _rows)
                if (row.target != null) taken.Add(row.target);

            var menu = new GenericMenu();
            foreach (var candidate in ObjectGadgets.Candidates(root))
            {
                string path = ObjectGadgets.PathOf(root, candidate);
                if (path == null) continue;
                // The merge's own object has no path; naming it after itself is what makes it
                // pickable at all, and toggling it is a legitimate thing to want.
                var label = new GUIContent(path.Length == 0 ? root.name : path);
                if (taken.Contains(candidate))
                {
                    menu.AddDisabledItem(label);
                    continue;
                }
                var captured = candidate;
                menu.AddItem(label, false, () => _rows.Add(new Row { target = captured }));
            }
            menu.ShowAsContext();
        }

        /// <summary>One target row: what it is, then what about it is animated. Returns false
        /// when the row's remove button was pressed.</summary>
        bool DrawRow(Row row, Transform root)
        {
            EditorGUILayout.BeginHorizontal();
            string path = ObjectGadgets.PathOf(root, row.target);
            if (row.target == null)
                EditorGUILayout.LabelField(new GUIContent(L.Tr("(missing object)"),
                    L.Tr("This target is gone from the prefab. Remove the row, or add the object again.")));
            else
                EditorGUILayout.LabelField(new GUIContent(
                    path != null && path.Length > 0 ? path : row.target.name, row.target.name));
            row.activeWhenOn = GUILayout.Toggle(row.activeWhenOn,
                new GUIContent(L.Tr("Active"),
                    L.Tr("Checked: the object is active while the toggle is ON. Unchecked inverts it.")),
                GUILayout.Width(60));
            bool removed = GUILayout.Button("−", EditorStyles.miniButton,
                GUILayout.Width(DaerDLayout.GlyphButton));
            EditorGUILayout.EndHorizontal();
            if (removed) return false;
            if (row.target == null) return true;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            row.toggleActive = GUILayout.Toggle(row.toggleActive,
                new GUIContent(L.Tr("Object"), L.Tr("Animate GameObject.m_IsActive")),
                EditorStyles.miniButton);
            var renderer = row.target.GetComponent<Renderer>();
            if (renderer != null)
                DrawEnabledChip(row, "Renderer", renderer.GetType(),
                    L.Tr("Animate the Renderer's enabled flag"));
            DrawEnabledChip(row, "Particle", typeof(ParticleSystem),
                L.Tr("Animate the ParticleSystem's enabled flag"));
            DrawEnabledChip(row, "Audio", typeof(AudioSource),
                L.Tr("Animate the AudioSource's enabled flag"));
            DrawEnabledChip(row, "Light", typeof(Light),
                L.Tr("Animate the Light's enabled flag"));
            DrawEnabledChip(row, "PhysBone", PhysBoneType,
                L.Tr("Animate the VRCPhysBone's enabled flag"));

            var skinned = row.target.GetComponent<SkinnedMeshRenderer>();
            bool hasShapes = skinned != null && skinned.sharedMesh != null
                && skinned.sharedMesh.blendShapeCount > 0;
            if (hasShapes)
                row.shapesExpanded = GUILayout.Toggle(row.shapesExpanded,
                    new GUIContent(L.Tr("BlendShapes"),
                        L.Tr("Animate blendshape weights (OFF/ON values per shape)")),
                    EditorStyles.miniButton);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (hasShapes && row.shapesExpanded)
                DrawShapeRows(row, skinned);
            DrawOtherBindings(row);
            return true;
        }

        /// <summary>A chip for one component's enabled flag, drawn only where the target has
        /// that component. It reads and writes the binding list directly, so the chip is a view
        /// of the record rather than a second copy of it.</summary>
        void DrawEnabledChip(Row row, string label, Type type, string tooltip)
        {
            if (type == null || row.target.GetComponent(type) == null) return;
            bool was = FindBinding(row, type.Name, "m_Enabled") != null;
            bool now = GUILayout.Toggle(was, new GUIContent(label, tooltip), EditorStyles.miniButton);
            if (now == was) return;
            if (now)
                row.bindings.Add(new GraphFrameData.BindingRecord
                {
                    typeName = type.Name,
                    property = "m_Enabled",
                });
            else
                row.bindings.Remove(FindBinding(row, type.Name, "m_Enabled"));
        }

        static GraphFrameData.BindingRecord FindBinding(Row row, string typeName, string property)
        {
            foreach (var binding in row.bindings)
                if (binding.typeName == typeName && binding.property == property)
                    return binding;
            return null;
        }

        void DrawShapeRows(Row row, SkinnedMeshRenderer skinned)
        {
            GraphFrameData.BindingRecord removeShape = null;
            foreach (var binding in row.bindings)
            {
                if (binding.property == null || !binding.property.StartsWith(ShapePrefix)) continue;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(24);
                EditorGUILayout.LabelField(binding.property.Substring(ShapePrefix.Length),
                    GUILayout.MinWidth(60));
                float saved = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 26f;
                binding.offValue = EditorGUILayout.FloatField(L.Tr("Off"), binding.offValue,
                    GUILayout.Width(70));
                binding.onValue = EditorGUILayout.FloatField(L.Tr("On"), binding.onValue,
                    GUILayout.Width(70));
                EditorGUIUtility.labelWidth = saved;
                if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    removeShape = binding;
                EditorGUILayout.EndHorizontal();
            }
            if (removeShape != null)
                row.bindings.Remove(removeShape);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(24);
            if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                ShowAddShapeMenu(row, skinned);
            EditorGUILayout.EndHorizontal();
        }

        const string ShapePrefix = "blendShape.";

        void ShowAddShapeMenu(Row row, SkinnedMeshRenderer skinned)
        {
            var menu = new GenericMenu();
            var mesh = skinned.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                string name = mesh.GetBlendShapeName(i);
                var label = new GUIContent(name);
                if (FindBinding(row, typeof(SkinnedMeshRenderer).Name, ShapePrefix + name) != null)
                {
                    menu.AddDisabledItem(label);
                    continue;
                }
                var captured = name;
                menu.AddItem(label, false, () => row.bindings.Add(new GraphFrameData.BindingRecord
                {
                    typeName = typeof(SkinnedMeshRenderer).Name,
                    property = ShapePrefix + captured,
                    onValue = 100f,
                }));
            }
            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent(L.Tr("No blendshapes")));
            menu.ShowAsContext();
        }

        /// <summary>Bindings this window has no chip for: a component the project no longer has,
        /// or one a later version of DaerD wrote. Shown so an edit does not look like it lost
        /// them, and removable, but not editable here.</summary>
        void DrawOtherBindings(Row row)
        {
            GraphFrameData.BindingRecord remove = null;
            foreach (var binding in row.bindings)
            {
                if (binding.property == null || binding.property.StartsWith(ShapePrefix)) continue;
                if (binding.property == "m_Enabled" && Charted(row, binding)) continue;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(24);
                EditorGUILayout.LabelField(new GUIContent(binding.typeName + "." + binding.property,
                    L.Tr("A binding this window has no button for. It is kept exactly as it is.")),
                    EditorStyles.miniLabel);
                if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    remove = binding;
                EditorGUILayout.EndHorizontal();
            }
            if (remove != null) row.bindings.Remove(remove);
        }

        /// <summary>Whether a chip is drawn for this binding — the target has the component, so
        /// the chip above is already showing it.</summary>
        bool Charted(Row row, GraphFrameData.BindingRecord binding)
        {
            var type = ToggleBuilder.FindComponentType(binding.typeName);
            return type != null && row.target != null && row.target.GetComponent(type) != null;
        }

        // ---- applying ----------------------------------------------------------

        internal GraphFrameData.ObjectGadgetConfig BuildConfig()
        {
            var config = new GraphFrameData.ObjectGadgetConfig
            {
                kind = (int)ObjectGadgets.Kind.Toggle,
                name = _name != null ? _name.Trim() : string.Empty,
                parameter = _parameter != null ? _parameter.Trim() : string.Empty,
                mode = (int)_mode,
                defaultOn = _defaultOn,
                declare = _declare,
                // Only the tree wiring has a host to choose; a Bool toggle is a layer of its own
                // and the builder fills this in with the layer it added.
                layer = _mode == ToggleBuilder.Mode.DirectBlendTree ? ChosenLayer() : null,
            };
            foreach (var row in _rows)
            {
                var record = new GraphFrameData.ObjectTargetRecord
                {
                    target = row.target,
                    activeWhenOn = row.activeWhenOn,
                    toggleActive = row.toggleActive,
                };
                record.bindings.AddRange(row.bindings);
                config.targets.Add(record);
            }
            return config;
        }

        AnimatorStateMachine ChosenLayer()
        {
            if (_layerChoice <= 0 || _layerChoice - 1 >= _layerCandidates.Count) return null;
            int index = _layerCandidates[_layerChoice - 1];
            var layers = _controller.layers;
            return index < layers.Length ? layers[index].stateMachine : null;
        }

        void TryApply()
        {
            var config = BuildConfig();
            var error = ObjectGadgets.Validate(_controller, config, _replaces);
            if (error != null)
            {
                EditorUtility.DisplayDialog(L.Tr("Object Gadget"), error, "OK");
                return;
            }
            ObjectGadgets.Apply(_controller, config, _replaces);
            _onApplied?.Invoke();
            Close();
        }
    }
}
