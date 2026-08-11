using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Standalone editor for a VRCExpressionsMenu asset: breadcrumb navigation through
    /// submenus, control list with reorder, and an inspector for the selected control with
    /// parameter warnings against the assigned controller. Lives outside the main DaerD
    /// window so the menu stays visible while the user edits the graph.
    /// </summary>
    class VrcMenuWindow : EditorWindow
    {
        [SerializeField] AnimatorController _controller;
        [SerializeField] Object _rootMenu;
        // Menus bind EXPRESSION parameters, so type checks prefer the controller's
        // associated parameter store; the controller parameter is only the fallback
        // (deliberate type mismatches are a supported VRChat technique).
        ParameterStore _paramStore;
        bool _paramStoreLoaded;
        // Breadcrumb path; [0] is the root menu. Object references survive domain reloads
        // poorly in plain lists — rebuilt to root when anything went stale.
        readonly List<Object> _stack = new List<Object>();
        int _selected = -1;
        Vector2 _listScroll;
        Vector2 _inspectorScroll;

        static readonly VrcMenuAccess.ControlType[] TypeValues =
        {
            VrcMenuAccess.ControlType.Button,
            VrcMenuAccess.ControlType.Toggle,
            VrcMenuAccess.ControlType.SubMenu,
            VrcMenuAccess.ControlType.TwoAxisPuppet,
            VrcMenuAccess.ControlType.FourAxisPuppet,
            VrcMenuAccess.ControlType.RadialPuppet,
        };
        static readonly string[] TypeLabels =
        {
            "Button", "Toggle", "Sub Menu", "Two Axis Puppet", "Four Axis Puppet", "Radial Puppet",
        };
        static readonly string[] AxisLabels = { "Up", "Right", "Down", "Left" };

        public static VrcMenuWindow Open(AnimatorController controller)
        {
            var window = GetWindow<VrcMenuWindow>();
            window.minSize = new Vector2(420, 320);
            if (controller != null)
            {
                window._controller = controller;
                // Only the explicit, persisted association — never a scene guess (DaerD is
                // also used on gimmick controllers that belong to no avatar).
                var stored = GraphFrameData.GetExpressionsMenu(controller);
                if (VrcMenuAccess.Is(stored))
                    window._rootMenu = stored;
                window.ResetToRoot();
            }
            window.Show();
            window.Focus();
            return window;
        }

        void OnEnable()
        {
            ApplyTitle();
            L.LanguageChanged += ApplyTitle;
            Undo.undoRedoPerformed += Repaint;
        }

        void OnDisable()
        {
            L.LanguageChanged -= ApplyTitle;
            Undo.undoRedoPerformed -= Repaint;
        }

        void ApplyTitle() => titleContent = new GUIContent(L.Tr("DaerD Menu"));

        void OnFocus() => _paramStoreLoaded = false;

        /// <summary>The effective type a control's parameter binds to: the store entry when
        /// the controller has an associated parameter store, else the controller parameter.
        /// Null when neither knows the name.</summary>
        VrcExpressionParameters.ValueType? BoundType(string parameterName)
        {
            if (!_paramStoreLoaded)
            {
                _paramStore = ParameterStore.Of(_controller);
                _paramStoreLoaded = true;
            }
            var entry = _paramStore?.Find(parameterName);
            if (entry != null && entry.typed) return entry.valueType;
            var parameter = _controller != null
                ? DbtBuilder.FindParameter(_controller, parameterName) : null;
            return parameter != null ? VrcExpressionParameters.MapType(parameter.type) : null;
        }

        void ResetToRoot()
        {
            _stack.Clear();
            if (_rootMenu != null) _stack.Add(_rootMenu);
            _selected = -1;
        }

        Object CurrentMenu => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        void OnGUI()
        {
            DrawHeader();
            var menu = CurrentMenu;
            if (menu == null)
            {
                EditorGUILayout.HelpBox(
                    L.Tr("Assign a VRC Expressions Menu asset (auto-detected from the scene avatar when possible)."),
                    MessageType.Info);
                return;
            }

            DrawBreadcrumb();
            var controls = VrcMenuAccess.Read(menu);
            if (_selected >= controls.Count) _selected = controls.Count - 1;

            DrawControlList(menu, controls);
            EditorGUILayout.Space(4);
            if (_selected >= 0 && _selected < controls.Count)
                DrawInspector(menu, controls[_selected]);
        }

        void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            var pickedController = (AnimatorController)EditorGUILayout.ObjectField(
                _controller, typeof(AnimatorController), false);
            if (pickedController != _controller)
                _controller = pickedController;
            var pickedMenu = EditorGUILayout.ObjectField(_rootMenu, typeof(ScriptableObject), false);
            if (pickedMenu != _rootMenu)
            {
                if (pickedMenu == null || VrcMenuAccess.Is(pickedMenu))
                {
                    _rootMenu = pickedMenu;
                    GraphFrameData.SetExpressionsMenu(_controller, pickedMenu);
                    ResetToRoot();
                }
                else
                    EditorUtility.DisplayDialog(L.Tr("DaerD Menu"),
                        L.Tr("That asset is not a VRC Expressions Menu."), "OK");
            }
            if (GUILayout.Button(new GUIContent(L.Tr("Detect"),
                    L.Tr("Search the scene for an avatar whose playable layers run this controller (exact match only).")),
                    GUILayout.Width(52)))
            {
                var detected = VrcMenuAccess.FindMenuFor(_controller);
                if (detected == null)
                    EditorUtility.DisplayDialog(L.Tr("DaerD Menu"),
                        L.Tr("No exact match in the scene — no avatar runs this controller."), "OK");
                else
                {
                    _rootMenu = detected;
                    GraphFrameData.SetExpressionsMenu(_controller, detected);
                    ResetToRoot();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawBreadcrumb()
        {
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < _stack.Count; i++)
            {
                if (i > 0) GUILayout.Label("›", GUILayout.ExpandWidth(false));
                string label = _stack[i] != null ? _stack[i].name : "?";
                if (i == _stack.Count - 1)
                    GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                else if (GUILayout.Button(label, EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    _stack.RemoveRange(i + 1, _stack.Count - i - 1);
                    _selected = -1;
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawControlList(Object menu, List<VrcMenuAccess.Control> controls)
        {
            int max = VrcMenuAccess.MaxControls(menu);
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll, GUILayout.MinHeight(90),
                GUILayout.MaxHeight(160));
            for (int i = 0; i < controls.Count; i++)
            {
                var control = controls[i];
                EditorGUILayout.BeginHorizontal();
                var prev = GUI.backgroundColor;
                if (i == _selected) GUI.backgroundColor = DaerDColors.SelectedRow;
                string label = control.name + "  (" + TypeLabel(control.type) + ")";
                if (GUILayout.Button(label, EditorStyles.miniButtonLeft))
                    _selected = i;
                GUI.backgroundColor = prev;
                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("↑", EditorStyles.miniButtonMid, GUILayout.Width(DaerDLayout.GlyphButton))
                        && VrcMenuAccess.MoveControl(menu, i, i - 1))
                    { _selected = i - 1; GUIUtility.ExitGUI(); }
                using (new EditorGUI.DisabledScope(i == controls.Count - 1))
                    if (GUILayout.Button("↓", EditorStyles.miniButtonMid, GUILayout.Width(DaerDLayout.GlyphButton))
                        && VrcMenuAccess.MoveControl(menu, i, i + 1))
                    { _selected = i + 1; GUIUtility.ExitGUI(); }
                if (control.type == VrcMenuAccess.ControlType.SubMenu && control.subMenu != null)
                    if (GUILayout.Button(new GUIContent(L.Tr("Open"), L.Tr("Edit this submenu")),
                            EditorStyles.miniButtonRight, GUILayout.Width(DaerDLayout.RowAction)))
                    {
                        _stack.Add(control.subMenu);
                        _selected = -1;
                        GUIUtility.ExitGUI();
                    }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(controls.Count + " / " + max, EditorStyles.miniLabel,
                GUILayout.Width(50));
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(controls.Count >= max))
                if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                    _selected = VrcMenuAccess.AddControl(menu);
            using (new EditorGUI.DisabledScope(_selected < 0 || controls.Count == 0))
                if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
                {
                    VrcMenuAccess.RemoveControl(menu, _selected);
                    _selected = -1;
                    GUIUtility.ExitGUI();
                }
            EditorGUILayout.EndHorizontal();
        }

        static string TypeLabel(VrcMenuAccess.ControlType type)
        {
            for (int i = 0; i < TypeValues.Length; i++)
                if (TypeValues[i] == type) return TypeLabels[i];
            return type.ToString();
        }

        void DrawInspector(Object menu, VrcMenuAccess.Control control)
        {
            _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);
            int index = _selected;

            string name = EditorGUILayout.DelayedTextField(L.Tr("Name"), control.name);
            if (name != control.name)
                VrcMenuAccess.SetName(menu, index, name);

            var icon = (Texture2D)EditorGUILayout.ObjectField(L.Tr("Icon"), control.icon,
                typeof(Texture2D), false);
            if (icon != control.icon)
                VrcMenuAccess.SetIcon(menu, index, icon);

            int typeIndex = System.Array.IndexOf(TypeValues, control.type);
            int newTypeIndex = EditorGUILayout.Popup(L.Tr("Type"), Mathf.Max(0, typeIndex), TypeLabels);
            if (newTypeIndex != typeIndex)
                VrcMenuAccess.SetType(menu, index, TypeValues[newTypeIndex]);

            if (control.type != VrcMenuAccess.ControlType.SubMenu)
                DrawParameterField(menu, index, L.Tr("Parameter"), control.parameter,
                    slot: -1, wantFloat: false);

            if (control.type == VrcMenuAccess.ControlType.Button
                || control.type == VrcMenuAccess.ControlType.Toggle)
                DrawValueField(menu, index, control);

            if (control.type == VrcMenuAccess.ControlType.SubMenu)
            {
                EditorGUILayout.BeginHorizontal();
                var subMenu = EditorGUILayout.ObjectField(L.Tr("Sub Menu"), control.subMenu,
                    typeof(ScriptableObject), false);
                if (subMenu != control.subMenu && (subMenu == null || VrcMenuAccess.Is(subMenu)))
                    VrcMenuAccess.SetSubMenu(menu, index, subMenu);
                if (control.subMenu == null
                    && GUILayout.Button(L.Tr("Create"), GUILayout.Width(60)))
                {
                    var created = VrcMenuAccess.CreateSubMenuAsset(menu, control.name);
                    if (created != null)
                        VrcMenuAccess.SetSubMenu(menu, index, created);
                }
                EditorGUILayout.EndHorizontal();
            }

            int subParameterCount = VrcMenuAccess.SubParameterCount(control.type);
            for (int slot = 0; slot < subParameterCount; slot++)
            {
                string current = slot < control.subParameters.Count ? control.subParameters[slot] : string.Empty;
                string label = control.type == VrcMenuAccess.ControlType.RadialPuppet
                    ? L.Tr("Rotation")
                    : subParameterCount == 2
                        ? (slot == 0 ? L.Tr("Horizontal") : L.Tr("Vertical"))
                        : AxisLabels[slot];
                DrawParameterField(menu, index, label, current, slot, wantFloat: true);
            }

            int labelCount = VrcMenuAccess.LabelCount(control.type);
            for (int slot = 0; slot < labelCount; slot++)
            {
                string current = slot < control.labels.Count ? control.labels[slot] : string.Empty;
                string label = EditorGUILayout.DelayedTextField(
                    L.Tr("Label {0}", AxisLabels[slot]), current);
                if (label != current)
                    VrcMenuAccess.SetLabel(menu, index, slot, label);
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>Parameter text field with a "▾" dropdown of controller parameters and
        /// inline warnings (missing / unexpected type).</summary>
        void DrawParameterField(Object menu, int index, string label, string current,
            int slot, bool wantFloat)
        {
            EditorGUILayout.BeginHorizontal();
            string typed = EditorGUILayout.DelayedTextField(label, current);
            if (typed != current)
                ApplyParameter(menu, index, slot, typed);
            if (GUILayout.Button("▾", EditorStyles.miniButton, GUILayout.Width(DaerDLayout.GlyphButton)))
            {
                var picker = new GenericMenu();
                if (_controller != null)
                    foreach (var parameter in _controller.parameters)
                    {
                        // No type filter: with parameter mismatching the expression-side
                        // type can differ from the controller's — the warning below flags
                        // actually-unsuitable picks.
                        var captured = parameter.name;
                        picker.AddItem(new GUIContent(captured.Replace('/', '∕')),
                            captured == current,
                            () => ApplyParameter(menu, index, slot, captured));
                    }
                if (picker.GetItemCount() == 0)
                    picker.AddDisabledItem(new GUIContent(L.Tr("No matching parameters")));
                picker.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            if (string.IsNullOrEmpty(current) || _controller == null) return;
            var bound = BoundType(current);
            if (bound == null)
            {
                if (DbtBuilder.FindParameter(_controller, current) == null)
                    EditorGUILayout.HelpBox(L.Tr("Parameter '{0}' is not in the controller.", current),
                        MessageType.Warning);
            }
            else if (wantFloat && bound.Value != VrcExpressionParameters.ValueType.Float)
                EditorGUILayout.HelpBox(L.Tr("Puppet parameters must be Float; '{0}' is {1}.",
                        current, bound.Value), MessageType.Warning);
            else
                EditorGUILayout.LabelField(" ", bound.Value.ToString(), EditorStyles.miniLabel);
        }

        void ApplyParameter(Object menu, int index, int slot, string parameterName)
        {
            if (slot < 0) VrcMenuAccess.SetParameter(menu, index, parameterName);
            else VrcMenuAccess.SetSubParameter(menu, index, slot, parameterName);
        }

        /// <summary>Value written when the control fires; UI adapts to the bound
        /// (expression-store first) type.</summary>
        void DrawValueField(Object menu, int index, VrcMenuAccess.Control control)
        {
            var bound = BoundType(control.parameter);
            float newValue;
            if (bound == VrcExpressionParameters.ValueType.Int)
                newValue = EditorGUILayout.IntSlider(L.Tr("Value"), Mathf.RoundToInt(control.value), 0, 255);
            else if (bound == VrcExpressionParameters.ValueType.Bool)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.Slider(L.Tr("Value"), 1f, 0f, 1f);
                newValue = 1f;
            }
            else
                newValue = EditorGUILayout.Slider(L.Tr("Value"), control.value, -1f, 1f);
            if (!Mathf.Approximately(newValue, control.value))
                VrcMenuAccess.SetValue(menu, index, newValue);
        }
    }
}
