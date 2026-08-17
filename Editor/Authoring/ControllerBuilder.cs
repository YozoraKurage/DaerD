using System;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// The recipe-facing fluent API, deliberately shaped like AnimatorAsCode V1 — the
    /// dialect AI models and gimmick authors already know: NewState / WithAnimation /
    /// TransitionsTo(x).When(param.IsTrue()) / Drives / DrivingRemaps. Parameters are typed
    /// handles, conditions are objects those handles produce. Everything is data until
    /// Generate applies it; nothing here touches a controller directly (use <see cref="Raw"/>).
    ///
    ///   var go = c.BoolParameter("Go");
    ///   var fx = c.Layer("Hand");
    ///   var idle = fx.NewState("Idle").WithAnimation(idleClip).At(260, 60);
    ///   idle.TransitionsTo(wave).When(go.IsTrue()).WithTransitionDurationSeconds(0.15f);
    ///   wave.Exits().When(go.IsFalse());
    ///   idle.Drives(step, 1).DrivingLocally();
    /// </summary>
    public sealed class ControllerBuilder
    {
        internal readonly ControllerIR IR = new ControllerIR();
        internal readonly List<Func<AnimatorController, List<string>>> PostOps =
            new List<Func<AnimatorController, List<string>>>();
        internal readonly List<string> Notes = new List<string>();
        internal readonly List<Action> PostBakeSyncs = new List<Action>();
        /// <summary>Layers a post step generated (async sync, gadgets). They belong to the
        /// recipe as much as the declared ones — the next Generate rebuilds them by name —
        /// so they carry the same ownership mark.</summary>
        internal readonly List<string> PostLayers = new List<string>();
        internal RecipeScript Script;

        // ---- parameters ----------------------------------------------------------

        public FloatParam FloatParameter(string name) =>
            Handle(new FloatParam(this, name), AnimatorControllerParameterType.Float,
                false, 0f, 0, false, $"FloatParameter({RecipeScript.S(name)})");

        public FloatParam FloatParameter(string name, float defaultValue) =>
            Handle(new FloatParam(this, name), AnimatorControllerParameterType.Float,
                true, defaultValue, 0, false,
                $"FloatParameter({RecipeScript.S(name)}, {RecipeScript.F(defaultValue)})");

        public IntParam IntParameter(string name) =>
            Handle(new IntParam(this, name), AnimatorControllerParameterType.Int,
                false, 0f, 0, false, $"IntParameter({RecipeScript.S(name)})");

        public IntParam IntParameter(string name, int defaultValue) =>
            Handle(new IntParam(this, name), AnimatorControllerParameterType.Int,
                true, 0f, defaultValue, false,
                $"IntParameter({RecipeScript.S(name)}, {defaultValue})");

        public BoolParam BoolParameter(string name) =>
            Handle(new BoolParam(this, name), AnimatorControllerParameterType.Bool,
                false, 0f, 0, false, $"BoolParameter({RecipeScript.S(name)})");

        public BoolParam BoolParameter(string name, bool defaultValue) =>
            Handle(new BoolParam(this, name), AnimatorControllerParameterType.Bool,
                true, 0f, 0, defaultValue,
                $"BoolParameter({RecipeScript.S(name)}, {RecipeScript.B(defaultValue)})");

        public TriggerParam TriggerParameter(string name) =>
            Handle(new TriggerParam(this, name), AnimatorControllerParameterType.Trigger,
                false, 0f, 0, false, $"TriggerParameter({RecipeScript.S(name)})");

        /// <summary>Registration is idempotent: naming a parameter twice refers to the same
        /// one; only a call that states a default value overwrites the default.</summary>
        T Handle<T>(T handle, AnimatorControllerParameterType type, bool hasDefault,
            float f, int i, bool b, string call) where T : ParamHandle
        {
            var existing = IR.parameters.Find(p => p.name == handle.Name);
            if (existing == null)
            {
                IR.parameters.Add(new ControllerIR.Param
                {
                    name = handle.Name,
                    type = type,
                    hasDefault = hasDefault,
                    defaultFloat = f,
                    defaultInt = i,
                    defaultBool = b,
                });
            }
            else if (existing.type != type)
                Notes.Add(L.Tr("Parameter '{0}' is declared with conflicting types ({1} and {2}).",
                    handle.Name, existing.type, type));
            else if (hasDefault)
            {
                existing.hasDefault = true;
                existing.defaultFloat = f;
                existing.defaultInt = i;
                existing.defaultBool = b;
            }
            Script?.Declare(handle, handle.Name, this, call);
            return handle;
        }

        // ---- layers ----------------------------------------------------------------

        public LayerBuilder Layer(string name)
        {
            var layer = new ControllerIR.Layer { name = name, machine = new ControllerIR.Machine { name = name } };
            IR.layers.Add(layer);
            var builder = new LayerBuilder(this, layer);
            Script?.Declare(builder, name, this, $"Layer({RecipeScript.S(name)})");
            return builder;
        }

        /// <summary>A synced layer mirroring <paramref name="sourceLayer"/> (a layer declared
        /// in this recipe, resolved by name at apply time).</summary>
        public SyncedLayerBuilder SyncedLayer(string name, string sourceLayer)
        {
            var layer = new ControllerIR.Layer { name = name };
            IR.layers.Add(layer);
            var builder = new SyncedLayerBuilder(this, layer, sourceLayer);
            Script?.Declare(builder, name, this,
                $"SyncedLayer({RecipeScript.S(name)}, {RecipeScript.S(sourceLayer)})");
            return builder;
        }

        /// <summary>An embedded blend tree; assign it with
        /// <see cref="StateBuilder.WithAnimation(TreeBuilder)"/> (AAC's aac.NewBlendTree flow).</summary>
        public TreeBuilder NewBlendTree(string name = "Blend Tree")
        {
            var builder = new TreeBuilder(this, new ControllerIR.Tree { name = name });
            Script?.Declare(builder, name, this, $"NewBlendTree({RecipeScript.S(name)})");
            return builder;
        }

        /// <summary>
        /// Escape hatch: runs after the declared layers are applied, with the live controller.
        /// Anything DaerD or Unity can do is available here — at the price that Verify can't
        /// see what it did. Exported code never contains Raw calls.
        /// </summary>
        public ControllerBuilder Raw(Action<AnimatorController> action)
        {
            if (action != null)
                PostOps.Add(controller =>
                {
                    action(controller);
                    return new List<string>();
                });
            return this;
        }

        /// <summary>
        /// Async Sync (parameter compression) as a post step: full wizard configuration plus
        /// the explicit per-step schedule the wizard doesn't expose. The generated layer is
        /// regenerated in place on every Generate, matched by base name. Left unnamed, the
        /// base name is derived from the target controller so two distributions that both
        /// multiplex don't fight over the same synced parameters.
        /// </summary>
        public AsyncSyncRecipeBuilder AsyncSync(string baseName = null)
        {
            // Idempotent like Gadgets, and for the same reason: a second builder over the same
            // name would post a second rebuild of the one layer, and the later one would undo
            // the earlier. The key is the argument as written, not the base name it resolves
            // to — an unnamed setup only learns its name once the controller is known.
            string key = baseName ?? string.Empty;
            if (!_asyncSyncs.TryGetValue(key, out var builder))
            {
                _asyncSyncs[key] = builder = new AsyncSyncRecipeBuilder(this, baseName);
                Script?.Declare(builder, "Async Sync", this,
                    baseName == null ? "AsyncSync()" : $"AsyncSync({RecipeScript.S(baseName)})");
            }
            return builder;
        }

        readonly Dictionary<string, AsyncSyncRecipeBuilder> _asyncSyncs =
            new Dictionary<string, AsyncSyncRecipeBuilder>();

        /// <summary>
        /// DBT (AAP) gadgets — the per-frame float math from the parameter panel's Add menu —
        /// as a post step, collected into one Direct blend tree layer named
        /// <paramref name="layerName"/>. The layer is rebuilt from scratch on every Generate;
        /// see <see cref="GadgetRecipeBuilder"/> for what that sweeps.
        ///
        ///   c.Gadgets("Math").Multiply(hue, gain, "Hue/Scaled")
        ///                    .Smooth("Hue/Scaled", "Hue/Smoothed", "Hue/Smoothing");
        /// </summary>
        public GadgetRecipeBuilder Gadgets(string layerName = "DBT")
        {
            // Idempotent like a parameter handle: naming the same layer twice keeps adding to
            // the same one. A second builder over the same name would sweep away what the
            // first one built — its rebuild starts by clearing that layer.
            string name = string.IsNullOrEmpty(layerName) ? "DBT" : layerName;
            if (!_gadgets.TryGetValue(name, out var builder))
            {
                _gadgets[name] = builder = new GadgetRecipeBuilder(this, name);
                // Only the first visit declares a variable; the ones after it add to a builder
                // the recorded code already has a name for.
                Script?.Declare(builder, name + " Gadgets", this,
                    name == "DBT" ? "Gadgets()" : $"Gadgets({RecipeScript.S(name)})");
            }
            return builder;
        }

        readonly Dictionary<string, GadgetRecipeBuilder> _gadgets =
            new Dictionary<string, GadgetRecipeBuilder>();

        /// <summary>
        /// Object gadgets — the toggles whose subject is an object inside the gimmick prefab
        /// this controller is pinned to — as a post step. The controller side is rebuilt on
        /// every Generate; the prefab is only ever read, and a target it cannot find stops the
        /// step by name (ADR 0047).
        ///
        ///   c.Objects().Toggle("Hat").Shows("Head/Hat")
        ///              .Toggle("Cape").AsTree().Hides("Body/Cape");
        ///
        /// One builder per recipe, unlike <see cref="Gadgets"/>, which is one per layer: an
        /// object gadget names no layer — a Bool toggle is a layer of its own and the tree-wired
        /// ones share one — so there is nothing here to key a second builder by.
        /// </summary>
        public ObjectRecipeBuilder Objects()
        {
            if (_objects == null)
            {
                _objects = new ObjectRecipeBuilder(this);
                Script?.Declare(_objects, "Objects", this, "Objects()");
            }
            return _objects;
        }

        ObjectRecipeBuilder _objects;

        // ---- bake ---------------------------------------------------------------

        /// <summary>Resolves deferred references (synced source names) and returns problems
        /// worth surfacing before the declaration is applied.</summary>
        internal List<string> Bake()
        {
            foreach (var resolve in PostBakeSyncs) resolve();

            var problems = new List<string>(Notes);
            var layerNames = new HashSet<string>();
            foreach (var layer in IR.layers)
                if (!layerNames.Add(layer.name))
                    problems.Add(L.Tr("Layer '{0}' is declared more than once.", layer.name));
            return problems;
        }

        internal int IndexOfLayer(string name)
        {
            for (int i = 0; i < IR.layers.Count; i++)
                if (IR.layers[i].name == name) return i;
            return -1;
        }
    }
}
