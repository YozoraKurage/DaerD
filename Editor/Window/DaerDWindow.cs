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

        DaerDContext _context;
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
            window.titleContent = new GUIContent("daerD");
            window.minSize = new Vector2(760, 440);
            window.SetController(controller);
            window.Show();
            window.Focus();
            return window;
        }

        public void SetController(AnimatorController controller)
        {
            _controller = controller;
            _layerIndex = 0;
            _context?.SetController(controller);
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
            rootVisualElement.Clear();
            _context = new DaerDContext();
            _statePreview = new StatePreview(_context);

            var styleSheet = LoadStyleSheet();
            if (styleSheet != null)
                rootVisualElement.styleSheets.Add(styleSheet);

            rootVisualElement.Add(BuildToolbar());

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
                _context.SetController(_controller);
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

            var layoutMenu = new ToolbarMenu { text = "Layout" };
            layoutMenu.menu.AppendAction("Grid", _ =>
            {
                GraphLayout.Grid(_context.CurrentStateMachine);
                _graphView.Sync.RequestRebuild();
            });
            layoutMenu.menu.AppendAction("Hierarchical", _ =>
            {
                GraphLayout.Hierarchical(_context.CurrentStateMachine);
                _graphView.Sync.RequestRebuild();
            });
            toolbar.Add(layoutMenu);

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
