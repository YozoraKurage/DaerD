using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>Visual node representing a BlendTree (the root of the view, or a nested tree).</summary>
    class BlendTreeRootNode : GraphNodeBase
    {
        public BlendTree Tree { get; }
        public override object Model => Tree;

        static readonly Color HeaderColor = new Color(0.32f, 0.42f, 0.62f);
        static readonly Color NestedHeaderColor = new Color(0.38f, 0.32f, 0.55f);

        public BlendTreeRootNode(BlendTree tree, bool isFocusedRoot, System.Action onClick, System.Action onDoubleClick)
        {
            Tree = tree;
            AddToClassList("blendtree-root-node");
            AddInputPort();
            AddOutputPort();
            // Ports are non-interactive: blend tree edges aren't user-editable.
            Input.pickingMode = PickingMode.Ignore;
            Output.pickingMode = PickingMode.Ignore;
            // The focused root has no incoming edge, so its left-side port would dangle.
            // Hiding it keeps the silhouette of the focal node clean.
            if (isFocusedRoot)
                inputContainer.style.display = DisplayStyle.None;

            // The tree is laid out automatically, so dragging individual nodes would just
            // desync the view. Keep them selectable for keyboard focus and ping, nothing else.
            capabilities = Capabilities.Selectable;

            title = tree != null ? tree.name : "Blend Tree";
            titleContainer.style.backgroundColor = isFocusedRoot ? HeaderColor : NestedHeaderColor;

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 4;
            body.style.paddingBottom = 4;

            var typeLabel = new Label("Type: " + (tree != null ? tree.blendType.ToString() : "?"));
            typeLabel.AddToClassList("blendtree-node__meta");
            body.Add(typeLabel);

            if (tree != null && tree.blendType != BlendTreeType.Direct)
            {
                body.Add(new Label("Param: " + (string.IsNullOrEmpty(tree.blendParameter) ? "(none)" : tree.blendParameter))
                {
                    pickingMode = PickingMode.Ignore,
                });
                if (Is2D(tree.blendType))
                {
                    body.Add(new Label("Param Y: " + (string.IsNullOrEmpty(tree.blendParameterY) ? "(none)" : tree.blendParameterY))
                    {
                        pickingMode = PickingMode.Ignore,
                    });
                }
            }

            extensionContainer.Add(body);
            RefreshExpandedState();
            RefreshPorts();

            tooltip = "Click to ping in Project · Double-click to focus on this tree";
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0) return;
                if (evt.clickCount == 2)
                {
                    onDoubleClick?.Invoke();
                    evt.StopPropagation();
                }
                else if (evt.clickCount == 1)
                {
                    onClick?.Invoke();
                }
            });
        }

        static bool Is2D(BlendTreeType type) =>
            type == BlendTreeType.SimpleDirectional2D ||
            type == BlendTreeType.FreeformDirectional2D ||
            type == BlendTreeType.FreeformCartesian2D;
    }
}
