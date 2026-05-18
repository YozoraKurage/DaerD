using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>The GraphView surface that renders and edits one state machine.</summary>
    class AnimatorGraphView : GraphView
    {
        readonly DaerDContext _context;
        readonly GraphSync _sync;
        readonly NodeSearchProvider _searchProvider;

        bool _syncingSelection;
        Vector2 _lastMouseGraphPosition;

        public EditorWindowOwner Owner { get; set; }

        public GraphSync Sync => _sync;

        public AnimatorGraphView(DaerDContext context)
        {
            _context = context;
            _sync = new GraphSync(context, this);

            style.flexGrow = 1;
            focusable = true;

            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();

            _searchProvider = ScriptableObject.CreateInstance<NodeSearchProvider>();
            _searchProvider.Init(OnSearchSelect);

            graphViewChanged = _sync.HandleChange;
            nodeCreationRequest = ctx =>
                SearchWindow.Open(new SearchWindowContext(ctx.screenMousePosition), _searchProvider);

            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<MouseMoveEvent>(e =>
                _lastMouseGraphPosition = contentViewContainer.WorldToLocal(e.mousePosition));

            context.ControllerChanged += _sync.RequestRebuild;
            context.LayerChanged += _sync.RequestRebuild;
            context.StateMachinePathChanged += _sync.RequestRebuild;
            context.LayersChanged += _sync.RequestRebuild;
            context.GraphStructureChanged += _sync.RequestRebuild;
            context.SelectionChanged += OnContextSelectionChanged;
        }

        public void Cleanup()
        {
            if (_searchProvider != null)
                Object.DestroyImmediate(_searchProvider);
        }

        public Vector2 ScreenToGraphPosition(Vector2 screenPosition)
        {
            if (Owner?.Window == null) return _lastMouseGraphPosition;
            var root = Owner.Window.rootVisualElement;
            var windowLocal = root.ChangeCoordinatesTo(root.parent, screenPosition - Owner.Window.position.position);
            return contentViewContainer.WorldToLocal(windowLocal);
        }

        // ---- port compatibility ---------------------------------------------

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var compatible = new List<Port>();
            ports.ForEach(port =>
            {
                if (port.direction != startPort.direction && port != startPort)
                    compatible.Add(port);
            });
            return compatible;
        }

        // ---- selection sync --------------------------------------------------

        public override void AddToSelection(ISelectable selectable)
        {
            base.AddToSelection(selectable);
            NotifyContextOfSelection();
        }

        public override void RemoveFromSelection(ISelectable selectable)
        {
            base.RemoveFromSelection(selectable);
            NotifyContextOfSelection();
        }

        public override void ClearSelection()
        {
            base.ClearSelection();
            NotifyContextOfSelection();
        }

        public void SetSelectionSilently(GraphElement element)
        {
            _syncingSelection = true;
            base.ClearSelection();
            if (element != null) base.AddToSelection(element);
            _syncingSelection = false;
        }

        void NotifyContextOfSelection()
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            _context.Select(ResolveSelectionModel());
            _syncingSelection = false;
        }

        object ResolveSelectionModel()
        {
            if (selection.Count == 0) return null;
            switch (selection[0])
            {
                case StateNode sn: return sn.State;
                case SubStateMachineNode mn: return mn.StateMachine;
                case TransitionEdge te: return te;
                case SpecialNode spn: return spn.Kind;
            }
            return null;
        }

        void OnContextSelectionChanged()
        {
            if (_syncingSelection) return;
            _syncingSelection = true;
            base.ClearSelection();
            var node = _sync.FindNode(_context.Selection);
            if (node != null)
            {
                base.AddToSelection(node);
            }
            else if (_context.Selection is TransitionEdge edge)
            {
                base.AddToSelection(edge);
            }
            else if (_context.Selection is AnimatorTransitionBase transition)
            {
                foreach (var e in _sync.Edges)
                    if (e.Transitions.Contains(transition)) { base.AddToSelection(e); break; }
            }
            _syncingSelection = false;
        }

        public List<TransitionEdge> GetSelectedEdges()
        {
            var edges = new List<TransitionEdge>();
            foreach (var s in selection)
                if (s is TransitionEdge te)
                    edges.Add(te);
            return edges;
        }

        // ---- input -----------------------------------------------------------

        void OnKeyDown(KeyDownEvent evt)
        {
            if (!(evt.ctrlKey || evt.commandKey)) return;
            if (evt.keyCode == KeyCode.C)
            {
                _sync.CopySelectedStates();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.V)
            {
                _sync.PasteStates(_lastMouseGraphPosition);
                evt.StopPropagation();
            }
        }

        void OnSearchSelect(string mode, Vector2 screenPosition)
        {
            var graphPosition = ScreenToGraphPosition(screenPosition);
            switch (mode)
            {
                case "state":
                case "state-clip":
                case "state-blendtree":
                    var state = _sync.CreateState(graphPosition, mode);
                    if (state != null) _context.Select(state);
                    _sync.RequestRebuild();
                    break;
                case "ssm":
                    _sync.CreateSubStateMachine(graphPosition);
                    _sync.RequestRebuild();
                    break;
                case "paste":
                    _sync.PasteStates(graphPosition);
                    break;
            }
        }

        // ---- contextual menu -------------------------------------------------

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            var edge = ResolveTargetEdge(evt.target as VisualElement);
            if (edge != null && !edge.IsDefaultEdge)
            {
                BuildTransitionMenu(evt, edge);
                return;
            }

            var graphPosition = _lastMouseGraphPosition;

            evt.menu.AppendAction("Create State", _ => CreateStateAt(graphPosition, "state"));
            evt.menu.AppendAction("Create Blend Tree State", _ => CreateStateAt(graphPosition, "state-blendtree"));
            evt.menu.AppendAction("Create Sub-State Machine", _ =>
            {
                _sync.CreateSubStateMachine(graphPosition);
                _sync.RequestRebuild();
            });

            evt.menu.AppendSeparator();

            int stateCount = CountSelected<StateNode>();
            evt.menu.AppendAction("Copy State(s)", _ => _sync.CopySelectedStates(),
                stateCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction("Paste State(s)", _ => _sync.PasteStates(graphPosition),
                StateClipboard.HasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction("Delete", _ => DeleteCurrentSelection(),
                HasDeletableSelection() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            if (stateCount == 1)
            {
                evt.menu.AppendSeparator();
                var stateNode = FirstSelected<StateNode>();
                evt.menu.AppendAction("Set as Default State", _ => _sync.SetDefaultState(stateNode.State));
            }

            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Auto Layout/Grid", _ =>
            {
                GraphLayout.Grid(_context.CurrentStateMachine);
                _sync.RequestRebuild();
            });
            evt.menu.AppendAction("Auto Layout/Hierarchical", _ =>
            {
                GraphLayout.Hierarchical(_context.CurrentStateMachine);
                _sync.RequestRebuild();
            });
            evt.menu.AppendAction("Frame All", _ => FrameAll());
        }

        // ---- transition edge menu --------------------------------------------

        /// <summary>Walks up from the right-clicked element to the transition edge it belongs to.</summary>
        static TransitionEdge ResolveTargetEdge(VisualElement element)
        {
            while (element != null)
            {
                if (element is TransitionEdge edge) return edge;
                element = element.parent;
            }
            return null;
        }

        void BuildTransitionMenu(ContextualMenuPopulateEvent evt, TransitionEdge edge)
        {
            int count = edge.Transitions.Count;
            string suffix = count > 1 ? " (" + count + ")" : string.Empty;

            evt.menu.AppendAction("Reverse Transition" + suffix,
                _ => _sync.ReverseEdge(edge),
                _sync.CanReverseEdge(edge) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            var targets = _sync.RedirectTargets(edge);
            if (targets.Count == 0)
            {
                evt.menu.AppendAction("Redirect Transition" + suffix, _ => { }, DropdownMenuAction.Status.Disabled);
            }
            else
            {
                foreach (var target in targets)
                {
                    var destination = target;
                    evt.menu.AppendAction(
                        "Redirect Transition" + suffix + "/" + MenuEscape(GraphSync.NodeLabel(destination)),
                        _ => _sync.RedirectEdge(edge, destination));
                }
            }

            evt.menu.AppendAction("Replicate Transition" + suffix, _ => _sync.ReplicateEdge(edge));
        }

        /// <summary>Neutralises '/' in node names so they don't spawn unintended submenus.</summary>
        static string MenuEscape(string name) =>
            string.IsNullOrEmpty(name) ? "?" : name.Replace('/', '\u2215');

        void CreateStateAt(Vector2 graphPosition, string mode)
        {
            var state = _sync.CreateState(graphPosition, mode);
            if (state != null) _context.Select(state);
            _sync.RequestRebuild();
        }

        void DeleteCurrentSelection()
        {
            var toRemove = new List<GraphElement>();
            foreach (var s in selection)
                if (s is GraphElement ge && (ge.capabilities & Capabilities.Deletable) != 0)
                    toRemove.Add(ge);
            if (toRemove.Count == 0) return;

            var change = new GraphViewChange { elementsToRemove = toRemove };
            _sync.HandleChange(change);
            foreach (var ge in toRemove)
                RemoveElement(ge);
        }

        bool HasDeletableSelection()
        {
            foreach (var s in selection)
                if (s is GraphElement ge && (ge.capabilities & Capabilities.Deletable) != 0)
                    return true;
            return false;
        }

        int CountSelected<T>() where T : class
        {
            int count = 0;
            foreach (var s in selection)
                if (s is T) count++;
            return count;
        }

        T FirstSelected<T>() where T : class
        {
            foreach (var s in selection)
                if (s is T match) return match;
            return null;
        }
    }

    /// <summary>Small indirection so the graph view can reach its hosting window for coordinate math.</summary>
    class EditorWindowOwner
    {
        public UnityEditor.EditorWindow Window;
    }
}
