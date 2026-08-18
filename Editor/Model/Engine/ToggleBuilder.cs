using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// The generation core of the object gadget family's Toggle kind: a pair of clips keying the
    /// listed targets ON and OFF, and the two ways of playing them — a classic two-state Bool
    /// layer, or a 1D tree inside a Write-Defaults-ON Direct blend tree layer driven by a Float.
    ///
    /// <para>PATHS ARRIVE ALREADY DERIVED.</para>
    /// Every path in a <see cref="Plan"/> was worked out by somebody else, and that somebody is
    /// <see cref="ObjectGadgets"/>, which derives them from references into the pinned prefab
    /// each time it applies (ADR 0044). This class used to own a Request whose targets were
    /// paths TYPED BY A PERSON against a scene hierarchy, plus a Validate and an Apply to run
    /// it. That face is gone rather than kept beside the family's: it recorded nothing, so
    /// nothing it built could be edited, regenerated or swept (the reason its wizard was hidden
    /// away in the first place — ADR 0036), and two entry points into one generator is how the
    /// two drift apart about what a toggle is.
    ///
    /// <para>NOTHING HERE OWNS ANYTHING.</para>
    /// No asset is created, attached or saved, and no record is written. The clips come back
    /// loose for the caller to put where it wants them — a sub-asset of the controller, or a
    /// clip the user supplied (ADR 0046) — which is what lets one core serve both.
    /// </summary>
    static class ToggleBuilder
    {
        public enum Mode
        {
            /// <summary>New layer, two states (OFF/ON) switched by a Bool parameter.</summary>
            Layer,
            /// <summary>1D tree (0 = OFF, 1 = ON) added to a Direct blend tree layer,
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
            /// <summary>Hierarchy path relative to the merge this controller is pinned to,
            /// derived from the saved reference by the caller. "" is the merge's own object,
            /// which is a legitimate thing to toggle.</summary>
            public string path;
            /// <summary>Unchecked inverts the toggle: every binding swaps its ON and OFF values.</summary>
            public bool activeWhenOn = true;
            /// <summary>Key GameObject.m_IsActive itself. Can be off when only components toggle.</summary>
            public bool toggleActive = true;
            public List<Binding> bindings = new List<Binding>();
        }

        /// <summary>One toggle as the generators need it: derived paths, a name, a parameter and
        /// where to put the result. Not saved anywhere — the record is
        /// <c>GraphFrameData.ObjectGadgetConfig</c>, and this is what it is turned into on the
        /// way to being built.</summary>
        public class Plan
        {
            public AnimatorController controller;
            public Mode mode;
            /// <summary>Base name for the layer, the generated clips and the 1D tree.</summary>
            public string name;
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

        /// <summary>
        /// One curve a toggle writes: where it goes, and the value it holds on each side.
        ///
        /// Rows exist as a description before they exist as curves so that the ledger a record
        /// keeps (ADR 0046: what the last generate wrote) and the curves themselves come from
        /// the same enumeration. A snapshot derived separately would be free to disagree with
        /// what was actually written, which is exactly the failure it is there to prevent.
        /// </summary>
        public readonly struct Row
        {
            public readonly EditorCurveBinding binding;
            public readonly float offValue;
            public readonly float onValue;

            public Row(EditorCurveBinding binding, float offValue, float onValue)
            {
                this.binding = binding;
                this.offValue = offValue;
                this.onValue = onValue;
            }

            public float Value(bool on) => on ? onValue : offValue;
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

        /// <summary>
        /// The same question asked of a CURVE's type rather than of a binding's component: the
        /// row that switches an object on and off binds to GameObject, which is not a Component
        /// and which <see cref="FindComponentType"/> therefore cannot find.
        ///
        /// It exists because a written row (ADR 0046) is stored as a type NAME, and taking those
        /// rows back out again means turning every one of them back into a binding — including
        /// the m_IsActive ones, which are most of them. A resolver that quietly answered null
        /// for "GameObject" would leave exactly the rows a rename strands.
        /// </summary>
        public static System.Type FindCurveType(string shortName) =>
            shortName == nameof(GameObject) ? typeof(GameObject) : FindComponentType(shortName);

        /// <summary>Every curve this plan writes, in both clips. An inverted target is folded in
        /// here — its rows come back with ON and OFF swapped — so nothing downstream has to know
        /// about inversion twice.</summary>
        public static List<Row> Rows(Plan plan)
        {
            var rows = new List<Row>();
            if (plan == null || plan.targets == null) return rows;
            foreach (var target in plan.targets)
            {
                if (target == null) continue;
                string path = target.path ?? string.Empty;
                if (target.toggleActive)
                    rows.Add(Keyed(
                        EditorCurveBinding.FloatCurve(path, typeof(GameObject), "m_IsActive"),
                        0f, 1f, target.activeWhenOn));
                if (target.bindings == null) continue;
                foreach (var extra in target.bindings)
                {
                    if (extra == null || extra.type == null || string.IsNullOrEmpty(extra.property))
                        continue;
                    rows.Add(Keyed(
                        EditorCurveBinding.FloatCurve(path, extra.type, extra.property),
                        extra.offValue, extra.onValue, target.activeWhenOn));
                }
            }
            return rows;
        }

        static Row Keyed(EditorCurveBinding binding, float off, float on, bool activeWhenOn) =>
            activeWhenOn ? new Row(binding, off, on) : new Row(binding, on, off);

        /// <summary>One side of the toggle as a fresh, unattached clip. The caller decides where
        /// it lives — see the class docs.</summary>
        public static AnimationClip BuildClip(Plan plan, bool on)
        {
            var clip = new AnimationClip { name = plan.name + (on ? " ON" : " OFF") };
            Write(clip, plan, on);
            return clip;
        }

        /// <summary>
        /// One side of the toggle written into a clip that already exists, touching nothing but
        /// its own rows.
        ///
        /// The same loop <see cref="BuildClip"/> runs, split out rather than duplicated because
        /// the two must not drift: a clip the user supplied is written by exactly the rows a
        /// generated one would have held, which is what makes the two kinds of clip the same
        /// feature (ADR 0046). Curves that are not this plan's are left where they are — taking
        /// somebody's clip over is a refusal, not a write, and it is made before this is called.
        ///
        /// No undo and no dirtying: the caller owns both, because it is the caller that knows
        /// whether the clip is an asset somebody can lose.
        /// </summary>
        public static void Write(AnimationClip clip, Plan plan, bool on)
        {
            if (clip == null) return;
            foreach (var row in Rows(plan))
                AnimationUtility.SetEditorCurve(clip, row.binding,
                    new AnimationCurve(new Keyframe(0f, row.Value(on))));
        }

        /// <summary>
        /// The classic toggle idiom: OFF and ON states with instant Bool transitions both ways,
        /// in a layer of its own. Both clips key every target, so Write Defaults stays OFF.
        /// Returns the layer's root state machine, which is how the record identifies the layer
        /// across renames and reorders.
        /// </summary>
        public static AnimatorStateMachine BuildLayer(Plan plan, AnimationClip onClip,
            AnimationClip offClip, out bool createdParameter)
        {
            var controller = plan.controller;
            EnsureParameter(controller, plan.parameter, AnimatorControllerParameterType.Bool,
                out createdParameter);
            if (createdParameter)
                SetBoolDefault(controller, plan.parameter, plan.defaultOn);
            // A reused parameter keeps its own default; start on the state that matches the
            // actual default so the layer doesn't transition on the first frame.
            bool startOn = DbtBuilder.FindParameter(controller, plan.parameter).defaultBool;

            controller.AddLayer(DbtBuilder.UniqueLayerName(controller, plan.name));
            var layers = controller.layers;
            layers[layers.Length - 1].defaultWeight = 1f;
            controller.layers = layers;

            var stateMachine = layers[layers.Length - 1].stateMachine;
            var offState = stateMachine.AddState(plan.name + " OFF", new Vector3(300f, 60f, 0f));
            var onState = stateMachine.AddState(plan.name + " ON", new Vector3(300f, 170f, 0f));
            offState.writeDefaultValues = false;
            onState.writeDefaultValues = false;
            offState.motion = offClip;
            onState.motion = onClip;
            stateMachine.defaultState = startOn ? onState : offState;

            InstantTransition(offState, onState, plan.parameter, AnimatorConditionMode.If);
            InstantTransition(onState, offState, plan.parameter, AnimatorConditionMode.IfNot);

            EditorUtility.SetDirty(offState);
            EditorUtility.SetDirty(onState);
            EditorUtility.SetDirty(stateMachine);
            return stateMachine;
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
        /// DBT layer with constant-One weight — one layer hosts any number of toggles. Returns
        /// the child it added, which is the handle on everything below it.</summary>
        public static Motion BuildDirectBlendTree(Plan plan, AnimationClip onClip,
            AnimationClip offClip, out bool createdParameter)
        {
            var controller = plan.controller;
            string one = DbtBuilder.EnsureConstantOneParameter(controller);
            EnsureParameter(controller, plan.parameter, AnimatorControllerParameterType.Float,
                out createdParameter);
            if (createdParameter)
                SetFloatDefault(controller, plan.parameter, plan.defaultOn ? 1f : 0f);

            var root = DbtBuilder.EnsureDirectBlendTreeLayer(controller, plan.layerIndex,
                plan.newLayerName);
            var tree = DbtBuilder.Tree1D(controller,
                DbtBuilder.Sanitize(plan.name) + " Toggle", plan.parameter);
            tree.AddChild(offClip, 0f);
            tree.AddChild(onClip, 1f);
            DbtBuilder.AddDirectChild(root, tree, one);
            return tree;
        }

        /// <summary>Adds the parameter when the controller has none by that name, and says
        /// whether it did — which is the only ground on which removing the gadget may take it
        /// away again.</summary>
        public static void EnsureParameter(AnimatorController controller, string name,
            AnimatorControllerParameterType type, out bool created)
        {
            created = DbtBuilder.FindParameter(controller, name) == null;
            DbtBuilder.EnsureParameter(controller, name, type);
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
