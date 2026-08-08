using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Shared low-level builders for the Direct-blend-tree (AAP) gadgets: parameter clips,
    /// 1D / Direct trees, the constant-One weight parameter and the Write-Defaults-ON DBT
    /// layer. Everything created is registered for undo and stored as a sub-asset of the
    /// controller (in-memory controllers just keep the loose references).
    /// </summary>
    static class DbtBuilder
    {
        /// <summary>DBT-layer candidates for the UI: empty layers and Direct-tree-only layers.</summary>
        public static bool CanHostGadget(AnimatorControllerLayer layer) =>
            layer != null && layer.syncedLayerIndex < 0
            && (IsLayerEmpty(layer) || ControllerAnalyzer.IsDirectBlendTreeOnlyLayer(layer));

        public static bool IsLayerEmpty(AnimatorControllerLayer layer)
        {
            if (layer.stateMachine == null) return false;
            foreach (var sm in layer.stateMachine.SelfAndDescendants())
                if (sm.states.Length > 0) return false;
            return true;
        }

        /// <summary>
        /// Returns the root Direct blend tree of the target layer, creating the layer and/or
        /// its single Write-Defaults-ON state as needed. The state sits directly at Entry
        /// (it is the layer's only, and therefore default, state).
        /// </summary>
        public static BlendTree EnsureDirectBlendTreeLayer(AnimatorController controller, int layerIndex, string newLayerName)
        {
            if (layerIndex < 0)
            {
                controller.AddLayer(UniqueLayerName(controller, newLayerName));
                var layers = controller.layers;
                layerIndex = layers.Length - 1;
                layers[layerIndex].defaultWeight = 1f;
                controller.layers = layers;
            }

            var stateMachine = controller.layers[layerIndex].stateMachine;
            foreach (var child in stateMachine.states)
                if (child.state != null && child.state.motion is BlendTree existing
                    && existing.blendType == BlendTreeType.Direct)
                    return existing;

            var state = stateMachine.AddState("DBT (WD On)", new Vector3(300f, 120f, 0f));
            state.writeDefaultValues = true;
            var root = new BlendTree
            {
                name = "DBT Root",
                blendType = BlendTreeType.Direct,
                hideFlags = HideFlags.HideInHierarchy,
            };
            Attach(controller, root);
            state.motion = root;
            EditorUtility.SetDirty(state);
            return root;
        }

        /// <summary>
        /// Direct blend tree weights need a float parameter that stays 1. Reuses a suitable
        /// existing "One" (Float with default 1); otherwise creates the first free candidate.
        /// </summary>
        public static string EnsureConstantOneParameter(AnimatorController controller)
        {
            for (int i = 0; ; i++)
            {
                string name = i == 0 ? "One" : i == 1 ? "DBT/One" : "DBT/One " + i;
                var existing = FindParameter(controller, name);
                if (existing == null)
                {
                    AddFloatParameter(controller, name, 1f);
                    return name;
                }
                if (existing.type == AnimatorControllerParameterType.Float && existing.defaultFloat == 1f)
                    return name;
            }
        }

        public static void EnsureFloatParameter(AnimatorController controller, string name, float defaultValue)
        {
            if (FindParameter(controller, name) == null)
                AddFloatParameter(controller, name, defaultValue);
        }

        public static void AddFloatParameter(AnimatorController controller, string name, float defaultValue)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = name,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = defaultValue,
            });
        }

        public static AnimatorControllerParameter FindParameter(AnimatorController controller, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in controller.parameters)
                if (p.name == name) return p;
            return null;
        }

        /// <summary>One-key clip animating the parameter itself on the Animator — the AAP.
        /// Left visible in the Project view so the generated pieces are discoverable.</summary>
        public static AnimationClip ParameterClip(AnimatorController controller, string parameter, float value)
        {
            var clip = new AnimationClip { name = Sanitize(parameter) + " = " + value.ToString("0.###") };
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), parameter);
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(new Keyframe(0f, value)));
            Attach(controller, clip);
            return clip;
        }

        /// <summary>Multi-key AAP clip: same binding as <see cref="ParameterClip"/>, but the
        /// parameter follows the curve over the clip's length — a state playing it by motion
        /// time turns the curve into a lookup table indexed by another parameter.</summary>
        public static AnimationClip CurveClip(AnimatorController controller, string name,
            string parameter, AnimationCurve curve, float frameRate)
        {
            var clip = new AnimationClip { name = name };
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), parameter);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            // Only the authoring grid the curve is drawn against; the keys keep their exact
            // times, and the clip's length stays the last key's time.
            clip.frameRate = frameRate;
            Attach(controller, clip);
            return clip;
        }

        /// <summary>A clip that animates nothing. Blend tree children and states both need a
        /// motion to exist, and some of them are there to carry a weight or to hold a layer
        /// still rather than to write anything.</summary>
        public static AnimationClip EmptyClip(AnimatorController controller, string name)
        {
            var clip = new AnimationClip { name = name };
            Attach(controller, clip);
            return clip;
        }

        public static BlendTree Tree1D(AnimatorController controller, string name, string blendParameter)
        {
            var tree = new BlendTree
            {
                name = name,
                blendType = BlendTreeType.Simple1D,
                blendParameter = blendParameter,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            Attach(controller, tree);
            return tree;
        }

        public static BlendTree DirectTree(AnimatorController controller, string name)
        {
            var tree = new BlendTree
            {
                name = name,
                blendType = BlendTreeType.Direct,
                hideFlags = HideFlags.HideInHierarchy,
            };
            Attach(controller, tree);
            return tree;
        }

        /// <summary>Appends a child to a Direct tree with the given weight parameter.</summary>
        public static void AddDirectChild(BlendTree direct, Motion motion, string weightParameter)
        {
            Undo.RegisterCompleteObjectUndo(direct, "DBT Gadget");
            direct.AddChild(motion);
            var children = direct.children;
            children[children.Length - 1].directBlendParameter = weightParameter;
            direct.children = children;
            EditorUtility.SetDirty(direct);
        }

        /// <summary>
        /// Flips the Direct tree's hidden "Normalized Blend Values" flag — Unity exposes it
        /// only in the inspector, not in the API. Normalized mode divides every weight by
        /// the weight sum, which is what the division-style gadgets exploit.
        /// </summary>
        public static void SetNormalizedBlendValues(BlendTree direct, bool normalized)
        {
            if (direct == null) return;
            using (var so = new SerializedObject(direct))
            {
                var prop = so.FindProperty("m_NormalizedBlendValues");
                if (prop == null) return;
                prop.boolValue = normalized;
                so.ApplyModifiedProperties();
            }
        }

        /// <summary>Registers undo for the new object and stores it inside the .controller
        /// file (in-memory controllers just keep the loose reference).</summary>
        public static void Attach(AnimatorController controller, Object obj)
        {
            Undo.RegisterCreatedObjectUndo(obj, "DBT Gadget");
            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(controller)))
                AssetDatabase.AddObjectToAsset(obj, controller);
        }

        /// <summary>Sub-asset names keep out of Unity's menu-splitting on '/'.</summary>
        public static string Sanitize(string parameterName) => parameterName.Replace('/', '_');

        public static string UniqueLayerName(AnimatorController controller, string baseName)
        {
            bool Taken(string n)
            {
                foreach (var l in controller.layers)
                    if (l.name == n) return true;
                return false;
            }
            if (!Taken(baseName)) return baseName;
            int i = 1;
            while (Taken(baseName + " " + i)) i++;
            return baseName + " " + i;
        }
    }
}
