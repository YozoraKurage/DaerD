using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A reusable blend tree subtree (deep copy, clips shared by reference) plus the Float
    /// parameters it references, stored as one .asset. "." in the template name nests it into
    /// submenus of the blend tree graph's Import Template menu.
    /// </summary>
    class DaerDBlendTreeTemplate : ScriptableObject
    {
        public BlendTree tree;
        public List<LayerClipboard.ParameterSnapshot> parameters =
            new List<LayerClipboard.ParameterSnapshot>();

        public static DaerDBlendTreeTemplate Save(AnimatorController controller, BlendTree source,
            string assetPath)
        {
            if (controller == null || source == null || string.IsNullOrEmpty(assetPath))
                return null;
            var template = CreateInstance<DaerDBlendTreeTemplate>();
            AssetDatabase.CreateAsset(template, assetPath);
            template.tree = (BlendTree)LayerClipboard.DeepCopyMotion(template, source);

            var referenced = LayerClipboard.CollectBlendTreeParameterNames(source);
            foreach (var parameter in controller.parameters)
                if (referenced.Contains(parameter.name))
                    template.parameters.Add(new LayerClipboard.ParameterSnapshot
                    {
                        name = parameter.name,
                        type = parameter.type,
                        defaultFloat = parameter.defaultFloat,
                        defaultInt = parameter.defaultInt,
                        defaultBool = parameter.defaultBool,
                    });

            EditorUtility.SetDirty(template);
            AssetDatabase.SaveAssets();
            return template;
        }

        public static List<DaerDBlendTreeTemplate> All()
        {
            var templates = new List<DaerDBlendTreeTemplate>();
            foreach (var guid in AssetDatabase.FindAssets("t:DaerDBlendTreeTemplate"))
            {
                var template = AssetDatabase.LoadAssetAtPath<DaerDBlendTreeTemplate>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (template != null) templates.Add(template);
            }
            templates.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return templates;
        }

        /// <summary>
        /// Imports the template as a new child of <paramref name="parent"/>: deep-copies the
        /// subtree into the controller, remaps its parameters per the map and creates the
        /// missing ones. Returns the imported subtree's root.
        /// </summary>
        public BlendTree Import(AnimatorController controller, BlendTree parent,
            IReadOnlyDictionary<string, string> parameterMap)
        {
            if (controller == null || parent == null || tree == null) return null;
            using (new UndoScope("Import Blend Tree Template"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Import Blend Tree Template");
                var copy = (BlendTree)LayerClipboard.DeepCopyMotion(controller, tree);

                foreach (var parameter in parameters)
                {
                    string mapped = parameterMap != null
                        && parameterMap.TryGetValue(parameter.name, out var chosen)
                        ? chosen : parameter.name;
                    if (string.IsNullOrEmpty(mapped)) mapped = parameter.name;
                    if (DbtBuilder.FindParameter(controller, mapped) == null)
                        controller.AddParameter(new AnimatorControllerParameter
                        {
                            name = mapped,
                            type = parameter.type,
                            defaultFloat = parameter.defaultFloat,
                            defaultInt = parameter.defaultInt,
                            defaultBool = parameter.defaultBool,
                        });
                }

                if (parameterMap != null && parameterMap.Count > 0)
                {
                    var effective = new Dictionary<string, string>();
                    foreach (var pair in parameterMap)
                        if (!string.IsNullOrEmpty(pair.Value) && pair.Key != pair.Value)
                            effective[pair.Key] = pair.Value;
                    LayerParameterRemapper.RemapTree(copy, effective);
                }

                Undo.RegisterCompleteObjectUndo(parent, "Import Blend Tree Template");
                parent.AddChild(copy);
                EditorUtility.SetDirty(parent);
                EditorUtility.SetDirty(controller);
                return copy;
            }
        }
    }
}
