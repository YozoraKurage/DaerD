using UnityEditor.Experimental.GraphView;
using UnityEngine;

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

            switch (kind)
            {
                case SpecialNodeKind.Entry:
                    title = "Entry";
                    AddOutputPort();
                    titleContainer.style.backgroundColor = new Color(0.27f, 0.43f, 0.27f);
                    break;
                case SpecialNodeKind.Exit:
                    title = "Exit";
                    AddInputPort();
                    titleContainer.style.backgroundColor = new Color(0.46f, 0.27f, 0.27f);
                    break;
                case SpecialNodeKind.AnyState:
                    title = "Any State";
                    AddOutputPort();
                    titleContainer.style.backgroundColor = new Color(0.30f, 0.40f, 0.46f);
                    break;
            }

            capabilities &= ~(Capabilities.Deletable | Capabilities.Copiable);
            capabilities |= Capabilities.Movable | Capabilities.Selectable | Capabilities.Snappable;

            RefreshExpandedState();
            RefreshPorts();
        }
    }
}
