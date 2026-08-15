using System.Collections.Generic;
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
        /// Human-readable reason the chosen layer can't host a gadget, or null when it can. The
        /// question every builder that adds a Direct child asks first — gadgets, toggles wired
        /// as a tree — because the answer is about the layer, not about what is being added:
        /// an index that no longer resolves, or a layer already carrying states that are not
        /// Direct trees and would be joined rather than shared.
        /// </summary>
        public static string ValidateLayerChoice(AnimatorController controller, int layerIndex,
            string newLayerName)
        {
            if (layerIndex >= 0)
            {
                if (layerIndex >= controller.layers.Length)
                    return L.Tr("The target layer no longer exists.");
                var layer = controller.layers[layerIndex];
                if (!IsLayerEmpty(layer) && !ControllerAnalyzer.IsDirectBlendTreeOnlyLayer(layer))
                    return L.Tr("The target layer must be empty or contain only Direct blend tree states.");
            }
            else if (string.IsNullOrEmpty(newLayerName))
            {
                return L.Tr("The new layer needs a name.");
            }
            return null;
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
        /// The state machine of the layer holding <paramref name="gadget"/> — either as a
        /// state's own motion (a layer's root Direct tree) or as one of such a root's direct
        /// children (one gadget inside it). <see cref="EnsureDirectBlendTreeLayer"/> hands back
        /// the root and nothing else, and a saved gadget record is keyed by the layer it landed
        /// in, so scanning back for it is the only way there. Null when no layer holds it.
        /// </summary>
        public static AnimatorStateMachine HostingMachine(AnimatorController controller, Motion gadget)
        {
            if (controller == null || gadget == null) return null;
            foreach (var layer in controller.layers)
            {
                var stateMachine = layer.stateMachine;
                if (stateMachine == null) continue;
                // A DBT layer keeps its tree on the one state at the machine's root; nothing
                // deeper can be a gadget host, so the search stops there.
                foreach (var child in stateMachine.states)
                {
                    var motion = child.state != null ? child.state.motion : null;
                    if (motion == gadget) return stateMachine;
                    if (!(motion is BlendTree root) || root.blendType != BlendTreeType.Direct) continue;
                    foreach (var entry in root.children)
                        if (entry.motion == gadget) return stateMachine;
                }
            }
            return null;
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

        /// <summary>Adds the parameter (with its type's zero/false default) when the controller
        /// has none by that name; an existing parameter is left as it is, type included.</summary>
        public static void EnsureParameter(AnimatorController controller, string name,
            AnimatorControllerParameterType type)
        {
            if (FindParameter(controller, name) == null)
                controller.AddParameter(name, type);
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

        /// <summary>
        /// Every parameter by name, read in one go. Use this instead of
        /// <see cref="FindParameter"/> whenever a loop asks about more than a couple of names:
        /// <c>AnimatorController.parameters</c> is a native property that builds and marshals a
        /// fresh array on every access, so asking per item costs the whole parameter list per
        /// item. On an avatar's worth of parameters, a loop over twenty targets was moving a
        /// few thousand objects across to answer twenty questions.
        /// </summary>
        public static Dictionary<string, AnimatorControllerParameter> ParametersByName(
            AnimatorController controller)
        {
            var byName = new Dictionary<string, AnimatorControllerParameter>();
            if (controller == null) return byName;
            foreach (var p in controller.parameters)
                // First wins, which is what FindParameter answers. A controller can carry the
                // same name twice — the analyzer reports it — and the two lookups disagreeing
                // about which one it means is the kind of difference nothing would notice.
                if (p != null && !string.IsNullOrEmpty(p.name) && !byName.ContainsKey(p.name))
                    byName[p.name] = p;
            return byName;
        }

        /// <summary>The parameter, or null — the dictionary form of <see cref="FindParameter"/>,
        /// so a loop reads the same way whichever it uses.</summary>
        public static AnimatorControllerParameter Find(
            this Dictionary<string, AnimatorControllerParameter> byName, string name) =>
            name != null && byName.TryGetValue(name, out var parameter) ? parameter : null;

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

        /// <summary>
        /// A 2D Freeform Directional tree. This mode blends by the *direction* of (x, y)
        /// rather than by the plane distance, which is what turns a ring of children into a
        /// lookup table over the angle; a child at the origin is the value the field collapses
        /// to when the vector has no direction to speak of.
        /// </summary>
        public static BlendTree Tree2DFreeformDirectional(AnimatorController controller, string name,
            string xParameter, string yParameter)
        {
            var tree = new BlendTree
            {
                name = name,
                blendType = BlendTreeType.FreeformDirectional2D,
                blendParameter = xParameter,
                blendParameterY = yParameter,
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

        /// <summary>Flushes freshly attached sub-assets into the imported artifact. The Project
        /// window lists sub-assets from the import, not from memory, so anything added with
        /// AddObjectToAsset stays invisible there until the file is saved and reimported. Once
        /// per batch — import churn is the reason this isn't part of Attach itself.</summary>
        public static void CommitSubAssets(AnimatorController controller)
        {
            var path = controller != null ? AssetDatabase.GetAssetPath(controller) : null;
            if (string.IsNullOrEmpty(path)) return;
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
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
