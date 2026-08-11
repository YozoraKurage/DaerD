using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// One visual edge between two nodes. All transitions sharing the same source/destination
    /// pair are collapsed into a single edge; the count is shown as a badge.
    /// </summary>
    /// <remarks>
    /// A stock GraphView edge is a port-to-port bezier designed for left-to-right DAGs. An Animator
    /// state machine transitions in every direction, so a bezier between fixed left/right ports
    /// loops back on itself and is hard to read. This edge is instead drawn as a straight line from
    /// node centre to node centre, clipped at the node borders, with a direction arrow at its
    /// midpoint — matching Unity's own Animator window. The geometry is computed here in
    /// <see cref="UpdateEdgeControl"/> (port positions are ignored) and rendered by
    /// <see cref="TransitionEdgeControl"/>.
    /// </remarks>
    class TransitionEdge : Edge
    {
        public readonly List<AnimatorTransitionBase> Transitions = new List<AnimatorTransitionBase>();
        public bool IsDefaultEdge;

        /// <summary>How far a bidirectional pair (A→B and B→A) is nudged apart, in graph units.</summary>
        const float ParallelOffset = 7f;

        readonly Label _badge;
        readonly Label _conditionLabel;
        bool _highlighted;
        bool _hovered;
        bool _allMuted;
        bool _sourceHasSolo;
        bool _shutOutBySolo;
        bool _runtimeActive;

        public TransitionEdge()
        {
            _badge = new Label { pickingMode = PickingMode.Ignore };
            _badge.AddToClassList("transition-edge__badge");
            _badge.style.position = Position.Absolute;
            _badge.style.display = DisplayStyle.None;
            Add(_badge);

            _conditionLabel = new Label { pickingMode = PickingMode.Ignore };
            _conditionLabel.AddToClassList("transition-edge__label");
            _conditionLabel.style.position = Position.Absolute;
            _conditionLabel.style.display = DisplayStyle.None;
            Add(_conditionLabel);

            RegisterCallback<GeometryChangedEvent>(_ => PlaceBadge());
        }

        /// <summary>Returns a control that draws a straight, arrowed, centre-to-centre line.</summary>
        protected override EdgeControl CreateEdgeControl()
        {
            return new TransitionEdgeControl { interceptWidth = 6f };
        }

        public void SetHighlight(bool on)
        {
            _highlighted = on;
            ApplyColor();
        }

        /// <summary>
        /// Tells the edge whether some transition leaving the same node is soloed. Solo is a
        /// property of the source's whole list — one soloed transition shuts out every other
        /// transition leaving that node — so no edge can work this out on its own.
        /// </summary>
        public void SetSoloContext(bool sourceHasSolo) => _sourceHasSolo = sourceHasSolo;

        /// <summary>The pointer is over the inspector row that names one of these transitions.</summary>
        public void SetHover(bool on)
        {
            if (_hovered == on) return;
            _hovered = on;
            ApplyColor();
        }

        /// <summary>The crossfade running right now travels this edge. Called every tick while
        /// the editor plays, so it does nothing when the answer has not changed.</summary>
        public void SetRuntimeActive(bool on)
        {
            if (_runtimeActive == on) return;
            _runtimeActive = on;
            ApplyColor();
        }

        public override void OnSelected()
        {
            base.OnSelected();
            ApplyColor();
        }

        public override void OnUnselected()
        {
            base.OnUnselected();
            ApplyColor();
        }

        public void Refresh()
        {
            if (IsDefaultEdge)
            {
                capabilities &= ~Capabilities.Deletable;
                tooltip = L.Tr("Default state");
                _badge.style.display = DisplayStyle.None;
                _conditionLabel.style.display = DisplayStyle.None;
                ApplyColor();
                return;
            }

            // One-line condition summary on single-transition edges; multi-transition edges
            // already carry the count badge, so the label would just be ambiguous there.
            bool showConditions = DaerDSettings.ShowTransitionConditions
                && Transitions.Count == 1 && Transitions[0] != null;
            if (showConditions)
            {
                _conditionLabel.text = Summarize(Transitions[0]);
                _conditionLabel.style.display = string.IsNullOrEmpty(_conditionLabel.text)
                    ? DisplayStyle.None
                    : DisplayStyle.Flex;
            }
            else
            {
                _conditionLabel.style.display = DisplayStyle.None;
            }

            _allMuted = Transitions.Count > 0;
            foreach (var t in Transitions)
            {
                if (t == null || !t.mute) { _allMuted = false; break; }
            }

            // Muting beats soloing, so a soloed-but-muted transition keeps nothing alive.
            bool carriesSolo = false;
            foreach (var t in Transitions)
                if (t != null && t.solo && !t.mute) { carriesSolo = true; break; }
            _shutOutBySolo = _sourceHasSolo && !carriesSolo && !_allMuted;

            tooltip = _allMuted ? L.Tr("{0} muted transition(s)", Transitions.Count)
                : _shutOutBySolo ? L.Tr("{0} transition(s), shut out by a soloed transition on the same state", Transitions.Count)
                : L.Tr("{0} transition(s)", Transitions.Count);

            if (Transitions.Count > 1)
            {
                _badge.text = Transitions.Count.ToString();
                _badge.style.display = DisplayStyle.Flex;
            }
            else
            {
                _badge.style.display = DisplayStyle.None;
            }

            ApplyColor();
            PlaceBadge();
        }

        /// <summary>Resolves the edge colour from its current state and pushes it to the control.</summary>
        void ApplyColor()
        {
            Color color;
            // First, above selection itself. Hover answers "which line is the row under my
            // pointer", and the question is asked precisely when several rows are selected at
            // once — if selection outranked it, the answer would be invisible exactly when it
            // is needed.
            if (_hovered) color = DaerDColors.Hovered;
            else if (selected) color = DaerDColors.Selected;
            // Above the rest: it is the one thing on screen that is only true for a few frames.
            else if (_runtimeActive) color = DaerDColors.PlayingEdge;
            else if (IsDefaultEdge) color = DaerDColors.DefaultEdge;
            else if (_highlighted) color = DaerDColors.FoundByQuery;
            // Same meaning as muted, arrived at from the other side: nothing on this line can
            // fire. Solo has no colour of its own — it is only ever visible in what it disables.
            else if (_allMuted || _shutOutBySolo) color = DaerDColors.Muted;
            else color = DaerDColors.Edge;

            edgeControl.inputColor = color;
            edgeControl.outputColor = color;
            // Thicker as well as recoloured. A hovered edge can be one of a dozen selected at
            // once, all the same colour a moment ago; weight reads across a zoomed-out graph
            // where a hue change on a one-pixel line does not.
            edgeControl.edgeWidth = (int)(_hovered
                ? TransitionEdgeControl.HoverWidth : TransitionEdgeControl.LineWidth);
            edgeControl.MarkDirtyRepaint();
        }

        /// <summary>
        /// Recomputes the endpoints from the two connected node rectangles rather than from the
        /// fixed input/output ports, so the line runs centre-to-centre and is clipped at the node
        /// borders. A bidirectional pair (A→B and B→A) is nudged perpendicular to its direction so
        /// both edges stay visible instead of overlapping into one line.
        /// </summary>
        public override bool UpdateEdgeControl()
        {
            var sourceNode = output?.node;
            var destNode = input?.node;
            if (sourceNode == null || destNode == null)
                return false;

            Rect sourceRect = NodeRectInEdgeSpace(sourceNode);
            Rect destRect = NodeRectInEdgeSpace(destNode);
            Vector2 sourceCenter = sourceRect.center;
            Vector2 destCenter = destRect.center;

            Vector2 axis = destCenter - sourceCenter;
            // Also rejects NaN (which arises before the nodes have a resolved layout).
            if (!(axis.sqrMagnitude >= 1f))
                return false;

            Vector2 dir = axis.normalized;
            Vector2 start = ClipToBorder(sourceRect, sourceCenter, dir);
            Vector2 end = ClipToBorder(destRect, destCenter, -dir);

            // If the nodes overlap enough that the clipped endpoints crossed over, collapse them.
            if (Vector2.Dot(end - start, axis) <= 0f)
                start = end = (sourceCenter + destCenter) * 0.5f;

            if (HasReverseEdge())
            {
                // A→B offsets one way; B→A has the opposite direction, so its perpendicular
                // flips sign and it offsets the other way — the pair separates automatically.
                Vector2 perp = new Vector2(-dir.y, dir.x) * ParallelOffset;
                start += perp;
                end += perp;
            }

            edgeControl.from = start;
            edgeControl.to = end;
            edgeControl.UpdateLayout();
            PlaceBadge();
            return true;
        }

        /// <summary>The node's visible rectangle expressed in this edge's local coordinate space.</summary>
        /// <remarks>
        /// Clips against <c>#node-border</c> (the rounded body) rather than the outer Node element.
        /// Unity's default node styling leaves a few pixels of inset between the two, so using the
        /// outer element made transitions terminate in empty space outside the visible border.
        /// </remarks>
        Rect NodeRectInEdgeSpace(VisualElement node)
        {
            VisualElement bordered = node.Q("node-border") ?? node;
            Rect world = bordered.worldBound;
            Vector2 min = this.WorldToLocal(new Vector2(world.xMin, world.yMin));
            Vector2 max = this.WorldToLocal(new Vector2(world.xMax, world.yMax));
            return new Rect(min, max - min);
        }

        /// <summary>True when an edge also runs from this edge's destination back to its source.</summary>
        bool HasReverseEdge()
        {
            var sourceNode = output?.node;
            var destOutput = (input?.node as GraphNodeBase)?.Output;
            if (sourceNode == null || destOutput == null)
                return false;
            foreach (var connection in destOutput.connections)
            {
                if (connection != this && connection.input?.node == sourceNode)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// From <paramref name="inside"/> (a point within <paramref name="rect"/>), the spot where
        /// a ray heading along <paramref name="dir"/> exits the rectangle border.
        /// </summary>
        static Vector2 ClipToBorder(Rect rect, Vector2 inside, Vector2 dir)
        {
            float t = float.MaxValue;
            if (Mathf.Abs(dir.x) > 1e-4f)
            {
                float edgeX = dir.x > 0f ? rect.xMax : rect.xMin;
                t = Mathf.Min(t, (edgeX - inside.x) / dir.x);
            }
            if (Mathf.Abs(dir.y) > 1e-4f)
            {
                float edgeY = dir.y > 0f ? rect.yMax : rect.yMin;
                t = Mathf.Min(t, (edgeY - inside.y) / dir.y);
            }
            if (t == float.MaxValue || t < 0f)
                return inside;
            return inside + dir * t;
        }

        void PlaceBadge()
        {
            Vector2 a = edgeControl.from;
            Vector2 b = edgeControl.to;
            Vector2 mid = (a + b) * 0.5f;
            if (float.IsNaN(mid.x) || float.IsNaN(mid.y)) return;

            Vector2 axis = b - a;
            Vector2 perp = Vector2.zero;
            if (axis.sqrMagnitude > 1f)
            {
                var dir = axis.normalized;
                perp = new Vector2(-dir.y, dir.x);
            }

            if (_badge.style.display != DisplayStyle.None)
            {
                // Nudge the badge clear of the line so it does not sit on top of the arrow.
                var badgeMid = mid + perp * 11f;
                _badge.style.left = badgeMid.x - 9;
                _badge.style.top = badgeMid.y - 8;
            }

            if (_conditionLabel.style.display != DisplayStyle.None)
            {
                // Condition summary sits on the opposite side of the line from the badge.
                var labelMid = mid - perp * 13f;
                _conditionLabel.style.left = labelMid.x - 70;
                _conditionLabel.style.top = labelMid.y - 8;
            }
        }

        /// <summary>One-line human-readable summary of a transition's firing rule.</summary>
        static string Summarize(AnimatorTransitionBase transition)
        {
            var conditions = transition.conditions;
            if (conditions.Length == 0)
            {
                if (transition is AnimatorStateTransition st && st.hasExitTime)
                    return "exit @ " + st.exitTime.ToString("0.##");
                return "(no conditions)";
            }
            string text = ParameterConverter.DescribeCondition(conditions[0]);
            if (conditions.Length == 2) text += "  ·  " + ParameterConverter.DescribeCondition(conditions[1]);
            else if (conditions.Length > 2) text += "  +" + (conditions.Length - 1);
            return text;
        }

    }

    /// <summary>
    /// Edge renderer for <see cref="TransitionEdge"/>. Bypasses the GraphView bezier machinery and
    /// paints a straight line between the endpoints supplied by
    /// <see cref="TransitionEdge.UpdateEdgeControl"/>, plus a filled arrowhead showing direction.
    /// Hit-testing is done against that straight segment so selection still works.
    /// </summary>
    class TransitionEdgeControl : EdgeControl
    {
        public const float LineWidth = 3f;
        /// <summary>Width while an inspector row naming this edge is under the pointer.</summary>
        public const float HoverWidth = 6f;
        const float ArrowLength = 13f;
        const float ArrowHalfWidth = 7f;

        public TransitionEdgeControl()
        {
            edgeWidth = (int)LineWidth;
            // Replace the base bezier renderer with our straight-line + arrow renderer. The base
            // constructor already added its own handler; assigning discards it.
            generateVisualContent = OnGenerateVisualContent;
        }

        void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            GetLocalEndpoints(out Vector2 a, out Vector2 b);
            Vector2 axis = b - a;
            float length = axis.magnitude;
            if (length < 1f)
                return;

            Color color = inputColor;
            var painter = mgc.painter2D;

            painter.lineWidth = Mathf.Max(edgeWidth, 1);
            painter.lineCap = LineCap.Round;
            painter.strokeColor = color;
            painter.BeginPath();
            painter.MoveTo(a);
            painter.LineTo(b);
            painter.Stroke();

            // Filled arrowhead at the midpoint, pointing from source to destination. The arrow
            // shrinks on very short edges so it never overshoots the line.
            Vector2 dir = axis / length;
            Vector2 perp = new Vector2(-dir.y, dir.x);
            float arrowLength = Mathf.Min(ArrowLength, length * 0.6f);
            float arrowHalfWidth = ArrowHalfWidth * (arrowLength / ArrowLength);
            Vector2 mid = (a + b) * 0.5f;
            Vector2 tip = mid + dir * (arrowLength * 0.5f);
            Vector2 tail = mid - dir * (arrowLength * 0.5f);

            painter.fillColor = color;
            painter.BeginPath();
            painter.MoveTo(tip);
            painter.LineTo(tail + perp * arrowHalfWidth);
            painter.LineTo(tail - perp * arrowHalfWidth);
            painter.ClosePath();
            painter.Fill();
        }

        /// <summary>The edge endpoints, converted from parent (edge) space to this control's space.</summary>
        void GetLocalEndpoints(out Vector2 a, out Vector2 b)
        {
            if (parent != null)
            {
                a = parent.ChangeCoordinatesTo(this, from);
                b = parent.ChangeCoordinatesTo(this, to);
            }
            else
            {
                a = from;
                b = to;
            }
        }

        public override bool ContainsPoint(Vector2 localPoint)
        {
            GetLocalEndpoints(out Vector2 a, out Vector2 b);
            if ((a - b).sqrMagnitude < 1f)
                return false;
            float tolerance = Mathf.Max(interceptWidth, edgeWidth * 0.5f + 4f);
            return DistanceToSegmentSqr(localPoint, a, b) <= tolerance * tolerance;
        }

        public override bool Overlaps(Rect rect)
        {
            GetLocalEndpoints(out Vector2 a, out Vector2 b);
            if (rect.Contains(a) || rect.Contains(b))
                return true;
            return SegmentIntersectsRect(a, b, rect);
        }

        static float DistanceToSegmentSqr(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lenSqr = ab.sqrMagnitude;
            float t = lenSqr > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSqr) : 0f;
            Vector2 closest = a + ab * t;
            return (p - closest).sqrMagnitude;
        }

        static bool SegmentIntersectsRect(Vector2 a, Vector2 b, Rect rect)
        {
            Vector2 tl = new Vector2(rect.xMin, rect.yMin);
            Vector2 tr = new Vector2(rect.xMax, rect.yMin);
            Vector2 bl = new Vector2(rect.xMin, rect.yMax);
            Vector2 br = new Vector2(rect.xMax, rect.yMax);
            return SegmentsIntersect(a, b, tl, tr)
                || SegmentsIntersect(a, b, tr, br)
                || SegmentsIntersect(a, b, br, bl)
                || SegmentsIntersect(a, b, bl, tl);
        }

        static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
        {
            float d1 = Cross(p4 - p3, p1 - p3);
            float d2 = Cross(p4 - p3, p2 - p3);
            float d3 = Cross(p2 - p1, p3 - p1);
            float d4 = Cross(p2 - p1, p4 - p1);
            return ((d1 > 0f) != (d2 > 0f)) && ((d3 > 0f) != (d4 > 0f));
        }

        static float Cross(Vector2 u, Vector2 v) => u.x * v.y - u.y * v.x;
    }
}
