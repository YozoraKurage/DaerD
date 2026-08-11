using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    enum SpecialNodeKind
    {
        Entry,
        Exit,
        AnyState
    }

    /// <summary>Entry / Exit / Any State pseudo-nodes. They cannot be deleted or copied.</summary>
    class SpecialNode : GraphNodeBase
    {
        public SpecialNodeKind Kind { get; }
        public override object Model => Kind;

        public SpecialNode(SpecialNodeKind kind)
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

            var nameLabel = new Label(label) { pickingMode = PickingMode.Ignore };
            nameLabel.AddToClassList("compact-node__name");
            nameLabel.style.backgroundColor = color;
            text.Add(nameLabel);
            topContainer.Insert(1, text);

            capabilities &= ~(Capabilities.Deletable | Capabilities.Copiable);
            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Snappable;

            RefreshExpandedState();
            RefreshPorts();
        }

        // Suppress the stock node menu; AnimatorGraphView builds the full context menu itself.
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt) { }
    }
}
