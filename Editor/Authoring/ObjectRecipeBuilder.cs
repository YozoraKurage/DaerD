using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Bridge;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Object gadgets from a recipe: the toggles the home screen builds against the gimmick
    /// prefab this controller is pinned to, declared in code and rebuilt on every Generate.
    ///
    ///   c.Objects()
    ///    .Toggle("Hat").Shows("Head/Hat").Enables("Head/Hat/Light", "Light")
    ///    .Toggle("Cape").AsTree().Hides("Body/Cape").DefaultOn();
    ///
    /// <para>THE RECIPE REBUILDS THE CONTROLLER AND ONLY VERIFIES THE PREFAB.</para>
    /// A gimmick prefab is a first-class asset that a person opens, edits and ships; it is not
    /// something a build script should be quietly rewriting behind them (ADR 0047). So these
    /// calls generate layers, clips, trees and parameters — the controller side, which is what a
    /// recipe is for — and treat the prefab as a statement to be checked: every target is a path
    /// looked up inside the pinned prefab, and a path that is not there stops the run. Nothing is
    /// created, moved or renamed in the prefab, ever.
    ///
    /// <para>WHY IT STOPS INSTEAD OF BUILDING WHAT IT CAN.</para>
    /// Half a gimmick is worse than none: a toggle built without the object it was supposed to
    /// hide is a switch that silently does nothing, and it looks exactly like a working one. So
    /// an unhealthy pin builds nothing at all, and a run with missing targets lists EVERY one of
    /// them before it stops. That list is the point — re-pin a controller at a different prefab,
    /// run the recipe, and what comes back is the port's to-do list rather than the first line
    /// of it.
    ///
    /// <para>WHAT IT DOES NOT SWEEP.</para>
    /// A toggle taken out of the recipe is not removed from the controller. Records are keyed by
    /// parameter and each call regenerates its own, but a record no call names is left alone —
    /// the same controller's gadgets may also have been made in the wizard, and a post step that
    /// swept "everything I did not declare" would delete somebody's work for the crime of not
    /// being in this file. Delete it from the home screen.
    /// </summary>
    public sealed class ObjectRecipeBuilder
    {
        /// <summary>What the shared Direct blend tree layer is called when the recipe does not
        /// say — the name the wizard gives it, so a recipe and the wizard land in the same
        /// layer.</summary>
        internal const string DefaultLayer = "DBT";

        readonly ControllerBuilder _root;
        readonly string _layerName;
        readonly List<ObjectToggleBuilder> _toggles = new List<ObjectToggleBuilder>();

        internal ObjectRecipeBuilder(ControllerBuilder root, string layerName = null)
        {
            _root = root;
            _layerName = string.IsNullOrEmpty(layerName) ? DefaultLayer : layerName;
            root.PostOps.Add(Run);
        }

        internal ControllerBuilder Root => _root;

        /// <summary>
        /// One toggle: objects in the pinned prefab switched on and off by a parameter. The
        /// parameter defaults to the gadget's name, which is what the wizard does and what most
        /// toggles want; it is also the key the record is saved under, so two toggles in one
        /// recipe must not share one.
        /// </summary>
        public ObjectToggleBuilder Toggle(string name, string parameter = null)
        {
            var toggle = new ObjectToggleBuilder(this, name,
                string.IsNullOrEmpty(parameter) ? name : parameter);
            _toggles.Add(toggle);
            _root.Script?.Declare(toggle, name + " Toggle", this,
                parameter == null
                    ? $"Toggle({RecipeScript.S(name)})"
                    : $"Toggle({RecipeScript.S(name)}, {RecipeScript.S(parameter)})");
            return toggle;
        }

        // ---- applying -----------------------------------------------------------

        List<string> Run(AnimatorController controller)
        {
            var warnings = new List<string>();
            if (_toggles.Count == 0) return warnings;

            // The pin first, and as one sentence rather than one per toggle: every path below
            // is relative to the merge, so without it there is not one gadget to build.
            string refusal = ObjectGadgets.LinkRefusal(controller);
            var root = ObjectGadgets.Root(controller);
            if (refusal != null || root == null)
            {
                warnings.Add(L.Tr("No object gadget was built: {0}",
                    refusal ?? L.Tr("The linked Merge Animator could not be resolved.")));
                return warnings;
            }

            var resolved = new Dictionary<ObjectToggleBuilder, List<GameObject>>();
            var missingTargets = new List<string>();
            var missingClips = new List<string>();
            foreach (var toggle in _toggles)
            {
                var objects = new List<GameObject>();
                foreach (var target in toggle.Targets)
                {
                    var found = Resolve(root, target.path);
                    if (found == null)
                        missingTargets.Add(L.Tr("'{0}' (needed by '{1}')", target.path, toggle.Name));
                    objects.Add(found);
                }
                resolved[toggle] = objects;
                foreach (var path in toggle.ClipPaths())
                    if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) == null)
                        missingClips.Add(L.Tr("'{0}' (needed by '{1}')", path, toggle.Name));
            }

            // Everything that is missing, not the first thing: this list is what a port to
            // another prefab is worked through, and one name at a time would make that a
            // rebuild per object.
            if (missingTargets.Count > 0)
                warnings.Add(L.Tr("No object gadget was built: the linked prefab '{0}' has no object at {1}.",
                    PrefabName(controller), string.Join(", ", missingTargets)));
            if (missingClips.Count > 0)
                warnings.Add(L.Tr("No object gadget was built: this project has no animation clip at {0}.",
                    string.Join(", ", missingClips)));
            if (missingTargets.Count > 0 || missingClips.Count > 0) return warnings;

            Undo.RegisterCompleteObjectUndo(controller, "Generate Recipe");
            AnimatorStateMachine host = null;
            foreach (var toggle in _toggles)
            {
                var replaces = Record(controller, toggle.Parameter);
                var config = toggle.ToConfig(resolved[toggle]);
                // Tree-wired toggles share one layer: the one an earlier toggle in this run
                // landed in, or the one this record was in last time, or — for a controller that
                // has neither, which is what a port to another gimmick is — a layer already
                // standing under the recipe's name. Null asks for a new one.
                if (config.mode == (int)ToggleBuilder.Mode.DirectBlendTree)
                    config.layer = host != null ? host
                        : replaces != null && replaces.layer != null ? replaces.layer
                        : NamedHost(controller);

                string error = ObjectGadgets.Validate(controller, config, replaces);
                if (error != null)
                {
                    warnings.Add(L.Tr("Object gadget '{0}': {1}", toggle.Name, error));
                    continue;
                }
                // One flush for the whole run: each toggle can mint two clips, and committing
                // per gadget reimports the controller once per call (ADR 0011).
                if (!ObjectGadgets.Apply(controller, config, replaces, commitSubAssets: false,
                    newLayerName: _layerName))
                    continue;
                if (config.mode == (int)ToggleBuilder.Mode.DirectBlendTree) host = config.layer;
                Claim(controller, config.layer);
            }
            DbtBuilder.CommitSubAssets(controller);
            return warnings;
        }

        /// <summary>The object a derived path names inside the pinned prefab, or null. The empty
        /// path is the merge's own object — a toggle that hides the object the merge sits on is
        /// a normal thing to write, and "" is how a curve addresses it.</summary>
        static GameObject Resolve(Transform root, string path)
        {
            if (root == null || path == null) return null;
            if (path.Length == 0) return root.gameObject;
            var found = root.Find(path);
            return found != null ? found.gameObject : null;
        }

        static string PrefabName(AnimatorController controller)
        {
            var prefab = PrefabLinks.Status(controller).prefab;
            return prefab != null ? prefab.name : "?";
        }

        /// <summary>
        /// The layer standing under the recipe's name that a tree-wired toggle can join, or null.
        ///
        /// It is what makes a shared Direct tree layer survive a replay. The layer the export
        /// declares as its raw remainder, and the one the DBT gadget step builds, are both known
        /// only by NAME on a controller that carries no record of this toggle — and without this
        /// the toggle would create a second layer beside them under a numbered name, splitting a
        /// gimmick in two the first time it is ported.
        ///
        /// Only a layer that can host a Direct child qualifies, so a name collision with somebody
        /// else's ordinary layer falls through to "make a new one" rather than joining states
        /// that have nothing to do with a blend tree.
        /// </summary>
        AnimatorStateMachine NamedHost(AnimatorController controller)
        {
            foreach (var layer in controller.layers)
                if (layer.name == _layerName && DbtBuilder.CanHostGadget(layer))
                    return layer.stateMachine;
            return null;
        }

        static GraphFrameData.ObjectGadgetConfig Record(AnimatorController controller,
            string parameter)
        {
            foreach (var config in GraphFrameData.GetObjectGadgets(controller))
                if (config.parameter == parameter) return config;
            return null;
        }

        /// <summary>Marks the layer a toggle landed in as the recipe's, like the gadget and sync
        /// post steps do with theirs: the next Generate rebuilds it, and the layer list says so
        /// rather than letting a hand edit there look permanent.</summary>
        void Claim(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (machine == null) return;
            foreach (var layer in controller.layers)
                if (layer.stateMachine == machine && !_root.PostLayers.Contains(layer.name))
                    _root.PostLayers.Add(layer.name);
        }
    }
}
