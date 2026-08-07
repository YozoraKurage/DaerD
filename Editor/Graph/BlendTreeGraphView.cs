using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The GraphView surface that visualises one BlendTree as a left-to-right tree.
    /// Standard Unity shows blend tree children in a flat list per nesting level; here we
    /// render the whole nesting hierarchy at once so the structure is glanceable, with
    /// single-click pinging the underlying asset and double-click drilling into a nested
    /// blend tree.
    /// </summary>
    class BlendTreeGraphView : GraphView
    {
        readonly DaerDContext _context;
        readonly Dictionary<BlendTree, BlendTreeRootNode> _treeNodes = new Dictionary<BlendTree, BlendTreeRootNode>();
        bool _rebuildScheduled;
        bool _framedOnce;

        // Layout constants. The tree grows left-to-right; each generation gets a fixed
        // horizontal slice, siblings stack with vertical padding, and every subtree
        // claims as much vertical room as its descendants need.
        const float ColumnWidth = 260f;
        const float NodeHeight = 70f;
        const float SiblingPadding = 16f;

        public BlendTreeGraphView(DaerDContext context)
        {
            _context = context;

            style.flexGrow = 1;
            focusable = true;

            // Wider than the stock 0.25–1.0 range so large blend-tree graphs can be zoomed out / in.
            SetupZoom(0.05f, 3.0f);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());

            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();

            context.BlendTreePathChanged += () => { _framedOnce = false; RequestRebuild(); };
            context.ControllerChanged += () => { _framedOnce = false; RequestRebuild(); };
            context.GraphStructureChanged += RequestRebuild;
            context.BlendTreeChanged += RequestRebuild;
            context.FrameRequested += FrameOn;
        }

        public void RequestRebuild()
        {
            if (_rebuildScheduled) return;
            _rebuildScheduled = true;
            schedule.Execute(() =>
            {
                _rebuildScheduled = false;
                Rebuild();
            });
        }

        /// <summary>Template save/import and parameter remap for the clicked (or focused) tree.</summary>
        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            var tree = SelectedTree() ?? _context.CurrentBlendTree;
            var controller = _context.Controller;
            if (tree == null || controller == null) return;

            evt.menu.AppendAction("Save as Template", _ => SaveTemplate(controller, tree));
            var templates = DaerDBlendTreeTemplate.All();
            if (templates.Count == 0)
                evt.menu.AppendAction("Import Template", null, DropdownMenuAction.Status.Disabled);
            else
                foreach (var template in templates)
                {
                    var captured = template;
                    evt.menu.AppendAction("Import Template/" + captured.name.Replace('.', '/'),
                        _ => ImportTemplate(controller, captured, tree));
                }
            foreach (var template in templates)
            {
                var captured = template;
                evt.menu.AppendAction("Delete Template/" + captured.name.Replace('.', '/'),
                    _ => DeleteTemplate(captured));
            }
            evt.menu.AppendAction("Remap Parameters",
                _ => BlendTreeRemapWindow.Open(controller, tree, () =>
                {
                    _context.NotifyBlendTreeChanged();
                    _context.NotifyGraphStructureChanged();
                }));
        }

        BlendTree SelectedTree()
        {
            foreach (var selected in selection)
                if (selected is BlendTreeRootNode node)
                    return node.Tree;
            return null;
        }

        void SaveTemplate(AnimatorController controller, BlendTree tree)
        {
            string path = EditorUtility.SaveFilePanelInProject(L.Tr("Save Blend Tree Template"),
                tree.name, "asset",
                L.Tr("Use '.' in the file name to nest the template into submenus."));
            if (string.IsNullOrEmpty(path)) return;
            DaerDBlendTreeTemplate.Save(controller, tree, path);
        }

        void ImportTemplate(AnimatorController controller, DaerDBlendTreeTemplate template, BlendTree parent)
        {
            LayerTemplateImportWindow.Open(controller, template.name, template.parameters, map =>
            {
                template.Import(controller, parent, map);
                _context.NotifyParametersChanged();
                _context.NotifyBlendTreeChanged();
                _context.NotifyGraphStructureChanged();
            });
        }

        static void DeleteTemplate(DaerDBlendTreeTemplate template)
        {
            string path = AssetDatabase.GetAssetPath(template);
            if (!EditorUtility.DisplayDialog(L.Tr("Delete Template"),
                    L.Tr("Delete blend tree template '{0}'?\n\n{1}", template.name, path),
                    L.Tr("Delete"), L.Tr("Cancel")))
                return;
            AssetDatabase.DeleteAsset(path);
        }

        public void Rebuild()
        {
            _treeNodes.Clear();
            foreach (var element in graphElements.ToList())
                RemoveElement(element);

            var root = _context.CurrentBlendTree;
            if (root == null) return;

            // Two-pass: measure subtree heights, then place nodes column-by-column. Both passes
            // share path-based recursion guards so a (defensively handled) self-nested tree is
            // rendered once as a leaf instead of recursing forever.
            float totalHeight = MeasureSubtree(root, new HashSet<BlendTree>());
            BuildSubtree(root, 0, 0f, totalHeight, isFocusedRoot: true, new HashSet<BlendTree>());

            // Re-apply the current DaerD selection so the visible blue outline survives
            // edit-driven rebuilds. Without this, every BlendTreePanel edit would silently
            // drop the selected-node highlight even though Context.Selection didn't change.
            var node = FindNode(_context.Selection);
            if (node != null) AddToSelection(node);

            // Frame on first build only — once the user has panned/zoomed, don't yank the
            // viewport away on every "edit a threshold" rebuild.
            if (_framedOnce) return;
            _framedOnce = true;
            schedule.Execute(() => FrameAll()).ExecuteLater(50);
        }

        /// <summary>Returns the vertical space needed to lay out <paramref name="tree"/> and its descendants.</summary>
        float MeasureSubtree(BlendTree tree, HashSet<BlendTree> path)
        {
            if (tree == null || tree.children == null || tree.children.Length == 0 || !path.Add(tree))
                return NodeHeight + SiblingPadding;
            float total = 0f;
            foreach (var child in tree.children)
            {
                total += child.motion is BlendTree nested
                    ? MeasureSubtree(nested, path)
                    : NodeHeight + SiblingPadding;
            }
            path.Remove(tree);
            // The parent slot itself must be at least one row tall so a node with a single
            // child still claims room for its own visual.
            return Mathf.Max(total, NodeHeight + SiblingPadding);
        }

        void BuildSubtree(BlendTree tree, int column, float top, float allocated, bool isFocusedRoot,
            HashSet<BlendTree> path)
        {
            float centerY = top + allocated * 0.5f;
            var rootNode = BuildRootNode(tree, isFocusedRoot);
            rootNode.SetPosition(new Rect(column * ColumnWidth, centerY - NodeHeight * 0.5f, 0f, 0f));

            if (tree.children == null || tree.children.Length == 0 || !path.Add(tree)) return;

            float cursor = top;
            for (int i = 0; i < tree.children.Length; i++)
            {
                var child = tree.children[i];
                float childSlot = child.motion is BlendTree nested
                    ? MeasureSubtree(nested, path)
                    : NodeHeight + SiblingPadding;

                if (child.motion is BlendTree nestedTree)
                {
                    BuildSubtree(nestedTree, column + 1, cursor, childSlot, isFocusedRoot: false, path);
                    LinkParentToChild(rootNode, _treeNodes[nestedTree]);
                }
                else
                {
                    var leaf = BuildChildNode(tree, child);
                    float childCenterY = cursor + childSlot * 0.5f;
                    leaf.SetPosition(new Rect((column + 1) * ColumnWidth, childCenterY - NodeHeight * 0.5f, 0f, 0f));
                    LinkParentToChild(rootNode, leaf);
                }
                cursor += childSlot;
            }
            path.Remove(tree);
        }

        BlendTreeRootNode BuildRootNode(BlendTree tree, bool isFocusedRoot)
        {
            if (_treeNodes.TryGetValue(tree, out var existing)) return existing;
            var node = new BlendTreeRootNode(tree, isFocusedRoot,
                onClick: () => SelectAndPing(tree),
                onDoubleClick: () =>
                {
                    if (tree != _context.CurrentBlendTree)
                        _context.EnterNestedBlendTree(tree);
                });
            AddElement(node);
            _treeNodes[tree] = node;
            return node;
        }

        BlendTreeChildNode BuildChildNode(BlendTree parent, ChildMotion child)
        {
            var motion = child.motion;
            var node = new BlendTreeChildNode(parent, child,
                onClick: () => SelectAndPing(motion),
                onDoubleClick: () =>
                {
                    if (motion != null) AssetDatabase.OpenAsset(motion);
                });
            AddElement(node);
            return node;
        }

        void LinkParentToChild(BlendTreeRootNode parent, GraphNodeBase child)
        {
            if (parent?.Output == null || child?.Input == null) return;
            var edge = new Edge
            {
                output = parent.Output,
                input = child.Input,
            };
            // The edges are decoration only — disable interaction so the user can't drag
            // them apart and there's no need to push edits back into the asset.
            edge.capabilities &= ~(Capabilities.Deletable | Capabilities.Selectable | Capabilities.Movable);
            edge.pickingMode = PickingMode.Ignore;
            parent.Output.Connect(edge);
            child.Input.Connect(edge);
            AddElement(edge);
        }

        void SelectAndPing(UnityEngine.Object obj)
        {
            if (obj == null) return;
            // Pinging highlights the asset in the Project window even when the object is a
            // sub-asset (BlendTrees are sub-assets of the controller). Selection updates the
            // DaerD inspector so the user can edit fields without switching tools.
            EditorGUIUtility.PingObject(obj);
            _context.Select(obj);
        }

        /// <summary>Centers the view on the node representing <paramref name="model"/> and outlines it.</summary>
        public void FrameOn(object model)
        {
            if (model == null) return;
            var node = FindNode(model);
            if (node == null) return;
            // Defer one frame so a rebuild triggered in the same call has time to place the
            // node before we measure its bounds.
            schedule.Execute(() =>
            {
                ClearSelection();
                AddToSelection(node);
                FrameSelection();
            }).ExecuteLater(20);
        }

        Node FindNode(object model)
        {
            // The BlendTree case must precede the Motion case because BlendTree IS-A Motion;
            // hitting the Motion branch first would mismatch (BlendTrees are root nodes, not
            // child nodes) for nested trees.
            if (model is BlendTree tree && _treeNodes.TryGetValue(tree, out var n))
                return n;
            if (model is Motion motion)
            {
                foreach (var element in graphElements.ToList())
                    if (element is BlendTreeChildNode child && child.Child.motion == motion)
                        return child;
            }
            return null;
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter) => new List<Port>();
    }

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

    /// <summary>Visual node representing an AnimationClip (or other Motion) child of a BlendTree.</summary>
    class BlendTreeChildNode : GraphNodeBase
    {
        public ChildMotion Child { get; }
        public BlendTree Parent { get; }
        public override object Model => Child.motion;

        static readonly Color HeaderColor = new Color(0.36f, 0.50f, 0.32f);
        static readonly Color EmptyHeaderColor = new Color(0.45f, 0.32f, 0.32f);

        public BlendTreeChildNode(BlendTree parent, ChildMotion child, System.Action onClick, System.Action onDoubleClick)
        {
            Parent = parent;
            Child = child;
            AddToClassList("blendtree-child-node");
            AddInputPort();
            // No output port: leaf motions don't have further children.
            Input.pickingMode = PickingMode.Ignore;

            capabilities = Capabilities.Selectable;

            var motion = child.motion;
            title = motion != null ? motion.name : "(empty)";
            titleContainer.style.backgroundColor = motion != null ? HeaderColor : EmptyHeaderColor;

            var body = new VisualElement();
            body.style.paddingLeft = 8;
            body.style.paddingRight = 8;
            body.style.paddingTop = 4;
            body.style.paddingBottom = 4;

            body.Add(new Label("Type: " + DescribeType(motion)) { pickingMode = PickingMode.Ignore });
            body.Add(new Label("Slot: " + DescribeSlot(parent, child)) { pickingMode = PickingMode.Ignore });
            body.Add(new Label("Time Scale: " + child.timeScale.ToString("0.###")) { pickingMode = PickingMode.Ignore });

            extensionContainer.Add(body);
            RefreshExpandedState();
            RefreshPorts();

            tooltip = motion is AnimationClip
                ? "Click to ping in Project · Double-click to open the clip"
                : "Click to ping in Project";

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

        static string DescribeType(Motion motion)
        {
            if (motion == null) return "(empty)";
            if (motion is BlendTree) return "Blend Tree";
            if (motion is AnimationClip) return "Animation Clip";
            return motion.GetType().Name;
        }

        static string DescribeSlot(BlendTree parent, ChildMotion child)
        {
            if (parent == null) return string.Empty;
            switch (parent.blendType)
            {
                case BlendTreeType.Simple1D:
                    return "threshold " + child.threshold.ToString("0.###");
                case BlendTreeType.Direct:
                    return "param " + (string.IsNullOrEmpty(child.directBlendParameter) ? "(none)" : child.directBlendParameter);
                default:
                    return "pos (" + child.position.x.ToString("0.##") + ", " + child.position.y.ToString("0.##") + ")";
            }
        }
    }
}
