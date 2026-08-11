using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>IMGUI drawer for blend tree editing, shown in the inspector side panel.</summary>
    static class BlendTreePanel
    {
        public static void Draw(BlendTree tree, DaerDContext context)
        {
            if (tree == null || context == null) return;
            var controller = context.Controller;
            if (controller == null) return;

            var floatParams = FloatParameterNames(controller);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField(L.Tr("Name"), tree.name);
            var blendType = (BlendTreeType)EditorGUILayout.EnumPopup(L.Tr("Blend Type"), tree.blendType);

            string blendParam = tree.blendParameter;
            string blendParamY = tree.blendParameterY;
            bool is2D = blendType == BlendTreeType.SimpleDirectional2D
                     || blendType == BlendTreeType.FreeformDirectional2D
                     || blendType == BlendTreeType.FreeformCartesian2D;

            if (blendType != BlendTreeType.Direct)
            {
                blendParam = ParameterPopup("Parameter", blendParam, floatParams);
                if (is2D)
                    blendParamY = ParameterPopup("Parameter Y", blendParamY, floatParams);
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(tree, "Edit Blend Tree");
                if (!string.IsNullOrEmpty(name)) tree.name = name;
                tree.blendType = blendType;
                if (!string.IsNullOrEmpty(blendParam)) tree.blendParameter = blendParam;
                if (is2D && !string.IsNullOrEmpty(blendParamY)) tree.blendParameterY = blendParamY;
                EditorUtility.SetDirty(tree);
                context.NotifyBlendTreeChanged();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(L.Tr("Motions"), EditorStyles.boldLabel);
            DrawChildHeader(blendType);

            var children = tree.children;
            int removeIndex = -1;
            EditorGUI.BeginChangeCheck();

            for (int i = 0; i < children.Length; i++)
            {
                var child = children[i];
                EditorGUILayout.BeginHorizontal();
                child.motion = (Motion)EditorGUILayout.ObjectField(child.motion, typeof(Motion), false);

                switch (blendType)
                {
                    case BlendTreeType.Simple1D:
                        child.threshold = EditorGUILayout.FloatField(child.threshold, GUILayout.Width(60));
                        break;
                    case BlendTreeType.Direct:
                        child.directBlendParameter = ParameterPopupInline(child.directBlendParameter, floatParams);
                        break;
                    default:
                        child.position = EditorGUILayout.Vector2Field(GUIContent.none, child.position, GUILayout.Width(120));
                        break;
                }

                child.timeScale = EditorGUILayout.FloatField(child.timeScale, GUILayout.Width(46));
                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22)))
                    removeIndex = i;
                children[i] = child;
                EditorGUILayout.EndHorizontal();
            }

            bool changed = EditorGUI.EndChangeCheck();
            var list = new List<ChildMotion>(children);
            if (removeIndex >= 0)
            {
                list.RemoveAt(removeIndex);
                changed = true;
            }
            if (GUILayout.Button(L.Tr("+ Add Motion")))
            {
                list.Add(new ChildMotion { timeScale = 1f, position = Vector2.zero });
                changed = true;
            }
            bool attachedSubAsset = false;
            if (GUILayout.Button(L.Tr("+ Add Nested Blend Tree")))
            {
                var nested = new BlendTree { name = "Nested Blend Tree", hideFlags = HideFlags.HideInHierarchy };
                var path = AssetDatabase.GetAssetPath(controller);
                if (!string.IsNullOrEmpty(path))
                {
                    AssetDatabase.AddObjectToAsset(nested, controller);
                    attachedSubAsset = true;
                }
                list.Add(new ChildMotion { motion = nested, timeScale = 1f });
                changed = true;
            }

            if (changed)
            {
                RejectCyclicMotions(tree, children, list);
                Undo.RegisterCompleteObjectUndo(tree, "Edit Blend Tree Motions");
                tree.children = list.ToArray();
                EditorUtility.SetDirty(tree);
                context.NotifyBlendTreeChanged();
            }
            // Only on the click that added the tree — the button's event, never a repaint — so
            // the Project window lists the new sub-asset without reimporting on every frame.
            if (attachedSubAsset)
                DbtBuilder.CommitSubAssets(controller);
        }

        /// <summary>
        /// Drops any motion assignment that would make <paramref name="tree"/> contain itself —
        /// a cycle would hang every traversal (graph view, hierarchy, analyzer). The slot is
        /// reverted to its previous motion where possible, otherwise cleared.
        /// </summary>
        static void RejectCyclicMotions(BlendTree tree, ChildMotion[] previous, List<ChildMotion> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i].motion is BlendTree nested) || !nested.ContainsTree(tree)) continue;
                Debug.LogWarning("DaerD: cannot use blend tree '" + nested.name + "' as a motion of '" +
                                 tree.name + "' — the tree would contain itself.");
                // Indices only line up with the previous array when no row was added/removed
                // in the same pass; otherwise fall back to clearing the slot.
                bool canRevert = list.Count == previous.Length
                    && !(previous[i].motion is BlendTree prevTree && prevTree.ContainsTree(tree));
                var child = list[i];
                child.motion = canRevert ? previous[i].motion : null;
                list[i] = child;
            }
        }

        /// <summary>
        /// Renders a header row that labels the per-child columns. Headers track the blend
        /// type because Simple1D shows a Threshold, 2D variants show a Position, and Direct
        /// shows a per-child Parameter dropdown — the same column width logic as the rows
        /// below so the labels line up.
        /// </summary>
        static void DrawChildHeader(BlendTreeType blendType)
        {
            var headerStyle = EditorStyles.miniBoldLabel;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(L.Tr("Motion"), headerStyle);
            switch (blendType)
            {
                case BlendTreeType.Simple1D:
                    EditorGUILayout.LabelField(L.Tr("Threshold"), headerStyle, GUILayout.Width(60));
                    break;
                case BlendTreeType.Direct:
                    EditorGUILayout.LabelField(L.Tr("Parameter"), headerStyle, GUILayout.Width(90));
                    break;
                default:
                    EditorGUILayout.LabelField(L.Tr("Position (X, Y)"), headerStyle, GUILayout.Width(120));
                    break;
            }
            EditorGUILayout.LabelField(L.Tr("Speed"), headerStyle, GUILayout.Width(46));
            // Spacer matches the per-row "X" remove button so the header line never wraps.
            GUILayout.Space(22 + 4);
            EditorGUILayout.EndHorizontal();
        }

        static string[] FloatParameterNames(AnimatorController controller)
        {
            var names = new List<string>();
            foreach (var p in controller.parameters)
                if (p.type == AnimatorControllerParameterType.Float)
                    names.Add(p.name);
            if (names.Count == 0) names.Add(string.Empty);
            return names.ToArray();
        }

        static string ParameterPopup(string label, string current, string[] options)
        {
            int index = Mathf.Max(0, System.Array.IndexOf(options, current));
            index = EditorGUILayout.Popup(label, index, options);
            return options[index];
        }

        static string ParameterPopupInline(string current, string[] options)
        {
            int index = Mathf.Max(0, System.Array.IndexOf(options, current));
            index = EditorGUILayout.Popup(index, options, GUILayout.Width(90));
            return options[index];
        }
    }
}
