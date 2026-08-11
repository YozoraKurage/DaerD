using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Owns the translation between an <see cref="AnimatorStateMachine"/> and the graph view:
    /// builds nodes/edges from the asset, and writes structural edits back to the asset.
    /// </summary>
    class GraphSync
    {
        readonly DaerDContext _context;
        readonly AnimatorGraphView _graphView;

        readonly Dictionary<AnimatorState, StateNode> _stateNodes = new Dictionary<AnimatorState, StateNode>();
        readonly Dictionary<AnimatorStateMachine, SubStateMachineNode> _ssmNodes = new Dictionary<AnimatorStateMachine, SubStateMachineNode>();
        readonly List<TransitionEdge> _edges = new List<TransitionEdge>();

        // States / machines nested anywhere inside a top-level child sub-state machine, mapped to
        // that child's node. Lets transitions targeting a deep state (common for Any State
        // transitions) render as an edge to the sub-state machine, like Unity's own editor.
        readonly Dictionary<AnimatorState, SubStateMachineNode> _nestedStateOwners = new Dictionary<AnimatorState, SubStateMachineNode>();
        readonly Dictionary<AnimatorStateMachine, SubStateMachineNode> _nestedMachineOwners = new Dictionary<AnimatorStateMachine, SubStateMachineNode>();

        readonly List<FrameNode> _frameNodes = new List<FrameNode>();
        readonly List<NoteNode> _noteNodes = new List<NoteNode>();

        SpecialNode _entryNode, _exitNode, _anyStateNode;
        bool _rebuildScheduled;

        // Full path hash -> the node that stands for that state on this screen. A state nested
        // inside a child sub-state machine has no node of its own here, so it maps to the box
        // that contains it — the same node its transitions already draw to.
        readonly Dictionary<int, GraphNodeBase> _runtimeNodes = new Dictionary<int, GraphNodeBase>();
        AnimatorPlayback.LayerPlayback _playback;

        readonly NodeCommands _nodes;
        readonly FrameCommands _frames;
        readonly EdgeCommands _transitions;
        readonly GraphClipboard _clipboard;

        public GraphSync(DaerDContext context, AnimatorGraphView graphView)
        {
            _context = context;
            _graphView = graphView;
            _nodes = new NodeCommands(context);
            _frames = new FrameCommands(context);
            _transitions = new EdgeCommands(context);
            _clipboard = new GraphClipboard(context, _transitions, _frames);
        }

        public IReadOnlyList<TransitionEdge> Edges => _edges;

        // ---- rebuild ---------------------------------------------------------

        public void RequestRebuild()
        {
            if (_rebuildScheduled) return;
            _rebuildScheduled = true;
            _graphView.schedule.Execute(() =>
            {
                _rebuildScheduled = false;
                Rebuild();
            });
        }

        public void Rebuild()
        {
            // Snapshot the current graph selection by underlying model so we can restore it
            // after the rebuild. Without this, Ctrl+Z and other rebuilds collapse multi-select
            // back to whatever single object Context.Selection points at.
            var capturedSelection = new GraphSelectionSet(_graphView.selection);

            _stateNodes.Clear();
            _ssmNodes.Clear();
            _nestedStateOwners.Clear();
            _nestedMachineOwners.Clear();
            _edges.Clear();
            _hoveredEdge = null;
            _frameNodes.Clear();
            _noteNodes.Clear();
            foreach (var element in _graphView.graphElements.ToList())
                _graphView.RemoveElement(element);

            var sm = _context.CurrentStateMachine;
            if (sm == null) return;

            var frameData = _frames.Find();
            if (frameData != null)
            {
                foreach (var frame in frameData.FramesIn(sm))
                {
                    FrameNode frameNode = null;
                    var capturedFrame = frame;
                    frameNode = new FrameNode(frame, () => PersistFrameGeometry(frameNode), NodesFullyInside,
                        newTitle => RenameFrame(capturedFrame, newTitle),
                        () => ToggleFrameLock(capturedFrame));
                    _frameNodes.Add(frameNode);
                    _graphView.AddElement(frameNode);
                    frameNode.SetPosition(frame.bounds);
                }
                foreach (var note in frameData.NotesIn(sm))
                {
                    NoteNode noteNode = null;
                    var capturedNote = note;
                    noteNode = new NoteNode(note, () => PersistNoteGeometry(noteNode),
                        newText => SetNoteText(capturedNote, newText));
                    _noteNodes.Add(noteNode);
                    _graphView.AddElement(noteNode);
                    noteNode.SetPosition(note.bounds);
                }
            }

            _entryNode = new SpecialNode(SpecialNodeKind.Entry);
            _exitNode = new SpecialNode(SpecialNodeKind.Exit);
            _anyStateNode = new SpecialNode(SpecialNodeKind.AnyState);
            AddNode(_entryNode, sm.entryPosition);
            AddNode(_exitNode, sm.exitPosition);
            AddNode(_anyStateNode, sm.anyStatePosition);

            foreach (var child in sm.states)
            {
                if (child.state == null) continue;
                var capturedState = child.state;
                var node = new StateNode(capturedState, s => _context.EnterBlendTree(s));
                _stateNodes[capturedState] = node;
                AddNode(node, child.position);
            }

            foreach (var child in sm.stateMachines)
            {
                if (child.stateMachine == null) continue;
                var childSm = child.stateMachine;
                var node = new SubStateMachineNode(childSm, () => _context.EnterStateMachine(childSm));
                _ssmNodes[childSm] = node;
                AddNode(node, child.position);

                foreach (var descendant in childSm.SelfAndDescendants())
                {
                    if (descendant != childSm) _nestedMachineOwners[descendant] = node;
                    foreach (var cs in descendant.states)
                        if (cs.state != null) _nestedStateOwners[cs.state] = node;
                }
            }

            if (sm.defaultState != null && _stateNodes.TryGetValue(sm.defaultState, out var defaultNode))
                CreateDefaultEdge(defaultNode);

            foreach (var pair in _stateNodes)
                pair.Value.SetIsDefault(pair.Key == sm.defaultState);

            var edgeMap = new Dictionary<(GraphNodeBase, GraphNodeBase), TransitionEdge>();
            foreach (var pair in _stateNodes)
                foreach (var t in pair.Key.transitions)
                    AddTransitionEdge(pair.Value, ResolveDestination(t), t, edgeMap);
            foreach (var t in sm.anyStateTransitions)
                AddTransitionEdge(_anyStateNode, ResolveDestination(t), t, edgeMap);
            foreach (var t in sm.entryTransitions)
                AddTransitionEdge(_entryNode, ResolveDestination(t), t, edgeMap);
            foreach (var pair in _ssmNodes)
                foreach (var t in sm.GetStateMachineTransitions(pair.Key))
                    AddTransitionEdge(pair.Value, ResolveDestination(t), t, edgeMap);

            foreach (var edge in _edges)
                edge.Refresh();

            RestoreSelection(capturedSelection);
            // Deleting a node can leave the shared selection pointing at a destroyed object
            // (the silent selection restore above never writes back to the context). Clear it
            // so the inspector falls back to the overview instead of touching a dead reference.
            if (_context.Selection is Object destroyed && destroyed == null)
                _context.Select(null);
            MapRuntimeNodes();
            RefreshRuntimeHighlight();
            _context.NotifyGraphRebuilt();
        }

        void AddNode(GraphNodeBase node, Vector3 position)
        {
            _graphView.AddElement(node);
            node.SetPosition(new Rect(position.x, position.y, 0f, 0f));
        }

        GraphNodeBase ResolveDestination(AnimatorTransitionBase transition)
        {
            if (transition == null) return null;
            if (transition.isExit) return _exitNode;
            if (transition.destinationState != null)
            {
                if (_stateNodes.TryGetValue(transition.destinationState, out var sn)) return sn;
                // Destination lives inside a child sub-state machine: draw the edge to that node.
                if (_nestedStateOwners.TryGetValue(transition.destinationState, out var owner)) return owner;
            }
            if (transition.destinationStateMachine != null)
            {
                if (_ssmNodes.TryGetValue(transition.destinationStateMachine, out var mn)) return mn;
                if (_nestedMachineOwners.TryGetValue(transition.destinationStateMachine, out var owner)) return owner;
            }
            return null;
        }

        void AddTransitionEdge(GraphNodeBase source, GraphNodeBase destination, AnimatorTransitionBase transition,
            Dictionary<(GraphNodeBase, GraphNodeBase), TransitionEdge> map)
        {
            if (source?.Output == null || destination?.Input == null) return;
            var key = (source, destination);
            if (!map.TryGetValue(key, out var edge))
            {
                edge = new TransitionEdge { output = source.Output, input = destination.Input };
                source.Output.Connect(edge);
                destination.Input.Connect(edge);
                _graphView.AddElement(edge);
                _edges.Add(edge);
                map[key] = edge;
            }
            edge.Transitions.Add(transition);
        }

        void CreateDefaultEdge(StateNode defaultNode)
        {
            if (_entryNode?.Output == null || defaultNode.Input == null) return;
            var edge = new TransitionEdge { IsDefaultEdge = true, output = _entryNode.Output, input = defaultNode.Input };
            _entryNode.Output.Connect(edge);
            defaultNode.Input.Connect(edge);
            _graphView.AddElement(edge);
            _edges.Add(edge);
        }

        // ---- selection -------------------------------------------------------

        void RestoreSelection(GraphSelectionSet captured)
        {
            var elements = new List<GraphElement>();

            foreach (var state in captured.States)
                if (_stateNodes.TryGetValue(state, out var node)) elements.Add(node);
            foreach (var sm in captured.StateMachines)
                if (_ssmNodes.TryGetValue(sm, out var node)) elements.Add(node);
            foreach (var frame in captured.Frames)
            {
                var node = FindFrameNode(frame);
                if (node != null) elements.Add(node);
            }
            foreach (var note in captured.Notes)
            {
                var node = FindNoteNode(note);
                if (node != null) elements.Add(node);
            }
            foreach (var kind in captured.Specials)
            {
                SpecialNode node;
                switch (kind)
                {
                    case SpecialNodeKind.Entry: node = _entryNode; break;
                    case SpecialNodeKind.Exit: node = _exitNode; break;
                    default: node = _anyStateNode; break;
                }
                if (node != null) elements.Add(node);
            }
            if (captured.Transitions.Count > 0)
            {
                var seen = new HashSet<TransitionEdge>();
                foreach (var transition in captured.Transitions)
                {
                    foreach (var edge in _edges)
                    {
                        if (seen.Contains(edge)) continue;
                        if (edge.Transitions.Contains(transition))
                        {
                            seen.Add(edge);
                            elements.Add(edge);
                            break;
                        }
                    }
                }
            }

            // If we have no captured selection to restore (e.g. the rebuild was triggered without
            // the graph ever being populated, or every captured element vanished), fall back to
            // Context.Selection — the panels rely on at least the single-selection contract.
            if (elements.Count == 0)
            {
                var selection = _context.Selection;
                switch (selection)
                {
                    case AnimatorState state when _stateNodes.TryGetValue(state, out var sn):
                        elements.Add(sn);
                        break;
                    case AnimatorStateMachine sm when _ssmNodes.TryGetValue(sm, out var mn):
                        elements.Add(mn);
                        break;
                    case AnimatorTransitionBase transition:
                        foreach (var edge in _edges)
                            if (edge.Transitions.Contains(transition)) { elements.Add(edge); break; }
                        break;
                    case TransitionEdge edge:
                        if (_edges.Contains(edge)) elements.Add(edge);
                        break;
                    case GraphFrameData.Frame frame:
                        var frameNode = FindFrameNode(frame);
                        if (frameNode != null) elements.Add(frameNode);
                        break;
                    case GraphFrameData.Note note:
                        var noteNode = FindNoteNode(note);
                        if (noteNode != null) elements.Add(noteNode);
                        break;
                }
            }

            _graphView.SetSelectionSilently(elements);
        }

        public FrameNode FindFrameNode(GraphFrameData.Frame frame)
        {
            if (frame == null) return null;
            foreach (var node in _frameNodes)
                if (node.Frame == frame) return node;
            return null;
        }

        public NoteNode FindNoteNode(GraphFrameData.Note note)
        {
            if (note == null) return null;
            foreach (var node in _noteNodes)
                if (node.Note == note) return node;
            return null;
        }

        public GraphNodeBase FindNode(object model)
        {
            switch (model)
            {
                case AnimatorState state:
                    return _stateNodes.TryGetValue(state, out var sn) ? sn : null;
                case AnimatorStateMachine sm:
                    return _ssmNodes.TryGetValue(sm, out var mn) ? mn : null;
                case SpecialNodeKind kind:
                    switch (kind)
                    {
                        case SpecialNodeKind.Entry: return _entryNode;
                        case SpecialNodeKind.Exit: return _exitNode;
                        default: return _anyStateNode;
                    }
                default:
                    return null;
            }
        }

        /// <summary>Re-reads one state's labels and badges without a full graph rebuild.</summary>
        public void RefreshStateNode(AnimatorState state)
        {
            if (state != null && _stateNodes.TryGetValue(state, out var node))
                node.RefreshLabels();
        }

        /// <summary>Re-reads every state node's labels and badges (e.g. after bulk Write Defaults).</summary>
        public void RefreshAllStateNodes()
        {
            foreach (var pair in _stateNodes)
                pair.Value.RefreshLabels();
        }

        /// <summary>
        /// Colours the edge carrying <paramref name="transition"/> as hovered and un-colours
        /// whichever one held that role before. Called from an inspector row's repaint, so it
        /// has to be free when the answer has not changed — which is why the previous edge is
        /// remembered rather than every edge asked.
        /// </summary>
        public void SetHoveredTransition(AnimatorTransitionBase transition)
        {
            var edge = transition != null ? FindEdge(transition) : null;
            if (ReferenceEquals(edge, _hoveredEdge)) return;
            _hoveredEdge?.SetHover(false);
            _hoveredEdge = edge;
            _hoveredEdge?.SetHover(true);
        }

        TransitionEdge _hoveredEdge;

        public TransitionEdge FindEdge(AnimatorTransitionBase transition)
        {
            if (transition == null) return null;
            foreach (var edge in _edges)
                if (edge.Transitions.Contains(transition))
                    return edge;
            return null;
        }

        // ---- structural edits applied from graph interaction -----------------

        public GraphViewChange HandleChange(GraphViewChange change)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null) return change;

            if (change.movedElements != null && change.movedElements.Count > 0)
                ApplyMoves(change.movedElements, sm);

            bool structural = false;

            if (change.elementsToRemove != null)
            {
                foreach (var element in change.elementsToRemove)
                {
                    switch (element)
                    {
                        case StateNode sn: DeleteState(sn.State); structural = true; break;
                        case SubStateMachineNode mn: DeleteSubStateMachine(mn.StateMachine); structural = true; break;
                        case TransitionEdge te when !te.IsDefaultEdge: DeleteTransitionEdge(te); structural = true; break;
                        case FrameNode fn: _frames.Data?.RemoveFrame(fn.Frame); structural = true; break;
                        case NoteNode nn: _frames.Data?.RemoveNote(nn.Note); structural = true; break;
                    }
                }
            }

            if (change.edgesToCreate != null && change.edgesToCreate.Count > 0)
            {
                foreach (var edge in change.edgesToCreate)
                    CreateTransition(edge.output?.node as GraphNodeBase, edge.input?.node as GraphNodeBase);
                change.edgesToCreate.Clear();
                structural = true;
            }

            if (structural)
                RequestRebuild();

            return change;
        }

        void ApplyMoves(List<GraphElement> moved, AnimatorStateMachine sm)
        {
            Undo.RegisterCompleteObjectUndo(sm, "Move Nodes");
            var states = sm.states;
            var machines = sm.stateMachines;
            bool statesDirty = false, machinesDirty = false;

            // The dragged elements themselves, plus any nodes a frame carried along live during
            // the drag — those moved visually too and persist the same way. A node that was both
            // selected and carried shows up twice; the second write is identical, so it's harmless.
            var toPersist = new List<GraphElement>(moved);
            foreach (var element in moved)
            {
                if (!(element is FrameNode fn) || fn.Frame == null || _frames.Data == null) continue;
                Undo.RecordObject(_frames.Data, "Move Frame");
                fn.Frame.bounds = fn.GetPosition();
                EditorUtility.SetDirty(_frames.Data);
                var carried = fn.TakeDraggedContents();
                if (carried != null) toPersist.AddRange(carried);
            }

            foreach (var element in toPersist)
            {
                if (element is StateNode sn)
                {
                    var p = sn.GetPosition().position;
                    for (int i = 0; i < states.Length; i++)
                        if (states[i].state == sn.State)
                        {
                            var cs = states[i];
                            cs.position = new Vector3(p.x, p.y, 0f);
                            states[i] = cs;
                            statesDirty = true;
                            break;
                        }
                }
                else if (element is SubStateMachineNode mn)
                {
                    var p = mn.GetPosition().position;
                    for (int i = 0; i < machines.Length; i++)
                        if (machines[i].stateMachine == mn.StateMachine)
                        {
                            var cm = machines[i];
                            cm.position = new Vector3(p.x, p.y, 0f);
                            machines[i] = cm;
                            machinesDirty = true;
                            break;
                        }
                }
                else if (element is SpecialNode spn)
                {
                    var p = spn.GetPosition().position;
                    var v = new Vector3(p.x, p.y, 0f);
                    if (spn.Kind == SpecialNodeKind.Entry) sm.entryPosition = v;
                    else if (spn.Kind == SpecialNodeKind.Exit) sm.exitPosition = v;
                    else sm.anyStatePosition = v;
                }
                else if (element is NoteNode nn && nn.Note != null && _frames.Data != null)
                {
                    // Covers notes dragged directly and notes carried along by a frame.
                    Undo.RecordObject(_frames.Data, "Move Note");
                    nn.Note.bounds = nn.GetPosition();
                    EditorUtility.SetDirty(_frames.Data);
                }
            }

            if (statesDirty) sm.states = states;
            if (machinesDirty) sm.stateMachines = machines;
            EditorUtility.SetDirty(sm);
        }

        void DeleteState(AnimatorState state)
        {
            var sm = _context.CurrentStateMachine;
            Undo.RegisterCompleteObjectUndo(sm, "Delete State");
            sm.RemoveState(state);
            EditorUtility.SetDirty(sm);
        }

        void DeleteSubStateMachine(AnimatorStateMachine child)
        {
            var sm = _context.CurrentStateMachine;
            Undo.RegisterCompleteObjectUndo(sm, "Delete Sub-State Machine");
            sm.RemoveStateMachine(child);
            EditorUtility.SetDirty(sm);
        }

        void DeleteTransitionEdge(TransitionEdge edge)
        {
            var source = edge.output?.node as GraphNodeBase;
            if (source == null) return;
            var sm = _context.CurrentStateMachine;
            Undo.RegisterCompleteObjectUndo(sm, "Delete Transition");
            foreach (var t in edge.Transitions)
                EdgeCommands.RemoveTransitionFrom(EndOf(source), t, sm);
            EditorUtility.SetDirty(sm);
        }

        /// <summary>Removes one transition from an edge and rebuilds (used by the inspector list).</summary>
        public void DeleteTransition(TransitionEdge edge, AnimatorTransitionBase transition)
        {
            var source = edge?.output?.node as GraphNodeBase;
            if (source == null || transition == null) return;
            var sm = _context.CurrentStateMachine;
            Undo.RegisterCompleteObjectUndo(sm, "Delete Transition");
            EdgeCommands.RemoveTransitionFrom(EndOf(source), transition, sm);
            EditorUtility.SetDirty(sm);
            Rebuild();
        }

        public AnimatorTransitionBase CreateTransition(GraphNodeBase source, GraphNodeBase destination)
        {
            if (source == null || destination == null) return null;
            return _transitions.CreateTransition(EndOf(source), EndOf(destination));
        }

        /// <summary>Shorthand for <see cref="GraphNodeBase.EndOf"/>, the single node-to-end conversion.</summary>
        static TransitionEnd EndOf(GraphNodeBase node) => GraphNodeBase.EndOf(node);

        static List<TransitionEnd> EndsOf(IEnumerable<GraphNodeBase> nodes)
        {
            var ends = new List<TransitionEnd>();
            foreach (var node in nodes)
                ends.Add(EndOf(node));
            return ends;
        }

        // ---- reverse / redirect / replicate ----------------------------------

        /// <summary>True when the edge's transitions can be flipped to run the opposite direction.</summary>
        public bool CanReverseEdge(TransitionEdge edge)
        {
            if (edge == null || edge.IsDefaultEdge || edge.Transitions.Count == 0) return false;
            var source = edge.output?.node as GraphNodeBase;
            var destination = edge.input?.node as GraphNodeBase;
            // After reversing, the destination becomes the source and vice versa, so both
            // ends must be a state or sub-state machine (Entry/Exit/Any State cannot swap roles).
            return IsConnectableState(source) && IsConnectableState(destination);
        }

        /// <summary>Recreates every transition on the edge running from destination back to source.</summary>
        public void ReverseEdge(TransitionEdge edge)
        {
            if (!CanReverseEdge(edge)) return;
            var source = edge.output?.node as GraphNodeBase;
            var destination = edge.input?.node as GraphNodeBase;

            var created = _transitions.Reverse(EndOf(source), EndOf(destination), edge.Transitions);
            if (created == null) return;

            Rebuild();
            if (created.Count > 0) _context.Select(created[0]);
        }

        /// <summary>Candidate destinations the edge's transitions can be pointed at instead.</summary>
        public List<GraphNodeBase> RedirectTargets(TransitionEdge edge)
        {
            var targets = new List<GraphNodeBase>();
            if (edge == null || edge.IsDefaultEdge) return targets;
            var source = edge.output?.node as GraphNodeBase;
            var current = edge.input?.node as GraphNodeBase;

            var states = new List<GraphNodeBase>(_stateNodes.Values);
            var machines = new List<GraphNodeBase>(_ssmNodes.Values);
            states.Sort((a, b) => string.CompareOrdinal(NodeLabel(a), NodeLabel(b)));
            machines.Sort((a, b) => string.CompareOrdinal(NodeLabel(a), NodeLabel(b)));

            foreach (var node in states)
                if (node != source && node != current) targets.Add(node);
            foreach (var node in machines)
                if (node != source && node != current) targets.Add(node);
            // Only states and sub-state machines may transition to Exit.
            if (_exitNode != null && current != _exitNode && IsConnectableState(source))
                targets.Add(_exitNode);
            return targets;
        }

        /// <summary>Points every transition on the edge at a new destination node.</summary>
        public void RedirectEdge(TransitionEdge edge, GraphNodeBase newDestination)
        {
            if (edge == null || edge.IsDefaultEdge || newDestination == null) return;
            if (edge.Transitions.Count == 0) return;
            var anchor = edge.Transitions[0];

            _transitions.Redirect(edge.Transitions, EndOf(newDestination));

            Rebuild();
            _context.Select(anchor);
        }

        /// <summary>Adds a duplicate of every transition on the edge alongside the originals.</summary>
        public void ReplicateEdge(TransitionEdge edge)
        {
            if (edge == null || edge.IsDefaultEdge || edge.Transitions.Count == 0) return;
            var source = edge.output?.node as GraphNodeBase;
            var destination = edge.input?.node as GraphNodeBase;
            if (source == null || destination == null) return;

            var created = _transitions.Replicate(EndOf(source), EndOf(destination), edge.Transitions);

            Rebuild();
            if (created.Count > 0) _context.Select(created[0]);
        }

        static bool IsConnectableState(GraphNodeBase node) =>
            node is StateNode || node is SubStateMachineNode;

        /// <summary>Human-readable name of a node, used for menu labels and sorting. The node
        /// becomes the end it stands for, so a menu entry and a transition row naming the same
        /// state read identically.</summary>
        public static string NodeLabel(GraphNodeBase node) => GraphNodeBase.EndOf(node).Label;

        // ---- node creation ---------------------------------------------------

        public AnimatorState CreateState(Vector2 position, string mode) => _nodes.CreateState(position, mode);

        public AnimatorState CreateStateWithMotion(Vector2 position, Motion motion) =>
            _nodes.CreateStateWithMotion(position, motion);

        public void AssignMotion(AnimatorState state, Motion motion) => _nodes.AssignMotion(state, motion);

        public AnimatorStateMachine CreateSubStateMachine(Vector2 position) => _nodes.CreateSubStateMachine(position);

        public void SetDefaultState(AnimatorState state)
        {
            if (!_nodes.SetDefaultState(state)) return;
            RequestRebuild();
        }

        // ---- frames ------------------------------------------------------------

        /// <summary>
        /// The graph nodes (and memo notes) whose whole visual rect lies inside
        /// <paramref name="bounds"/>. Elements merely touching or crossing the outline are
        /// excluded, so the result matches what visually reads as "inside the frame".
        /// </summary>
        public List<GraphElement> NodesFullyInside(Rect bounds)
        {
            var result = new List<GraphElement>();
            void AddIfInside(GraphElement element)
            {
                if (element == null) return;
                var rect = element.GetPosition();
                if (rect.width <= 0f || rect.height <= 0f) return;   // not laid out yet
                if (bounds.Contains(rect.min) && bounds.Contains(rect.max))
                    result.Add(element);
            }

            foreach (var pair in _stateNodes) AddIfInside(pair.Value);
            foreach (var pair in _ssmNodes) AddIfInside(pair.Value);
            AddIfInside(_entryNode);
            AddIfInside(_exitNode);
            AddIfInside(_anyStateNode);
            foreach (var note in _noteNodes) AddIfInside(note);
            return result;
        }

        /// <summary>Creates an empty frame at <paramref name="position"/> (graph coordinates) and selects it.</summary>
        public void CreateFrameAt(Vector2 position)
        {
            var frame = _frames.CreateFrame(new Rect(position.x, position.y, 320f, 220f));
            if (frame == null) return;
            RequestRebuild();
            _context.Select(frame);
        }

        /// <summary>Creates a frame around the given graph nodes and selects it.</summary>
        public void CreateFrameAroundNodes(IList<GraphNodeBase> nodes)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null || nodes == null || nodes.Count == 0) return;

            var bounds = nodes[0].GetPosition();
            for (int i = 1; i < nodes.Count; i++)
            {
                var r = nodes[i].GetPosition();
                bounds = Rect.MinMaxRect(
                    Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin),
                    Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax));
            }
            bounds = Rect.MinMaxRect(bounds.xMin - 24f, bounds.yMin - 44f, bounds.xMax + 24f, bounds.yMax + 24f);

            var frame = _frames.CreateFrame(bounds);
            if (frame == null) return;
            RequestRebuild();
            _context.Select(frame);
        }

        /// <summary>
        /// Writes a frame's size back to the asset after the resize handle changed it. Pure
        /// position changes are ignored on purpose: geometry events fire continuously while a
        /// frame is dragged, and the move is persisted once, on drop, by <see cref="ApplyMoves"/>.
        /// </summary>
        void PersistFrameGeometry(FrameNode node)
        {
            if (node?.Frame == null || _frames.Data == null) return;
            var rect = node.GetPosition();
            var bounds = node.Frame.bounds;
            if (Mathf.Approximately(rect.width, bounds.width) && Mathf.Approximately(rect.height, bounds.height))
                return;
            _frames.ResizeFrame(node.Frame, rect);
        }

        /// <summary>Refreshes a frame's visuals after its title/color changed in the inspector.</summary>
        public void RefreshFrameVisuals(GraphFrameData.Frame frame) => FindFrameNode(frame)?.RefreshVisuals();

        public void DeleteFrame(GraphFrameData.Frame frame)
        {
            if (!_frames.DeleteFrame(frame)) return;
            RequestRebuild();
        }

        /// <summary>
        /// Duplicates the frame's box and every state and note lying fully inside it. Transitions
        /// whose source and destination are both in the duplicated set are reproduced; transitions
        /// crossing the frame's edge are dropped intentionally so the copy is self-contained.
        /// </summary>
        public void DuplicateFrame(GraphFrameData.Frame frame)
        {
            if (frame == null) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null) return;

            var insideNodes = NodesFullyInside(frame.bounds);
            var states = new List<AnimatorState>();
            var notesInside = new List<GraphFrameData.Note>();
            foreach (var element in insideNodes)
            {
                if (element is StateNode sn && sn.State != null) states.Add(sn.State);
                else if (element is NoteNode nn && nn.Note != null) notesInside.Add(nn.Note);
            }

            var newFrame = _frames.DuplicateFrame(frame, states, notesInside);
            if (newFrame == null) return;

            RequestRebuild();
            _context.Select(newFrame);
        }

        public void ToggleFrameMoveNodes(GraphFrameData.Frame frame) => _frames.ToggleFrameMoveNodes(frame);

        public void RenameFrame(GraphFrameData.Frame frame, string title)
        {
            if (!_frames.RenameFrame(frame, title)) return;
            RefreshFrameVisuals(frame);
        }

        public void ToggleFrameLock(GraphFrameData.Frame frame)
        {
            if (!_frames.ToggleFrameLock(frame)) return;
            RefreshFrameVisuals(frame);
        }

        public void SetFrameColor(GraphFrameData.Frame frame, Color color)
        {
            if (!_frames.SetFrameColor(frame, color)) return;
            RefreshFrameVisuals(frame);
        }

        /// <summary>Replaces the graph selection with the nodes lying fully inside the frame.</summary>
        public void SelectFrameContents(GraphFrameData.Frame frame)
        {
            if (frame == null) return;
            var nodes = NodesFullyInside(frame.bounds);
            if (nodes.Count == 0) return;
            _graphView.ClearSelection();
            foreach (var node in nodes)
                _graphView.AddToSelection(node);
        }

        /// <summary>
        /// Replaces the graph selection with every <see cref="StateNode"/> lying fully inside the
        /// frame. Sub-state machines, notes, special nodes and the frame itself are dropped, so
        /// the next action (e.g. Ctrl+D, the multi-state inspector) targets only the states.
        /// Returns the number of states selected.
        /// </summary>
        public int SelectFrameInternalStates(GraphFrameData.Frame frame)
        {
            if (frame == null) return 0;
            var nodes = NodesFullyInside(frame.bounds);
            _graphView.ClearSelection();
            int selected = 0;
            AnimatorState firstState = null;
            foreach (var node in nodes)
            {
                if (node is StateNode sn && sn.State != null)
                {
                    _graphView.AddToSelection(sn);
                    if (firstState == null) firstState = sn.State;
                    selected++;
                }
            }
            // The inspector reads Context.Selection; flip it to one of the states so the
            // multi-state editor opens immediately instead of staying on the frame inspector.
            if (firstState != null) _context.Select(firstState);
            else _context.Select(null);
            return selected;
        }

        /// <summary>Shrinks (or grows) the frame to snugly wrap the nodes currently inside it.</summary>
        public void FitFrameToContents(GraphFrameData.Frame frame)
        {
            if (frame == null || _frames.Data == null || frame.locked) return;
            var nodes = NodesFullyInside(frame.bounds);
            if (nodes.Count == 0) return;

            var bounds = nodes[0].GetPosition();
            for (int i = 1; i < nodes.Count; i++)
            {
                var r = nodes[i].GetPosition();
                bounds = Rect.MinMaxRect(
                    Mathf.Min(bounds.xMin, r.xMin), Mathf.Min(bounds.yMin, r.yMin),
                    Mathf.Max(bounds.xMax, r.xMax), Mathf.Max(bounds.yMax, r.yMax));
            }
            bounds = Rect.MinMaxRect(bounds.xMin - 24f, bounds.yMin - 44f, bounds.xMax + 24f, bounds.yMax + 24f);

            _frames.FitFrame(frame, bounds);
            FindFrameNode(frame)?.SetPosition(bounds);
        }

        // ---- notes -------------------------------------------------------------

        /// <summary>Creates an empty memo note at <paramref name="position"/> and selects it.</summary>
        public void CreateNoteAt(Vector2 position)
        {
            var note = _frames.CreateNote(new Rect(position.x, position.y, 200f, 100f));
            if (note == null) return;
            RequestRebuild();
            _context.Select(note);
        }

        public void DeleteNote(GraphFrameData.Note note)
        {
            if (!_frames.DeleteNote(note)) return;
            RequestRebuild();
        }

        public void SetNoteText(GraphFrameData.Note note, string text)
        {
            if (!_frames.SetNoteText(note, text)) return;
            RefreshNoteVisuals(note);
        }

        public void SetNoteColor(GraphFrameData.Note note, Color color)
        {
            if (!_frames.SetNoteColor(note, color)) return;
            RefreshNoteVisuals(note);
        }

        public void SetNoteFontSize(GraphFrameData.Note note, int fontSize)
        {
            if (!_frames.SetNoteFontSize(note, fontSize)) return;
            RefreshNoteVisuals(note);
        }

        /// <summary>Refreshes a note's visuals after its fields changed in the inspector.</summary>
        public void RefreshNoteVisuals(GraphFrameData.Note note) => FindNoteNode(note)?.RefreshVisuals();

        /// <summary>
        /// Writes a note's size back to the asset after the resize handle changed it; moves are
        /// persisted on drop via <see cref="ApplyMoves"/>, mirroring the frame behaviour.
        /// </summary>
        void PersistNoteGeometry(NoteNode node)
        {
            if (node?.Note == null || _frames.Data == null) return;
            var rect = node.GetPosition();
            var bounds = node.Note.bounds;
            if (Mathf.Approximately(rect.width, bounds.width) && Mathf.Approximately(rect.height, bounds.height))
                return;
            _frames.ResizeNote(node.Note, rect);
        }

        // ---- pack / unpack -----------------------------------------------------

        public void PackSelectedStates(List<AnimatorState> states)
        {
            var child = _nodes.PackStates(states);
            if (child == null) return;
            RequestRebuild();
            _context.Select(child);
        }

        public void UnpackSubStateMachine(AnimatorStateMachine child)
        {
            if (!_nodes.UnpackSubStateMachine(child)) return;
            RequestRebuild();
            _context.Select(null);
        }

        // ---- chain / fan transitions --------------------------------------------

        public void ChainNodes(IList<GraphNodeBase> nodes, bool seeded = false)
        {
            if (nodes == null || nodes.Count < 2) return;
            SelectBatch(_transitions.Chain(EndsOf(nodes), seeded));
        }

        public void FanOutNodes(GraphNodeBase source, IEnumerable<GraphNodeBase> targets, bool seeded = false)
        {
            if (source == null || targets == null) return;
            SelectBatch(_transitions.FanOut(EndOf(source), EndsOf(targets), seeded));
        }

        public void FanInNodes(IEnumerable<GraphNodeBase> sources, GraphNodeBase target, bool seeded = false)
        {
            if (sources == null || target == null) return;
            SelectBatch(_transitions.FanIn(EndsOf(sources), EndOf(target), seeded));
        }

        public void CrossProductNodes(IList<GraphNodeBase> sources, IList<GraphNodeBase> targets, bool seeded = false)
        {
            if (sources == null || targets == null || sources.Count == 0 || targets.Count == 0) return;
            SelectBatch(_transitions.CrossProduct(EndsOf(sources), EndsOf(targets), seeded));
        }

        /// <summary>Shows the result of a chain / fan batch: nothing created means nothing to redraw.</summary>
        void SelectBatch(List<AnimatorTransitionBase> created)
        {
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        // ---- copy / paste ----------------------------------------------------

        public void CopySelectedStates() =>
            _clipboard.CopyStates(new GraphSelectionSet(_graphView.selection).States, StateNodePosition);

        public void PasteStates(Vector2 position)
        {
            if (!_clipboard.PasteStates(position)) return;
            RequestRebuild();
        }

        Vector2 StateNodePosition(AnimatorState state) =>
            state != null && _stateNodes.TryGetValue(state, out var node)
                ? node.GetPosition().position
                : Vector2.zero;

        // ---- frame / note copy / paste ---------------------------------------

        public void CopySelectedElements()
        {
            var selected = new GraphSelectionSet(_graphView.selection);
            _clipboard.CopyElements(selected.States, selected.Frames, selected.Notes,
                selected.StateMachines.Count, StateNodePosition);
        }

        public void CopyFrame(GraphFrameData.Frame frame) => _clipboard.CopyFrame(frame);

        public void CopyNote(GraphFrameData.Note note) => _clipboard.CopyNote(note);

        public void PasteFramesAndNotes(Vector2 position)
        {
            var created = _clipboard.PasteFramesAndNotes(position);
            if (created == null || created.Count == 0) return;

            RequestRebuild();
            _context.Select(created[0]);
        }

        /// <summary>Pastes at the position the copy was taken from. Used where there is no mouse
        /// position to paste at (the inspector buttons), and it lands the copy in the same spot
        /// of the layer now on screen.</summary>
        public void PasteFramesAndNotesAtOrigin() => PasteFramesAndNotes(FrameNoteClipboard.Anchor);

        // ---- transition copy / paste -----------------------------------------

        public void CopyTransitionsFromEdges(IEnumerable<TransitionEdge> edges)
        {
            var content = new List<(TransitionEnd, IList<AnimatorTransitionBase>)>();
            foreach (var edge in edges)
            {
                if (edge == null || edge.IsDefaultEdge) continue;
                content.Add((EndOf(edge.output?.node as GraphNodeBase), edge.Transitions));
            }
            _clipboard.CopyTransitions(content);
        }

        public void PasteTransitionSettingsOntoEdges(IEnumerable<TransitionEdge> edges)
        {
            var transitions = new List<AnimatorTransitionBase>();
            foreach (var edge in edges)
            {
                if (edge == null || edge.IsDefaultEdge) continue;
                transitions.AddRange(edge.Transitions);
            }
            if (_clipboard.PasteTransitionSettingsOnto(transitions)) Rebuild();
        }

        public void PasteTransitionsAsNewOnEdges(IEnumerable<TransitionEdge> edges)
        {
            var pairs = new List<(TransitionEnd, TransitionEnd)>();
            foreach (var edge in edges)
            {
                if (edge == null || edge.IsDefaultEdge) continue;
                var source = edge.output?.node as GraphNodeBase;
                var destination = edge.input?.node as GraphNodeBase;
                if (source == null || destination == null) continue;
                pairs.Add((EndOf(source), EndOf(destination)));
            }
            if (!_clipboard.PasteTransitionsAsNewOn(pairs, out var last)) return;
            Rebuild();
            if (last != null) _context.Select(last);
        }

        public void PasteTransitionsWithStateAsSource(AnimatorState state) =>
            SelectPasted(_clipboard.PasteTransitionsWithStateAsSource(state));

        public void PasteTransitionsWithStateAsDestination(AnimatorState state) =>
            SelectPasted(_clipboard.PasteTransitionsWithStateAsDestination(state));

        /// <summary>Shows the result of a transition paste; null means the paste never ran.</summary>
        void SelectPasted(List<AnimatorTransitionBase> created)
        {
            if (created == null) return;
            Rebuild();
            if (created.Count > 0) _context.Select(created[0]);
        }

        // ---- highlighting ----------------------------------------------------

        public void HighlightParameter(string parameterName)
        {
            bool active = !string.IsNullOrEmpty(parameterName);
            foreach (var edge in _edges)
            {
                bool used = false;
                if (active)
                    foreach (var t in edge.Transitions)
                    {
                        if (t == null) continue;
                        foreach (var c in t.conditions)
                            if (c.parameter == parameterName) { used = true; break; }
                        if (used) break;
                    }
                edge.SetHighlight(used);
            }
            foreach (var pair in _stateNodes)
            {
                var s = pair.Key;
                bool used = active && (
                    (s.speedParameterActive && s.speedParameter == parameterName) ||
                    (s.timeParameterActive && s.timeParameter == parameterName) ||
                    (s.cycleOffsetParameterActive && s.cycleOffsetParameter == parameterName) ||
                    (s.mirrorParameterActive && s.mirrorParameter == parameterName));
                pair.Value.SetHighlight(used);
            }
        }

        public void SetRuntimePlayback(AnimatorPlayback.LayerPlayback playback)
        {
            _playback = playback;
            RefreshRuntimeHighlight();
        }

        /// <summary>
        /// Indexes every state reachable from this screen by the hash Unity identifies it with.
        /// Built once per rebuild so the per-tick refresh is a dictionary lookup.
        /// </summary>
        void MapRuntimeNodes()
        {
            _runtimeNodes.Clear();
            var path = new List<AnimatorStateMachine>(_context.StateMachinePath);
            if (path.Count == 0) return;

            foreach (var pair in _stateNodes)
                _runtimeNodes[AnimatorPlayback.FullPathHash(path, pair.Key.name)] = pair.Value;
            foreach (var pair in _ssmNodes)
                MapNestedRuntimeNodes(path, pair.Key, pair.Value);
        }

        void MapNestedRuntimeNodes(List<AnimatorStateMachine> path, AnimatorStateMachine machine,
            GraphNodeBase node)
        {
            path.Add(machine);
            foreach (var cs in machine.states)
                if (cs.state != null)
                    _runtimeNodes[AnimatorPlayback.FullPathHash(path, cs.state.name)] = node;
            foreach (var child in machine.stateMachines)
                if (child.stateMachine != null)
                    MapNestedRuntimeNodes(path, child.stateMachine, node);
            path.RemoveAt(path.Count - 1);
        }

        void RefreshRuntimeHighlight()
        {
            bool playing = EditorApplication.isPlaying && _playback.valid;
            GraphNodeBase current = null, next = null;
            if (playing)
            {
                _runtimeNodes.TryGetValue(_playback.stateHash, out current);
                if (_playback.inTransition)
                    _runtimeNodes.TryGetValue(_playback.nextStateHash, out next);
            }

            foreach (var pair in _stateNodes)
                pair.Value.SetPlayback(pair.Value == current, pair.Value == next, _playback.progress);
            foreach (var pair in _ssmNodes)
                pair.Value.SetPlayback(pair.Value == current, pair.Value == next, 0f);

            // An Any State transition leaves the Any State node, not the state it interrupted.
            var running = playing && _playback.inTransition
                ? FindRuntimeEdge(_playback.fromAnyState ? _anyStateNode : current, next)
                : null;
            foreach (var edge in _edges)
                edge.SetRuntimeActive(edge == running);
        }

        TransitionEdge FindRuntimeEdge(GraphNodeBase from, GraphNodeBase to)
        {
            if (from == null || to == null) return null;
            foreach (var edge in _edges)
                if (edge.output?.node == from && edge.input?.node == to) return edge;
            return null;
        }
    }
}
