using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A compact "Hierarchy"-style tree of the BlendTree currently open in the graph view.
    /// Lets the user jump to any motion in the tree without having to navigate the graph
    /// itself — clicking a row selects + frames the corresponding node, double-click on a
    /// nested BlendTree drills the graph view into it, and clicking the disclosure arrow
    /// expands or collapses that subtree like Unity's scene Hierarchy.
    /// </summary>
    class BlendTreeHierarchyPanel : PanelBase
    {
        // Tracks the last single-click row + time so the panel can recognise its own
        // double-click without help from IMGUI's event system (which doesn't expose one).
        object _lastClickedRow;
        double _lastClickTime;
        const double DoubleClickInterval = 0.4;

        // Subtrees the user has expanded. Default behavior matches Unity's Hierarchy:
        // everything is collapsed on first display, and selecting a descendant auto-opens
        // the path to it.
        readonly HashSet<BlendTree> _expanded = new HashSet<BlendTree>();
        BlendTree _trackedRoot;

        public BlendTreeHierarchyPanel(DaerDContext context) : base(context, "Blend Tree Hierarchy")
        {
            context.BlendTreePathChanged += OnPathChanged;
            context.BlendTreeChanged += Refresh;
            context.SelectionChanged += OnSelectionChanged;
            context.ControllerChanged += OnPathChanged;
            context.GraphRebuilt += Refresh;
        }

        void OnPathChanged()
        {
            // Reset expansion state whenever the focused root tree changes — old expansion
            // entries reference BlendTrees that may no longer be reachable from here.
            var root = Context.CurrentBlendTree;
            if (root != _trackedRoot)
            {
                _expanded.Clear();
                _trackedRoot = root;
            }
            Refresh();
        }

        void OnSelectionChanged()
        {
            ExpandToSelection();
            Refresh();
        }

        protected override void DrawContent()
        {
            // The panel is reparented out of the layout while not in blend tree mode (see
            // DaerDWindow.RefreshRightColumnLayout), but DrawContent can still run during
            // the transition frame, so guard defensively.
            if (!Context.IsViewingBlendTree) return;
            DrawTree(Context.CurrentBlendTree, 0, new HashSet<BlendTree>());
        }

        void DrawTree(BlendTree tree, int depth, HashSet<BlendTree> path)
        {
            if (tree == null) return;

            // A tree that is its own ancestor is drawn once and never expanded, so a
            // (defensively handled) cyclic reference can't recurse forever.
            bool cyclic = path.Contains(tree);
            DrawRow(tree, depth, label: tree.name, suffix: cyclic ? "  [recursive]" : "  [" + tree.blendType + "]",
                isTree: true, hasChildren: !cyclic && HasAnyChildren(tree));

            if (cyclic || !_expanded.Contains(tree)) return;
            if (tree.children == null) return;
            path.Add(tree);
            foreach (var child in tree.children)
            {
                var motion = child.motion;
                if (motion is BlendTree nested)
                {
                    DrawTree(nested, depth + 1, path);
                }
                else
                {
                    string label = motion != null ? motion.name : "(empty)";
                    DrawRow(motion, depth + 1,
                        label: label,
                        suffix: SlotSuffix(tree, child),
                        isTree: false,
                        hasChildren: false);
                }
            }
            path.Remove(tree);
        }

        void DrawRow(Object target, int depth, string label, string suffix, bool isTree, bool hasChildren)
        {
            const float indentPerLevel = 14f;
            const float arrowWidth = 14f;
            // Rect-based row instead of EditorGUILayout so the whole horizontal strip — not
            // just the label — is clickable and can paint a selection highlight.
            var rect = GUILayoutUtility.GetRect(0, 18f, GUILayout.ExpandWidth(true));
            bool isSelected = ReferenceEquals(Context.Selection, target) && target != null;
            bool isFocused = isTree && ReferenceEquals(target, Context.CurrentBlendTree);

            if (isSelected)
                EditorGUI.DrawRect(rect, DaerDColors.SelectedRowFill);
            else if (isFocused)
                EditorGUI.DrawRect(rect, DaerDColors.FocusedRowFill);

            float leftPad = rect.x + 4 + depth * indentPerLevel;
            var arrowRect = new Rect(leftPad, rect.y + 2, arrowWidth, rect.height - 2);
            var iconRect = new Rect(arrowRect.xMax + 1, rect.y + 1, 16, 16);
            var labelRect = new Rect(iconRect.xMax + 4, rect.y, rect.width - iconRect.xMax - 4, rect.height);

            if (isTree && hasChildren && target is BlendTree tree)
            {
                // Match Unity's foldout chevron: ▶ when collapsed, ▼ when expanded. Drawn
                // ourselves rather than using EditorGUI.Foldout so the arrow is a tight hit
                // target separate from the row-click that selects.
                bool expanded = _expanded.Contains(tree);
                GUI.Label(arrowRect, expanded ? "▼" : "▶", EditorStyles.miniLabel);
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && arrowRect.Contains(Event.current.mousePosition))
                {
                    ToggleExpanded(tree);
                    Event.current.Use();
                    return;
                }
            }

            var icon = ResolveIcon(target, isTree);
            if (icon != null) GUI.Label(iconRect, icon);

            var style = isFocused ? EditorStyles.boldLabel : EditorStyles.label;
            GUI.Label(labelRect, label + suffix, style);

            HandleRowClick(rect, target, isTree);
        }

        void HandleRowClick(Rect rect, Object target, bool isTree)
        {
            var evt = Event.current;
            if (evt.type != EventType.MouseDown || evt.button != 0 || !rect.Contains(evt.mousePosition))
                return;

            // Manual double-click detection: IMGUI doesn't differentiate between single and
            // double clicks via clickCount in MouseDown the way UIToolkit does, so we
            // compare the previous click target + timestamp.
            bool isDoubleClick = ReferenceEquals(_lastClickedRow, target)
                && (EditorApplication.timeSinceStartup - _lastClickTime) < DoubleClickInterval;
            _lastClickedRow = target;
            _lastClickTime = EditorApplication.timeSinceStartup;

            if (target != null)
            {
                EditorGUIUtility.PingObject(target);
                Context.Select(target);
                Context.RequestFrameOn(target);
            }

            if (isDoubleClick && isTree && target is BlendTree nested && nested != Context.CurrentBlendTree)
                Context.EnterNestedBlendTree(nested);

            evt.Use();
        }

        void ToggleExpanded(BlendTree tree)
        {
            if (_expanded.Contains(tree)) _expanded.Remove(tree);
            else _expanded.Add(tree);
            Refresh();
        }

        /// <summary>Expands every ancestor of the current selection so the highlighted row is visible.</summary>
        void ExpandToSelection()
        {
            if (!Context.IsViewingBlendTree) return;
            var selection = Context.Selection;
            if (selection == null) return;

            var ancestors = new List<BlendTree>();
            if (FindAncestors(Context.CurrentBlendTree, selection, ancestors, new HashSet<BlendTree>()))
                foreach (var tree in ancestors)
                    _expanded.Add(tree);
        }

        /// <summary>
        /// Depth-first search for <paramref name="target"/> under <paramref name="current"/>.
        /// When found, fills <paramref name="ancestors"/> with the chain of BlendTrees from
        /// the focused root down to (but not including) the target.
        /// </summary>
        static bool FindAncestors(BlendTree current, object target, List<BlendTree> ancestors,
            HashSet<BlendTree> visited)
        {
            if (current == null || !visited.Add(current)) return false;
            // The focused root itself is the selection — no ancestors to expand.
            if (ReferenceEquals(current, target)) return true;
            if (current.children == null) return false;

            foreach (var child in current.children)
            {
                if (ReferenceEquals(child.motion, target))
                {
                    ancestors.Add(current);
                    return true;
                }
                if (child.motion is BlendTree nested && FindAncestors(nested, target, ancestors, visited))
                {
                    ancestors.Add(current);
                    return true;
                }
            }
            return false;
        }

        static bool HasAnyChildren(BlendTree tree) =>
            tree != null && tree.children != null && tree.children.Length > 0;

        static GUIContent ResolveIcon(Object target, bool isTree)
        {
            if (isTree) return EditorGUIUtility.IconContent("BlendTree Icon");
            if (target is AnimationClip) return EditorGUIUtility.IconContent("AnimationClip Icon");
            return EditorGUIUtility.IconContent("Animation Icon");
        }

        static string SlotSuffix(BlendTree parent, ChildMotion child)
        {
            switch (parent.blendType)
            {
                case BlendTreeType.Simple1D:
                    return "   threshold " + child.threshold.ToString("0.###");
                case BlendTreeType.Direct:
                    return "   param " + (string.IsNullOrEmpty(child.directBlendParameter) ? "(none)" : child.directBlendParameter);
                default:
                    return "   pos (" + child.position.x.ToString("0.##") + ", " + child.position.y.ToString("0.##") + ")";
            }
        }
    }
}
