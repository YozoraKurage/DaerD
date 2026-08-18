using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.IR;

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

        /// <summary>
        /// The one line a run comes back with when the loaded code may not be the code on disk,
        /// or null when nothing suggests a mismatch.
        ///
        /// Every entry point below asks this first and touches nothing when it gets an answer.
        /// Verify and Compare included, and that is the point rather than an over-reach: holding
        /// a controller against last week's assembly produces a confident list of differences
        /// that are not there, which is worse than a refusal because it reads like a finding.
        /// </summary>
        List<string> StaleRefusal()
        {
            var staleness = RecipeFreshness.Check(this);
            return staleness == RecipeFreshness.Staleness.Fresh
                ? null
                : new List<string> { RecipeFreshness.Reason(staleness) };
        }

        /// <summary>Applies the declaration to the target controller. Returns warnings —
        /// an empty list is a clean run.</summary>
        public List<string> Generate()
        {
            var stale = StaleRefusal();
            if (stale != null) return stale;

            var warnings = new List<string>();
            if (targetController == null)
            {
                warnings.Add(L.Tr("No target controller is assigned."));
                return warnings;
            }

            var builder = BuildDeclaration();
            warnings.AddRange(builder.Bake());
            var atRisk = RecordsInDeclaredLayers(builder.IR);

            using (new UndoScope("Generate Recipe"))
            {
                warnings.AddRange(ControllerIRBuilder.Rebuild(
                    builder.IR, targetController, exclusive));
                foreach (var op in builder.PostOps)
                    warnings.AddRange(op(targetController));
            }
            ReportLostRecords(atRisk, warnings);

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
            // Self-healing: a recipe that writes into a controller belongs to it, and saying so
            // here is what adopts every .asset exported before the link existed. Generate is the
            // one moment guaranteed to happen to a recipe somebody is actually using.
            GraphFrameData.LinkRecipe(targetController, this);

            EditorUtility.SetDirty(this);
            EditorUtility.SetDirty(targetController);
            return warnings;
        }

        // ---- the records a declared layer stands to destroy -----------------------

        /// <summary>One saved gadget that lives in the machinery a declared layer is about to
        /// replace: the layer's name, the key its record is filed under, and what to call it in
        /// a sentence.</summary>
        class RecordAtRisk
        {
            public string layer;
            public string key;
            public string label;
            public bool objectGadget;
        }

        /// <summary>
        /// The saved gadgets standing in the way of a declared layer.
        ///
        /// Declaring a layer means rebuilding its state machine from scratch, and every object
        /// and DBT gadget recorded in that layer points at machinery that is about to stop
        /// existing. Most of the time a call in the same recipe puts them back — that is what an
        /// export writes when it can — but the fallback to raw states survives for the cases it
        /// cannot (an unhealthy pin, a layer reshaped by hand), and a Generate that quietly
        /// forgets a gadget while leaving its states behind is the worst possible outcome: the
        /// controller still works and DaerD no longer knows why.
        ///
        /// So the list is taken before the run and checked after it, rather than warned about up
        /// front. What survives is not knowable here — the post steps have not run yet, and they
        /// are what re-register everything the calls rebuild.
        /// </summary>
        List<RecordAtRisk> RecordsInDeclaredLayers(ControllerIR ir)
        {
            var risk = new List<RecordAtRisk>();
            var declared = new Dictionary<AnimatorStateMachine, string>();
            foreach (var layer in ir.layers)
                foreach (var live in targetController.layers)
                    if (live.name == layer.name && live.stateMachine != null)
                        declared[live.stateMachine] = layer.name;
            if (declared.Count == 0) return risk;

            foreach (var config in GraphFrameData.GetObjectGadgets(targetController))
                if (config.layer != null && declared.TryGetValue(config.layer, out var layerName))
                    risk.Add(new RecordAtRisk
                    {
                        layer = layerName,
                        key = config.parameter,
                        label = config.name,
                        objectGadget = true,
                    });
            foreach (var config in GraphFrameData.GetGadgets(targetController))
                if (config.layer != null && declared.TryGetValue(config.layer, out var layerName))
                    risk.Add(new RecordAtRisk
                    {
                        layer = layerName,
                        key = config.output,
                        label = config.output,
                    });
            return risk;
        }

        /// <summary>Names the gadgets the run really did lose, one sentence per layer. Reading
        /// the records back is what proves it: a record that is still there was put back by a
        /// call, and one that is gone is gone whatever the reason.</summary>
        void ReportLostRecords(List<RecordAtRisk> risk, List<string> warnings)
        {
            if (risk.Count == 0) return;
            var objects = new HashSet<string>();
            foreach (var config in GraphFrameData.GetObjectGadgets(targetController))
                objects.Add(config.parameter);
            var gadgets = new HashSet<string>();
            foreach (var config in GraphFrameData.GetGadgets(targetController))
                gadgets.Add(config.output);

            var order = new List<string>();
            var lost = new Dictionary<string, List<string>>();
            foreach (var entry in risk)
            {
                if ((entry.objectGadget ? objects : gadgets).Contains(entry.key)) continue;
                if (!lost.TryGetValue(entry.layer, out var names))
                {
                    lost[entry.layer] = names = new List<string>();
                    order.Add(entry.layer);
                }
                string named = "'" + entry.label + "'";
                if (!names.Contains(named)) names.Add(named);
            }
            foreach (var layer in order)
                warnings.Add(L.Tr("Layer '{0}' is declared by this recipe, so generating it rebuilt what these saved gadgets were built into and DaerD has lost their records: {1}. What they built is still in the controller as plain states — export the layer again to write them back as calls, or rebuild them from the home screen.",
                    layer, string.Join(", ", lost[layer])));
        }

        /// <summary>
        /// Compares what the code declares against what the controller currently contains.
        /// Empty result: no drift. Only the declared layers and parameters are compared for
        /// non-exclusive recipes; what the post steps built — <see cref="ControllerBuilder.Raw"/>
        /// blocks, async sync, gadget layers — is invisible here by nature.
        /// </summary>
        public List<string> Verify()
        {
            var stale = StaleRefusal();
            if (stale != null) return stale;

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
                report.Add(L.Tr("This recipe has post steps (Raw, Async Sync, DBT gadgets, object gadgets); their output is not covered by Verify."));

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
            var stale = StaleRefusal();
            if (stale != null) return stale;

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
                report.Add(L.Tr("Post steps (Raw, Async Sync, DBT gadgets, object gadgets) declare nothing comparable — check those by hand."));
            return report;
        }
    }
}
