using System;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    enum SpecialNodeKind
    {
        Entry,
        Exit,
        AnyState,
        Up
    }

    /// <summary>
    /// Entry / Exit / Any State pseudo-nodes, and the "(Up) parent" node shown inside a
    /// sub-state machine. They cannot be deleted or copied.
    /// </summary>
    class SpecialNode : GraphNodeBase
    {
        public SpecialNodeKind Kind { get; }
        public override object Model => Kind;

        readonly Label _nameLabel;
        bool _playing;
        bool _next;

        /// <param name="parentName">The parent machine's name; read only by <see cref="SpecialNodeKind.Up"/>.</param>
        /// <param name="onOpen">Double-click action; only <see cref="SpecialNodeKind.Up"/> has one.</param>
        public SpecialNode(SpecialNodeKind kind, string parentName = null, Action onOpen = null)
        {
            Kind = kind;
            AddToClassList("special-node");
            AddToClassList("compact-node");

            string label;
            Color color;
            switch (kind)
            {
                case SpecialNodeKind.Entry:
                    label = "Entry";
                    color = DaerDColors.EntryNode;
                    AddOutputPort();
                    break;
                case SpecialNodeKind.Exit:
                    label = "Exit";
                    color = DaerDColors.ExitNode;
                    AddInputPort();
                    break;
                case SpecialNodeKind.Up:
                    // Where transitions leaving this sub-state machine are drawn to. Input only:
                    // nothing starts at the parent from in here.
                    label = "(Up) " + parentName;
                    color = DaerDColors.SubStateMachineHeader;
                    AddInputPort();
                    break;
                default: // AnyState
                    label = "Any State";
                    color = DaerDColors.AnyStateNode;
                    AddOutputPort();
                    break;
            }
            title = label;

            // Same compact form as StateNode: no title bar, the name centred in a text
            // column, with the node's single port small and on its edge (see DaerD.uss).
            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.AddToClassList("compact-node__text");

            _nameLabel = new Label(label) { pickingMode = PickingMode.Ignore };
            _nameLabel.AddToClassList("compact-node__name");
            _nameLabel.style.backgroundColor = color;
            text.Add(_nameLabel);
            topContainer.Insert(1, text);

            capabilities &= ~(Capabilities.Deletable | Capabilities.Copiable);
            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Snappable;

            if (onOpen != null)
            {
                RegisterCallback<MouseDownEvent>(evt =>
                {
                    if (evt.clickCount == 2 && evt.button == 0)
                    {
                        onOpen();
                        evt.StopPropagation();
                    }
                });
            }

            RefreshExpandedState();
            RefreshPorts();
        }

        /// <summary>Only "(Up)" lights: it stands in for every state outside this sub-state
        /// machine, so when the layer is playing out there it is the one thing on this screen that
        /// can say so. Entry, Exit and Any State are never a playing state.</summary>
        public override void SetPlayback(bool playing, bool next, float progress)
        {
            if (Kind != SpecialNodeKind.Up) return;
            // Every tick while playing; only touch the style when the answer changed.
            if (playing == _playing && next == _next) return;
            _playing = playing;
            _next = next;
            _nameLabel.style.backgroundColor =
                playing ? DaerDColors.Playing : next ? DaerDColors.PlayingNext : DaerDColors.SubStateMachineHeader;
        }

        // Suppress the stock node menu; AnimatorGraphView builds the full context menu itself.
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) { }
    }
}
