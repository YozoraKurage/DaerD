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
}
