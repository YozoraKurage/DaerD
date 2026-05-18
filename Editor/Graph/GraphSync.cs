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
            _stateNodes.Clear();
            _ssmNodes.Clear();
            _edges.Clear();
            foreach (var element in _graphView.graphElements.ToList())
                _graphView.RemoveElement(element);

            var sm = _context.CurrentStateMachine;
            if (sm == null) return;

            _entryNode = new SpecialNode(SpecialNodeKind.Entry);
            _exitNode = new SpecialNode(SpecialNodeKind.Exit);
            _anyStateNode = new SpecialNode(SpecialNodeKind.AnyState);
            AddNode(_entryNode, sm.entryPosition);
            AddNode(_exitNode, sm.exitPosition);
            AddNode(_anyStateNode, sm.anyStatePosition);

            foreach (var child in sm.states)
            {
                if (child.state == null) continue;
                var node = new StateNode(child.state);
                _stateNodes[child.state] = node;
                AddNode(node, child.position);
            }

            foreach (var child in sm.stateMachines)
            {
                if (child.stateMachine == null) continue;
                var childSm = child.stateMachine;
                var node = new SubStateMachineNode(childSm, () => _context.EnterStateMachine(childSm));
                _ssmNodes[childSm] = node;
                AddNode(node, child.position);
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

            RestoreSelection();
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
            if (transition.destinationState != null && _stateNodes.TryGetValue(transition.destinationState, out var sn))
                return sn;
            if (transition.destinationStateMachine != null && _ssmNodes.TryGetValue(transition.destinationStateMachine, out var mn))
                return mn;
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

        void RestoreSelection()
        {
            var selection = _context.Selection;
            GraphElement element = null;
            switch (selection)
            {
                case AnimatorState state when _stateNodes.TryGetValue(state, out var sn):
                    element = sn;
                    break;
                case AnimatorStateMachine sm when _ssmNodes.TryGetValue(sm, out var mn):
                    element = mn;
                    break;
                case AnimatorTransitionBase transition:
                    foreach (var edge in _edges)
                        if (edge.Transitions.Contains(transition)) { element = edge; break; }
                    break;
                case TransitionEdge edge:
                    if (_edges.Contains(edge)) element = edge;
                    break;
            }
            _graphView.SetSelectionSilently(element);
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

            foreach (var element in moved)
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

        static string MakeUniqueName(AnimatorStateMachine sm, string baseName)
        {
            var taken = new HashSet<string>();
            foreach (var cs in sm.states)
                if (cs.state != null) taken.Add(cs.state.name);
            foreach (var cm in sm.stateMachines)
                if (cm.stateMachine != null) taken.Add(cm.stateMachine.name);
            if (!taken.Contains(baseName)) return baseName;
            int i = 1;
            while (taken.Contains(baseName + " " + i)) i++;
            return baseName + " " + i;
        }

        // ---- copy / paste ----------------------------------------------------

        public void CopySelectedStates()
        {
            var states = new List<AnimatorState>();
            foreach (var selectable in _graphView.selection)
                if (selectable is StateNode sn)
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
