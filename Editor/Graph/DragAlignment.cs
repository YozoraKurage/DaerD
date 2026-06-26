using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Snaps the element currently being dragged (State / Sub-State Machine / Frame /
    /// Note / Special node) to the X or Y of any other element of the same family when
    /// within a small threshold, and draws guide lines so the user sees the alignment
    /// land. The snap logic is the same for every node kind so dragging a State and
    /// dragging a Frame feels identical.
    /// </summary>
    class DragAlignment
    {
        /// <summary>Snap radius in graph-coord pixels — the distance at which the dragged
        /// element jumps onto a candidate axis. Roughly the size of a node corner so the
        /// snap kicks in when the user is "close enough" that a small tweak would line them up.</summary>
        const float SnapRadius = 10f;

        static readonly Color GuideColor = new Color(1.0f, 0.78f, 0.20f, 0.9f);

        readonly AnimatorGraphView _graph;
        readonly VisualElement _verticalLine;
        readonly VisualElement _horizontalLine;

        bool _dragging;
        bool _snapping;
        GraphElement _leader;
        readonly Dictionary<GraphElement, Vector2> _followerOffsets = new Dictionary<GraphElement, Vector2>();

        public DragAlignment(AnimatorGraphView graph)
        {
            _graph = graph;

            // Two thin children of contentViewContainer — they share the graph's zoom /
            // pan transform automatically, so positioning them in graph coords (via
            // style.left / style.top) lines them up with the nodes underneath. The line
            // is much taller / wider than any reasonable graph so it always stretches
            // edge-to-edge of the visible area.
            _verticalLine = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    top = -50000f,
                    height = 100000f,
                    width = 1.5f,
                    backgroundColor = GuideColor,
                    display = DisplayStyle.None,
                },
            };
            _horizontalLine = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
                style =
                {
                    position = Position.Absolute,
                    left = -50000f,
                    width = 100000f,
                    height = 1.5f,
                    backgroundColor = GuideColor,
                    display = DisplayStyle.None,
                },
            };
            _graph.contentViewContainer.Add(_verticalLine);
            _graph.contentViewContainer.Add(_horizontalLine);

            graph.RegisterCallback<MouseDownEvent>(OnMouseDown, TrickleDown.TrickleDown);
            graph.RegisterCallback<MouseUpEvent>(OnMouseUp, TrickleDown.TrickleDown);
        }

        static bool IsSnapTarget(GraphElement element) =>
            element is StateNode || element is SubStateMachineNode
            || element is FrameNode || element is NoteNode || element is SpecialNode;

        void OnMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 0 || _dragging) return;
            var leader = FindDraggable(evt.target as VisualElement);
            if (leader == null) return;

            _leader = leader;
            _dragging = true;

            // Snapshot the other-selected elements' offsets so the snap moves the whole
            // group as one block — without this, snap displaces only the leader and the
            // followers drift away from their click-time positions.
            _followerOffsets.Clear();
            var leaderRect = leader.GetPosition();
            foreach (var s in _graph.selection)
            {
                if (s is GraphElement ge && ge != leader && IsSnapTarget(ge))
                {
                    var r = ge.GetPosition();
                    _followerOffsets[ge] = new Vector2(r.x - leaderRect.x, r.y - leaderRect.y);
                }
            }

            _leader.RegisterCallback<GeometryChangedEvent>(OnLeaderMoved);
        }

        void OnLeaderMoved(GeometryChangedEvent evt)
        {
            if (_snapping || !_dragging || _leader == null) return;

            var current = _leader.GetPosition();
            ComputeSnap(_leader, current, out var snappedX, out var snappedY,
                out var verticalGuide, out var horizontalGuide);

            float newX = snappedX ?? current.x;
            float newY = snappedY ?? current.y;
            bool moved = !Mathf.Approximately(newX, current.x) || !Mathf.Approximately(newY, current.y);
            bool hit = snappedX.HasValue || snappedY.HasValue;

            if (moved)
            {
                _snapping = true;
                try
                {
                    _leader.SetPosition(new Rect(newX, newY, current.width, current.height));
                    foreach (var pair in _followerOffsets)
                    {
                        var follower = pair.Key;
                        if (follower?.parent == null) continue;
                        var fr = follower.GetPosition();
                        follower.SetPosition(new Rect(newX + pair.Value.x, newY + pair.Value.y,
                            fr.width, fr.height));
                    }
                }
                finally
                {
                    _snapping = false;
                }
            }

            if (hit) ShowGuides(verticalGuide, horizontalGuide);
            else HideGuides();
        }

        /// <summary>
        /// Looks for the best snap target. <paramref name="snappedX"/> / <paramref name="snappedY"/>
        /// receive the new rect.x / rect.y to use (null if no candidate is in range). The two
        /// guide values are the alignment X (vertical line) and alignment Y (horizontal line) the
        /// snap landed on — drawn by the overlay so the user sees what they aligned to.
        /// </summary>
        void ComputeSnap(GraphElement target, Rect current,
            out float? snappedX, out float? snappedY,
            out float? verticalGuide, out float? horizontalGuide)
        {
            snappedX = snappedY = verticalGuide = horizontalGuide = null;

            float leftX = current.x;
            float centerX = current.x + current.width / 2f;
            float rightX = current.x + current.width;
            float topY = current.y;
            float centerY = current.y + current.height / 2f;
            float bottomY = current.y + current.height;

            float bestXDelta = SnapRadius;
            float bestYDelta = SnapRadius;

            foreach (var ge in _graph.graphElements)
            {
                if (ge == target) continue;
                if (!IsSnapTarget(ge)) continue;
                if (_followerOffsets.ContainsKey(ge)) continue;

                var r = ge.GetPosition();
                if (r.width <= 0f || r.height <= 0f) continue;

                float candidateLeft = r.x;
                float candidateCenterX = r.x + r.width / 2f;
                float candidateRight = r.x + r.width;
                float candidateTop = r.y;
                float candidateCenterY = r.y + r.height / 2f;
                float candidateBottom = r.y + r.height;

                // Try every (own edge, candidate edge) X pair — the smallest gap wins.
                TryAxis(leftX, candidateLeft, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: 0f, basis: current.x);
                TryAxis(leftX, candidateCenterX, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: 0f, basis: current.x);
                TryAxis(leftX, candidateRight, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: 0f, basis: current.x);
                TryAxis(centerX, candidateLeft, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: current.width / 2f, basis: current.x);
                TryAxis(centerX, candidateCenterX, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: current.width / 2f, basis: current.x);
                TryAxis(centerX, candidateRight, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: current.width / 2f, basis: current.x);
                TryAxis(rightX, candidateLeft, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: current.width, basis: current.x);
                TryAxis(rightX, candidateCenterX, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: current.width, basis: current.x);
                TryAxis(rightX, candidateRight, ref bestXDelta, ref snappedX, ref verticalGuide, edgeOffset: current.width, basis: current.x);

                TryAxis(topY, candidateTop, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: 0f, basis: current.y);
                TryAxis(topY, candidateCenterY, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: 0f, basis: current.y);
                TryAxis(topY, candidateBottom, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: 0f, basis: current.y);
                TryAxis(centerY, candidateTop, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: current.height / 2f, basis: current.y);
                TryAxis(centerY, candidateCenterY, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: current.height / 2f, basis: current.y);
                TryAxis(centerY, candidateBottom, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: current.height / 2f, basis: current.y);
                TryAxis(bottomY, candidateTop, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: current.height, basis: current.y);
                TryAxis(bottomY, candidateCenterY, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: current.height, basis: current.y);
                TryAxis(bottomY, candidateBottom, ref bestYDelta, ref snappedY, ref horizontalGuide, edgeOffset: current.height, basis: current.y);
            }
        }

        static void TryAxis(float ownEdge, float candidateLine,
            ref float bestDelta, ref float? snapped, ref float? guide, float edgeOffset, float basis)
        {
            float diff = Mathf.Abs(ownEdge - candidateLine);
            if (diff >= bestDelta) return;
            bestDelta = diff;
            // basis + (candidateLine - ownEdge) reads as: leave the rect where it is, then
            // shift it by the gap between this edge and the candidate axis. Same as
            // snapped = candidateLine - edgeOffset; expressed in delta form so the
            // followers' offsets land correctly.
            snapped = candidateLine - edgeOffset;
            guide = candidateLine;
        }

        void ShowGuides(float? verticalX, float? horizontalY)
        {
            if (verticalX.HasValue)
            {
                _verticalLine.style.left = verticalX.Value;
                _verticalLine.style.display = DisplayStyle.Flex;
            }
            else
            {
                _verticalLine.style.display = DisplayStyle.None;
            }
            if (horizontalY.HasValue)
            {
                _horizontalLine.style.top = horizontalY.Value;
                _horizontalLine.style.display = DisplayStyle.Flex;
            }
            else
            {
                _horizontalLine.style.display = DisplayStyle.None;
            }
        }

        void HideGuides()
        {
            _verticalLine.style.display = DisplayStyle.None;
            _horizontalLine.style.display = DisplayStyle.None;
        }

        void OnMouseUp(MouseUpEvent evt) => EndDrag();

        void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_leader != null)
            {
                _leader.UnregisterCallback<GeometryChangedEvent>(OnLeaderMoved);
                _leader = null;
            }
            _followerOffsets.Clear();
            HideGuides();
        }

        static GraphElement FindDraggable(VisualElement element)
        {
            while (element != null)
            {
                if (element is StateNode || element is SubStateMachineNode
                    || element is FrameNode || element is NoteNode || element is SpecialNode)
                    return (GraphElement)element;
                element = element.parent;
            }
            return null;
        }
    }
}
