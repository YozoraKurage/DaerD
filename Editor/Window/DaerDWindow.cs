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
        // Home is a selection like a layer is, so it has to survive a domain reload the same way.
        [SerializeField] bool _homeSelected;

        // Remembered across domain reloads so the open tabs survive script recompiles / play mode.
        [SerializeField] List<AnimatorController> _openControllers = new List<AnimatorController>();
        // One remembered layer index per tab, parallel to _openControllers. Switching tabs
        // restores the layer the user last had open in that tab instead of resetting to 0.
        [SerializeField] List<int> _openControllerLayers = new List<int>();

        DaerDContext _context;
        TabStrip _tabs;
        // Kept so a language change can restamp their labels in place. Rebuilding the toolbar
        // instead would re-fire the toggle callbacks (opening the Animation window as a side
        // effect) and reset toggle state.
        ToolbarToggle _selectSyncToggle;
        ToolbarToggle _previewToggle;
        ToolbarButton _frameAllButton;
        ToolbarButton _analyzeButton;
        ToolbarButton _settingsButton;
        StateSearchField _searchField;
        AnimatorGraphView _graphView;
        BlendTreeGraphView _blendTreeView;
        AsyncSyncPanel _asyncSyncPanel;
        HomePanel _homePanel;
        // The centre pane shows the async-sync settings panel instead of the graph while an
        // async-sync layer is active — its states are generated machinery. "Show Graph" flips
        // this for a peek; it resets on every layer/controller switch, so the settings view
        // is always the way back in.
        bool _showSyncGraph;
        Button _syncViewButton;
        VisualElement _graphHost;
        LayersPanel _layersPanel;
        ParametersPanel _parametersPanel;
        InspectorPanel _inspectorPanel;
        BlendTreeHierarchyPanel _hierarchyPanel;
        VisualElement _rightColumn;
        TwoPaneSplitView _rightSplit;
        StatePreview _statePreview;
        AnimationWindowSync _animationSync;
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
            if (!_tabs.Contains(controller))
                _tabs.Add(controller);
            // Re-opening the already-active controller must not reset layer / selection / drill-down.
            if (controller != _controller)
                ActivateController(controller);
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

            // Save the outgoing tab's current layer so we can return to it next time.
            RememberCurrentLayer();

            int restoredLayer = _tabs.Lookup(controller);
            SetController(controller);
            if (restoredLayer > 0)
                _context?.SetLayer(restoredLayer);
            RefreshTabBar();
        }

        void CloseController(AnimatorController controller)
        {
            int index = _tabs.Remove(controller);
            if (index < 0) return;
            if (controller == _controller)
            {
                var next = _tabs.NextAfter(index);
                if (next != null)
                {
                    int restoredLayer = _tabs.Lookup(next);
                    SetController(next);
                    if (restoredLayer > 0)
                        _context?.SetLayer(restoredLayer);
                }
                else
                {
                    SetController(null);
                }
            }
            RefreshTabBar();
        }

        void RememberCurrentLayer() => _tabs.Remember(_controller, _layerIndex);

        void RefreshTabBar() => _tabs.Refresh(_controller, _layerIndex);

        void OnEnable()
        {
            // Built here, not in CreateGUI: the serialized tab lists are restored just before
            // this runs, and a controller can be opened into the window before its UI exists.
            _tabs = new TabStrip(_openControllers, _openControllerLayers, ActivateController, CloseController);
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += PollRuntime;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= PollRuntime;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            L.LanguageChanged -= OnLanguageChanged;
            _graphView?.Cleanup();
            _statePreview?.Stop();
            _animationSync?.Stop();
        }

        void CreateGUI()
        {
            // Captured before the wiring below, because SetController (fired during restore) resets
            // the layer to 0 and clears the home flag via SyncSerializedState, clobbering the
            // serialized values.
            int restoredLayer = _layerIndex;
            bool restoredHome = _homeSelected;

            rootVisualElement.Clear();
            _context = new DaerDContext();
            _statePreview = new StatePreview(_context);
            _animationSync = new AnimationWindowSync(_context);

            var styleSheet = LoadStyleSheet();
            if (styleSheet != null)
                rootVisualElement.styleSheets.Add(styleSheet);

            rootVisualElement.Add(BuildToolbar());
            L.LanguageChanged -= OnLanguageChanged;   // CreateGUI can run again after a reload
            L.LanguageChanged += OnLanguageChanged;

            rootVisualElement.Add(_tabs.Bar);

            _graphView = new AnimatorGraphView(_context) { Owner = new EditorWindowOwner { Window = this } };
            _blendTreeView = new BlendTreeGraphView(_context);
            _layersPanel = new LayersPanel(_context);
            _parametersPanel = new ParametersPanel(_context);
            _inspectorPanel = new InspectorPanel(_context, _graphView.Sync);
            _hierarchyPanel = new BlendTreeHierarchyPanel(_context);

            // The four centre surfaces share the pane; one is visible at a time depending on
            // whether Home is picked, the user has drilled into a blend tree, or sits on an
            // async-sync layer (whose graph is generated machinery — the settings panel is
            // the view).
            _graphHost = new VisualElement { style = { flexGrow = 1 } };
            _graphView.style.flexGrow = 1;
            _blendTreeView.style.flexGrow = 1;
            _asyncSyncPanel = new AsyncSyncPanel(_context) { style = { flexGrow = 1 } };
            _asyncSyncPanel.ShowGraphRequested += () =>
            {
                _showSyncGraph = true;
                RefreshGraphVisibility();
            };
            _homePanel = new HomePanel(_context) { style = { flexGrow = 1 } };
            _graphHost.Add(_graphView);
            _graphHost.Add(_blendTreeView);
            _graphHost.Add(_asyncSyncPanel);
            _graphHost.Add(_homePanel);

            // Floating way back from the raw graph to the settings view; only visible while
            // peeking at an async-sync layer's graph.
            _syncViewButton = new Button(() =>
            {
                _showSyncGraph = false;
                RefreshGraphVisibility();
            });
            _syncViewButton.style.position = Position.Absolute;
            _syncViewButton.style.top = 6;
            _syncViewButton.style.right = 6;
            ApplySyncViewButtonText();
            _graphHost.Add(_syncViewButton);

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

            // Shift + scroll steps through the controller's layers (scroll down = next layer).
            // TrickleDown on the root so the graph view's zoom never sees the event while Shift
            // is held; unregister first because CreateGUI can run again after a domain reload
            // and rootVisualElement.Clear() does not remove callbacks on the root itself.
            rootVisualElement.UnregisterCallback<WheelEvent>(OnShiftScroll, TrickleDown.TrickleDown);
            rootVisualElement.RegisterCallback<WheelEvent>(OnShiftScroll, TrickleDown.TrickleDown);

            _context.LayerChanged += RefreshBreadcrumb;
            _context.StateMachinePathChanged += RefreshBreadcrumb;
            _context.BlendTreePathChanged += RefreshBreadcrumb;
            _context.ControllerChanged += RefreshBreadcrumb;
            _context.HomeChanged += RefreshBreadcrumb;
            _context.LayerChanged += SyncSerializedState;
            _context.ControllerChanged += SyncSerializedState;
            _context.HomeChanged += SyncSerializedState;

            // Subscribed before RefreshGraphVisibility so the flag is fresh when it runs.
            _context.LayerChanged += ResetSyncGraphPeek;
            _context.ControllerChanged += ResetSyncGraphPeek;

            _context.BlendTreePathChanged += RefreshGraphVisibility;
            _context.StateMachinePathChanged += RefreshGraphVisibility;
            _context.ControllerChanged += RefreshGraphVisibility;
            _context.LayerChanged += RefreshGraphVisibility;
            // A layer can become (or stop being) an async-sync layer without the selection
            // moving: the wizard applying onto it, an undo, a recipe regenerating it.
            _context.LayersChanged += RefreshGraphVisibility;
            _context.GraphStructureChanged += RefreshGraphVisibility;
            _context.HomeChanged += RefreshGraphVisibility;
            RefreshGraphVisibility();

            // SelectSync must subscribe before StatePreview so that on a State selection change
            // the clip is pushed into the AnimationWindow *first*; otherwise StatePreview's
            // re-toggle would re-acquire against the previous clip.
            _animationSync.Start();
            _statePreview.Start();

            if (_controller != null)
            {
                _context.SetController(_controller);
                if (restoredLayer > 0)
                    _context.SetLayer(restoredLayer);   // restore the active layer after a domain reload
                // After the layer, not instead of it: home keeps the layer underneath as the
                // place it returns to.
                if (restoredHome)
                    _context.SelectHome();
            }

            RefreshTabBar();
        }

        void OnShiftScroll(WheelEvent evt)
        {
            if (!evt.shiftKey || _context == null || _context.Controller == null) return;
            // Consume every Shift+wheel so the gesture never zooms the graph, even when the
            // layer index is already clamped at either end of the list.
            evt.StopPropagation();

            // Some platforms deliver Shift+wheel as a horizontal delta.
            float delta = Mathf.Abs(evt.delta.y) >= Mathf.Abs(evt.delta.x) ? evt.delta.y : evt.delta.x;
            if (Mathf.Approximately(delta, 0f)) return;

            int count = _context.Controller.layers.Length;
            bool down = delta > 0f;
            // Home sits above layer 0 in the list, so the gesture walks the two as one strip:
            // down off home lands on the first layer, up off the first layer goes back to it.
            if (_context.IsHomeSelected)
            {
                if (down && count > 0) _context.SetLayer(0);
                return;
            }
            if (!down && _context.LayerIndex == 0)
            {
                _context.SelectHome();
                return;
            }

            int next = Mathf.Clamp(_context.LayerIndex + (down ? 1 : -1), 0, Mathf.Max(0, count - 1));
            if (next != _context.LayerIndex)
                _context.SetLayer(next);
        }

        void SyncSerializedState()
        {
            _controller = _context.Controller;
            _layerIndex = _context.LayerIndex;
            _homeSelected = _context.IsHomeSelected;
            RememberCurrentLayer();
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

            _searchField = new StateSearchField(_context, rootVisualElement);
            toolbar.Add(_searchField);

            // Pushes the selected State's AnimationClip into the Animation window. On by
            // default so first-time users see the sync land immediately; opening the DD window
            // will auto-open the Animation window as part of enabling the sync.
            _selectSyncToggle = new ToolbarToggle();
            _selectSyncToggle.AddToClassList("dd-toolbar-item");
            _selectSyncToggle.RegisterValueChangedCallback(evt => _animationSync.SetEnabled(evt.newValue));
            toolbar.Add(_selectSyncToggle);

            // Preview presupposes Select Sync — without the clip push, there's no new clip for
            // Preview to re-toggle against. Flipping Preview on therefore auto-flips Select Sync
            // on too; flipping Preview off leaves Select Sync where the user had it.
            _previewToggle = new ToolbarToggle();
            _previewToggle.AddToClassList("dd-toolbar-item");
            _previewToggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue && !_selectSyncToggle.value)
                    _selectSyncToggle.value = true;   // also fires its own ValueChanged → SelectSync ON
                _statePreview.SetEnabled(evt.newValue);
            });
            toolbar.Add(_previewToggle);

            // Layout (Grid / Hierarchical / Align Selected) lives in the graph's right-click menu now.
            _frameAllButton = new ToolbarButton(() => _graphView.FrameAll());
            _frameAllButton.AddToClassList("dd-toolbar-item");
            toolbar.Add(_frameAllButton);
            _analyzeButton = new ToolbarButton(() => AnalyzerWindow.Open(_context.Controller));
            _analyzeButton.AddToClassList("dd-toolbar-item");
            toolbar.Add(_analyzeButton);
            _settingsButton = new ToolbarButton(
                () => SettingsService.OpenUserPreferences(DaerDSettingsProvider.Path));
            _settingsButton.AddToClassList("dd-toolbar-item");
            toolbar.Add(_settingsButton);

            ApplyToolbarTexts();
            _selectSyncToggle.value = true;   // default ON; fires the callback above

            return toolbar;
        }

        /// <summary>(Re)stamps the localized toolbar labels; used at build time and on language change.</summary>
        void ApplyToolbarTexts()
        {
            _selectSyncToggle.text = L.Tr("Select Sync");
            _selectSyncToggle.tooltip =
                L.Tr("Sync the Animation window's clip to the selected State's AnimationClip");
            _previewToggle.text = L.Tr("Preview");
            _previewToggle.tooltip =
                L.Tr("Auto-toggle the Animation window's Preview on clip change. " +
                     "Implies Select Sync. Requires a scene GameObject with an Animator " +
                     "running this controller to be selected — Unity's preview can't run " +
                     "without a target.");
            _frameAllButton.text = L.Tr("Frame All");
            _frameAllButton.tooltip = L.Tr("Fit the whole graph in view (A). F fits the selection.");
            _analyzeButton.text = L.Tr("Analyze");
            _settingsButton.text = L.Tr("Settings");
            _searchField.RefreshTooltip();
        }

        void ApplySyncViewButtonText()
        {
            if (_syncViewButton == null) return;
            _syncViewButton.text = L.Tr("Sync Settings");
            _syncViewButton.tooltip = L.Tr("Back to this async-sync layer's settings view");
        }

        /// <summary>Restamps localized labels in place — no rebuild, so toggle state and the
        /// subsystems they drive are untouched.</summary>
        void OnLanguageChanged()
        {
            if (_selectSyncToggle == null) return;
            ApplyToolbarTexts();
            ApplySyncViewButtonText();
            RefreshTabBar();   // "Close tab" tooltips
        }

        void RefreshBreadcrumb()
        {
            if (_breadcrumb == null) return;
            _breadcrumb.Clear();
            if (_context == null || !_context.HasController) return;

            // Home is not inside any layer, so there is no trail to draw — just where we are.
            if (_context.IsHomeSelected)
            {
                var home = new Label(L.Tr("Home"));
                home.style.unityFontStyleAndWeight = FontStyle.Bold;
                home.style.marginLeft = 6;
                home.style.marginRight = 2;
                _breadcrumb.Add(home);
                return;
            }

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
                    tooltip = L.Tr("Return to the state machine view"),
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

        void ResetSyncGraphPeek() => _showSyncGraph = false;

        /// <summary>Picks the centre view: the home screen when it is selected, the blend tree
        /// editor when drilled into one, the async-sync settings panel when sitting at the root
        /// of a generated sync layer (unless the user asked to peek at the graph), the state
        /// machine graph otherwise.</summary>
        void RefreshGraphVisibility()
        {
            if (_graphView == null || _blendTreeView == null) return;

            // Home wins over everything: it is about the controller, so none of the views of a
            // single layer (nor the way back into one of them) belongs on screen beside it.
            bool home = _context != null && _context.IsHomeSelected;
            if (_homePanel != null)
            {
                _homePanel.style.display = home ? DisplayStyle.Flex : DisplayStyle.None;
                if (home) _homePanel.Refresh();
            }
            if (home)
            {
                _graphView.style.display = DisplayStyle.None;
                _blendTreeView.style.display = DisplayStyle.None;
                if (_asyncSyncPanel != null) _asyncSyncPanel.style.display = DisplayStyle.None;
                if (_syncViewButton != null) _syncViewButton.style.display = DisplayStyle.None;
                RefreshRightColumnLayout(false);
                return;
            }

            bool inBlendTree = _context != null && _context.IsViewingBlendTree;

            // Only the layer ROOT swaps to the settings panel — a generated layer has no sub
            // machines, so a drilled-down path means the user is somewhere hand-made. The path
            // always carries the layer's root machine at index 0, so "not drilled" is one entry
            // and not none: at zero the layer has no state machine at all, and there would be
            // nothing to match a saved setup against anyway.
            var syncConfig = !inBlendTree && _context != null
                && _context.StateMachinePath.Count <= 1
                ? AsyncSyncPanel.ConfigOf(_context.Controller, _context.CurrentLayer?.stateMachine)
                : null;
            bool showSyncPanel = syncConfig != null && !_showSyncGraph;

            _graphView.style.display = inBlendTree || showSyncPanel
                ? DisplayStyle.None : DisplayStyle.Flex;
            _blendTreeView.style.display = inBlendTree ? DisplayStyle.Flex : DisplayStyle.None;
            if (_asyncSyncPanel != null)
            {
                _asyncSyncPanel.style.display = showSyncPanel
                    ? DisplayStyle.Flex : DisplayStyle.None;
                if (showSyncPanel) _asyncSyncPanel.Refresh();
            }
            if (_syncViewButton != null)
                _syncViewButton.style.display = syncConfig != null && !showSyncPanel
                    ? DisplayStyle.Flex : DisplayStyle.None;
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

        /// <summary>
        /// Ping from the analyzer window that actually lands somewhere useful: states,
        /// sub-state machines, transitions and blend trees are located inside the controller
        /// and focused in the graph (a plain PingObject only blinks the .controller asset in
        /// the Project window, which is why sub-assets never got focused). Layer-scoped issues
        /// open their layer. Returns true when it navigated; the caller falls back to a
        /// Project ping otherwise.
        /// </summary>
        internal bool TryFocusIssue(AnalyzerIssue issue)
        {
            // Freshly opened window: CreateGUI (and with it the context) hasn't run yet.
            if (_context == null || _context.Controller == null) return false;

            var location = ControllerLocator.LocateIssue(_context.Controller, issue);
            if (location == null) return false;
            return TryNavigateTo(location.layerIndex, location.stateMachinePath, location.target);
        }

        /// <summary>Navigation entry point for the satellite windows (analyzer, clip index):
        /// jumps to the given layer / drill path and selects the target. Returns false when
        /// the window has no live context yet (freshly opened, CreateGUI pending).</summary>
        internal bool TryNavigateTo(int layerIndex, IList<AnimatorStateMachine> stateMachinePath, object target)
        {
            if (_context == null || _context.Controller == null) return false;
            // Same dance as the toolbar search: leave blend tree mode first so the state
            // machine graph that holds the hit is actually visible.
            if (_context.IsViewingBlendTree)
                _context.ExitBlendTree();
            _context.NavigateTo(layerIndex, stateMachinePath, target);
            return true;
        }

        /// <summary>Called after another window (the analyzer's Fix) mutates a controller;
        /// refreshes the graph and panels when that controller is the one on screen.</summary>
        internal void OnControllerModifiedExternally(AnimatorController controller)
        {
            if (_context == null || controller == null || _context.Controller != controller) return;
            _context.ValidatePath();
            _context.NotifyParametersChanged();
            _context.NotifyGraphStructureChanged();
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
            _asyncSyncPanel?.Refresh();
            _homePanel?.Refresh();
        }

        void OnPlayModeChanged(PlayModeStateChange change)
        {
            // Leaving play mode destroys whatever was being read; the panels must stop showing
            // its last values as though they were still live.
            _context?.Live.Clear();
            _parametersPanel?.Refresh();
            _layersPanel?.Refresh();
            _graphView?.Sync.SetRuntimePlayback(default);
            _graphView?.Sync.RequestRebuild();
        }

        void PollRuntime()
        {
            if (!EditorApplication.isPlaying || _context == null || _controller == null) return;

            // Two cadences. The panels are throttled — numbers changing more than ten times a
            // second are unreadable anyway, and each refresh repaints an IMGUI list. The graph
            // reads every tick, because a progress bar that steps ten times a second looks
            // broken rather than slow.
            if (EditorApplication.timeSinceStartup - _lastRuntimePoll >= 0.1)
            {
                _lastRuntimePoll = EditorApplication.timeSinceStartup;
                _context.Live.Poll(_controller, EditorApplication.timeSinceStartup);
                _parametersPanel?.Refresh();
                _layersPanel?.Refresh();
            }

            if (_graphView == null) return;
            _graphView.Sync.SetRuntimePlayback(
                AnimatorPlayback.Read(_context.Live.Current, _context.LayerIndex));
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
