using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>The main editor window: toolbar, layers/parameters panels, graph and inspector.</summary>
    public class DaerDWindow : EditorWindow
    {
        [SerializeField] AnimatorController _controller;
        [SerializeField] int _layerIndex;

        // Remembered across domain reloads so the open tabs survive script recompiles / play mode.
        [SerializeField] List<AnimatorController> _openControllers = new List<AnimatorController>();

        DaerDContext _context;
        VisualElement _tabBar;
        AnimatorGraphView _graphView;
        BlendTreeGraphView _blendTreeView;
        VisualElement _graphHost;
        LayersPanel _layersPanel;
        ParametersPanel _parametersPanel;
        InspectorPanel _inspectorPanel;
        BlendTreeHierarchyPanel _hierarchyPanel;
        VisualElement _rightColumn;
        TwoPaneSplitView _rightSplit;
        StatePreview _statePreview;
        VisualElement _breadcrumb;
        double _lastRuntimePoll;

        public static DaerDWindow Open(AnimatorController controller)
        {
            var window = GetWindow<DaerDWindow>();
            window.titleContent = new GUIContent("DaerD");
            window.minSize = new Vector2(760, 440);
            window.OpenController(controller);
            window.Show();
            window.Focus();
            return window;
        }

        /// <summary>Adds <paramref name="controller"/> as a tab (if not already open) and activates it.</summary>
        public void OpenController(AnimatorController controller)
        {
            // Opened from the menu with nothing selected: keep whatever is already open rather than
            // blanking the window.
            if (controller == null)
            {
                RefreshTabBar();
                return;
            }
            if (!_openControllers.Contains(controller))
                _openControllers.Add(controller);
            // Re-opening the already-active controller must not reset layer / selection / drill-down.
            if (controller != _controller)
                SetController(controller);
            RefreshTabBar();
        }

        public void SetController(AnimatorController controller)
        {
            _controller = controller;
            _layerIndex = 0;
            _context?.SetController(controller);
        }

        // ---- controller tabs -------------------------------------------------

        void ActivateController(AnimatorController controller)
        {
            if (controller == null || controller == _controller) return;
            SetController(controller);
            RefreshTabBar();
        }

        void CloseController(AnimatorController controller)
        {
            int index = _openControllers.IndexOf(controller);
            if (index < 0) return;
            _openControllers.RemoveAt(index);
            if (controller == _controller)
            {
                var next = _openControllers.Count > 0
                    ? _openControllers[Mathf.Clamp(index, 0, _openControllers.Count - 1)]
                    : null;
                SetController(next);
            }
            RefreshTabBar();
        }

        /// <summary>Rebuilds the tab strip from the open-controller list, highlighting the active one.</summary>
        void RefreshTabBar()
        {
            if (_tabBar == null) return;

            _openControllers.RemoveAll(c => c == null);   // drop deleted assets
            if (_controller != null && !_openControllers.Contains(_controller))
                _openControllers.Add(_controller);

            _tabBar.Clear();
            _tabBar.style.display = _openControllers.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (var controller in _openControllers)
            {
                var captured = controller;
                var tab = new VisualElement();
                tab.AddToClassList("dd-tab");
                if (controller == _controller) tab.AddToClassList("dd-tab--active");

                var label = new Label(controller.name) { tooltip = AssetDatabase.GetAssetPath(controller) };
                label.AddToClassList("dd-tab__label");
                tab.Add(label);

                var close = new Label("×") { tooltip = "Close tab" };   // U+00D7, widely available
                close.AddToClassList("dd-tab__close");
                tab.Add(close);

                tab.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button == 0) { ActivateController(captured); evt.StopPropagation(); }
                    else if (evt.button == 2) { CloseController(captured); evt.StopPropagation(); }   // middle-click
                });
                close.RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.button != 0) return;
                    CloseController(captured);
                    evt.StopPropagation();   // don't also activate the tab
                });

                _tabBar.Add(tab);
            }
        }

        void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += PollRuntime;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= PollRuntime;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            _graphView?.Cleanup();
            _statePreview?.Stop();
        }

        void CreateGUI()
        {
            // Captured before the wiring below, because SetController (fired during restore) resets
            // the layer to 0 via SyncSerializedState, clobbering the serialized value.
            int restoredLayer = _layerIndex;

            rootVisualElement.Clear();
            _context = new DaerDContext();
            _statePreview = new StatePreview(_context);

            var styleSheet = LoadStyleSheet();
            if (styleSheet != null)
                rootVisualElement.styleSheets.Add(styleSheet);

            rootVisualElement.Add(BuildToolbar());

            _tabBar = new VisualElement();
            _tabBar.AddToClassList("dd-tabbar");
            rootVisualElement.Add(_tabBar);

            _graphView = new AnimatorGraphView(_context) { Owner = new EditorWindowOwner { Window = this } };
            _blendTreeView = new BlendTreeGraphView(_context);
            _layersPanel = new LayersPanel(_context);
            _parametersPanel = new ParametersPanel(_context);
            _inspectorPanel = new InspectorPanel(_context, _graphView);
            _hierarchyPanel = new BlendTreeHierarchyPanel(_context);

            // The two graph surfaces share the centre pane; only one is visible at a time
            // depending on whether the user has drilled into a blend tree.
            _graphHost = new VisualElement { style = { flexGrow = 1 } };
            _graphView.style.flexGrow = 1;
            _blendTreeView.style.flexGrow = 1;
            _graphHost.Add(_graphView);
            _graphHost.Add(_blendTreeView);

            var leftSplit = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Vertical);
            leftSplit.Add(_layersPanel);
            leftSplit.Add(_parametersPanel);

            // The right column either shows the inspector full-height (state machine view)
            // or splits inspector-on-top / hierarchy-on-bottom (blend tree view). The
            // hierarchy isn't useful outside blend tree mode, so we swap layouts rather
            // than just hide a pane, otherwise the splitter gutter would still take space.
            _rightColumn = new VisualElement { style = { flexGrow = 1 } };
            _rightColumn.Add(_inspectorPanel);

            var centerRightSplit = new TwoPaneSplitView(1, 320, TwoPaneSplitViewOrientation.Horizontal);
            centerRightSplit.Add(_graphHost);
            centerRightSplit.Add(_rightColumn);

            var mainSplit = new TwoPaneSplitView(0, 250, TwoPaneSplitViewOrientation.Horizontal);
            mainSplit.style.flexGrow = 1;
            mainSplit.Add(leftSplit);
            mainSplit.Add(centerRightSplit);
            rootVisualElement.Add(mainSplit);

            _context.LayerChanged += RefreshBreadcrumb;
            _context.StateMachinePathChanged += RefreshBreadcrumb;
            _context.BlendTreePathChanged += RefreshBreadcrumb;
            _context.ControllerChanged += RefreshBreadcrumb;
            _context.LayerChanged += SyncSerializedState;
            _context.ControllerChanged += SyncSerializedState;

            _context.BlendTreePathChanged += RefreshGraphVisibility;
            _context.StateMachinePathChanged += RefreshGraphVisibility;
            _context.ControllerChanged += RefreshGraphVisibility;
            _context.LayerChanged += RefreshGraphVisibility;
            RefreshGraphVisibility();

            _statePreview.Start();

            if (_controller != null)
            {
                _context.SetController(_controller);
                if (restoredLayer > 0)
                    _context.SetLayer(restoredLayer);   // restore the active layer after a domain reload
            }

            RefreshTabBar();
        }

        void SyncSerializedState()
        {
            _controller = _context.Controller;
            _layerIndex = _context.LayerIndex;
        }

        VisualElement BuildToolbar()
        {
            var toolbar = new Toolbar();

            _breadcrumb = new VisualElement();
            _breadcrumb.AddToClassList("ce-breadcrumb");
            toolbar.Add(_breadcrumb);

            var spacer = new VisualElement();
            spacer.style.flexGrow = 1;
            toolbar.Add(spacer);

            var previewToggle = new ToolbarToggle
            {
                text = "Preview",
                tooltip = "Preview frame 0 of the selected clip state on the matching scene object",
            };
            previewToggle.RegisterValueChangedCallback(evt => _statePreview.SetEnabled(evt.newValue));
            toolbar.Add(previewToggle);

            // Layout (Grid / Hierarchical / Align Selected) lives in the graph's right-click menu now.
            toolbar.Add(new ToolbarButton(() => _graphView.FrameAll()) { text = "Frame All" });
            toolbar.Add(new ToolbarButton(() => _inspectorPanel.ShowAnalysis()) { text = "Analyze" });
            toolbar.Add(new ToolbarButton(
                () => SettingsService.OpenUserPreferences(DaerDSettingsProvider.Path)) { text = "Settings" });

            return toolbar;
        }

        void RefreshBreadcrumb()
        {
            if (_breadcrumb == null) return;
            _breadcrumb.Clear();
            if (_context == null || !_context.HasController) return;

            var layer = _context.CurrentLayer;
            if (layer != null)
            {
                var label = new Label(layer.name);
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.marginLeft = 6;
                label.style.marginRight = 2;
                _breadcrumb.Add(label);
            }
            for (int i = 0; i < _context.StateMachinePath.Count; i++)
            {
                int depth = i;
                var stateMachine = _context.StateMachinePath[i];
                _breadcrumb.Add(new Label("›"));
                _breadcrumb.Add(new ToolbarButton(() => _context.GoToBreadcrumb(depth))
                {
                    text = stateMachine != null ? stateMachine.name : "?",
                });
            }
            // When inside a blend tree, extend the trail: state entry name (clickable to
            // exit) and then one entry per nesting level. The trailing arrow makes it clear
            // the visible graph is the deepest item, matching the sub-state machine UX.
            if (_context.IsViewingBlendTree)
            {
                var originState = _context.BlendTreeOriginState;
                _breadcrumb.Add(new Label("›"));
                _breadcrumb.Add(new ToolbarButton(() => _context.ExitBlendTree())
                {
                    text = (originState != null ? originState.name : "Blend Tree") + " ▾",
                    tooltip = "Return to the state machine view",
                });
                for (int i = 0; i < _context.BlendTreePath.Count; i++)
                {
                    int depth = i;
                    var tree = _context.BlendTreePath[i];
                    _breadcrumb.Add(new Label("›"));
                    _breadcrumb.Add(new ToolbarButton(() => _context.GoToBlendTreeBreadcrumb(depth))
                    {
                        text = tree != null ? tree.name : "?",
                    });
                }
            }
        }

        /// <summary>Shows the blend tree view when the context has drilled into one, otherwise the state machine view.</summary>
        void RefreshGraphVisibility()
        {
            if (_graphView == null || _blendTreeView == null) return;
            bool inBlendTree = _context != null && _context.IsViewingBlendTree;
            _graphView.style.display = inBlendTree ? DisplayStyle.None : DisplayStyle.Flex;
            _blendTreeView.style.display = inBlendTree ? DisplayStyle.Flex : DisplayStyle.None;
            if (inBlendTree)
                _blendTreeView.RequestRebuild();

            RefreshRightColumnLayout(inBlendTree);
        }

        /// <summary>
        /// Reparents the inspector + hierarchy so the hierarchy panel only exists in the
        /// visual tree when the user is editing a BlendTree. Outside blend tree mode the
        /// inspector occupies the full right column with no splitter gutter.
        /// </summary>
        void RefreshRightColumnLayout(bool inBlendTree)
        {
            if (_rightColumn == null || _inspectorPanel == null || _hierarchyPanel == null) return;

            if (inBlendTree)
            {
                if (_rightSplit != null && _rightSplit.parent == _rightColumn) return;
                _rightColumn.Clear();
                _rightSplit = new TwoPaneSplitView(1, 200, TwoPaneSplitViewOrientation.Vertical);
                _rightSplit.style.flexGrow = 1;
                _rightSplit.Add(_inspectorPanel);
                _rightSplit.Add(_hierarchyPanel);
                _rightColumn.Add(_rightSplit);
            }
            else
            {
                if (_rightSplit == null && _inspectorPanel.parent == _rightColumn) return;
                _rightColumn.Clear();
                _rightSplit = null;
                _rightColumn.Add(_inspectorPanel);
            }
        }

        void OnUndoRedo()
        {
            if (_context == null) return;
            _context.ValidatePath();
            _graphView?.Sync.RequestRebuild();
            _blendTreeView?.RequestRebuild();
            RefreshGraphVisibility();
            _layersPanel?.Refresh();
            _parametersPanel?.Refresh();
            _inspectorPanel?.Refresh();
            _hierarchyPanel?.Refresh();
        }

        void OnPlayModeChanged(PlayModeStateChange change) => _graphView?.Sync.RequestRebuild();

        void PollRuntime()
        {
            if (!EditorApplication.isPlaying || _context == null || _graphView == null || _controller == null) return;
            if (EditorApplication.timeSinceStartup - _lastRuntimePoll < 0.1) return;
            _lastRuntimePoll = EditorApplication.timeSinceStartup;

            var go = Selection.activeGameObject;
            if (go == null) return;
            var animator = go.GetComponent<Animator>();
            if (animator == null || !ControllerMatches(animator.runtimeAnimatorController)) return;
            if (animator.layerCount == 0) return;

            int layer = Mathf.Clamp(_context.LayerIndex, 0, animator.layerCount - 1);
            var info = animator.GetCurrentAnimatorStateInfo(layer);
            _graphView.Sync.SetRuntimeStateHash(info.shortNameHash);
        }

        bool ControllerMatches(RuntimeAnimatorController runtime)
        {
            if (runtime == _controller) return true;
            return runtime is AnimatorOverrideController over && over.runtimeAnimatorController == _controller;
        }

        static StyleSheet LoadStyleSheet()
        {
            foreach (var guid in AssetDatabase.FindAssets("DaerD t:StyleSheet"))
            {
                var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (sheet != null) return sheet;
            }
            return null;
        }
    }
}
