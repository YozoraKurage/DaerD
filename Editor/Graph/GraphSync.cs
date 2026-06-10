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
        GraphFrameData _frameData;

        SpecialNode _entryNode, _exitNode, _anyStateNode;
        bool _rebuildScheduled;
        int _runtimeStateHash;

        public GraphSync(DaerDContext context, AnimatorGraphView graphView)
        {
            _context = context;
            _graphView = graphView;
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
            var capturedSelection = CaptureGraphSelection();

            _stateNodes.Clear();
            _ssmNodes.Clear();
            _nestedStateOwners.Clear();
            _nestedMachineOwners.Clear();
            _edges.Clear();
            _frameNodes.Clear();
            foreach (var element in _graphView.graphElements.ToList())
                _graphView.RemoveElement(element);

            var sm = _context.CurrentStateMachine;
            if (sm == null) return;

            _frameData = GraphFrameData.Find(_context.Controller);
            if (_frameData != null)
                foreach (var frame in _frameData.FramesIn(sm))
                {
                    FrameNode frameNode = null;
                    frameNode = new FrameNode(frame, () => PersistFrameGeometry(frameNode));
                    _frameNodes.Add(frameNode);
                    _graphView.AddElement(frameNode);
                    frameNode.SetPosition(frame.bounds);
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

        /// <summary>
        /// Per-element identity capture of the current graph selection. We store the
        /// underlying animator objects (and SpecialNode kinds) so the matching graph
        /// elements can be found again after a rebuild has replaced every visual node.
        /// </summary>
        class CapturedSelection
        {
            public readonly List<AnimatorState> States = new List<AnimatorState>();
            public readonly List<AnimatorStateMachine> StateMachines = new List<AnimatorStateMachine>();
            public readonly List<SpecialNodeKind> Specials = new List<SpecialNodeKind>();
            public readonly List<AnimatorTransitionBase> Transitions = new List<AnimatorTransitionBase>();
            public readonly List<GraphFrameData.Frame> Frames = new List<GraphFrameData.Frame>();
        }

        CapturedSelection CaptureGraphSelection()
        {
            var captured = new CapturedSelection();
            foreach (var selectable in _graphView.selection)
            {
                switch (selectable)
                {
                    case StateNode sn when sn.State != null:
                        captured.States.Add(sn.State);
                        break;
                    case SubStateMachineNode mn when mn.StateMachine != null:
                        captured.StateMachines.Add(mn.StateMachine);
                        break;
                    case SpecialNode spn:
                        captured.Specials.Add(spn.Kind);
                        break;
                    case TransitionEdge te:
                        foreach (var t in te.Transitions)
                            if (t != null) captured.Transitions.Add(t);
                        break;
                    case FrameNode fn when fn.Frame != null:
                        captured.Frames.Add(fn.Frame);
                        break;
                }
            }
            return captured;
        }

        void RestoreSelection(CapturedSelection captured)
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

        public GraphNodeBase FindNode(object model)
        {
            if (model is AnimatorState state && _stateNodes.TryGetValue(state, out var sn)) return sn;
            if (model is AnimatorStateMachine sm && _ssmNodes.TryGetValue(sm, out var mn)) return mn;
            return null;
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
            bool frameShiftedNodes = false;

            // Models that moved on their own — a frame dragging its contents must not shift
            // these a second time.
            var movedModels = new HashSet<object>();
            foreach (var element in moved)
            {
                if (element is StateNode s && s.State != null) movedModels.Add(s.State);
                else if (element is SubStateMachineNode m && m.StateMachine != null) movedModels.Add(m.StateMachine);
            }

            foreach (var element in moved)
            {
                if (element is FrameNode fn && fn.Frame != null && _frameData != null)
                {
                    var oldBounds = fn.Frame.bounds;
                    var newPosition = fn.GetPosition().position;
                    var delta = newPosition - oldBounds.position;
                    Undo.RecordObject(_frameData, "Move Frame");
                    fn.Frame.bounds.position = newPosition;
                    EditorUtility.SetDirty(_frameData);

                    if (fn.Frame.moveNodesWithFrame && delta.sqrMagnitude > 0.01f)
                    {
                        for (int i = 0; i < states.Length; i++)
                        {
                            if (states[i].state == null || movedModels.Contains(states[i].state)) continue;
                            if (!oldBounds.Contains(new Vector2(states[i].position.x, states[i].position.y))) continue;
                            var cs = states[i];
                            cs.position += new Vector3(delta.x, delta.y, 0f);
                            states[i] = cs;
                            statesDirty = true;
                            frameShiftedNodes = true;
                        }
                        for (int i = 0; i < machines.Length; i++)
                        {
                            if (machines[i].stateMachine == null || movedModels.Contains(machines[i].stateMachine)) continue;
                            if (!oldBounds.Contains(new Vector2(machines[i].position.x, machines[i].position.y))) continue;
                            var cm = machines[i];
                            cm.position += new Vector3(delta.x, delta.y, 0f);
                            machines[i] = cm;
                            machinesDirty = true;
                            frameShiftedNodes = true;
                        }
                    }
                    continue;
                }
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
            }

            if (statesDirty) sm.states = states;
            if (machinesDirty) sm.stateMachines = machines;
            EditorUtility.SetDirty(sm);
            // Nodes carried along by a frame changed only in the asset; refresh their visuals.
            if (frameShiftedNodes) RequestRebuild();
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
            AnimatorTransitionBase created = null;

            using (new UndoScope("Create Transition"))
            {
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
            }
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

        public AnimatorState CreateState(Vector2 position, string mode)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;

            AnimatorState state;
            using (new UndoScope("Create State"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create State");
                state = sm.AddState(MakeUniqueName(sm, "New State"), new Vector3(position.x, position.y, 0f));
                DaerDSettings.ApplyStateDefaultsTo(state);

                if (mode == "state-clip" && Selection.activeObject is AnimationClip clip)
                {
                    state.motion = clip;
                    state.name = MakeUniqueName(sm, clip.name);
                }
                else if (mode == "state-blendtree")
                {
                    var blendTree = new BlendTree { name = "Blend Tree", hideFlags = HideFlags.HideInHierarchy };
                    var path = AssetDatabase.GetAssetPath(_context.Controller);
                    if (!string.IsNullOrEmpty(path))
                        AssetDatabase.AddObjectToAsset(blendTree, _context.Controller);
                    state.motion = blendTree;
                }
                EditorUtility.SetDirty(sm);
            }
            return state;
        }

        /// <summary>
        /// Creates a state at <paramref name="position"/> using <paramref name="clip"/> as its
        /// motion. Used when an AnimationClip is dropped onto empty graph space.
        /// </summary>
        public AnimatorState CreateStateWithClip(Vector2 position, AnimationClip clip)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || clip == null) return null;

            AnimatorState state;
            using (new UndoScope("Create State"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create State");
                state = sm.AddState(MakeUniqueName(sm, clip.name), new Vector3(position.x, position.y, 0f));
                DaerDSettings.ApplyStateDefaultsTo(state);
                state.motion = clip;
                EditorUtility.SetDirty(sm);
            }
            return state;
        }

        /// <summary>Replaces a state's motion, used when an AnimationClip is dropped onto its node.</summary>
        public void AssignMotion(AnimatorState state, Motion motion)
        {
            if (state == null) return;
            using (new UndoScope("Assign Motion"))
            {
                Undo.RegisterCompleteObjectUndo(state, "Assign Motion");
                state.motion = motion;
                EditorUtility.SetDirty(state);
                if (_context.Controller != null)
                    EditorUtility.SetDirty(_context.Controller);
            }
        }

        public AnimatorStateMachine CreateSubStateMachine(Vector2 position)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null) return null;

            AnimatorStateMachine child;
            using (new UndoScope("Create Sub-State Machine"))
            {
                Undo.RegisterCompleteObjectUndo(sm, "Create Sub-State Machine");
                child = sm.AddStateMachine(MakeUniqueName(sm, "New Sub-State Machine"), new Vector3(position.x, position.y, 0f));
                EditorUtility.SetDirty(sm);
            }
            return child;
        }

        public void SetDefaultState(AnimatorState state)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || state == null) return;
            Undo.RegisterCompleteObjectUndo(sm, "Set Default State");
            sm.defaultState = state;
            EditorUtility.SetDirty(sm);
            RequestRebuild();
        }

        static string MakeUniqueName(AnimatorStateMachine sm, string baseName) =>
            StateDuplicator.MakeUniqueName(sm, baseName);

        // ---- frames ------------------------------------------------------------

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
        /// position changes are ignored on purpose: moves go through <see cref="ApplyMoves"/>,
        /// which needs the asset to still hold the pre-drag position to compute its delta.
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
            if (frame == null || _frameData == null) return;
            _frameData.RemoveFrame(frame);
            RequestRebuild();
        }

        public void ToggleFrameMoveNodes(GraphFrameData.Frame frame)
        {
            if (frame == null || _frameData == null) return;
            Undo.RecordObject(_frameData, "Edit Frame");
            frame.moveNodesWithFrame = !frame.moveNodesWithFrame;
            EditorUtility.SetDirty(_frameData);
        }

        // ---- pack / unpack -----------------------------------------------------

        public void PackSelectedStates(List<AnimatorState> states)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || states == null || states.Count == 0) return;
            var child = StatePacker.Pack(sm, states);
            if (child == null) return;
            RequestRebuild();
            _context.Select(child);
        }

        public void UnpackSubStateMachine(AnimatorStateMachine child)
        {
            var sm = _context.CurrentStateMachine;
            if (sm == null || child == null) return;
            var warnings = StatePacker.Unpack(sm, child, _context.Controller);
            foreach (var warning in warnings)
                Debug.LogWarning("DaerD: " + warning);
            RequestRebuild();
            _context.Select(null);
        }

        // ---- chain / fan transitions --------------------------------------------

        public void ChainStates(IList<AnimatorState> states)
        {
            var created = TransitionBatch.Chain(states);
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        public void FanOut(AnimatorState source, IEnumerable<AnimatorState> targets)
        {
            var created = TransitionBatch.FanOut(source, targets);
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        public void FanIn(IEnumerable<AnimatorState> sources, AnimatorState target)
        {
            var created = TransitionBatch.FanIn(sources, target);
            if (created.Count == 0) return;
            Rebuild();
            _context.Select(created[0]);
        }

        // ---- copy / paste ----------------------------------------------------

        public void CopySelectedStates()
        {
            var states = new List<AnimatorState>();
            foreach (var selectable in _graphView.selection)
                if (selectable is StateNode sn && sn.State != null)
                    states.Add(sn.State);
            if (states.Count == 0) return;
            StateClipboard.Copy(states, s => _stateNodes.TryGetValue(s, out var node)
                ? node.GetPosition().position
                : Vector2.zero);
        }

        public void PasteStates(Vector2 position)
        {
            if (!StateClipboard.HasData) return;
            StateClipboard.Paste(_context.CurrentStateMachine, position);
            RequestRebuild();
        }

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
                    else if (snap.destinationState != null && IsStateInStateMachine(snap.destinationState, sm))
                    {
                        if (snap.destinationState == state) continue;
                        t = state.AddTransition(snap.destinationState);
                    }
                    else if (snap.destinationStateMachine != null && IsStateMachineInStateMachine(snap.destinationStateMachine, sm))
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
                            if (!IsStateInStateMachine(snap.sourceState, sm)) break;
                            Undo.RegisterCompleteObjectUndo(snap.sourceState, "Paste Transition");
                            t = snap.sourceState.AddTransition(state);
                            break;
                        case TransitionClipboard.SourceKind.SubStateMachine:
                            if (snap.sourceStateMachine == null) break;
                            if (!IsStateMachineInStateMachine(snap.sourceStateMachine, sm)) break;
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

        static bool IsStateInStateMachine(AnimatorState target, AnimatorStateMachine sm)
        {
            if (target == null || sm == null) return false;
            foreach (var cs in sm.states)
                if (cs.state == target) return true;
            return false;
        }

        static bool IsStateMachineInStateMachine(AnimatorStateMachine target, AnimatorStateMachine sm)
        {
            if (target == null || sm == null) return false;
            foreach (var cm in sm.stateMachines)
                if (cm.stateMachine == target) return true;
            return false;
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
