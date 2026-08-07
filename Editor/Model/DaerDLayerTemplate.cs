using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// A reusable layer: the deep-cloned state machine (with behaviours and blend trees) plus
    /// the parameters it references, stored as one .asset. "/" in the template name nests it
    /// into submenus of the Add Layer dropdown. Clips are referenced, not copied — templates
    /// built from clips embedded in a .controller keep pointing there.
    /// </summary>
    class DaerDLayerTemplate : ScriptableObject
    {
        public string layerName;
        public float defaultWeight = 1f;
        public AnimatorLayerBlendingMode blendingMode;
        public AvatarMask avatarMask;
        public bool ikPass;
        public AnimatorStateMachine stateMachine;
        public List<LayerClipboard.ParameterSnapshot> parameters =
            new List<LayerClipboard.ParameterSnapshot>();

        /// <summary>Saves the layer as a template asset at <paramref name="assetPath"/>.</summary>
        public static DaerDLayerTemplate Save(AnimatorController controller, int layerIndex,
            string assetPath)
        {
            if (controller == null || layerIndex < 0 || layerIndex >= controller.layers.Length
                || string.IsNullOrEmpty(assetPath))
                return null;
            var layer = controller.layers[layerIndex];
            if (layer.stateMachine == null) return null;

            var template = CreateInstance<DaerDLayerTemplate>();
            template.layerName = layer.name;
            template.defaultWeight = layerIndex == 0 ? 1f : layer.defaultWeight;
            template.blendingMode = layer.blendingMode;
            template.avatarMask = layer.avatarMask;
            template.ikPass = layer.iKPass;
            AssetDatabase.CreateAsset(template, assetPath);

            // The state machine lives inside the template asset; cloning into a persistent
            // root auto-attaches states, nested machines and transitions.
            var root = new AnimatorStateMachine { name = layer.name };
            AssetDatabase.AddObjectToAsset(root, template);
            template.stateMachine = root;
            StateMachineCloner.Clone(layer.stateMachine, root, out var stateMap, out _);
            LayerClipboard.CopyBehaviours(stateMap);
            LayerClipboard.DeepCopyBlendTrees(template, stateMap.Values);

            var referenced = LayerClipboard.CollectParameterNames(layer.stateMachine);
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

        public static List<DaerDLayerTemplate> All()
        {
            var templates = new List<DaerDLayerTemplate>();
            foreach (var guid in AssetDatabase.FindAssets("t:DaerDLayerTemplate"))
            {
                var template = AssetDatabase.LoadAssetAtPath<DaerDLayerTemplate>(
                    AssetDatabase.GUIDToAssetPath(guid));
                if (template != null) templates.Add(template);
            }
            templates.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return templates;
        }

        /// <summary>
        /// Imports the template as a new layer. <paramref name="parameterMap"/> maps each
        /// template parameter name to the (possibly new) name it should use; missing
        /// parameters are created with the template's defaults. Returns the layer index.
        /// </summary>
        public int Import(AnimatorController controller,
            IReadOnlyDictionary<string, string> parameterMap)
        {
            if (controller == null || stateMachine == null) return -1;
            using (new UndoScope("Import Layer Template"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Import Layer Template");
                controller.AddLayer(DbtBuilder.UniqueLayerName(controller,
                    string.IsNullOrEmpty(layerName) ? name : layerName));
                var layers = controller.layers;
                int index = layers.Length - 1;
                layers[index].defaultWeight = defaultWeight;
                layers[index].blendingMode = blendingMode;
                layers[index].avatarMask = avatarMask;
                layers[index].iKPass = ikPass;
                controller.layers = layers;

                var target = layers[index].stateMachine;
                StateMachineCloner.Clone(stateMachine, target, out var stateMap, out _);
                LayerClipboard.CopyBehaviours(stateMap);
                // Deep copies belong to the destination controller, which also makes the
                // remap below safe (it never touches the template's own trees).
                LayerClipboard.DeepCopyBlendTrees(controller, stateMap.Values);

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
                    LayerParameterRemapper.Remap(target, effective);
                }

                EditorUtility.SetDirty(controller);
                return index;
            }
        }
    }
}
