using System.Collections.Generic;
using UnityEditor;
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
        readonly GraphContextMenu _menu;

        bool _syncingSelection;
        Vector2 _lastMouseGraphPosition;
        Vector2 _lastMouseWorld;
        StateNode _dropHoverNode;

        public EditorWindowOwner Owner { get; set; }

        public GraphSync Sync => _sync;

        public AnimatorGraphView(DaerDContext context)
        {
            _context = context;
            _sync = new GraphSync(context, this);
            _menu = new GraphContextMenu(this, context, _sync);

            style.flexGrow = 1;
            focusable = true;

            // Much wider than the stock 0.25–1.0 range: zoom far out to read big state machines and
            // further in for detail.
            SetupZoom(0.05f, 3.0f);
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
            // Trickle-down so the position is captured even while a port's edge-drag holds the mouse
            // (used by the drop-on-node-body fallback).
            RegisterCallback<MouseMoveEvent>(OnTrackMouse, TrickleDown.TrickleDown);
            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
            RegisterCallback<DragLeaveEvent>(_ => SetDropHover(null));

            context.ControllerChanged += _sync.RequestRebuild;
            context.LayerChanged += _sync.RequestRebuild;
            // Switching layers happens in the (IMGUI) layers panel, which takes keyboard focus
            // with it — the next Ctrl+V would then never reach the graph. Take focus back so
            // "copy here, switch layer, paste there" works as one gesture.
            context.LayerChanged += TakeKeyboardFocus;
            context.StateMachinePathChanged += TakeKeyboardFocus;
            // Cross-product source marks only make sense within one layer: a transition cannot
            // point at a state of another layer or controller.
            context.ControllerChanged += GraphContextMenu.ClearMarkedSources;
            context.LayerChanged += GraphContextMenu.ClearMarkedSources;
            context.StateMachinePathChanged += _sync.RequestRebuild;
            context.LayersChanged += _sync.RequestRebuild;
            context.GraphStructureChanged += _sync.RequestRebuild;
            context.SelectionChanged += OnContextSelectionChanged;
            context.FrameRequested += FrameOn;
            context.GraphVisualsChanged += OnGraphVisualsChanged;
            context.NoteEditRequested += note => _sync.FindNoteNode(note)?.BeginEdit();
            // Registered, not subscribed: the panels read these while repainting and need the live
            // selection — see the provider fields on DaerDContext for why a push would go stale.
            context.SelectedStatesProvider = GetSelectedStates;
            context.SelectedTransitionGroupsProvider = SelectedTransitionGroups;
        }

        /// <summary>
        /// Repaints what the graph draws for one model object (or, for a
        /// <see cref="DaerDContext.GraphVisuals"/> target, for all of them) without rebuilding —
        /// the inspector edited a field that only changes how something looks.
        /// </summary>
        void OnGraphVisualsChanged(object target)
        {
            switch (target)
            {
                case AnimatorState state:
                    _sync.RefreshStateNode(state);
                    break;
                case AnimatorTransitionBase transition:
                    _sync.FindEdge(transition)?.Refresh();
                    break;
                case GraphFrameData.Frame frame:
                    _sync.RefreshFrameVisuals(frame);
                    break;
                case GraphFrameData.Note note:
                    _sync.RefreshNoteVisuals(note);
                    break;
                case DaerDContext.GraphVisuals bulk:
                    RefreshAll(bulk);
                    break;
            }
        }

        /// <summary>The two whole-graph repaints: every state node, or every transition edge.</summary>
        void RefreshAll(DaerDContext.GraphVisuals target)
        {
            switch (target)
            {
                case DaerDContext.GraphVisuals.AllStateNodes:
                    _sync.RefreshAllStateNodes();
                    break;
                case DaerDContext.GraphVisuals.AllEdges:
                    _sync.RefreshAllEdges();
                    break;
            }
        }

        /// <summary>
        /// Hands keyboard focus back to the graph, deferred so it lands after the panel that
        /// triggered the change has finished its own event. Skipped while a text field is being
        /// edited (an inline rename must keep the keyboard) or while the view is detached.
        /// </summary>
        void TakeKeyboardFocus()
        {
            schedule.Execute(() =>
            {
                if (panel == null || IsEditingText()) return;
                Focus();
            }).ExecuteLater(1);
        }

        /// <summary>Centers the view on the node representing <paramref name="model"/>.</summary>
        public void FrameOn(object model)
        {
            if (model == null) return;
            var node = _sync.FindNode(model);
            if (node == null && model is AnimatorTransitionBase transition)
            {
                var edge = _sync.FindEdge(transition);
                if (edge == null) return;
                schedule.Execute(() =>
                {
                    ClearSelection();
                    AddToSelection(edge);
                    FrameSelection();
                }).ExecuteLater(20);
                return;
            }
            if (node == null) return;
            // Defer one frame so a rebuild triggered in the same call has time to place the
            // node before we measure its bounds.
            schedule.Execute(() =>
            {
                ClearSelection();
                AddToSelection(node);
                FrameSelection();
            }).ExecuteLater(20);
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

        void OnTrackMouse(MouseMoveEvent evt)
        {
            _lastMouseWorld = evt.mousePosition;
            _lastMouseGraphPosition = contentViewContainer.WorldToLocal(evt.mousePosition);
        }

        /// <summary>Where the pointer last was, in graph coordinates — the spot a context-menu
        /// entry drops what it creates, snapshotted before the menu takes the pointer.</summary>
        public Vector2 LastMouseGraphPosition => _lastMouseGraphPosition;

        /// <summary>The top-most graph node whose visible rectangle contains <paramref name="worldPosition"/>, if any.</summary>
        public GraphNodeBase NodeAtWorldPosition(Vector2 worldPosition)
        {
            GraphNodeBase found = null;
            // Later siblings render on top, so keep overwriting: the last hit is the top-most node.
            nodes.ForEach(node =>
            {
                if (node is GraphNodeBase gnb && gnb.worldBound.Contains(worldPosition))
                    found = gnb;
            });
            return found;
        }

        /// <summary>
        /// Completes a left-drag transition that was released on a node's body rather than precisely
        /// on its input port. The dragged-from port fixes one end; the node under the cursor is the
        /// other. Invoked by <see cref="NodeBodyEdgeConnectorListener"/>.
        /// </summary>
        public void CompleteEdgeDropOnNode(Edge edge, Vector2 dropWorld)
        {
            if (edge == null) return;
            // Prefer the exact release point Unity reports; fall back to the last tracked position.
            var dropNode = NodeAtWorldPosition(dropWorld) ?? NodeAtWorldPosition(_lastMouseWorld);
            if (dropNode == null) return;

            GraphNodeBase source, destination;
            if (edge.output != null)        // dragged out from a source's output port
            {
                source = edge.output.node as GraphNodeBase;
                destination = dropNode;
            }
            else if (edge.input != null)    // dragged in toward a destination's input port
            {
                destination = edge.input.node as GraphNodeBase;
                source = dropNode;
            }
            else return;

            if (source == null || destination == null || source == destination) return;
            if (!TransitionConnect.CanConnect(source, destination)) return;

            _sync.CreateTransition(source, destination);
            // Defer the rebuild: this runs inside Unity's EdgeDragHelper.HandleMouseUp, which keeps
            // using the drag candidate / ports after we return. Rebuilding synchronously would tear
            // those down mid-event. (Mirrors the port-to-port path, which also RequestRebuilds.)
            _sync.RequestRebuild();
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

        public void SetSelectionSilently(IEnumerable<GraphElement> elements)
        {
            _syncingSelection = true;
            base.ClearSelection();
            if (elements != null)
                foreach (var element in elements)
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
                case FrameNode fn: return fn.Frame;
                case NoteNode nn: return nn.Note;
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
            else if (_context.Selection is GraphFrameData.Note note)
            {
                var noteNode = _sync.FindNoteNode(note);
                if (noteNode != null) base.AddToSelection(noteNode);
            }
            else if (_context.Selection is GraphFrameData.Frame frame)
            {
                var frameNode = _sync.FindFrameNode(frame);
                if (frameNode != null) base.AddToSelection(frameNode);
            }
            _syncingSelection = false;
        }

        /// <summary>
        /// The current selection sorted into model buckets — one pass over the selection instead of
        /// a type switch per accessor. Accessors that need the graph elements themselves (rather
        /// than the animator objects behind them), or that reject a node by its type regardless of
        /// whether its model still exists, keep walking the raw selection instead.
        /// </summary>
        GraphSelectionSet SelectionSet() => new GraphSelectionSet(selection);

        public List<TransitionEdge> GetSelectedEdges() => SelectionSet().TransitionEdges;

        public List<AnimatorState> GetSelectedStates() => SelectionSet().States;

        /// <summary>
        /// The transitions the inspector should offer for editing, one entry per selected edge and
        /// reduced to model data so no <see cref="TransitionEdge"/> reaches a panel. With no edge
        /// selected in the graph it falls back to the edge behind
        /// <see cref="DaerDContext.Selection"/>: the shared selection can name a transition (or an
        /// edge) that the graph itself is not highlighting.
        /// </summary>
        List<TransitionGroup> SelectedTransitionGroups()
        {
            var edges = GetSelectedEdges();
            if (edges.Count == 0)
            {
                var fallback = _context.Selection as TransitionEdge
                    ?? (_context.Selection is AnimatorTransitionBase tb ? _sync.FindEdge(tb) : null);
                if (fallback != null) edges.Add(fallback);
            }
            var groups = new List<TransitionGroup>(edges.Count);
            foreach (var edge in edges)
                groups.Add(new TransitionGroup(
                    GraphNodeBase.EndOf(edge.output?.node as GraphNodeBase),
                    edge.IsDefaultEdge, edge.Transitions));
            return groups;
        }

        /// <summary>
        /// Every selected node that can take part in transitions: plain states, sub-state machines,
        /// and the Entry / Exit / Any State pseudo-nodes. Order matches the click order so chain /
        /// fan connects respect what the user picked first.
        /// </summary>
        public List<GraphNodeBase> GetSelectedConnectables()
        {
            var result = new List<GraphNodeBase>();
            foreach (var s in selection)
            {
                if (s is StateNode sn && sn.State != null) result.Add(sn);
                else if (s is SubStateMachineNode mn && mn.StateMachine != null) result.Add(mn);
                else if (s is SpecialNode spn) result.Add(spn);
            }
            return result;
        }

        // ---- input -----------------------------------------------------------

        void OnKeyDown(KeyDownEvent evt)
        {
            // While an inline rename (or any text field) has focus, leave the keyboard to it.
            if (IsEditingText()) return;

            if (evt.keyCode == KeyCode.F2)
            {
                // F2 renames the selected state (Ctrl/Cmd+F2: its clip), retitles the selected
                // frame, or edits the selected note — whichever single element is selected.
                if (selection.Count == 1 && selection[0] is FrameNode frameNode)
                {
                    frameNode.BeginRename();
                    evt.StopPropagation();
                    return;
                }
                if (selection.Count == 1 && selection[0] is NoteNode noteNode)
                {
                    noteNode.BeginEdit();
                    evt.StopPropagation();
                    return;
                }
                if (BeginRenameSelectedState(clip: evt.ctrlKey || evt.commandKey))
                    evt.StopPropagation();
                return;
            }

            if (!(evt.ctrlKey || evt.commandKey))
            {
                // I / O / P select the incoming / outgoing / all transitions of the selected
                // state nodes — quick keyboard access to the context-menu selections.
                if (evt.keyCode == KeyCode.I && SelectTransitionsOfSelection(incoming: true, outgoing: false))
                    evt.StopPropagation();
                else if (evt.keyCode == KeyCode.O && SelectTransitionsOfSelection(incoming: false, outgoing: true))
                    evt.StopPropagation();
                else if (evt.keyCode == KeyCode.P && SelectTransitionsOfSelection(incoming: true, outgoing: true))
                    evt.StopPropagation();
                // F frames the selection and A frames everything, the way every other graph
                // view in the editor does it. F with nothing selected has nothing to aim at,
                // so it falls through to framing everything rather than doing nothing.
                else if (evt.keyCode == KeyCode.F)
                {
                    if (selection.Count > 0) FrameSelection();
                    else FrameAll();
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.A)
                {
                    FrameAll();
                    evt.StopPropagation();
                }
                // M marks, T wires. Both are the keyboard face of the Connect States menu,
                // which otherwise takes two trips through a submenu to join two states.
                else if (evt.keyCode == KeyCode.M && _menu.MarkSelectedAsSources())
                {
                    Owner?.Window?.ShowNotification(new GUIContent(
                        L.Tr("{0} marked as sources. Select the destinations and press T.",
                            GraphContextMenu.MarkedSourceCount)));
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.T && _menu.ConnectSelection())
                {
                    evt.StopPropagation();
                }
                else if (MoveSelectionKey(evt.keyCode, out var direction) && MoveSelection(direction))
                {
                    evt.StopPropagation();
                }
                return;
            }
            if (evt.keyCode == KeyCode.F)
            {
                // The search box lives in the toolbar, which the graph cannot reach; the
                // context carries the request to whoever owns the box.
                _context.RequestSearch();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.C)
            {
                CopySelection();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.V)
            {
                if (evt.shiftKey) PasteTransitionsAsNew();
                else PasteSelection();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.D)
            {
                DuplicateSelectedStates();
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.A)
            {
                if (evt.shiftKey) SelectAllTransitions();
                else SelectAllNodes();
                evt.StopPropagation();
            }
        }

        /// <summary>The graph direction an arrow key means; false for any other key. Y grows
        /// downwards here, so "up" is negative.</summary>
        static bool MoveSelectionKey(KeyCode key, out Vector2 direction)
        {
            switch (key)
            {
                case KeyCode.UpArrow: direction = new Vector2(0f, -1f); return true;
                case KeyCode.DownArrow: direction = new Vector2(0f, 1f); return true;
                case KeyCode.LeftArrow: direction = new Vector2(-1f, 0f); return true;
                case KeyCode.RightArrow: direction = new Vector2(1f, 0f); return true;
                default: direction = Vector2.zero; return false;
            }
        }

        /// <summary>
        /// Arrow keys walk from the selected node to its neighbour in that direction. Only from a
        /// single node: with several selected there is no "from" to measure against.
        ///
        /// Nodes are scored by how far along the direction they sit plus twice how far off to the
        /// side, so a near neighbour slightly off-axis beats a distant one dead ahead. Anything
        /// more sideways than forwards is held back and used only when nothing is ahead of it —
        /// otherwise pressing Up in a wide layout would jump to something that is really beside.
        /// </summary>
        bool MoveSelection(Vector2 direction)
        {
            if (selection.Count != 1 || !(selection[0] is GraphNodeBase current)) return false;

            var candidates = new List<GraphNodeBase>();
            var centres = new List<Vector2>();
            nodes.ForEach(node =>
            {
                if (!(node is GraphNodeBase candidate) || candidate == current) return;
                candidates.Add(candidate);
                centres.Add(candidate.GetPosition().center);
            });

            int index = PickNeighbour(current.GetPosition().center, centres, direction);
            if (index < 0) return false;

            ReplaceSelection(new List<GraphElement> { candidates[index] });
            ScrollIntoView(candidates[index]);
            return true;
        }

        /// <summary>
        /// Which of <paramref name="centres"/> the arrow key lands on, or -1 when nothing lies
        /// that way. Scored by distance along the direction plus twice the distance off to the
        /// side, so a near neighbour slightly off-axis beats a distant one dead ahead.
        /// </summary>
        public static int PickNeighbour(Vector2 from, IList<Vector2> centres, Vector2 direction)
        {
            var sideways = new Vector2(-direction.y, direction.x);
            int ahead = -1, beside = -1;
            float aheadScore = float.MaxValue, besideScore = float.MaxValue;

            for (int i = 0; i < centres.Count; i++)
            {
                Vector2 delta = centres[i] - from;
                float along = Vector2.Dot(delta, direction);
                if (along <= 1f) continue;
                float across = Mathf.Abs(Vector2.Dot(delta, sideways));
                float score = along + across * 2f;
                if (score < besideScore) { besideScore = score; beside = i; }
                // More sideways than forwards is not "the one above" — kept only for when
                // nothing is properly ahead, so an arrow key in a wide layout still moves.
                if (across > along * 2f) continue;
                if (score < aheadScore) { aheadScore = score; ahead = i; }
            }

            return ahead >= 0 ? ahead : beside;
        }

        /// <summary>
        /// Pans just enough to bring a node inside the viewport, keeping the zoom. Framing it
        /// instead would re-zoom on every arrow press, which turns walking a state machine into
        /// a series of lurches.
        /// </summary>
        void ScrollIntoView(GraphNodeBase node)
        {
            const float Margin = 48f;
            float scale = viewTransform.scale.x;
            if (scale <= 0f) return;
            Vector2 origin = viewTransform.position;

            Rect placed = node.GetPosition();
            var onScreen = new Rect(placed.position * scale + origin, placed.size * scale);
            var viewport = new Rect(Margin, Margin, layout.width - Margin * 2f, layout.height - Margin * 2f);
            if (viewport.width <= 0f || viewport.height <= 0f) return;

            Vector2 shift = Vector2.zero;
            if (onScreen.xMin < viewport.xMin) shift.x = viewport.xMin - onScreen.xMin;
            else if (onScreen.xMax > viewport.xMax) shift.x = viewport.xMax - onScreen.xMax;
            if (onScreen.yMin < viewport.yMin) shift.y = viewport.yMin - onScreen.yMin;
            else if (onScreen.yMax > viewport.yMax) shift.y = viewport.yMax - onScreen.yMax;
            if (shift == Vector2.zero) return;

            UpdateViewTransform(origin + shift, viewTransform.scale);
        }

        /// <summary>Ctrl+A: every node (states, sub-state machines, special nodes).</summary>
        void SelectAllNodes()
        {
            var wanted = new List<GraphElement>();
            nodes.ForEach(node =>
            {
                if (node is StateNode || node is SubStateMachineNode || node is SpecialNode)
                    wanted.Add(node);
            });
            ReplaceSelection(wanted);
        }

        /// <summary>Ctrl+Shift+A: every transition edge (the default-state link is not one).</summary>
        void SelectAllTransitions()
        {
            var wanted = new List<GraphElement>();
            edges.ForEach(edge =>
            {
                if (edge is TransitionEdge transitionEdge && !transitionEdge.IsDefaultEdge)
                    wanted.Add(transitionEdge);
            });
            ReplaceSelection(wanted);
        }

        /// <summary>
        /// Selects exactly <paramref name="elements"/>, which must already have been collected.
        /// Selecting a node brings it to the front of its parent, and reordering the visual tree
        /// while a UQuery is still walking it makes the walk skip elements and revisit others —
        /// the same key press then selects a different set every time. Gathering first and
        /// selecting afterwards is what makes the answer the same twice running.
        /// </summary>
        void ReplaceSelection(List<GraphElement> elements)
        {
            ClearSelection();
            foreach (var element in elements)
                AddToSelection(element);
        }

        /// <summary>
        /// Every selected node a transition can start from or land on. Not only states: a
        /// sub-state machine and the Entry / Any State nodes carry transitions too, and asking
        /// for "the transitions connected to what I have selected" means those as well.
        /// </summary>
        public HashSet<Node> SelectedTransitionEndpoints()
        {
            var endpoints = new HashSet<Node>();
            foreach (var selected in selection)
                if (selected is GraphNodeBase node) endpoints.Add(node);
            return endpoints;
        }

        /// <summary>Counts the non-default transition edges entering, leaving and touching any
        /// of <paramref name="endpoints"/>. An edge between two of them is incoming and outgoing
        /// at once, and is one connection.</summary>
        public void CountConnectedTransitions(HashSet<Node> endpoints,
            out int incoming, out int outgoing, out int connected)
        {
            int inc = 0, outg = 0, conn = 0;
            edges.ForEach(edge =>
            {
                if (!(edge is TransitionEdge te) || te.IsDefaultEdge) return;
                bool into = te.input?.node != null && endpoints.Contains(te.input.node);
                bool from = te.output?.node != null && endpoints.Contains(te.output.node);
                if (into) inc++;
                if (from) outg++;
                if (into || from) conn++;
            });
            incoming = inc;
            outgoing = outg;
            connected = conn;
        }

        /// <summary>
        /// Replaces the selection with the transitions touching <paramref name="endpoints"/>.
        /// False when that would select nothing — the keyboard shortcut then falls through
        /// instead of clearing what the user had.
        /// </summary>
        public bool SelectTransitionsOf(HashSet<Node> endpoints, bool incoming, bool outgoing)
        {
            if (endpoints.Count == 0) return false;

            var wanted = new List<GraphElement>();
            edges.ForEach(edge =>
            {
                if (!(edge is TransitionEdge te) || te.IsDefaultEdge) return;
                if ((incoming && te.input?.node != null && endpoints.Contains(te.input.node))
                    || (outgoing && te.output?.node != null && endpoints.Contains(te.output.node)))
                    wanted.Add(te);
            });
            if (wanted.Count == 0) return false;

            ReplaceSelection(wanted);
            return true;
        }

        /// <summary>I / O / P: the transitions touching whatever is selected right now.</summary>
        bool SelectTransitionsOfSelection(bool incoming, bool outgoing) =>
            SelectTransitionsOf(SelectedTransitionEndpoints(), incoming, outgoing);

        /// <summary>Duplicates the selected states in place (Ctrl+D), keeping their internal transitions.</summary>
        public void DuplicateSelectedStates()
        {
            var states = GetSelectedStates();
            if (states.Count == 0) return;
            var created = StateDuplicator.Duplicate(_context.CurrentStateMachine, states, new Vector2(40f, 40f));
            if (created.Count == 0) return;
            _sync.RequestRebuild();
            _context.Select(created[0]);
        }

        /// <summary>Non-default transition edges (each holding ≥1 transition) in the current selection.</summary>
        List<TransitionEdge> GetSelectedTransitionEdges()
        {
            var result = new List<TransitionEdge>();
            foreach (var te in SelectionSet().TransitionEdges)
                if (!te.IsDefaultEdge && te.Transitions.Count > 0)
                    result.Add(te);
            return result;
        }

        // Ctrl+C / Ctrl+V act on transitions when transition edges are selected, otherwise on the
        // canvas contents: states, frames and notes. The transition clipboard is separate and
        // paste is chosen by what's selected, so the two never clobber or shadow each other.
        // Frames and notes carry no state machine reference, so a copy taken here pastes into
        // whichever layer is open when Ctrl+V is pressed.

        /// <summary>
        /// True when the selection is nothing but transition edges. Ctrl+C/V take the transition
        /// branch only then: a rubber-band select (or "select all" followed by a drag over the
        /// canvas) picks up the edges *and* the nodes, and there the user means "copy what's on
        /// the canvas" — treating that as a transition copy silently loses the states.
        /// </summary>
        bool HasOnlyTransitionSelection()
        {
            bool anyEdge = false;
            foreach (var selected in selection)
            {
                switch (selected)
                {
                    case StateNode _:
                    case FrameNode _:
                    case NoteNode _:
                    case SubStateMachineNode _:
                        return false;
                    case TransitionEdge te when !te.IsDefaultEdge && te.Transitions.Count > 0:
                        anyEdge = true;
                        break;
                }
            }
            return anyEdge;
        }

        void CopySelection()
        {
            if (HasOnlyTransitionSelection()) _sync.CopyTransitionsFromEdges(GetSelectedTransitionEdges());
            else _sync.CopySelectedElements();
        }

        void PasteSelection()
        {
            if (HasOnlyTransitionSelection())
            {
                // Only transitions are selected: Ctrl+V pastes the copied transition's settings
                // onto them. Don't fall back to pasting states over a selected transition.
                if (TransitionClipboard.HasData)
                    _sync.PasteTransitionSettingsOntoEdges(GetSelectedTransitionEdges());
                return;
            }
            _sync.PasteStates(_lastMouseGraphPosition);
            _sync.PasteFramesAndNotes(_lastMouseGraphPosition);
        }

        void PasteTransitionsAsNew()
        {
            var edges = GetSelectedTransitionEdges();
            if (edges.Count > 0 && TransitionClipboard.HasData)
                _sync.PasteTransitionsAsNewOnEdges(edges);
        }

        /// <summary>True while a text input element (e.g. an inline rename field) holds focus.</summary>
        bool IsEditingText()
        {
            var focused = panel?.focusController?.focusedElement as VisualElement;
            return focused != null && (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null);
        }

        /// <summary>The single selected state node, or null if zero / more than one element is selected.</summary>
        StateNode SingleSelectedStateNode()
        {
            StateNode result = null;
            foreach (var selectable in selection)
            {
                if (selectable is StateNode sn)
                {
                    if (result != null) return null;   // more than one state selected
                    result = sn;
                }
                else
                {
                    return null;   // a non-state element is part of the selection
                }
            }
            return result;
        }

        /// <summary>Starts an inline rename of the selected state (or its clip). Returns true if F2 was consumed.</summary>
        bool BeginRenameSelectedState(bool clip)
        {
            var node = SingleSelectedStateNode();
            if (node?.State == null) return false;
            var state = node.State;

            if (clip)
            {
                if (!(state.motion is AnimationClip clipMotion))
                {
                    Owner?.Window?.ShowNotification(new GUIContent(state.motion is BlendTree
                        ? "This state holds a Blend Tree, not a single clip."
                        : "This state has no clip to rename."));
                    return true;
                }
                node.BeginInlineEdit(clipMotion.name,
                    value => ClipRenamer.Rename(clipMotion, value, _context), motionLabel: true);
            }
            else
            {
                node.BeginInlineEdit(state.name, value =>
                {
                    if (string.IsNullOrEmpty(value) || value == state.name) return;
                    Undo.RegisterCompleteObjectUndo(state, "Rename State");
                    state.name = value;
                    EditorUtility.SetDirty(state);
                    _context.NotifyGraphStructureChanged();
                }, motionLabel: false);
            }
            return true;
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

        // ---- Motion (AnimationClip / BlendTree) drag & drop -------------------

        void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (_context.CurrentStateMachine == null || !DragHasMotion())
                return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            SetDropHover(ResolveTarget<StateNode>(evt.target as VisualElement));
            evt.StopPropagation();
        }

        void OnDragPerform(DragPerformEvent evt)
        {
            SetDropHover(null);
            if (_context.CurrentStateMachine == null)
                return;

            var motions = new List<Motion>();
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is Motion motion)
                    motions.Add(motion);
            if (motions.Count == 0)
                return;

            DragAndDrop.AcceptDrag();
            evt.StopPropagation();

            var targetNode = ResolveTarget<StateNode>(evt.target as VisualElement);
            using (new UndoScope(motions.Count > 1 ? "Drop Motions" : "Drop Motion"))
            {
                if (targetNode != null)
                {
                    // Dropped onto an existing state: replace its motion with the first one.
                    _sync.AssignMotion(targetNode.State, motions[0]);
                    _context.Select(targetNode.State);
                }
                else
                {
                    // Dropped onto empty space: create one state per motion, stacked downward.
                    var origin = contentViewContainer.WorldToLocal(evt.mousePosition);
                    AnimatorState firstState = null;
                    for (int i = 0; i < motions.Count; i++)
                    {
                        var state = _sync.CreateStateWithMotion(origin + new Vector2(0f, i * 68f), motions[i]);
                        if (firstState == null) firstState = state;
                    }
                    if (firstState != null) _context.Select(firstState);
                }
            }
            _sync.RequestRebuild();
        }

        /// <summary>Highlights the state node currently under a motion being dragged.</summary>
        void SetDropHover(StateNode node)
        {
            if (_dropHoverNode == node) return;
            _dropHoverNode?.SetDropTarget(false);
            _dropHoverNode = node;
            _dropHoverNode?.SetDropTarget(true);
        }

        /// <summary>True when the active drag carries at least one AnimationClip or BlendTree.</summary>
        static bool DragHasMotion()
        {
            var refs = DragAndDrop.objectReferences;
            if (refs != null)
                foreach (var obj in refs)
                    if (obj is Motion)
                        return true;
            return false;
        }

        // ---- element lookup --------------------------------------------------

        /// <summary>Walks up from <paramref name="element"/> to the first ancestor of type T.</summary>
        public static T ResolveTarget<T>(VisualElement element) where T : VisualElement
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = element.parent;
            }
            return null;
        }

        // ---- contextual menu -------------------------------------------------

        /// <summary>Every entry lives in <see cref="GraphContextMenu"/>; the view only forwards.</summary>
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) => _menu.Build(evt);
    }
}
