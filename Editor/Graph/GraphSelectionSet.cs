using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Per-element identity capture of the current graph selection. We store the
    /// underlying animator objects (and SpecialNode kinds) so the matching graph
    /// elements can be found again after a rebuild has replaced every visual node.
    ///
    /// It is also what lets the command classes stay view-free: they are handed these
    /// plain model buckets instead of graph elements. Building the set is read-only —
    /// it never touches the graph or the asset.
    /// </summary>
    class GraphSelectionSet
    {
        public readonly List<AnimatorState> States = new List<AnimatorState>();
        public readonly List<AnimatorStateMachine> StateMachines = new List<AnimatorStateMachine>();
        public readonly List<SpecialNodeKind> Specials = new List<SpecialNodeKind>();
        public readonly List<TransitionEdge> TransitionEdges = new List<TransitionEdge>();

        /// <summary>Every transition carried by the selected edges, flattened and null-free.</summary>
        public readonly List<AnimatorTransitionBase> Transitions = new List<AnimatorTransitionBase>();

        public readonly List<GraphFrameData.Frame> Frames = new List<GraphFrameData.Frame>();
        public readonly List<GraphFrameData.Note> Notes = new List<GraphFrameData.Note>();

        public GraphSelectionSet(IEnumerable<ISelectable> selection)
        {
            foreach (var selectable in selection)
            {
                switch (selectable)
                {
                    case StateNode sn when sn.State != null:
                        States.Add(sn.State);
                        break;
                    case SubStateMachineNode mn when mn.StateMachine != null:
                        StateMachines.Add(mn.StateMachine);
                        break;
                    case SpecialNode spn:
                        Specials.Add(spn.Kind);
                        break;
                    case TransitionEdge te:
                        TransitionEdges.Add(te);
                        foreach (var t in te.Transitions)
                            if (t != null) Transitions.Add(t);
                        break;
                    case FrameNode fn when fn.Frame != null:
                        Frames.Add(fn.Frame);
                        break;
                    case NoteNode nn when nn.Note != null:
                        Notes.Add(nn.Note);
                        break;
                }
            }
        }
    }
}
