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
    class TransitionEdge : Edge
    {
        public readonly List<AnimatorTransitionBase> Transitions = new List<AnimatorTransitionBase>();
        public bool IsDefaultEdge;

        static readonly Color HighlightColor = new Color(0.96f, 0.84f, 0.22f);
        static readonly Color NormalColor = new Color(0.56f, 0.56f, 0.56f);
        static readonly Color MutedColor = new Color(0.72f, 0.22f, 0.22f);
        static readonly Color DefaultEdgeColor = new Color(0.85f, 0.55f, 0.20f);

        readonly Label _badge;
        bool _highlighted;

        public TransitionEdge()
        {
            _badge = new Label { pickingMode = PickingMode.Ignore };
            _badge.AddToClassList("transition-edge__badge");
            _badge.style.position = Position.Absolute;
            _badge.style.display = DisplayStyle.None;
            Add(_badge);
            RegisterCallback<GeometryChangedEvent>(_ => PlaceBadge());
        }

        public void SetHighlight(bool on)
        {
            _highlighted = on;
            Refresh();
        }

        public void Refresh()
        {
            if (IsDefaultEdge)
            {
                capabilities &= ~Capabilities.Deletable;
                edgeControl.inputColor = edgeControl.outputColor = DefaultEdgeColor;
                tooltip = "Default state";
                _badge.style.display = DisplayStyle.None;
                return;
            }

            bool allMuted = Transitions.Count > 0;
            foreach (var t in Transitions)
            {
                if (t == null || !t.mute) { allMuted = false; break; }
            }

            Color color = _highlighted ? HighlightColor : (allMuted ? MutedColor : NormalColor);
            edgeControl.inputColor = color;
            edgeControl.outputColor = color;
            tooltip = allMuted
                ? Transitions.Count + " muted transition(s)"
                : Transitions.Count + " transition(s)";

            if (Transitions.Count > 1)
            {
                _badge.text = Transitions.Count.ToString();
                _badge.style.display = DisplayStyle.Flex;
            }
            else
            {
                _badge.style.display = DisplayStyle.None;
            }
            PlaceBadge();
        }

        void PlaceBadge()
        {
            if (_badge.style.display == DisplayStyle.None || edgeControl == null) return;
            var center = edgeControl.layout.center;
            if (float.IsNaN(center.x) || float.IsNaN(center.y)) return;
            _badge.style.left = center.x - 9;
            _badge.style.top = center.y - 8;
        }
    }
}
