using System.Collections.Generic;
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

        /// <summary>Declare the controller here. Runs both for Generate and for Verify.</summary>
        protected abstract void Build(ControllerBuilder c);

        internal ControllerBuilder BuildDeclaration(RecipeScript script = null)
        {
            var builder = new ControllerBuilder { Script = script };
            script?.RegisterRoot(builder);
            Build(builder);
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
            var machines = new List<AnimatorStateMachine>();
            foreach (var layer in builder.IR.layers)
            {
                ownedLayers.Add(layer.name);
                foreach (var live in targetController.layers)
                    if (live.name == layer.name && live.stateMachine != null)
                        machines.Add(live.stateMachine);
            }
            GraphFrameData.SetCodeOwned(targetController, machines, this);

            EditorUtility.SetDirty(this);
            EditorUtility.SetDirty(targetController);
            return warnings;
        }

        /// <summary>
        /// Compares what the code declares against what the controller currently contains.
        /// Empty result: no drift. Only the declared layers and parameters are compared for
        /// non-exclusive recipes; whatever <see cref="ControllerBuilder.Raw"/> blocks did is
        /// invisible here by nature.
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
                report.Add(L.Tr("This recipe has Raw steps; their output is not covered by Verify."));

            report.AddRange(ControllerIRDiff.Compare(declared, actual));
            return report;
        }
    }
}
