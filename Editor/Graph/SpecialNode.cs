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
                    color = new Color(0.27f, 0.43f, 0.27f);
                    AddOutputPort();
                    break;
                case SpecialNodeKind.Exit:
                    label = "Exit";
                    color = new Color(0.46f, 0.27f, 0.27f);
                    AddInputPort();
                    break;
                default: // AnyState
                    label = "Any State";
                    color = new Color(0.30f, 0.40f, 0.46f);
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
    }
}
