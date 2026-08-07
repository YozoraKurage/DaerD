using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Builds GameObject ON/OFF toggles: a pair of clips animating m_IsActive for the picked
    /// hierarchy paths, wired up either as a classic two-state Bool layer or as a 1D tree
    /// inside a Write-Defaults-ON Direct blend tree layer (Float weight). The clips are saved
    /// as .anim assets next to the .controller file so they survive controller cleanup and
    /// can be edited normally (in-memory controllers just keep the loose references).
    /// </summary>
    static class ToggleBuilder
    {
        public enum Mode
        {
            /// <summary>New layer, two states (OFF/ON) switched by a Bool parameter.</summary>
            Layer,
            /// <summary>1D tree (0 = OFF clip, 1 = ON clip) added to a Direct blend tree layer,
            /// driven by a Float parameter.</summary>
            DirectBlendTree,
        }

        /// <summary>One extra animated property besides GameObject.m_IsActive: a component's
        /// m_Enabled flag, or a blendshape weight with explicit OFF/ON values.</summary>
        public class Binding
        {
            /// <summary>Component type the curve binds to (concrete type, e.g. SkinnedMeshRenderer).</summary>
            public System.Type type;
            /// <summary>"m_Enabled" or "blendShape.&lt;name&gt;".</summary>
            public string property;
            public float offValue;
            public float onValue = 1f;

            public static Binding Enabled(System.Type componentType) => new Binding
            {
                type = componentType,
                property = "m_Enabled",
            };

            public static Binding BlendShape(string shapeName, float off, float on) => new Binding
            {
                type = typeof(SkinnedMeshRenderer),
                property = "blendShape." + shapeName,
                offValue = off,
                onValue = on,
            };
        }

        public class Target
        {
            /// <summary>Hierarchy path relative to the Animator root ("" is the root itself).</summary>
            public string path;
            /// <summary>Unchecked inverts the toggle: every binding swaps its ON and OFF values.</summary>
            public bool activeWhenOn = true;
            /// <summary>Key GameObject.m_IsActive itself. Can be off when only components toggle.</summary>
            public bool toggleActive = true;
            public List<Binding> bindings = new List<Binding>();
        }

        public class Request
        {
            public AnimatorController controller;
            public Mode mode;
            /// <summary>Base name for the layer, the generated clips and the 1D tree.</summary>
            public string toggleName;
            /// <summary>Bool (Layer) or Float (DirectBlendTree). Created if missing, reused when
            /// the type matches.</summary>
            public string parameter;
            /// <summary>Stored as the parameter's default; Layer mode also starts on the ON state.</summary>
            public bool defaultOn;
            public List<Target> targets = new List<Target>();
            /// <summary>DirectBlendTree mode: existing DBT (or empty) layer, or -1 to create one.</summary>
            public int layerIndex = -1;
            public string newLayerName = "DBT";
        }

        /// <summary>Resolves a component type by short name (e.g. "VRCPhysBone") without an
        /// SDK assembly reference; null when no loaded type matches.</summary>
        public static System.Type FindComponentType(string shortName)
        {
            foreach (var type in TypeCache.GetTypesDerivedFrom<Component>())
                if (!type.IsAbstract && type.Name == shortName)
                    return type;
            return null;
        }

        /// <summary>Human-readable reason the request can't run, or null when it can.</summary>
        public static string Validate(Request r)
        {
            var controller = r.controller;
            if (controller == null) return L.Tr("No controller.");
            if (string.IsNullOrEmpty(r.toggleName))
                return L.Tr("The toggle needs a name.");
            if (string.IsNullOrEmpty(r.parameter))
                return L.Tr("The parameter needs a name.");

            var existing = DbtBuilder.FindParameter(controller, r.parameter);
            if (existing != null)
            {
                if (r.mode == Mode.Layer && existing.type != AnimatorControllerParameterType.Bool)
                    return L.Tr("Parameter '{0}' exists but is not a Bool.", r.parameter);
                if (r.mode == Mode.DirectBlendTree && existing.type != AnimatorControllerParameterType.Float)
                    return L.Tr("Parameter '{0}' exists but is not a Float.", r.parameter);
            }

            if (r.targets == null || r.targets.Count == 0)
                return L.Tr("Add at least one target object.");
            var seen = new HashSet<string>();
            foreach (var target in r.targets)
            {
                if (target == null || target.path == null || target.path.Trim().Length == 0)
                    return L.Tr("Every target needs a hierarchy path.");
                if (!seen.Add(target.path.Trim()))
                    return L.Tr("Target path '{0}' is listed more than once.", target.path.Trim());
                if (!target.toggleActive && (target.bindings == null || target.bindings.Count == 0))
                    return L.Tr("Target '{0}' has nothing to animate — enable Object or add a component binding.", target.path.Trim());
                if (target.bindings != null)
                    foreach (var binding in target.bindings)
                        if (binding == null || binding.type == null || string.IsNullOrEmpty(binding.property))
                            return L.Tr("Target '{0}' has an invalid component binding.", target.path.Trim());
            }

            if (r.mode == Mode.DirectBlendTree)
                return AapSmoothing.ValidateLayerChoice(controller, r.layerIndex, r.newLayerName);
            return null;
        }

        /// <summary>Runs the (pre-validated) request; returns false when validation fails.</summary>
        public static bool Apply(Request r)
        {
            if (Validate(r) != null) return false;
            var controller = r.controller;

            using (new UndoScope("Object Toggle"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Object Toggle");

                var onClip = BuildClip(r, on: true);
                var offClip = BuildClip(r, on: false);

                if (r.mode == Mode.Layer)
                    BuildLayer(r, onClip, offClip);
                else
                    BuildDirectBlendTree(r, onClip, offClip);

                EditorUtility.SetDirty(controller);
            }
            return true;
        }

        /// <summary>One key per target on GameObject.m_IsActive; "activeWhenOn: false" targets
        /// are inverted. Saved as an .anim next to the controller asset (see class docs).</summary>
        static AnimationClip BuildClip(Request r, bool on)
        {
            var clip = new AnimationClip { name = r.toggleName + (on ? " ON" : " OFF") };
            foreach (var target in r.targets)
            {
                // activeWhenOn == false inverts the whole target: its bindings take their
                // ON values in the OFF clip and vice versa.
                bool takeOn = on == target.activeWhenOn;
                string path = target.path.Trim();
                if (target.toggleActive)
                {
                    var binding = EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive");
                    AnimationUtility.SetEditorCurve(clip, binding,
                        new AnimationCurve(new Keyframe(0f, takeOn ? 1f : 0f)));
                }
                if (target.bindings == null) continue;
                foreach (var extra in target.bindings)
                {
                    var binding = EditorCurveBinding.FloatCurve(path, extra.type, extra.property);
                    AnimationUtility.SetEditorCurve(clip, binding,
                        new AnimationCurve(new Keyframe(0f, takeOn ? extra.onValue : extra.offValue)));
                }
            }
            SaveClipBesideController(r.controller, clip);
            return clip;
        }

        static void SaveClipBesideController(AnimatorController controller, AnimationClip clip)
        {
            string controllerPath = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(controllerPath)) return;   // in-memory: loose reference

            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.GetDirectoryName(controllerPath) + "/" + FileSafe(clip.name) + ".anim");
            AssetDatabase.CreateAsset(clip, assetPath);
        }

        /// <summary>Clip names double as file names — strip the characters a path can't hold.</summary>
        static string FileSafe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>The classic toggle idiom: OFF and ON states with instant Bool transitions
        /// both ways. Both clips key every target, so Write Defaults stays OFF.</summary>
        static void BuildLayer(Request r, AnimationClip onClip, AnimationClip offClip)
        {
            var controller = r.controller;
            bool created;
            EnsureParameter(controller, r.parameter, AnimatorControllerParameterType.Bool, out created);
            if (created)
                SetBoolDefault(controller, r.parameter, r.defaultOn);
            // A reused parameter keeps its own default; start on the state that matches the
            // actual default so the layer doesn't transition on the first frame.
            bool startOn = DbtBuilder.FindParameter(controller, r.parameter).defaultBool;

            controller.AddLayer(DbtBuilder.UniqueLayerName(controller, r.toggleName));
            var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1f;
            controller.layers = layers;

            var stateMachine = layers[layers.Length - 1].stateMachine;
            var offState = stateMachine.AddState(r.toggleName + " OFF", new Vector3(300f, 60f, 0f));
            var onState = stateMachine.AddState(r.toggleName + " ON", new Vector3(300f, 170f, 0f));
            offState.writeDefaultValues = false;
            onState.writeDefaultValues = false;
            offState.motion = offClip;
            onState.motion = onClip;
            stateMachine.defaultState = startOn ? onState : offState;

            InstantTransition(offState, onState, r.parameter, AnimatorConditionMode.If);
            InstantTransition(onState, offState, r.parameter, AnimatorConditionMode.IfNot);

            EditorUtility.SetDirty(offState);
            EditorUtility.SetDirty(onState);
            EditorUtility.SetDirty(stateMachine);
        }

        static void InstantTransition(AnimatorState from, AnimatorState to,
            string parameter, AnimatorConditionMode mode)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(mode, 0f, parameter);
        }

        /// <summary>A 1D tree (OFF at 0, ON at 1) blended by the Float parameter, added to the
        /// DBT layer with constant-One weight — one layer hosts any number of toggles.</summary>
        static void BuildDirectBlendTree(Request r, AnimationClip onClip, AnimationClip offClip)
        {
            var controller = r.controller;
            string one = DbtBuilder.EnsureConstantOneParameter(controller);
            bool created;
            EnsureParameter(controller, r.parameter, AnimatorControllerParameterType.Float, out created);
            if (created)
                SetFloatDefault(controller, r.parameter, r.defaultOn ? 1f : 0f);

            var root = DbtBuilder.EnsureDirectBlendTreeLayer(controller, r.layerIndex, r.newLayerName);
            var tree = DbtBuilder.Tree1D(controller,
                DbtBuilder.Sanitize(r.toggleName) + " Toggle", r.parameter);
            tree.AddChild(offClip, 0f);
            tree.AddChild(onClip, 1f);
            DbtBuilder.AddDirectChild(root, tree, one);
        }

        static void EnsureParameter(AnimatorController controller, string name,
            AnimatorControllerParameterType type, out bool created)
        {
            created = DbtBuilder.FindParameter(controller, name) == null;
            if (created)
                controller.AddParameter(name, type);
        }

        static void SetBoolDefault(AnimatorController controller, string name, bool value)
        {
            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].name == name)
                    parameters[i].defaultBool = value;
            controller.parameters = parameters;
        }

        static void SetFloatDefault(AnimatorController controller, string name, float value)
        {
            var parameters = controller.parameters;
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].name == name)
                    parameters[i].defaultFloat = value;
            controller.parameters = parameters;
        }
    }
}
