using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The GraphView surface that draws one <see cref="PlayableGraphSnapshot"/>.
    ///
    /// The layout owns every position: outputs sit in the rightmost column and each step away
    /// from an output moves one column left, so the picture reads the way the data flows —
    /// sources on the left, the Animator being written to on the right. Siblings stack, and a
    /// subtree claims as much vertical room as its descendants need, which is the same two-pass
    /// measure-then-place as the blend tree view.
    ///
    /// <para>A running graph changes its weights every frame and its shape almost never, so a
    /// refresh compares the snapshot's <see cref="PlayableGraphSnapshot.Signature"/> and only
    /// rebuilds when the shape moved. Otherwise the existing nodes and edges are handed the new
    /// facts, which keeps the selection, the pan and the zoom where the user put them.</para>
    /// </summary>
    sealed class PlayableGraphView : GraphView
    {
        // The tree grows right-to-left; each generation gets a fixed horizontal slice and
        // siblings stack with vertical padding.
        const float ColumnWidth = 240f;
        const float NodeHeight = 84f;
        const float SiblingPadding = 14f;

        readonly List<PlayableGraphNode> _nodes = new List<PlayableGraphNode>();
        readonly List<WeightedEdge> _edges = new List<WeightedEdge>();
        readonly Dictionary<int, List<PlayableNodeInfo>> _children = new Dictionary<int, List<PlayableNodeInfo>>();

        PlayableGraphSnapshot _snapshot;
        string _signature;
        int _selectedIndex = -1;
        bool _framedOnce;

        public PlayableGraphView()
        {
            style.flexGrow = 1;
            focusable = true;

            SetupZoom(0.05f, 3.0f);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new RectangleSelector());
            // No SelectionDragger: the layout places the nodes and a dragged one would snap
            // back on the next refresh.

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
        }

        /// <summary>The node the user picked, from the newest snapshot, or null.</summary>
        public PlayableNodeInfo Selected
        {
            get
            {
                foreach (var element in selection)
                    if (element is PlayableGraphNode node) return node.Info;
                return null;
            }
        }

        /// <summary>Draws <paramref name="snapshot"/>, rebuilding only if its shape differs from
        /// what is on screen. A null snapshot empties the view.</summary>
        public void Show(PlayableGraphSnapshot snapshot)
        {
            _snapshot = snapshot;
            if (snapshot == null)
            {
                _signature = null;
                Wipe();
                return;
            }

            string signature = snapshot.Signature;
            if (signature == _signature) { ApplyLive(snapshot); return; }

            _signature = signature;
            Rebuild(snapshot);
        }

        void Wipe()
        {
            _nodes.Clear();
            _edges.Clear();
            _children.Clear();
            foreach (var element in graphElements.ToList())
                RemoveElement(element);
        }

        void Rebuild(PlayableGraphSnapshot snapshot)
        {
            int keepSelected = Selected?.Index ?? _selectedIndex;
            Wipe();

            foreach (var node in snapshot.Nodes)
            {
                if (node.ParentIndex < 0) continue;
                if (!_children.TryGetValue(node.ParentIndex, out var list))
                    _children[node.ParentIndex] = list = new List<PlayableNodeInfo>();
                list.Add(node);
            }

            // One node per entry, in snapshot order, so index i of _nodes is always snapshot
            // node i — which is what lets a live refresh find its node without a lookup.
            foreach (var info in snapshot.Nodes)
                _nodes.Add(Make(info));

            float cursor = 0f;
            foreach (var info in snapshot.Nodes)
            {
                if (!info.IsOutput) continue;
                float height = Measure(info);
                Place(info, cursor, height);
                cursor += height;
            }

            foreach (var info in snapshot.Nodes)
            {
                if (info.ParentIndex < 0) continue;
                Link(_nodes[info.Index], _nodes[info.ParentIndex], info.InputWeight);
            }

            if (keepSelected >= 0 && keepSelected < _nodes.Count)
            {
                _selectedIndex = keepSelected;
                AddToSelection(_nodes[keepSelected]);
            }

            if (_framedOnce) return;
            _framedOnce = true;
            schedule.Execute(() => FrameAll()).ExecuteLater(50);
        }

        PlayableGraphNode Make(PlayableNodeInfo info)
        {
            bool hasChildren = _children.ContainsKey(info.Index);
            var node = new PlayableGraphNode(info, hasParent: info.ParentIndex >= 0, hasChildren: hasChildren);
            AddElement(node);
            return node;
        }

        /// <summary>The vertical space this node and its descendants need.</summary>
        float Measure(PlayableNodeInfo info)
        {
            if (!_children.TryGetValue(info.Index, out var children)) return NodeHeight + SiblingPadding;
            float total = 0f;
            foreach (var child in children) total += Measure(child);
            return Mathf.Max(total, NodeHeight + SiblingPadding);
        }

        void Place(PlayableNodeInfo info, float top, float allocated)
        {
            float centerY = top + allocated * 0.5f;
            _nodes[info.Index].SetPosition(
                new Rect(-info.Depth * ColumnWidth, centerY - NodeHeight * 0.5f, 0f, 0f));

            if (!_children.TryGetValue(info.Index, out var children)) return;
            float cursor = top;
            foreach (var child in children)
            {
                float slot = Measure(child);
                Place(child, cursor, slot);
                cursor += slot;
            }
        }

        void Link(PlayableGraphNode from, PlayableGraphNode to, float weight)
        {
            if (from?.Output == null || to?.Input == null) return;
            var edge = new WeightedEdge { output = from.Output, input = to.Input };
            edge.capabilities &= ~(Capabilities.Deletable | Capabilities.Selectable | Capabilities.Movable);
            edge.pickingMode = PickingMode.Ignore;
            from.Output.Connect(edge);
            to.Input.Connect(edge);
            AddElement(edge);
            edge.SetWeight(weight);
            _edges.Add(edge);
        }

        /// <summary>Same shape, newer numbers.</summary>
        void ApplyLive(PlayableGraphSnapshot snapshot)
        {
            if (snapshot.Nodes.Count != _nodes.Count) { Rebuild(snapshot); return; }
            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].SetInfo(snapshot.Nodes[i]);

            int edge = 0;
            foreach (var info in snapshot.Nodes)
            {
                if (info.ParentIndex < 0) continue;
                if (edge >= _edges.Count) break;
                _edges[edge++].SetWeight(info.InputWeight);
            }
        }

        /// <summary>Nothing may be connected by hand; the graph on screen is somebody else's.</summary>
        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter) => new List<Port>();

        /// <summary>
        /// An edge whose colour carries the weight of the connection it stands for: a silent
        /// input fades into the background, a full one is drawn bright. The colour is re-applied
        /// on every style resolve because the stock edge takes its own back from the stylesheet
        /// whenever it is restyled.
        /// </summary>
        sealed class WeightedEdge : Edge
        {
            float _weight;

            public void SetWeight(float weight)
            {
                _weight = Mathf.Clamp01(weight);
                ApplyColor();
            }

            protected override void OnCustomStyleResolved(ICustomStyle styles)
            {
                base.OnCustomStyleResolved(styles);
                ApplyColor();
            }

            /// <summary>
            /// The stock edge repaints itself in its own colour from here, every time the
            /// geometry is recomputed — which a pointer moving over the graph does constantly.
            /// With the weight colour re-applied ten times a second from the other side, the
            /// two took turns and the edges flickered between yellow and grey. Re-applying
            /// after the base class has had its say is what settles it.
            /// </summary>
            public override bool UpdateEdgeControl()
            {
                if (!base.UpdateEdgeControl()) return false;
                ApplyColor();
                return true;
            }

            void ApplyColor()
            {
                var color = Color.Lerp(DaerDColors.PlayableLinkSilent, DaerDColors.PlayableLinkFull, _weight);
                edgeControl.inputColor = color;
                edgeControl.outputColor = color;
                edgeControl.edgeWidth = _weight > 0.5f ? 3 : 2;
                edgeControl.MarkDirtyRepaint();
            }
        }
    }
}
