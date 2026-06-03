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
            context.StateMachinePathChanged += _sync.RequestRebuild;
            context.LayersChanged += _sync.RequestRebuild;
            context.GraphStructureChanged += _sync.RequestRebuild;
            context.SelectionChanged += OnContextSelectionChanged;
            context.FrameRequested += FrameOn;
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

        public List<AnimatorState> GetSelectedStates()
        {
            var states = new List<AnimatorState>();
            foreach (var s in selection)
                if (s is StateNode sn && sn.State != null)
                    states.Add(sn.State);
            return states;
        }

        // ---- input -----------------------------------------------------------

        void OnKeyDown(KeyDownEvent evt)
        {
            // While an inline rename (or any text field) has focus, leave the keyboard to it.
            if (IsEditingText()) return;

            if (evt.keyCode == KeyCode.F2)
            {
                // F2 renames the selected state; Ctrl/Cmd+F2 renames its clip.
                if (BeginRenameSelectedState(clip: evt.ctrlKey || evt.commandKey))
                    evt.StopPropagation();
                return;
            }

            if (!(evt.ctrlKey || evt.commandKey)) return;
            if (evt.keyCode == KeyCode.C)
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
        }

        /// <summary>Non-default transition edges (each holding ≥1 transition) in the current selection.</summary>
        List<TransitionEdge> GetSelectedTransitionEdges()
        {
            var result = new List<TransitionEdge>();
            foreach (var s in selection)
                if (s is TransitionEdge te && !te.IsDefaultEdge && te.Transitions.Count > 0)
                    result.Add(te);
            return result;
        }

        // Ctrl+C / Ctrl+V act on transitions when transition edges are selected, otherwise on states.
        // The two clipboards are separate and paste is chosen by what's selected, so state and
        // transition copy/paste never clobber or shadow each other.

        void CopySelection()
        {
            var edges = GetSelectedTransitionEdges();
            if (edges.Count > 0) _sync.CopyTransitionsFromEdges(edges);
            else _sync.CopySelectedStates();
        }

        void PasteSelection()
        {
            var edges = GetSelectedTransitionEdges();
            if (edges.Count > 0)
            {
                // A transition is selected: Ctrl+V pastes the copied transition's settings onto it.
                // Don't fall back to pasting states over a selected transition.
                if (TransitionClipboard.HasData) _sync.PasteTransitionSettingsOntoEdges(edges);
            }
            else
            {
                _sync.PasteStates(_lastMouseGraphPosition);
            }
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

        // ---- AnimationClip drag & drop ---------------------------------------

        void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (_context.CurrentStateMachine == null || !DragHasClip())
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

            var clips = new List<AnimationClip>();
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is AnimationClip clip)
                    clips.Add(clip);
            if (clips.Count == 0)
                return;

            DragAndDrop.AcceptDrag();
            evt.StopPropagation();

            var targetNode = ResolveTarget<StateNode>(evt.target as VisualElement);
            using (new UndoScope(clips.Count > 1 ? "Drop Animation Clips" : "Drop Animation Clip"))
            {
                if (targetNode != null)
                {
                    // Dropped onto an existing state: replace its motion with the first clip.
                    _sync.AssignMotion(targetNode.State, clips[0]);
                    _context.Select(targetNode.State);
                }
                else
                {
                    // Dropped onto empty space: create one state per clip, stacked downward.
                    var origin = contentViewContainer.WorldToLocal(evt.mousePosition);
                    AnimatorState firstState = null;
                    for (int i = 0; i < clips.Count; i++)
                    {
                        var state = _sync.CreateStateWithClip(origin + new Vector2(0f, i * 68f), clips[i]);
                        if (firstState == null) firstState = state;
                    }
                    if (firstState != null) _context.Select(firstState);
                }
            }
            _sync.RequestRebuild();
        }

        /// <summary>Highlights the state node currently under a clip being dragged.</summary>
        void SetDropHover(StateNode node)
        {
            if (_dropHoverNode == node) return;
            _dropHoverNode?.SetDropTarget(false);
            _dropHoverNode = node;
            _dropHoverNode?.SetDropTarget(true);
        }

        /// <summary>True when the active drag carries at least one AnimationClip.</summary>
        static bool DragHasClip()
        {
            var refs = DragAndDrop.objectReferences;
            if (refs != null)
                foreach (var obj in refs)
                    if (obj is AnimationClip)
                        return true;
            return false;
        }

        // ---- contextual menu -------------------------------------------------

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            var edge = ResolveTargetEdge(evt.target as VisualElement);
            if (edge != null && !edge.IsDefaultEdge)
            {
                // If the right-clicked edge is part of a larger multi-edge selection, the
                // menu acts on every selected edge; otherwise just on the clicked one. This
                // matches how Unity's other contextual menus behave when right-clicking
                // inside an existing selection.
                var selectedEdges = GetSelectedEdges();
                if (!selectedEdges.Contains(edge))
                    selectedEdges = new List<TransitionEdge> { edge };
                BuildTransitionMenu(evt, edge, selectedEdges);
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

                CountConnectedTransitions(stateNode, out int incoming, out int outgoing, out int connected);
                evt.menu.AppendAction("Select Transitions/Incoming (" + incoming + ")",
                    _ => SelectTransitions(stateNode, incoming: true, outgoing: false),
                    incoming > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Select Transitions/Outgoing (" + outgoing + ")",
                    _ => SelectTransitions(stateNode, incoming: false, outgoing: true),
                    outgoing > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction("Select Transitions/All Connected (" + connected + ")",
                    _ => SelectTransitions(stateNode, incoming: true, outgoing: true),
                    connected > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

                int pasted = TransitionClipboard.Count;
                string pasteSuffix = pasted > 1 ? " (" + pasted + ")" : string.Empty;
                evt.menu.AppendAction(
                    "Paste Transition/This State → Original Destinations" + pasteSuffix,
                    _ => _sync.PasteTransitionsWithStateAsSource(stateNode.State),
                    TransitionClipboard.HasDestinationContext
                        ? DropdownMenuAction.Status.Normal
                        : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction(
                    "Paste Transition/Original Sources → This State" + pasteSuffix,
                    _ => _sync.PasteTransitionsWithStateAsDestination(stateNode.State),
                    TransitionClipboard.HasSourceContext
                        ? DropdownMenuAction.Status.Normal
                        : DropdownMenuAction.Status.Disabled);
            }

            evt.menu.AppendSeparator();
            // Align acts on the current multi-selection, so it grays out until ≥2 states are selected.
            evt.menu.AppendAction("Align horizontal",
                _ => AlignSelectedStates(GraphLayout.AlignAxis.Row), AlignStatus);
            evt.menu.AppendAction("Align vertical",
                _ => AlignSelectedStates(GraphLayout.AlignAxis.Column), AlignStatus);
            evt.menu.AppendAction("Frame All", _ => FrameAll());

            if (stateCount == 1)
            {
                // Destructive (removes every transition touching the state), so it sits at the very
                // bottom of the menu where it can't be triggered by accident.
                var stateNode = FirstSelected<StateNode>();
                CountConnectedTransitions(stateNode, out _, out _, out int connected);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction("Disconnect All", _ => DisconnectStateNode(stateNode),
                    connected > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }
        }

        void AlignSelectedStates(GraphLayout.AlignAxis axis)
        {
            GraphLayout.Align(_context.CurrentStateMachine, GetSelectedStates(), axis);
            _sync.RequestRebuild();
        }

        DropdownMenuAction.Status AlignStatus(DropdownMenuAction _) =>
            GetSelectedStates().Count >= 2 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;

        /// <summary>Removes every (non-default) transition entering or leaving the state.</summary>
        void DisconnectStateNode(StateNode node)
        {
            var toRemove = new List<GraphElement>();
            edges.ForEach(e =>
            {
                if (e is TransitionEdge te && !te.IsDefaultEdge &&
                    (te.input?.node == node || te.output?.node == node))
                    toRemove.Add(te);
            });
            if (toRemove.Count == 0) return;
            using (new UndoScope("Disconnect All"))
                _sync.HandleChange(new GraphViewChange { elementsToRemove = toRemove });
            foreach (var ge in toRemove) RemoveElement(ge);
        }

        // ---- transition bulk selection ---------------------------------------

        /// <summary>Counts the non-default transition edges entering, leaving and touching the state.</summary>
        void CountConnectedTransitions(StateNode stateNode, out int incoming, out int outgoing, out int connected)
        {
            int inc = 0, outg = 0, conn = 0;
            edges.ForEach(e =>
            {
                if (!(e is TransitionEdge te) || te.IsDefaultEdge) return;
                bool isIncoming = te.input?.node == stateNode;
                bool isOutgoing = te.output?.node == stateNode;
                if (isIncoming) inc++;
                if (isOutgoing) outg++;
                if (isIncoming || isOutgoing) conn++;
            });
            incoming = inc;
            outgoing = outg;
            connected = conn;
        }

        /// <summary>Replaces the graph selection with the transition edges connected to the state.</summary>
        void SelectTransitions(StateNode stateNode, bool incoming, bool outgoing)
        {
            ClearSelection();
            edges.ForEach(e =>
            {
                if (!(e is TransitionEdge te) || te.IsDefaultEdge) return;
                if ((incoming && te.input?.node == stateNode) ||
                    (outgoing && te.output?.node == stateNode))
                    AddToSelection(te);
            });
        }

        // ---- transition edge menu --------------------------------------------

        /// <summary>Walks up from the right-clicked element to the transition edge it belongs to.</summary>
        static TransitionEdge ResolveTargetEdge(VisualElement element) => ResolveTarget<TransitionEdge>(element);

        /// <summary>Walks up from <paramref name="element"/> to the first ancestor of type T.</summary>
        static T ResolveTarget<T>(VisualElement element) where T : VisualElement
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = element.parent;
            }
            return null;
        }

        void BuildTransitionMenu(ContextualMenuPopulateEvent evt, TransitionEdge edge,
            List<TransitionEdge> selectedEdges)
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

            int copyCount = 0;
            foreach (var e in selectedEdges)
                if (!e.IsDefaultEdge) copyCount += e.Transitions.Count;
            string copySuffix = copyCount > 1 ? " (" + copyCount + ")" : string.Empty;
            evt.menu.AppendAction("Copy Transition" + copySuffix,
                _ => _sync.CopyTransitionsFromEdges(selectedEdges),
                copyCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
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
