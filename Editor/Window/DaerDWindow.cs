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
        LayersPanel _layersPanel;
        ParametersPanel _parametersPanel;
        InspectorPanel _inspectorPanel;
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
        }

        void CreateGUI()
        {
            rootVisualElement.Clear();
            _context = new DaerDContext();

            var styleSheet = LoadStyleSheet();
            if (styleSheet != null)
                rootVisualElement.styleSheets.Add(styleSheet);

            rootVisualElement.Add(BuildToolbar());

            _graphView = new AnimatorGraphView(_context) { Owner = new EditorWindowOwner { Window = this } };
            _layersPanel = new LayersPanel(_context);
            _parametersPanel = new ParametersPanel(_context, parameter => _graphView.Sync.HighlightParameter(parameter));
            _inspectorPanel = new InspectorPanel(_context, _graphView);

            var leftSplit = new TwoPaneSplitView(0, 220, TwoPaneSplitViewOrientation.Vertical);
            leftSplit.Add(_layersPanel);
            leftSplit.Add(_parametersPanel);

            var centerRightSplit = new TwoPaneSplitView(1, 320, TwoPaneSplitViewOrientation.Horizontal);
            centerRightSplit.Add(_graphView);
            centerRightSplit.Add(_inspectorPanel);

            var mainSplit = new TwoPaneSplitView(0, 250, TwoPaneSplitViewOrientation.Horizontal);
            mainSplit.style.flexGrow = 1;
            mainSplit.Add(leftSplit);
            mainSplit.Add(centerRightSplit);
            rootVisualElement.Add(mainSplit);

            _context.LayerChanged += RefreshBreadcrumb;
            _context.StateMachinePathChanged += RefreshBreadcrumb;
            _context.ControllerChanged += RefreshBreadcrumb;
            _context.LayerChanged += SyncSerializedState;
            _context.ControllerChanged += SyncSerializedState;

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
        }

        void OnUndoRedo()
        {
            if (_context == null) return;
            _context.ValidatePath();
            _graphView?.Sync.RequestRebuild();
            _layersPanel?.Refresh();
            _parametersPanel?.Refresh();
            _inspectorPanel?.Refresh();
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
