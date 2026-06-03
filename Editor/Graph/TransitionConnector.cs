using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>Shared rules for which graph nodes may start / receive a transition.</summary>
    static class TransitionConnect
    {
        /// <summary>Mirrors the source/destination combinations <see cref="GraphSync.CreateTransition"/> accepts.</summary>
        public static bool CanConnect(GraphNodeBase source, GraphNodeBase destination)
        {
            if (source == null || destination == null) return false;
            bool destState = destination is StateNode;
            bool destSsm = destination is SubStateMachineNode;
            bool destExit = destination is SpecialNode dsp && dsp.Kind == SpecialNodeKind.Exit;
            switch (source)
            {
                case StateNode _:
                case SubStateMachineNode _:
                    return destState || destSsm || destExit;
                case SpecialNode sp when sp.Kind == SpecialNodeKind.AnyState || sp.Kind == SpecialNodeKind.Entry:
                    // Entry / Any State cannot transition straight to Exit.
                    return destState || destSsm;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Lets a left-drag transition drop land anywhere on a destination node's body, and reproduces
    /// Unity's internal DefaultEdgeConnectorListener for the precise port-to-port case so the
    /// existing behaviour is unchanged.
    /// </summary>
    class NodeBodyEdgeConnectorListener : IEdgeConnectorListener
    {
        readonly List<Edge> _edgesToCreate = new List<Edge>();
        readonly List<GraphElement> _edgesToDelete = new List<GraphElement>();
        GraphViewChange _change;

        public NodeBodyEdgeConnectorListener()
        {
            _change.edgesToCreate = _edgesToCreate;
        }

        // Faithful copy of the stock listener: our ports are Multi-capacity so the single-capacity
        // cleanup never runs, but it is kept for parity. GraphSync.HandleChange turns the candidate
        // into a real transition and clears the list, so the foreach below adds nothing back.
        public void OnDrop(GraphView graphView, Edge edge)
        {
            _edgesToCreate.Clear();
            _edgesToCreate.Add(edge);
            _edgesToDelete.Clear();
            if (edge.input != null && edge.input.capacity == Port.Capacity.Single)
                foreach (var connection in edge.input.connections)
                    if (connection != edge) _edgesToDelete.Add(connection);
            if (edge.output != null && edge.output.capacity == Port.Capacity.Single)
                foreach (var connection in edge.output.connections)
                    if (connection != edge) _edgesToDelete.Add(connection);
            if (_edgesToDelete.Count > 0) graphView.DeleteElements(_edgesToDelete);

            var edgesToCreate = _edgesToCreate;
            if (graphView.graphViewChanged != null)
                edgesToCreate = graphView.graphViewChanged(_change).edgesToCreate;
            foreach (var e in edgesToCreate)
            {
                graphView.AddElement(e);
                edge.input?.Connect(e);
                edge.output?.Connect(e);
            }
        }

        // Released somewhere other than a compatible port: if it landed on a node, connect to it.
        // 'position' is the release point in panel/world coordinates — the same space worldBound uses.
        public void OnDropOutsidePort(Edge edge, Vector2 position)
        {
            var view = (edge?.output ?? edge?.input)?.GetFirstAncestorOfType<AnimatorGraphView>();
            view?.CompleteEdgeDropOnNode(edge, position);
        }
    }
}
