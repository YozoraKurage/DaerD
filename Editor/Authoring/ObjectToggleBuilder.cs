using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// One toggle of the object gadget family, as a recipe writes it: which objects inside the
    /// pinned prefab it animates, what about them, and how it is wired.
    ///
    ///   c.Objects().Toggle("Hat")
    ///    .Shows("Head/Hat")                       // keyed active while the toggle is ON
    ///    .Hides("Head/Hair")                      // and the other way round
    ///    .Enables("Head/Hat/Light", "Light")      // a component's enabled flag
    ///    .BlendShape("Body", "Shrink", 0, 100)    // a blendshape's OFF and ON weights
    ///    .DefaultOn().Declare();
    ///
    /// <para>PATHS, NOT REFERENCES.</para>
    /// The wizard saves a REFERENCE to each object and derives its path when it generates
    /// (ADR 0044), because a reference survives a rename and a saved path does not. Source code
    /// has no references to save: what a recipe can carry is a name. So a path is what these
    /// calls take, it is resolved inside the pinned prefab when the recipe runs, and what the
    /// record ends up holding is the reference that lookup found — after that the gadget is the
    /// wizard's kind again and a rename moves it as usual. The consequence to know: rename an
    /// object and the recipe stops naming it, by name, on the next run. That is the trade a text
    /// file makes, and it is why the failure is loud.
    ///
    /// <para>THE TARGET IS CREATED BY WHICHEVER CALL MENTIONS IT FIRST.</para>
    /// Every call names its own path, so the order they come in does not matter and nothing
    /// depends on a hidden "current target". <see cref="Shows"/> and <see cref="Hides"/> say the
    /// object itself is switched; a binding call on a path nothing has mentioned adds a target
    /// whose object is NOT switched, which is how a toggle animates only a component.
    /// </summary>
    public sealed class ObjectToggleBuilder
    {
        /// <summary>One target as the recipe describes it — the record shape (ADR 0044) with the
        /// reference still a path.</summary>
        internal class TargetSpec
        {
            public string path;
            public bool activeWhenOn = true;
            public bool toggleActive;
            public readonly List<GraphFrameData.BindingRecord> bindings =
                new List<GraphFrameData.BindingRecord>();
        }

        readonly ObjectRecipeBuilder _owner;
        internal readonly List<TargetSpec> Targets = new List<TargetSpec>();
        internal string Name { get; }
        internal string Parameter { get; }
        internal ToggleBuilder.Mode Mode { get; private set; } = ToggleBuilder.Mode.Layer;
        internal bool IsDefaultOn { get; private set; }
        internal bool IsDeclared { get; private set; }
        internal string OnClipPath { get; private set; }
        internal string OffClipPath { get; private set; }

        internal ObjectToggleBuilder(ObjectRecipeBuilder owner, string name, string parameter)
        {
            _owner = owner;
            Name = name;
            Parameter = parameter;
        }

        // ---- wiring -------------------------------------------------------------

        /// <summary>Wire this toggle as a 1D tree inside a Direct blend tree layer, driven by a
        /// Float, instead of as a Bool layer of its own. Every tree-wired toggle in one recipe
        /// shares one layer, which is the whole point of the wiring — a hundred toggles cost one
        /// layer instead of a hundred.</summary>
        public ObjectToggleBuilder AsTree() => Set(() => Mode = ToggleBuilder.Mode.DirectBlendTree,
            "AsTree()");

        /// <summary>Start switched on: the parameter's default value, and the ON state for the
        /// layer wiring.</summary>
        public ObjectToggleBuilder DefaultOn() => Set(() => IsDefaultOn = true, "DefaultOn()");

        /// <summary>
        /// Also declare the parameter through the controller's parameter store, so the built
        /// avatar knows about it.
        ///
        /// Off unless asked for, which is the opposite of the wizard's default and deliberate:
        /// the store of a gimmick is usually an MA Parameters component INSIDE the pinned prefab,
        /// and a recipe that declared by default would write to somebody's prefab as a side
        /// effect of generating a controller. Written out it is a line of code that says so.
        /// </summary>
        public ObjectToggleBuilder Declare() => Set(() => IsDeclared = true, "Declare()");

        // ---- targets ------------------------------------------------------------

        /// <summary>The object at <paramref name="path"/> is active while the toggle is ON.
        /// The path is relative to the merge; "" is the merge's own object.</summary>
        public ObjectToggleBuilder Shows(string path) => Switched(path, true, "Shows");

        /// <summary>The reverse: active while the toggle is OFF. Everything else this target
        /// animates is inverted with it — one target is one direction.</summary>
        public ObjectToggleBuilder Hides(string path) => Switched(path, false, "Hides");

        /// <summary>Inverts a target without switching the object itself: the bindings on this
        /// path read their ON value while the toggle is OFF. For a toggle that only turns a
        /// component off, on an object that has to stay active.</summary>
        public ObjectToggleBuilder Inverted(string path) =>
            Set(() => Target(path).activeWhenOn = false, $"Inverted({RecipeScript.S(path)})");

        /// <summary>Animates a component's enabled flag on this path. The component is named
        /// rather than typed — the name is what a record can hold and what is resolved against
        /// the types this project actually has, so a PhysBone in a project without the SDK is a
        /// named refusal instead of a corrupted gadget.</summary>
        public ObjectToggleBuilder Enables(string path, string component) =>
            Bind(path, component, "m_Enabled", 0f, 1f,
                $"Enables({RecipeScript.S(path)}, {RecipeScript.S(component)})");

        /// <summary>Animates one blendshape weight on this path, between the two values it holds
        /// on each side of the toggle.</summary>
        public ObjectToggleBuilder BlendShape(string path, string shape,
            float off = 0f, float on = 100f) =>
            Bind(path, nameof(SkinnedMeshRenderer), "blendShape." + shape, off, on,
                $"BlendShape({RecipeScript.S(path)}, {RecipeScript.S(shape)}, "
                + $"{RecipeScript.F(off)}, {RecipeScript.F(on)})");

        /// <summary>Any other property of any other component, for the bindings the two calls
        /// above do not cover. The property is Unity's serialized name ("m_Enabled",
        /// "m_Intensity"), which is what a curve binds to.</summary>
        public ObjectToggleBuilder Property(string path, string component, string property,
            float off = 0f, float on = 1f) =>
            Bind(path, component, property, off, on,
                $"Property({RecipeScript.S(path)}, {RecipeScript.S(component)}, "
                + $"{RecipeScript.S(property)}, {RecipeScript.F(off)}, {RecipeScript.F(on)})");

        // ---- clips --------------------------------------------------------------

        /// <summary>
        /// Write the ON side into a clip of your own instead of generating one, named by asset
        /// path. DaerD writes this gadget's rows into it, leaves every other curve alone and
        /// never deletes the file (ADR 0046); a row it needs that the clip already has stops the
        /// run by name.
        ///
        /// A path rather than a serialized asset field, unlike every other asset a recipe
        /// references. A gadget's target is already a path — the whole call reads as a
        /// description of a project — and a field would have to be assigned on the recipe asset
        /// by hand before the code that mentions it could run at all.
        /// </summary>
        public ObjectToggleBuilder OnClip(string assetPath) =>
            Set(() => OnClipPath = assetPath, $"OnClip({RecipeScript.S(assetPath)})");

        /// <summary>The OFF side. See <see cref="OnClip"/>.</summary>
        public ObjectToggleBuilder OffClip(string assetPath) =>
            Set(() => OffClipPath = assetPath, $"OffClip({RecipeScript.S(assetPath)})");

        internal IEnumerable<string> ClipPaths()
        {
            if (!string.IsNullOrEmpty(OnClipPath)) yield return OnClipPath;
            if (!string.IsNullOrEmpty(OffClipPath)) yield return OffClipPath;
        }

        // ---- turning into a record ----------------------------------------------

        /// <summary>
        /// The record this toggle describes, with <paramref name="resolved"/> holding the object
        /// each target path was found at (in declaration order). The caller resolves rather than
        /// this: a lookup that failed has to be collected with every other failure before
        /// anything is built, and a record half full of nulls is not something to hand onwards.
        /// </summary>
        internal GraphFrameData.ObjectGadgetConfig ToConfig(List<GameObject> resolved)
        {
            var config = new GraphFrameData.ObjectGadgetConfig
            {
                kind = (int)ObjectGadgets.Kind.Toggle,
                name = Name,
                parameter = Parameter,
                mode = (int)Mode,
                defaultOn = IsDefaultOn,
                declare = IsDeclared,
                onClip = Slot(OnClipPath),
                offClip = Slot(OffClipPath),
            };
            for (int i = 0; i < Targets.Count; i++)
            {
                var spec = Targets[i];
                var record = new GraphFrameData.ObjectTargetRecord
                {
                    target = i < resolved.Count ? resolved[i] : null,
                    activeWhenOn = spec.activeWhenOn,
                    toggleActive = spec.toggleActive,
                };
                foreach (var binding in spec.bindings)
                    record.bindings.Add(new GraphFrameData.BindingRecord
                    {
                        typeName = binding.typeName,
                        property = binding.property,
                        offValue = binding.offValue,
                        onValue = binding.onValue,
                    });
                config.targets.Add(record);
            }
            return config;
        }

        static GraphFrameData.ClipOutput Slot(string assetPath) =>
            string.IsNullOrEmpty(assetPath)
                ? new GraphFrameData.ClipOutput()
                : new GraphFrameData.ClipOutput
                {
                    clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath),
                    userProvided = true,
                };

        // ---- plumbing -----------------------------------------------------------

        TargetSpec Target(string path)
        {
            foreach (var spec in Targets)
                if (spec.path == path) return spec;
            var made = new TargetSpec { path = path ?? string.Empty };
            Targets.Add(made);
            return made;
        }

        ObjectToggleBuilder Switched(string path, bool activeWhenOn, string method) =>
            Set(() =>
            {
                var spec = Target(path);
                spec.toggleActive = true;
                spec.activeWhenOn = activeWhenOn;
            }, $"{method}({RecipeScript.S(path)})");

        ObjectToggleBuilder Bind(string path, string component, string property,
            float off, float on, string call) =>
            Set(() => Target(path).bindings.Add(new GraphFrameData.BindingRecord
            {
                typeName = component,
                property = property,
                offValue = off,
                onValue = on,
            }), call);

        ObjectToggleBuilder Set(System.Action change, string call)
        {
            change();
            _owner.Root.Script?.Call(this, call);
            return this;
        }
    }
}
