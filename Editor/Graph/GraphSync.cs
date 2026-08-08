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
        GraphFrameData _frameData;

        SpecialNode _entryNode, _exitNode, _anyStateNode;
        bool _rebuildScheduled;
        int _runtimeStateHash;

        readonly NodeCommands _nodes;

        public GraphSync(DaerDContext context, AnimatorGraphView graphView)
        {
            _context = context;
            _graphView = graphView;
            _nodes = new NodeCommands(context);
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
            _frameNodes.Clear();
            _noteNodes.Clear();
            foreach (var element in _graphView.graphElements.ToList())
                _graphView.RemoveElement(element);

            var sm = _context.CurrentStateMachine;
            if (sm == null) return;

            _frameData = GraphFrameData.Find(_context.Controller);
            if (_frameData != null)
            {
                foreach (var frame in _frameData.FramesIn(sm))
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
                foreach (var note in _frameData.NotesIn(sm))
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
                        case FrameNode fn: _frameData?.RemoveFrame(fn.Frame); structural = true; break;
                        case NoteNode nn: _frameData?.RemoveNote(nn.Note); structural = true; break;
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
                if (!(element is FrameNode fn) || fn.Frame == null || _frameData == null) continue;
                Undo.RecordObject(_frameData, "Move Frame");
                fn.Frame.bounds = fn.GetPosition();
                EditorUtility.SetDirty(_frameData);
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
                else if (element is NoteNode nn && nn.Note != null && _frameData != null)
                {
                    // Covers notes dragged directly and notes carried along by a frame.
                    Undo.RecordObject(_frameData, "Move Note");
                    nn.Note.bounds = nn.GetPosition();
                    EditorUtility.SetDirty(_frameData);
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
                RemoveTransitionFrom(source, t, sm);
            EditorUtility.SetDirty(sm);
        }

        /// <summary>Removes one transition from an edge and rebuilds (used by the inspector list).</summary>
        public void DeleteTransition(TransitionEdge edge, AnimatorTransitionBase transition)
        {
            var source = edge?.output?.node as GraphNodeBase;
            if (source == null || transition == null) return;
            var sm = _context.CurrentStateMachine;
            Undo.RegisterCompleteObjectUndo(sm, "Delete Transition");
            RemoveTransitionFrom(source, transition, sm);
            EditorUtility.SetDirty(sm);
            Rebuild();
        }

        static void RemoveTransitionFrom(GraphNodeBase source, AnimatorTransitionBase t, AnimatorStateMachine sm)
        {
            if (t == null) return;
            if (source is StateNode sn && sn.State != null && t is AnimatorStateTransition stateTransition)
                sn.State.RemoveTransition(stateTransition);
            else if (source is SpecialNode spn && spn.Kind == SpecialNodeKind.AnyState && t is AnimatorStateTransition anyTransition)
                sm.RemoveAnyStateTransition(anyTransition);
            else if (source is SpecialNode entrySpn && entrySpn.Kind == SpecialNodeKind.Entry && t is AnimatorTransition entryTransition)
                sm.RemoveEntryTransition(entryTransition);
            else if (source is SubStateMachineNode mn && t is AnimatorTransition smTransition)
                sm.RemoveStateMachineTransition(mn.StateMachine, smTransition);
        }

        public AnimatorTransitionBase CreateTransition(GraphNodeBase source, GraphNodeBase destination)
        {
            if (source == null || destination == null) return null;
            var sm = _context.CurrentStateMachine;
            AnimatorTransitionBase created;
            using (new UndoScope("Create Transition"))
                created = CreateTransitionCore(source, destination, sm);
            return created;
        }

        /// <summary>
        /// Inner shared by single and batch transition creation. The caller is responsible for
        /// opening an <see cref="UndoScope"/> so the batch name (e.g. "Chain Transitions") wins
        /// over the per-pair undo label.
        /// </summary>
        AnimatorTransitionBase CreateTransitionCore(GraphNodeBase source, GraphNodeBase destination,
            AnimatorStateMachine sm)
        {
            AnimatorTransitionBase created = null;
            if (source is StateNode sn)
            {
                Undo.RegisterCompleteObjectUndo(sn.State, "Create Transition");
                if (destination is StateNode dn) created = sn.State.AddTransition(dn.State);
                else if (destination is SubStateMachineNode dm) created = sn.State.AddTransition(dm.StateMachine);
                else if (destination is SpecialNode dsp && dsp.Kind == SpecialNodeKind.Exit) created = sn.State.AddExitTransition();
            }
            else if (source is SpecialNode ssp && ssp.Kind == SpecialNodeKind.AnyState)
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Transition");
                if (destination is StateNode dn) created = sm.AddAnyStateTransition(dn.State);
                else if (destination is SubStateMachineNode dm) created = sm.AddAnyStateTransition(dm.StateMachine);
            }
            else if (source is SpecialNode esp && esp.Kind == SpecialNodeKind.Entry)
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Transition");
                if (destination is StateNode dn) created = sm.AddEntryTransition(dn.State);
                else if (destination is SubStateMachineNode dm) created = sm.AddEntryTransition(dm.StateMachine);
            }
            else if (source is SubStateMachineNode smn)
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Transition");
                if (destination is StateNode dn) created = sm.AddStateMachineTransition(smn.StateMachine, dn.State);
                else if (destination is SubStateMachineNode dm) created = sm.AddStateMachineTransition(smn.StateMachine, dm.StateMachine);
                else if (destination is SpecialNode dsp && dsp.Kind == SpecialNodeKind.Exit) created = sm.AddStateMachineExitTransition(smn.StateMachine);
            }

            if (created is AnimatorStateTransition newStateTransition)
                DaerDSettings.ApplyTransitionDefaultsTo(newStateTransition);

            if (created != null && _context.Controller != null)
                EditorUtility.SetDirty(_context.Controller);
            return created;
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
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;

            var snapshots = new List<TransitionClipboard.Snapshot>();
            foreach (var t in edge.Transitions)
                if (t != null) snapshots.Add(TransitionClipboard.Capture(t));
            var originals = new List<AnimatorTransitionBase>(edge.Transitions);

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Reverse Transition"))
            {
                RegisterRemoveUndo(source, sm, "Reverse Transition");
                foreach (var t in originals)
                    RemoveTransitionFrom(source, t, sm);

                foreach (var snap in snapshots)
                {
                    var t = CreateTransition(destination, source);
                    if (t != null)
                    {
                        TransitionClipboard.Apply(t, snap);
                        created.Add(t);
                    }
                }
                EditorUtility.SetDirty(sm);
            }

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

            using (new UndoScope("Redirect Transition"))
            {
                foreach (var t in edge.Transitions)
                {
                    if (t == null) continue;
                    Undo.RegisterCompleteObjectUndo(t, "Redirect Transition");
                    AssignDestination(t, newDestination);
                    EditorUtility.SetDirty(t);
                }
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }

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

            var snapshots = new List<TransitionClipboard.Snapshot>();
            foreach (var t in edge.Transitions)
                if (t != null) snapshots.Add(TransitionClipboard.Capture(t));

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Replicate Transition"))
            {
                foreach (var snap in snapshots)
                {
                    var t = CreateTransition(source, destination);
                    if (t != null)
                    {
                        TransitionClipboard.Apply(t, snap);
                        created.Add(t);
                    }
                }
            }

            Rebuild();
            if (created.Count > 0) _context.Select(created[0]);
        }

        static bool IsConnectableState(GraphNodeBase node) =>
            node is StateNode || node is SubStateMachineNode;

        static void AssignDestination(AnimatorTransitionBase transition, GraphNodeBase destination)
        {
            switch (destination)
            {
                case StateNode sn:
                    transition.destinationStateMachine = null;
                    transition.isExit = false;
                    transition.destinationState = sn.State;
                    break;
                case SubStateMachineNode mn:
                    transition.destinationState = null;
                    transition.isExit = false;
                    transition.destinationStateMachine = mn.StateMachine;
                    break;
                case SpecialNode spn when spn.Kind == SpecialNodeKind.Exit:
                    transition.destinationState = null;
                    transition.destinationStateMachine = null;
                    transition.isExit = true;
                    break;
            }
        }

        static void RegisterRemoveUndo(GraphNodeBase source, AnimatorStateMachine sm, string name)
        {
            Undo.RegisterCompleteObjectUndo(sm, name);
            if (source is StateNode sn && sn.State != null)
                Undo.RegisterCompleteObjectUndo(sn.State, name);
        }

        /// <summary>Human-readable name of a node, used for menu labels and sorting.</summary>
        public static string NodeLabel(GraphNodeBase node)
        {
            switch (node)
            {
                case StateNode sn: return sn.State != null ? sn.State.name : "(state)";
                case SubStateMachineNode mn: return mn.StateMachine != null ? mn.StateMachine.name : "(sub-state machine)";
                case SpecialNode spn: return spn.Kind.ToString();
                default: return "?";
            }
        }

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
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null) return;
            _frameData = GraphFrameData.GetOrCreate(_context.Controller);
            var frame = _frameData.AddFrame(sm, new Rect(position.x, position.y, 320f, 220f));
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

            _frameData = GraphFrameData.GetOrCreate(_context.Controller);
            var frame = _frameData.AddFrame(sm, bounds);
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
            if (node?.Frame == null || _frameData == null) return;
            var rect = node.GetPosition();
            var bounds = node.Frame.bounds;
            if (Mathf.Approximately(rect.width, bounds.width) && Mathf.Approximately(rect.height, bounds.height))
                return;
            Undo.RecordObject(_frameData, "Resize Frame");
            node.Frame.bounds = rect;
            EditorUtility.SetDirty(_frameData);
        }

        /// <summary>Refreshes a frame's visuals after its title/color changed in the inspector.</summary>
        public void RefreshFrameVisuals(GraphFrameData.Frame frame) => FindFrameNode(frame)?.RefreshVisuals();

        public void DeleteFrame(GraphFrameData.Frame frame)
        {
            if (frame == null || _frameData == null || frame.locked) return;
            _frameData.RemoveFrame(frame);
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

            _frameData = GraphFrameData.GetOrCreate(_context.Controller);
            var newFrame = FrameDuplicator.Duplicate(_frameData, _context.Controller, sm, frame, states, notesInside);
            if (newFrame == null) return;

            RequestRebuild();
            _context.Select(newFrame);
        }

        public void ToggleFrameMoveNodes(GraphFrameData.Frame frame)
        {
            if (frame == null || _frameData == null) return;
            Undo.RecordObject(_frameData, "Edit Frame");
            frame.moveNodesWithFrame = !frame.moveNodesWithFrame;
            EditorUtility.SetDirty(_frameData);
        }

        public void RenameFrame(GraphFrameData.Frame frame, string title)
        {
            if (frame == null || _frameData == null || string.IsNullOrEmpty(title)) return;
            Undo.RecordObject(_frameData, "Rename Frame");
            frame.title = title;
            EditorUtility.SetDirty(_frameData);
            RefreshFrameVisuals(frame);
        }

        public void ToggleFrameLock(GraphFrameData.Frame frame)
        {
            if (frame == null || _frameData == null) return;
            Undo.RecordObject(_frameData, frame.locked ? "Unlock Frame" : "Lock Frame");
            frame.locked = !frame.locked;
            EditorUtility.SetDirty(_frameData);
            RefreshFrameVisuals(frame);
        }

        public void SetFrameColor(GraphFrameData.Frame frame, Color color)
        {
            if (frame == null || _frameData == null) return;
            Undo.RecordObject(_frameData, "Frame Color");
            frame.color = color;
            EditorUtility.SetDirty(_frameData);
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
            if (frame == null || _frameData == null || frame.locked) return;
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

            Undo.RecordObject(_frameData, "Fit Frame To Contents");
            frame.bounds = bounds;
            EditorUtility.SetDirty(_frameData);
            FindFrameNode(frame)?.SetPosition(bounds);
        }

        // ---- notes -------------------------------------------------------------

        /// <summary>Creates an empty memo note at <paramref name="position"/> and selects it.</summary>
        public void CreateNoteAt(Vector2 position)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || _context.Controller == null) return;
            _frameData = GraphFrameData.GetOrCreate(_context.Controller);
            var note = _frameData.AddNote(sm, new Rect(position.x, position.y, 200f, 100f));
            RequestRebuild();
            _context.Select(note);
        }

        public void DeleteNote(GraphFrameData.Note note)
        {
            if (note == null || _frameData == null) return;
            _frameData.RemoveNote(note);
            RequestRebuild();
        }

        public void SetNoteText(GraphFrameData.Note note, string text)
        {
            if (note == null || _frameData == null) return;
            Undo.RecordObject(_frameData, "Edit Note");
            note.text = text ?? string.Empty;
            EditorUtility.SetDirty(_frameData);
            RefreshNoteVisuals(note);
        }

        public void SetNoteColor(GraphFrameData.Note note, Color color)
        {
            if (note == null || _frameData == null) return;
            Undo.RecordObject(_frameData, "Note Color");
            note.color = color;
            EditorUtility.SetDirty(_frameData);
            RefreshNoteVisuals(note);
        }

        public void SetNoteFontSize(GraphFrameData.Note note, int fontSize)
        {
            if (note == null || _frameData == null) return;
            Undo.RecordObject(_frameData, "Note Font Size");
            note.fontSize = fontSize;
            EditorUtility.SetDirty(_frameData);
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
            if (node?.Note == null || _frameData == null) return;
            var rect = node.GetPosition();
            var bounds = node.Note.bounds;
            if (Mathf.Approximately(rect.width, bounds.width) && Mathf.Approximately(rect.height, bounds.height))
                return;
            Undo.RecordObject(_frameData, "Resize Note");
            node.Note.bounds = rect;
            EditorUtility.SetDirty(_frameData);
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
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;
            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Chain Transitions"))
            {
                for (int i = 0; i < nodes.Count - 1; i++)
                    AddBatchTransition(nodes[i], nodes[i + 1], sm, created);
                if (seeded) SeedCreated(created);
            }
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        public void FanOutNodes(GraphNodeBase source, IEnumerable<GraphNodeBase> targets, bool seeded = false)
        {
            if (source == null || targets == null) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;
            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Fan-Out Transitions"))
            {
                foreach (var target in targets)
                    AddBatchTransition(source, target, sm, created);
                if (seeded) SeedCreated(created);
            }
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        public void FanInNodes(IEnumerable<GraphNodeBase> sources, GraphNodeBase target, bool seeded = false)
        {
            if (sources == null || target == null) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;
            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Fan-In Transitions"))
            {
                foreach (var source in sources)
                    AddBatchTransition(source, target, sm, created);
                if (seeded) SeedCreated(created);
            }
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        public void CrossProductNodes(IList<GraphNodeBase> sources, IList<GraphNodeBase> targets, bool seeded = false)
        {
            if (sources == null || targets == null || sources.Count == 0 || targets.Count == 0) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;
            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Multi Transition"))
            {
                foreach (var source in sources)
                    foreach (var target in targets)
                        AddBatchTransition(source, target, sm, created);
                if (seeded) SeedCreated(created);
            }
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        /// <summary>Seeded batch creation: the first copied transition's settings and
        /// conditions stamp every created transition.</summary>
        static void SeedCreated(List<AnimatorTransitionBase> created)
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            foreach (var transition in created)
                TransitionClipboard.Apply(transition, snapshot);
        }

        /// <summary>
        /// One step of a chain / fan / cross batch. Skips invalid pairs (per
        /// <see cref="TransitionConnect.CanConnect"/>) and self-loops on plain states, so
        /// overlapping selections never produce nonsense transitions.
        /// </summary>
        void AddBatchTransition(GraphNodeBase source, GraphNodeBase destination, AnimatorStateMachine sm,
            List<AnimatorTransitionBase> created)
        {
            if (source == null || destination == null || source == destination) return;
            if (!TransitionConnect.CanConnect(source, destination)) return;
            var transition = CreateTransitionCore(source, destination, sm);
            if (transition != null) created.Add(transition);
        }

        // ---- copy / paste ----------------------------------------------------

        public void CopySelectedStates()
        {
            var states = new GraphSelectionSet(_graphView.selection).States;
            if (states.Count == 0) return;
            StateClipboard.Copy(states, StateNodePosition, null, _context.Controller, _context.CurrentStateMachine);
            // States and frames/notes paste together, so a fresh copy of one kind has to drop the
            // other — otherwise the next paste would also drop whatever was copied before it.
            FrameNoteClipboard.Clear();
        }

        /// <summary>
        /// Pastes into the state machine currently on screen, so switching layers between the
        /// copy and the paste is what moves states from one layer to another. Parameters the
        /// states reference are recreated when the destination controller lacks them.
        /// </summary>
        public void PasteStates(Vector2 position)
        {
            if (!StateClipboard.HasData) return;
            var controller = _context.Controller;
            int parametersBefore = controller != null ? controller.parameters.Length : 0;
            StateClipboard.Paste(_context.CurrentStateMachine, position, controller);
            if (controller != null && controller.parameters.Length != parametersBefore)
                _context.NotifyParametersChanged();
            RequestRebuild();
        }

        Vector2 StateNodePosition(AnimatorState state) =>
            state != null && _stateNodes.TryGetValue(state, out var node)
                ? node.GetPosition().position
                : Vector2.zero;

        // ---- frame / note copy / paste ---------------------------------------

        /// <summary>
        /// Ctrl+C over the canvas: the states, frames and notes in the selection all go to their
        /// clipboards in one gesture, sharing a single anchor so a mixed selection keeps its
        /// relative layout when it is pasted — including into a different layer.
        /// </summary>
        public void CopySelectedElements()
        {
            var selected = new GraphSelectionSet(_graphView.selection);
            var states = selected.States;
            var frames = selected.Frames;
            var notes = selected.Notes;
            int subStateMachines = selected.StateMachines.Count;
            // Sub-state machines aren't part of the state clipboard. Say so instead of copying a
            // silently incomplete selection — "select all" in a layer that has them looks like it
            // worked until the paste comes up short.
            if (subStateMachines > 0)
                Debug.Log("DaerD: " + subStateMachines + " sub-state machine(s) were left out of the copy"
                    + " — copy the whole layer (layer settings > Copy Layer) to move those too.");

            if (states.Count == 0 && frames.Count == 0 && notes.Count == 0) return;

            var anchor = new Vector2(float.MaxValue, float.MaxValue);
            foreach (var state in states) anchor = Vector2.Min(anchor, StateNodePosition(state));
            foreach (var frame in frames) anchor = Vector2.Min(anchor, frame.bounds.position);
            foreach (var note in notes) anchor = Vector2.Min(anchor, note.bounds.position);

            StateClipboard.Copy(states, StateNodePosition, anchor, _context.Controller, _context.CurrentStateMachine);
            FrameNoteClipboard.Copy(frames, notes, anchor);
        }

        /// <summary>Copies one frame (from its context menu), dropping any copied states.</summary>
        public void CopyFrame(GraphFrameData.Frame frame)
        {
            if (frame == null) return;
            StateClipboard.Clear();
            FrameNoteClipboard.Copy(new List<GraphFrameData.Frame> { frame }, null);
        }

        /// <summary>Copies one note (from its context menu), dropping any copied states.</summary>
        public void CopyNote(GraphFrameData.Note note)
        {
            if (note == null) return;
            StateClipboard.Clear();
            FrameNoteClipboard.Copy(null, new List<GraphFrameData.Note> { note });
        }

        /// <summary>
        /// Pastes the copied frames and notes into the state machine currently on screen — the
        /// clipboard holds no state machine reference, so this is what makes the copy land in
        /// whichever layer the user has open.
        /// </summary>
        public void PasteFramesAndNotes(Vector2 position)
        {
            if (!FrameNoteClipboard.HasData || _context.Controller == null) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;

            _frameData = GraphFrameData.GetOrCreate(_context.Controller);
            var created = FrameNoteClipboard.Paste(_frameData, sm, position);
            if (created.Count == 0) return;

            RequestRebuild();
            _context.Select(created[0]);
        }

        /// <summary>Pastes at the position the copy was taken from. Used where there is no mouse
        /// position to paste at (the inspector buttons), and it lands the copy in the same spot
        /// of the layer now on screen.</summary>
        public void PasteFramesAndNotesAtOrigin() => PasteFramesAndNotes(FrameNoteClipboard.Anchor);

        // ---- transition copy / paste -----------------------------------------

        /// <summary>
        /// Copies every (non-default) transition reachable from the given edges, recording each
        /// transition's source kind / source node and its destination so the snapshots can later
        /// be pasted onto a different state either as the new source or as the new destination.
        /// </summary>
        public void CopyTransitionsFromEdges(IEnumerable<TransitionEdge> edges)
        {
            var snapshots = new List<TransitionClipboard.Snapshot>();
            foreach (var edge in edges)
            {
                if (edge == null || edge.IsDefaultEdge) continue;
                var sourceNode = edge.output?.node as GraphNodeBase;
                ResolveSourceContext(sourceNode,
                    out var kind, out var sourceState, out var sourceSm);
                foreach (var t in edge.Transitions)
                {
                    if (t == null) continue;
                    snapshots.Add(TransitionClipboard.CaptureWithContext(t, kind, sourceState, sourceSm));
                }
            }
            if (snapshots.Count > 0)
                TransitionClipboard.CopySnapshots(snapshots);
        }

        /// <summary>
        /// Applies the first copied transition's settings (timing, conditions, mute/solo) onto every
        /// transition of the given edges — the "paste onto" behaviour, driven from Ctrl+V.
        /// </summary>
        public void PasteTransitionSettingsOntoEdges(IEnumerable<TransitionEdge> edges)
        {
            if (!TransitionClipboard.HasData) return;
            var snapshot = TransitionClipboard.Snapshots[0];
            bool any = false;
            using (new UndoScope("Paste Transition Settings"))
            {
                foreach (var edge in edges)
                {
                    if (edge == null || edge.IsDefaultEdge) continue;
                    foreach (var t in edge.Transitions)
                        if (t != null) { TransitionClipboard.Apply(t, snapshot); any = true; }
                }
            }
            if (any) Rebuild();
        }

        /// <summary>
        /// Adds a new transition alongside the existing ones on each given edge for every copied
        /// snapshot, applying its settings — the "paste as new" behaviour, driven from Ctrl+Shift+V.
        /// </summary>
        public void PasteTransitionsAsNewOnEdges(IEnumerable<TransitionEdge> edges)
        {
            if (!TransitionClipboard.HasData) return;
            var snapshots = TransitionClipboard.Snapshots;
            AnimatorTransitionBase last = null;
            using (new UndoScope("Paste Transition As New"))
            {
                foreach (var edge in edges)
                {
                    if (edge == null || edge.IsDefaultEdge) continue;
                    var source = edge.output?.node as GraphNodeBase;
                    var destination = edge.input?.node as GraphNodeBase;
                    if (source == null || destination == null) continue;
                    foreach (var snap in snapshots)
                    {
                        var created = CreateTransition(source, destination);
                        if (created != null) { TransitionClipboard.Apply(created, snap); last = created; }
                    }
                }
            }
            Rebuild();
            if (last != null) _context.Select(last);
        }

        static void ResolveSourceContext(GraphNodeBase node,
            out TransitionClipboard.SourceKind kind,
            out AnimatorState state,
            out AnimatorStateMachine stateMachine)
        {
            kind = TransitionClipboard.SourceKind.None;
            state = null;
            stateMachine = null;
            switch (node)
            {
                case StateNode sn:
                    kind = TransitionClipboard.SourceKind.State;
                    state = sn.State;
                    break;
                case SubStateMachineNode mn:
                    kind = TransitionClipboard.SourceKind.SubStateMachine;
                    stateMachine = mn.StateMachine;
                    break;
                case SpecialNode spn when spn.Kind == SpecialNodeKind.AnyState:
                    kind = TransitionClipboard.SourceKind.AnyState;
                    break;
                case SpecialNode spn when spn.Kind == SpecialNodeKind.Entry:
                    kind = TransitionClipboard.SourceKind.Entry;
                    break;
            }
        }

        /// <summary>
        /// Pastes the clipboard transitions onto <paramref name="state"/>, using it as the source
        /// for every new transition. Each new transition's destination is the snapshot's recorded
        /// destination (state, sub-state machine, or Exit). Snapshots whose destination cannot be
        /// resolved inside the current state machine are skipped.
        /// </summary>
        public void PasteTransitionsWithStateAsSource(AnimatorState state)
        {
            if (state == null) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;
            var snapshots = TransitionClipboard.Snapshots;
            if (snapshots.Count == 0) return;

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Paste Transition (As Source)"))
            {
                Undo.RegisterCompleteObjectUndo(state, "Paste Transition");
                foreach (var snap in snapshots)
                {
                    AnimatorTransitionBase t = null;
                    if (snap.isExit)
                    {
                        t = state.AddExitTransition();
                    }
                    else if (snap.destinationState != null && sm.ContainsState(snap.destinationState))
                    {
                        if (snap.destinationState == state) continue;
                        t = state.AddTransition(snap.destinationState);
                    }
                    else if (snap.destinationStateMachine != null && sm.ContainsStateMachine(snap.destinationStateMachine))
                    {
                        t = state.AddTransition(snap.destinationStateMachine);
                    }
                    if (t == null) continue;
                    TransitionClipboard.Apply(t, snap);
                    created.Add(t);
                }
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }

            Rebuild();
            if (created.Count > 0) _context.Select(created[0]);
        }

        /// <summary>
        /// Pastes the clipboard transitions onto <paramref name="state"/>, using it as the
        /// destination for every new transition. Each new transition is added at the snapshot's
        /// original source (state, sub-state machine, AnyState, or Entry of the current state
        /// machine). Snapshots whose source cannot be resolved are skipped.
        /// </summary>
        public void PasteTransitionsWithStateAsDestination(AnimatorState state)
        {
            if (state == null) return;
            var sm = _context.CurrentStateMachine;
            if (sm == null) return;
            var snapshots = TransitionClipboard.Snapshots;
            if (snapshots.Count == 0) return;

            var created = new List<AnimatorTransitionBase>();
            using (new UndoScope("Paste Transition (As Destination)"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Paste Transition");
                foreach (var snap in snapshots)
                {
                    AnimatorTransitionBase t = null;
                    switch (snap.sourceKind)
                    {
                        case TransitionClipboard.SourceKind.State:
                            if (snap.sourceState == null || snap.sourceState == state) break;
                            if (!sm.ContainsState(snap.sourceState)) break;
                            Undo.RegisterCompleteObjectUndo(snap.sourceState, "Paste Transition");
                            t = snap.sourceState.AddTransition(state);
                            break;
                        case TransitionClipboard.SourceKind.SubStateMachine:
                            if (snap.sourceStateMachine == null) break;
                            if (!sm.ContainsStateMachine(snap.sourceStateMachine)) break;
                            t = sm.AddStateMachineTransition(snap.sourceStateMachine, state);
                            break;
                        case TransitionClipboard.SourceKind.AnyState:
                            t = sm.AddAnyStateTransition(state);
                            break;
                        case TransitionClipboard.SourceKind.Entry:
                            t = sm.AddEntryTransition(state);
                            break;
                    }
                    if (t == null) continue;
                    TransitionClipboard.Apply(t, snap);
                    created.Add(t);
                }
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }

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

        public void SetRuntimeStateHash(int hash)
        {
            _runtimeStateHash = hash;
            RefreshRuntimeHighlight();
        }

        void RefreshRuntimeHighlight()
        {
            bool playing = EditorApplication.isPlaying;
            foreach (var pair in _stateNodes)
                pair.Value.SetIsCurrent(playing && pair.Key.nameHash == _runtimeStateHash);
        }
    }
}
