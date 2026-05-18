using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>IMGUI drawer for blend tree editing, shown in the inspector side panel.</summary>
    static class BlendTreePanel
    {
        public static void Draw(BlendTree tree, AnimatorController controller)
        {
            if (tree == null) return;

            var floatParams = FloatParameterNames(controller);

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.DelayedTextField("Name", tree.name);
            var blendType = (BlendTreeType)EditorGUILayout.EnumPopup("Blend Type", tree.blendType);

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
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Motions", EditorStyles.boldLabel);

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
            if (GUILayout.Button("+ Add Motion"))
            {
                list.Add(new ChildMotion { timeScale = 1f, position = Vector2.zero });
                changed = true;
            }
            if (GUILayout.Button("+ Add Nested Blend Tree"))
            {
                var nested = new BlendTree { name = "Nested Blend Tree", hideFlags = HideFlags.HideInHierarchy };
                var path = AssetDatabase.GetAssetPath(controller);
                if (!string.IsNullOrEmpty(path))
                    AssetDatabase.AddObjectToAsset(nested, controller);
                list.Add(new ChildMotion { motion = nested, timeScale = 1f });
                changed = true;
            }

            if (changed)
            {
                Undo.RegisterCompleteObjectUndo(tree, "Edit Blend Tree Motions");
                tree.children = list.ToArray();
                EditorUtility.SetDirty(tree);
            }
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
