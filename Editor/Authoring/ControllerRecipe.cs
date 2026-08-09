using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Base class for a controller recipe: a ScriptableObject whose <see cref="Build"/>
    /// method declares layers in C#, with asset references living in serialized fields of
    /// the derived class (drag & drop in the inspector — no GUIDs in code). Generate applies
    /// the declaration to <see cref="targetController"/>; Verify reports where the live
    /// controller has drifted from the code.
    ///
    /// A recipe owns the layers it declares. Non-exclusive recipes replace those layers by
    /// name and leave everything else alone; an exclusive recipe owns the whole controller —
    /// parameters and layer list become exactly what the code declares.
    ///
    /// An exported recipe comes in two halves of one partial class, because the round trip
    /// (export → reshape the code → Generate → export again) would otherwise throw the
    /// reshaping away every time: "&lt;Name&gt;.Generated.cs" is the exporter's, rewritten on
    /// every export, and "&lt;Name&gt;.cs" is yours (or an AI's), never overwritten. Only
    /// <see cref="Build"/> runs; <see cref="Compare"/> is what keeps the two honest.
    /// </summary>
    public abstract class ControllerRecipe : ScriptableObject
    {
        [Tooltip("The .controller this recipe generates into.")]
        public AnimatorController targetController;

        [Tooltip("Own the whole controller: parameters and layers become exactly what the "
            + "code declares. Off: only the declared layers are replaced, by name.")]
        public bool exclusive;

        [SerializeField, HideInInspector] List<string> ownedLayers = new List<string>();

        /// <summary>Layer names created by the last Generate.</summary>
        public IReadOnlyList<string> OwnedLayers => ownedLayers;

        /// <summary>Declare the controller here. Runs both for Generate and for Verify — and
        /// in an exported recipe this is the half DaerD never overwrites, so reshaping it
        /// (loops, helpers, your own names, an AI pass) survives the next export.</summary>
        protected abstract void Build(ControllerBuilder c);

        /// <summary>
        /// The exporter's own declaration, as written to "&lt;Name&gt;.Generated.cs". Nothing
        /// runs it: it is the reference a fresh export arrives in — read its diff to see what
        /// changed in the controller, and hold <see cref="Build"/> against it with
        /// <see cref="Compare"/>. A hand-written recipe has no exported half, and this just
        /// mirrors <see cref="Build"/>.
        /// </summary>
        protected virtual void BuildGenerated(ControllerBuilder c) => Build(c);

        /// <summary>Whether an exported half exists — that is, whether the recipe's
        /// .Generated.cs still declares <see cref="BuildGenerated"/>.</summary>
        public bool HasGeneratedHalf
        {
            get
            {
                var method = GetType().GetMethod("BuildGenerated",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return method != null && method.DeclaringType != typeof(ControllerRecipe);
            }
        }

        internal ControllerBuilder BuildDeclaration(RecipeScript script = null) =>
            Declare(Build, script);

        /// <summary>The exported half's declaration (see <see cref="BuildGenerated"/>).</summary>
        internal ControllerBuilder GeneratedDeclaration() => Declare(BuildGenerated, null);

        ControllerBuilder Declare(Action<ControllerBuilder> body, RecipeScript script)
        {
            var builder = new ControllerBuilder { Script = script };
            script?.RegisterRoot(builder);
            body(builder);
            return builder;
        }

        /// <summary>Applies the declaration to the target controller. Returns warnings —
        /// an empty list is a clean run.</summary>
        public List<string> Generate()
        {
            var warnings = new List<string>();
            if (targetController == null)
            {
                warnings.Add(L.Tr("No target controller is assigned."));
                return warnings;
            }

            var builder = BuildDeclaration();
            warnings.AddRange(builder.Bake());

            using (new UndoScope("Generate Recipe"))
            {
                warnings.AddRange(ControllerIRBuilder.Rebuild(
                    builder.IR, targetController, exclusive));
                foreach (var op in builder.PostOps)
                    warnings.AddRange(op(targetController));
            }

            ownedLayers.Clear();
            foreach (var layer in builder.IR.layers)
                ownedLayers.Add(layer.name);
            // What a post step generated is owned too — an async sync cycle, a gadget layer
            // and the supporting layers it brings are all rebuilt by the next Generate.
            foreach (var name in builder.PostLayers)
                if (!ownedLayers.Contains(name)) ownedLayers.Add(name);

            var machines = new List<AnimatorStateMachine>();
            foreach (var name in ownedLayers)
                foreach (var live in targetController.layers)
                    if (live.name == name && live.stateMachine != null)
                        machines.Add(live.stateMachine);
            GraphFrameData.SetCodeOwned(targetController, machines, this);

            EditorUtility.SetDirty(this);
            EditorUtility.SetDirty(targetController);
            return warnings;
        }

        /// <summary>
        /// Compares what the code declares against what the controller currently contains.
        /// Empty result: no drift. Only the declared layers and parameters are compared for
        /// non-exclusive recipes; what the post steps built — <see cref="ControllerBuilder.Raw"/>
        /// blocks, async sync, gadget layers — is invisible here by nature.
        /// </summary>
        public List<string> Verify()
        {
            var report = new List<string>();
            if (targetController == null)
            {
                report.Add(L.Tr("No target controller is assigned."));
                return report;
            }

            var builder = BuildDeclaration();
            report.AddRange(builder.Bake());
            var declared = builder.IR;

            var actual = ControllerIR.Parse(targetController);
            if (!exclusive)
            {
                var layerNames = new HashSet<string>();
                foreach (var layer in declared.layers) layerNames.Add(layer.name);
                var parameterNames = new HashSet<string>();
                foreach (var parameter in declared.parameters) parameterNames.Add(parameter.name);
                actual = actual.FilterTo(layerNames, parameterNames);
            }
            else if (declared.layers.Count > 0)
            {
                // Parse normalizes the base layer's weight to 1; hold the declaration to the
                // same rule so a recipe that wrote something else doesn't self-diff forever.
                declared.layers[0].defaultWeight = 1f;
            }

            if (builder.PostOps.Count > 0)
                report.Add(L.Tr("This recipe has post steps (Raw, Async Sync, DBT gadgets); their output is not covered by Verify."));

            report.AddRange(ControllerIRDiff.Compare(declared, actual));
            return report;
        }

        /// <summary>
        /// Holds <see cref="Build"/> — your half — against <see cref="BuildGenerated"/>, the
        /// export it came from. Empty means the reshaped code still declares exactly the same
        /// controller — which is what makes reformatting safe to do by hand, or to hand to an
        /// AI: the comparison is on what the two build, not on how they read. Post steps (Raw,
        /// async sync, gadgets) sit outside it, as they sit outside Verify.
        ///
        /// The workflow this exists for: export, reshape your half, Generate, edit the
        /// controller, export again (only the generated half changes — diff it), carry the
        /// change into yours, Compare.
        /// </summary>
        public List<string> Compare()
        {
            var report = new List<string>();
            if (!HasGeneratedHalf)
            {
                report.Add(L.Tr("This recipe has no exported half to compare against."));
                return report;
            }

            var mine = BuildDeclaration();
            report.AddRange(mine.Bake());
            var exported = GeneratedDeclaration();
            report.AddRange(exported.Bake());

            report.AddRange(ControllerIRDiff.Compare(exported.IR, mine.IR));
            if (mine.PostOps.Count > 0 || exported.PostOps.Count > 0)
                report.Add(L.Tr("Post steps (Raw, Async Sync, DBT gadgets) declare nothing comparable — check those by hand."));
            return report;
        }
    }
}
