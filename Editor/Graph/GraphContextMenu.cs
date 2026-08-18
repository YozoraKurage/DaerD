using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Builds every right-click menu of <see cref="AnimatorGraphView"/> — the canvas menu, the
    /// frame and note menus, the transition-edge menu — and holds the commands those entries run.
    /// The view keeps the GraphView override and hands the event straight over, so the menu can
    /// grow without the view growing with it.
    /// </summary>
    class GraphContextMenu
    {
        readonly AnimatorGraphView _view;
        readonly DaerDContext _context;
        readonly GraphSync _sync;

        public GraphContextMenu(AnimatorGraphView view, DaerDContext context, GraphSync sync)
        {
            _view = view;
            _context = context;
            _sync = sync;
        }

        public void Build(ContextualMenuPopulateEvent evt)
        {
            var frameNode = AnimatorGraphView.ResolveTarget<FrameNode>(evt.target as VisualElement);
            if (frameNode != null)
            {
                BuildFrameMenu(evt, frameNode);
                return;
            }

            var noteNode = AnimatorGraphView.ResolveTarget<NoteNode>(evt.target as VisualElement);
            if (noteNode != null)
            {
                BuildNoteMenu(evt, noteNode);
                return;
            }

            var edge = ResolveTargetEdge(evt.target as VisualElement);
            if (edge != null && !edge.IsDefaultEdge)
            {
                // If the right-clicked edge is part of a larger multi-edge selection, the
                // menu acts on every selected edge; otherwise just on the clicked one. This
                // matches how Unity's other contextual menus behave when right-clicking
                // inside an existing selection.
                var selectedEdges = _view.GetSelectedEdges();
                if (!selectedEdges.Contains(edge))
                    selectedEdges = new List<TransitionEdge> { edge };
                BuildTransitionMenu(evt, edge, selectedEdges);
                return;
            }

            var graphPosition = _view.LastMouseGraphPosition;

            evt.menu.AppendAction(L.Tr("Create State"), _ => CreateStateAt(graphPosition, "state"));
            evt.menu.AppendAction(L.Tr("Create Blend Tree State"), _ => CreateStateAt(graphPosition, "state-blendtree"));
            evt.menu.AppendAction(L.Tr("Create Sub-State Machine"), _ =>
            {
                _sync.CreateSubStateMachine(graphPosition);
                _sync.RequestRebuild();
            });

            evt.menu.AppendSeparator();

            int stateCount = CountSelected<StateNode>();
            evt.menu.AppendAction(L.Tr("Copy State(s)"), _ => _sync.CopySelectedStates(),
                stateCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(L.Tr("Paste State(s)"), _ => _sync.PasteStates(graphPosition),
                StateClipboard.HasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(L.Tr("Duplicate State(s)"), _ => _view.DuplicateSelectedStates(),
                stateCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            BuildBehaviourClipboardEntries(evt, stateCount);
            evt.menu.AppendAction(L.Tr("Delete"), _ => DeleteCurrentSelection(),
                HasDeletableSelection() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            evt.menu.AppendSeparator();
            BuildConnectMenu(evt);

            if (stateCount == 1)
            {
                var stateNode = FirstSelected<StateNode>();
                evt.menu.AppendAction(L.Tr("Set as Default State"), _ => _sync.SetDefaultState(stateNode.State));
            }

            BuildSelectTransitionsMenu(evt);

            if (stateCount == 1)
            {
                var stateNode = FirstSelected<StateNode>();
                int pasted = TransitionClipboard.Count;
                string pasteSuffix = pasted > 1 ? " (" + pasted + ")" : string.Empty;
                evt.menu.AppendAction(
                    MenuPath(L.Tr("Paste Transition"), L.Tr("This State → Original Destinations")) + pasteSuffix,
                    _ => _sync.PasteTransitionsWithStateAsSource(stateNode.State),
                    TransitionClipboard.HasDestinationContext
                        ? DropdownMenuAction.Status.Normal
                        : DropdownMenuAction.Status.Disabled);
                evt.menu.AppendAction(
                    MenuPath(L.Tr("Paste Transition"), L.Tr("Original Sources → This State")) + pasteSuffix,
                    _ => _sync.PasteTransitionsWithStateAsDestination(stateNode.State),
                    TransitionClipboard.HasSourceContext
                        ? DropdownMenuAction.Status.Normal
                        : DropdownMenuAction.Status.Disabled);
            }

            evt.menu.AppendSeparator();
            BuildFrameNoteMenu(evt, graphPosition);
            BuildSubStateMachineMenu(evt, stateCount);
            BuildLayoutMenu(evt);
            BuildClipMenu(evt);

            if (stateCount == 1)
            {
                // Destructive (removes every transition touching the state), so it sits at the very
                // bottom of the menu where it can't be triggered by accident.
                var stateNode = FirstSelected<StateNode>();
                CountConnectedTransitions(stateNode, out _, out _, out int connected);
                evt.menu.AppendSeparator();
                evt.menu.AppendAction(L.Tr("Disconnect All"), _ => DisconnectStateNode(stateNode),
                    connected > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            }
        }

        // The canvas menu is long, so everything that isn't a per-state action lives in its own
        // submenu: annotations, grouping, layout and clip settings each get one line in the root
        // menu instead of four loose entries competing with the state commands.

        /// <summary>Frame / Note creation and pasting.</summary>
        void BuildFrameNoteMenu(ContextualMenuPopulateEvent evt, Vector2 graphPosition)
        {
            string group = L.Tr("Frame & Note");
            int selectedNodeCount = CountSelected<StateNode>() + CountSelected<SubStateMachineNode>();

            evt.menu.AppendAction(MenuPath(group, L.Tr("Create Frame")), _ => _sync.CreateFrameAt(graphPosition));
            evt.menu.AppendAction(MenuPath(group, L.Tr("Create Frame Around Selection")), _ => CreateFrameAroundSelection(),
                selectedNodeCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(MenuPath(group, L.Tr("Create Note")), _ => _sync.CreateNoteAt(graphPosition));
            // The frame/note clipboard is layer-agnostic: this pastes into the layer on screen.
            evt.menu.AppendAction(MenuPath(group, PasteFramesAndNotesLabel()), _ => _sync.PasteFramesAndNotes(graphPosition),
                FrameNoteClipboard.HasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        /// <summary>Pack the selection into a sub-state machine / unpack one back out.</summary>
        void BuildSubStateMachineMenu(ContextualMenuPopulateEvent evt, int stateCount)
        {
            string group = L.Tr("Sub-State Machine");

            evt.menu.AppendAction(
                MenuPath(group, L.Tr("Pack Selected States")) + (stateCount > 1 ? " (" + stateCount + ")" : string.Empty),
                _ => _sync.PackSelectedStates(_view.GetSelectedStates()),
                stateCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            bool unpackable = CountSelected<SubStateMachineNode>() == 1 && stateCount == 0;
            var ssmNode = unpackable ? FirstSelected<SubStateMachineNode>() : null;
            evt.menu.AppendAction(MenuPath(group, L.Tr("Unpack Into Parent")),
                _ => _sync.UnpackSubStateMachine(ssmNode.StateMachine),
                unpackable ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        /// <summary>Align / distribute the selection and frame the whole graph.</summary>
        void BuildLayoutMenu(ContextualMenuPopulateEvent evt)
        {
            string group = L.Tr("Layout");
            // Align acts on the current multi-selection, so it grays out until ≥2 states are selected.
            evt.menu.AppendAction(MenuPath(group, L.Tr("Align horizontal")),
                _ => AlignSelectedStates(GraphLayout.AlignAxis.Row), AlignStatus);
            evt.menu.AppendAction(MenuPath(group, L.Tr("Align vertical")),
                _ => AlignSelectedStates(GraphLayout.AlignAxis.Column), AlignStatus);
            var distributeStatus = _view.GetSelectedStates().Count >= 3
                ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            evt.menu.AppendAction(MenuPath(group, L.Tr("Distribute horizontal")),
                _ => DistributeSelectedStates(GraphLayout.AlignAxis.Row), distributeStatus);
            evt.menu.AppendAction(MenuPath(group, L.Tr("Distribute vertical")),
                _ => DistributeSelectedStates(GraphLayout.AlignAxis.Column), distributeStatus);
            evt.menu.AppendAction(MenuPath(group, L.Tr("Frame All")), _ => _view.FrameAll());
        }

        /// <summary>Loop-time toggle for the clips of the selected states (skips blend trees).</summary>
        void BuildClipMenu(ContextualMenuPopulateEvent evt)
        {
            int clipStates = CountSelectedClipStates();
            var loopStatus = clipStates > 0
                ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            string group = L.Tr("Clip Loop Time");
            evt.menu.AppendAction(MenuPath(group, L.Tr("On")) + " (" + clipStates + ")",
                _ => SetSelectedClipLoopTime(true), loopStatus);
            evt.menu.AppendAction(MenuPath(group, L.Tr("Off")) + " (" + clipStates + ")",
                _ => SetSelectedClipLoopTime(false), loopStatus);
        }

        /// <summary>Copy Behaviours from one state / paste onto every selected state. Paste
        /// offers Replace (clear targets first) and Append.</summary>
        void BuildBehaviourClipboardEntries(ContextualMenuPopulateEvent evt, int stateCount)
        {
            var states = _view.GetSelectedStates();
            bool anyBehaviours = false;
            foreach (var state in states)
                if (state.behaviours != null && state.behaviours.Length > 0)
                { anyBehaviours = true; break; }

            evt.menu.AppendAction(MenuPath(L.Tr("Behaviours"), L.Tr("Copy From This State")),
                _ => VrcBehaviours.Copy(FirstSelected<StateNode>().State.behaviours),
                stateCount == 1 && anyBehaviours
                    ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            int copied = VrcBehaviours.ClipboardCount;
            string suffix = copied > 0 ? " (" + copied + ")" : string.Empty;
            var pasteStatus = stateCount > 0 && copied > 0
                ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            evt.menu.AppendAction(MenuPath(L.Tr("Behaviours"), L.Tr("Paste (Append)")) + suffix,
                _ => PasteBehavioursOnSelection(replace: false), pasteStatus);
            evt.menu.AppendAction(MenuPath(L.Tr("Behaviours"), L.Tr("Paste (Replace)")) + suffix,
                _ => PasteBehavioursOnSelection(replace: true), pasteStatus);
        }

        void PasteBehavioursOnSelection(bool replace)
        {
            var states = _view.GetSelectedStates();
            using (new UndoScope("Paste Behaviours"))
                foreach (var state in states)
                {
                    VrcBehaviours.Paste(state, replace);
                    _sync.RefreshStateNode(state);   // B badge updates immediately
                }
        }

        // Source set marked for the two-step cross-product flow. Static so it survives the menu
        // closing and the user changing the selection before the second step. We store the
        // underlying models (AnimatorState / AnimatorStateMachine / SpecialNodeKind) rather than
        // the live GraphNodeBase visuals so the marks survive a rebuild.
        static List<object> s_markedSources;

        public static int MarkedSourceCount => s_markedSources?.Count ?? 0;

        /// <summary>
        /// M: remember the selection as the source set of a wiring pass. Nothing is created yet —
        /// the marks survive changing the selection, which is the whole point of them.
        /// </summary>
        public bool MarkSelectedAsSources()
        {
            var selected = _view.GetSelectedConnectables();
            if (selected.Count == 0) return false;
            s_markedSources = ToModels(selected);
            return true;
        }

        /// <summary>
        /// T: the one wiring key. With sources marked it connects every marked node to every
        /// selected one and drops the marks; with nothing marked it chains the selection in the
        /// order it was clicked, which is the two-node case that needs no marking at all.
        /// Returns false when there is nothing it could mean, so the key falls through.
        /// </summary>
        public bool ConnectSelection()
        {
            s_markedSources?.RemoveAll(IsMarkedSourceStale);
            var selected = _view.GetSelectedConnectables();
            if (selected.Count == 0) return false;

            if (MarkedSourceCount > 0)
            {
                _sync.CrossProductNodes(ResolveMarkedSources(), selected);
                s_markedSources = null;
                return true;
            }

            if (selected.Count < 2) return false;
            _sync.ChainNodes(selected);
            return true;
        }

        public static void ClearMarkedSources() => s_markedSources = null;

        /// <summary>
        /// Chain / fan / cross-product transition creation between the selected nodes. Every entry
        /// spells out its direction with the actual node name and the number of nodes involved,
        /// because "this → other selected" reads as a riddle when you are looking at a graph.
        /// Entries that stamp the copied transition's settings onto what they create are grouped
        /// under one sub-item instead of doubling the list.
        /// </summary>
        void BuildConnectMenu(ContextualMenuPopulateEvent evt)
        {
            var selected = _view.GetSelectedConnectables();
            string group = L.Tr("Connect States");
            var chainStatus = selected.Count >= 2
                ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;

            evt.menu.AppendAction(
                MenuPath(group, L.Tr("Chain in click order ({0})", selected.Count)
                    + DaerDShortcuts.Hint(ShortcutScope.Graph, DaerDCommand.Connect)),
                _ => _sync.ChainNodes(_view.GetSelectedConnectables()), chainStatus);

            BuildFanEntries(evt, selected, group);
            BuildCrossProductEntries(evt, selected, group);

            if (!TransitionClipboard.HasData) return;

            // "Seeded" = every created transition gets the copied transition's settings and
            // conditions. One sub-group keeps the main list readable.
            string seeded = L.Tr("Using the copied Transition as a template");
            evt.menu.AppendAction(
                MenuPath(group, seeded, L.Tr("Chain in click order ({0})", selected.Count)),
                _ => _sync.ChainNodes(_view.GetSelectedConnectables(), seeded: true), chainStatus);

            var target = ConnectTarget(evt, selected);
            if (target != null)
            {
                var others = OthersThan(target, selected);
                string name = MenuEscape(GraphSync.NodeLabel(target));
                evt.menu.AppendAction(
                    MenuPath(group, seeded, L.Tr("'{0}' → the other {1} selected", name, others.Count)),
                    _ => _sync.FanOutNodes(target, others, seeded: true));
                evt.menu.AppendAction(
                    MenuPath(group, seeded, L.Tr("The other {1} selected → '{0}'", name, others.Count)),
                    _ => _sync.FanInNodes(others, target, seeded: true));
            }

            int marked = s_markedSources?.Count ?? 0;
            if (marked > 0 && selected.Count > 0)
                evt.menu.AppendAction(
                    MenuPath(group, seeded, L.Tr("Step 2: marked ({0}) → selected ({1})", marked, selected.Count)),
                    _ =>
                    {
                        _sync.CrossProductNodes(ResolveMarkedSources(), _view.GetSelectedConnectables(), seeded: true);
                        s_markedSources = null;
                        _view.RefreshHint();
                    });
        }

        /// <summary>The right-clicked node, when it is part of a multi-node selection.</summary>
        GraphNodeBase ConnectTarget(ContextualMenuPopulateEvent evt, List<GraphNodeBase> selected)
        {
            var target = AnimatorGraphView.ResolveTarget<GraphNodeBase>(evt.target as VisualElement);
            return target != null && selected.Count >= 2 && selected.Contains(target) ? target : null;
        }

        static List<GraphNodeBase> OthersThan(GraphNodeBase target, List<GraphNodeBase> selected)
        {
            var others = new List<GraphNodeBase>();
            foreach (var node in selected)
                if (node != target) others.Add(node);
            return others;
        }

        void BuildFanEntries(ContextualMenuPopulateEvent evt, List<GraphNodeBase> selected, string group)
        {
            var target = ConnectTarget(evt, selected);
            if (target == null) return;
            // Entry/Exit/AnyState aren't context targets — they have no contextual menu of their
            // own. So the "this" target will always be a state or sub-state machine. We still
            // pass it through TransitionConnect.CanConnect at use time so any nonsense pair
            // (e.g. AnyState → AnyState in `others`) silently drops.
            var others = OthersThan(target, selected);
            string name = MenuEscape(GraphSync.NodeLabel(target));

            evt.menu.AppendAction(MenuPath(group, L.Tr("'{0}' → the other {1} selected", name, others.Count)),
                _ => _sync.FanOutNodes(target, others));
            evt.menu.AppendAction(MenuPath(group, L.Tr("The other {1} selected → '{0}'", name, others.Count)),
                _ => _sync.FanInNodes(others, target));
        }

        /// <summary>
        /// Two-step cross product: mark the current selection as the source set, change the
        /// selection, then connect every marked source to every now-selected node.
        /// </summary>
        void BuildCrossProductEntries(ContextualMenuPopulateEvent evt, List<GraphNodeBase> selected, string group)
        {
            // Marked items whose model was destroyed (or never resolved on this rebuild) drop out.
            s_markedSources?.RemoveAll(IsMarkedSourceStale);
            int marked = s_markedSources?.Count ?? 0;

            evt.menu.AppendSeparator(MenuEscape(group) + "/");
            // Numbered, because this is the one flow in the menu that takes two passes: mark a set,
            // change the selection, then connect. Without the numbers nobody guesses the order.
            evt.menu.AppendAction(
                MenuPath(group, L.Tr("Step 1: mark the selected {0} as sources", selected.Count)
                    + DaerDShortcuts.Hint(ShortcutScope.Graph, DaerDCommand.MarkSources)),
                _ =>
                {
                    s_markedSources = ToModels(_view.GetSelectedConnectables());
                    _view.RefreshHint();
                },
                selected.Count > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(
                MenuPath(group, L.Tr("Step 2: marked ({0}) → selected ({1})", marked, selected.Count)
                    + DaerDShortcuts.Hint(ShortcutScope.Graph, DaerDCommand.Connect)),
                _ =>
                {
                    _sync.CrossProductNodes(ResolveMarkedSources(), _view.GetSelectedConnectables());
                    s_markedSources = null;
                    _view.RefreshHint();
                },
                marked > 0 && selected.Count > 0
                    ? DropdownMenuAction.Status.Normal
                    : DropdownMenuAction.Status.Disabled);
            if (marked > 0)
                evt.menu.AppendAction(MenuPath(group, L.Tr("Clear the source marks ({0})", marked)),
                    _ =>
                    {
                        s_markedSources = null;
                        _view.RefreshHint();
                    });
        }

        static List<object> ToModels(IList<GraphNodeBase> nodes)
        {
            var result = new List<object>(nodes.Count);
            foreach (var node in nodes)
                if (node?.Model != null) result.Add(node.Model);
            return result;
        }

        List<GraphNodeBase> ResolveMarkedSources()
        {
            var result = new List<GraphNodeBase>();
            if (s_markedSources == null) return result;
            foreach (var model in s_markedSources)
            {
                var node = _sync.FindNode(model);
                if (node != null) result.Add(node);
            }
            return result;
        }

        bool IsMarkedSourceStale(object model)
        {
            if (model == null) return true;
            // Destroyed UnityEngine.Object references become "fake null" — check that explicitly.
            if (model is UnityEngine.Object obj && obj == null) return true;
            return false;
        }

        void CreateFrameAroundSelection()
        {
            var nodes = new List<GraphNodeBase>();
            foreach (var s in _view.selection)
                if (s is StateNode || s is SubStateMachineNode)
                    nodes.Add((GraphNodeBase)s);
            _sync.CreateFrameAroundNodes(nodes);
        }

        static readonly (string name, int size)[] NoteFontSizes =
        {
            ("Small", 10),
            ("Medium", 12),
            ("Large", 16),
        };

        /// <summary>Names the paste entry after what is actually on the clipboard, so the menu
        /// says what will land on the canvas.</summary>
        static string PasteFramesAndNotesLabel()
        {
            int frames = FrameNoteClipboard.FrameCount;
            int notes = FrameNoteClipboard.NoteCount;
            if (frames > 0 && notes > 0) return L.Tr("Paste Frames and Notes") + " (" + (frames + notes) + ")";
            if (notes > 0) return L.Tr("Paste Notes") + " (" + notes + ")";
            if (frames > 0) return L.Tr("Paste Frames") + " (" + frames + ")";
            return L.Tr("Paste Frame or Note");
        }

        void BuildFrameMenu(ContextualMenuPopulateEvent evt, FrameNode frameNode)
        {
            var frame = frameNode.Frame;
            var unlessLocked = frame.locked ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal;
            // Snapshot the position now: the pointer is over the menu by the time an entry runs.
            var graphPosition = _view.LastMouseGraphPosition;

            evt.menu.AppendAction(L.Tr("Rename Frame"), _ => frameNode.BeginRename(), unlessLocked);
            // Duplicates the frame, the states inside, and the transitions among those states —
            // available even on a locked frame, since the copy is independent of the original.
            evt.menu.AppendAction(L.Tr("Duplicate Frame"), _ => _sync.DuplicateFrame(frame));
            // Copy takes the box alone (title, size, color) and survives a layer switch — use
            // Duplicate for a same-layer copy that brings the contents along.
            evt.menu.AppendAction(L.Tr("Copy Frame"), _ => _sync.CopyFrame(frame));
            evt.menu.AppendAction(PasteFramesAndNotesLabel(), _ => _sync.PasteFramesAndNotes(graphPosition),
                FrameNoteClipboard.HasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(L.Tr("Lock Frame"), _ => _sync.ToggleFrameLock(frame),
                frame.locked ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            evt.menu.AppendAction(L.Tr("Move Nodes With Frame"),
                _ => _sync.ToggleFrameMoveNodes(frame),
                frame.moveNodesWithFrame ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);

            evt.menu.AppendSeparator();
            int contents = _sync.NodesFullyInside(frame.bounds).Count;
            int stateCount = CountFrameStates(frame);
            evt.menu.AppendAction(L.Tr("Select Contents") + " (" + contents + ")",
                _ => _sync.SelectFrameContents(frame),
                contents > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            // States-only variant: drops the frame from the selection so the next action
            // (Ctrl+D, the multi-state inspector, alignment) operates purely on the states.
            evt.menu.AppendAction(L.Tr("Select States Inside") + " (" + stateCount + ")",
                _ => _sync.SelectFrameInternalStates(frame),
                stateCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(L.Tr("Fit To Contents"), _ => _sync.FitFrameToContents(frame),
                !frame.locked && contents > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            foreach (var preset in DaerDColors.FramePalette)
            {
                var captured = preset.color;
                evt.menu.AppendAction(MenuPath(L.Tr("Frame Color"), L.Tr(preset.name)), _ => _sync.SetFrameColor(frame, captured));
            }

            evt.menu.AppendSeparator();
            evt.menu.AppendAction(L.Tr("Delete Frame"), _ => _sync.DeleteFrame(frame), unlessLocked);
        }

        void BuildNoteMenu(ContextualMenuPopulateEvent evt, NoteNode noteNode)
        {
            var note = noteNode.Note;
            var graphPosition = _view.LastMouseGraphPosition;
            evt.menu.AppendAction(L.Tr("Edit Note"), _ => noteNode.BeginEdit());
            // Notes carry no layer reference once copied, so this is the way to reuse a memo in
            // another layer (or another controller).
            evt.menu.AppendAction(L.Tr("Copy Note"), _ => _sync.CopyNote(note));
            evt.menu.AppendAction(PasteFramesAndNotesLabel(), _ => _sync.PasteFramesAndNotes(graphPosition),
                FrameNoteClipboard.HasData ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            foreach (var preset in DaerDColors.NotePalette)
            {
                var captured = preset.color;
                evt.menu.AppendAction(MenuPath(L.Tr("Note Color"), L.Tr(preset.name)), _ =>
                    _sync.SetNoteColor(note, DaerDColors.Fade(captured, note.color.a)));
            }
            foreach (var percent in new[] { 100, 80, 60, 40 })
            {
                float alpha = percent / 100f;
                evt.menu.AppendAction(MenuPath(L.Tr("Opacity"), percent + "%"), _ =>
                    _sync.SetNoteColor(note, DaerDColors.Fade(note.color, alpha)),
                    Mathf.Abs(note.color.a - alpha) < 0.01f
                        ? DropdownMenuAction.Status.Checked
                        : DropdownMenuAction.Status.Normal);
            }
            foreach (var option in NoteFontSizes)
            {
                var captured = option.size;
                evt.menu.AppendAction(MenuPath(L.Tr("Font Size"), L.Tr(option.name)), _ => _sync.SetNoteFontSize(note, captured),
                    note.fontSize == captured ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            }

            evt.menu.AppendSeparator();
            evt.menu.AppendAction(L.Tr("Delete Note"), _ => _sync.DeleteNote(note));
        }

        void AlignSelectedStates(GraphLayout.AlignAxis axis)
        {
            GraphLayout.Align(_context.CurrentStateMachine, _view.GetSelectedStates(), axis);
            _sync.RequestRebuild();
        }

        void DistributeSelectedStates(GraphLayout.AlignAxis axis)
        {
            GraphLayout.Distribute(_context.CurrentStateMachine, _view.GetSelectedStates(), axis);
            _sync.RequestRebuild();
        }

        int CountSelectedClipStates()
        {
            int count = 0;
            foreach (var state in _view.GetSelectedStates())
                if (state.motion is AnimationClip) count++;
            return count;
        }

        /// <summary>Toggles loop time on the clips assigned to the selected states. Edits the
        /// clip assets themselves — every other state using the same clip is affected too.</summary>
        void SetSelectedClipLoopTime(bool loop)
        {
            var seen = new HashSet<AnimationClip>();
            using (new UndoScope("Set Clip Loop Time"))
                foreach (var state in _view.GetSelectedStates())
                {
                    if (!(state.motion is AnimationClip clip) || !seen.Add(clip)) continue;
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    if (settings.loopTime == loop) continue;
                    Undo.RegisterCompleteObjectUndo(clip, "Set Clip Loop Time");
                    settings.loopTime = loop;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                    EditorUtility.SetDirty(clip);
                }
        }

        DropdownMenuAction.Status AlignStatus(DropdownMenuAction _) =>
            _view.GetSelectedStates().Count >= 2 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;

        /// <summary>Removes every (non-default) transition entering or leaving the state.</summary>
        void DisconnectStateNode(StateNode node)
        {
            var toRemove = new List<GraphElement>();
            _view.edges.ForEach(e =>
            {
                if (e is TransitionEdge te && !te.IsDefaultEdge &&
                    (te.input?.node == node || te.output?.node == node))
                    toRemove.Add(te);
            });
            if (toRemove.Count == 0) return;
            using (new UndoScope("Disconnect All"))
                _sync.HandleChange(new GraphViewChange { elementsToRemove = toRemove });
            foreach (var ge in toRemove) _view.RemoveElement(ge);
        }

        // ---- transition bulk selection ---------------------------------------

        /// <summary>
        /// Select Transitions, over the whole selection rather than one state. The counts are
        /// what the entry would select, so "Incoming (0)" is greyed out and says why. The
        /// keyboard's I / O / P run the same code over the same set.
        /// </summary>
        void BuildSelectTransitionsMenu(ContextualMenuPopulateEvent evt)
        {
            var endpoints = _view.SelectedTransitionEndpoints();
            if (endpoints.Count == 0) return;

            _view.CountConnectedTransitions(endpoints, out int incoming, out int outgoing, out int connected);
            string group = L.Tr("Select Transitions");
            evt.menu.AppendAction(MenuPath(group, L.Tr("Incoming")) + " (" + incoming + ")",
                _ => _view.SelectTransitionsOf(endpoints, incoming: true, outgoing: false),
                incoming > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(MenuPath(group, L.Tr("Outgoing")) + " (" + outgoing + ")",
                _ => _view.SelectTransitionsOf(endpoints, incoming: false, outgoing: true),
                outgoing > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            evt.menu.AppendAction(MenuPath(group, L.Tr("All Connected")) + " (" + connected + ")",
                _ => _view.SelectTransitionsOf(endpoints, incoming: true, outgoing: true),
                connected > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        /// <summary>What one node alone is connected to, for the commands that stay single-state.</summary>
        void CountConnectedTransitions(StateNode stateNode, out int incoming, out int outgoing, out int connected) =>
            _view.CountConnectedTransitions(new HashSet<Node> { stateNode }, out incoming, out outgoing, out connected);

        // ---- transition edge menu --------------------------------------------

        /// <summary>Walks up from the right-clicked element to the transition edge it belongs to.</summary>
        static TransitionEdge ResolveTargetEdge(VisualElement element) =>
            AnimatorGraphView.ResolveTarget<TransitionEdge>(element);

        void BuildTransitionMenu(ContextualMenuPopulateEvent evt, TransitionEdge edge,
            List<TransitionEdge> selectedEdges)
        {
            int count = edge.Transitions.Count;
            string suffix = count > 1 ? " (" + count + ")" : string.Empty;

            evt.menu.AppendAction(L.Tr("Reverse Transition") + suffix,
                _ => _sync.ReverseEdge(edge),
                _sync.CanReverseEdge(edge) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            var targets = _sync.RedirectTargets(edge);
            if (targets.Count == 0)
            {
                evt.menu.AppendAction(L.Tr("Redirect Transition") + suffix, _ => { }, DropdownMenuAction.Status.Disabled);
            }
            else
            {
                foreach (var target in targets)
                {
                    var destination = target;
                    evt.menu.AppendAction(
                        MenuEscape(L.Tr("Redirect Transition") + suffix) + "/" + MenuEscape(GraphSync.NodeLabel(destination)),
                        _ => _sync.RedirectEdge(edge, destination));
                }
            }

            evt.menu.AppendAction(L.Tr("Replicate Transition") + suffix, _ => _sync.ReplicateEdge(edge));

            int copyCount = 0;
            foreach (var e in selectedEdges)
                if (!e.IsDefaultEdge) copyCount += e.Transitions.Count;
            string copySuffix = copyCount > 1 ? " (" + copyCount + ")" : string.Empty;
            evt.menu.AppendAction(L.Tr("Copy Transition") + copySuffix,
                _ => _sync.CopyTransitionsFromEdges(selectedEdges),
                copyCount > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        /// <summary>Neutralises '/' in node names so they don't spawn unintended submenus.</summary>
        static string MenuEscape(string name) =>
            string.IsNullOrEmpty(name) ? "?" : name.Replace('/', '\u2215');

        /// <summary>
        /// One "Group/Item" menu path. Every segment is escaped, so a '/' inside a label — or
        /// inside somebody's translation of one — adds no extra submenu level. ("Frame / Note"
        /// as a group name used to bury its items two levels deep.)
        /// </summary>
        static string MenuPath(string group, string item) =>
            MenuEscape(group) + "/" + MenuEscape(item);

        static string MenuPath(string group, string sub, string item) =>
            MenuEscape(group) + "/" + MenuEscape(sub) + "/" + MenuEscape(item);

        int CountFrameStates(GraphFrameData.Frame frame)
        {
            int count = 0;
            foreach (var element in _sync.NodesFullyInside(frame.bounds))
                if (element is StateNode sn && sn.State != null) count++;
            return count;
        }

        void CreateStateAt(Vector2 graphPosition, string mode)
        {
            var state = _sync.CreateState(graphPosition, mode);
            if (state != null) _context.Select(state);
            _sync.RequestRebuild();
        }

        void DeleteCurrentSelection()
        {
            var toRemove = new List<GraphElement>();
            foreach (var s in _view.selection)
                if (s is GraphElement ge && (ge.capabilities & Capabilities.Deletable) != 0)
                    toRemove.Add(ge);
            if (toRemove.Count == 0) return;

            var change = new GraphViewChange { elementsToRemove = toRemove };
            _sync.HandleChange(change);
            foreach (var ge in toRemove)
                _view.RemoveElement(ge);
        }

        bool HasDeletableSelection()
        {
            foreach (var s in _view.selection)
                if (s is GraphElement ge && (ge.capabilities & Capabilities.Deletable) != 0)
                    return true;
            return false;
        }

        int CountSelected<T>() where T : class
        {
            int count = 0;
            foreach (var s in _view.selection)
                if (s is T) count++;
            return count;
        }

        T FirstSelected<T>() where T : class
        {
            foreach (var s in _view.selection)
                if (s is T match) return match;
            return null;
        }
    }
}
