using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
using MaPathMode = nadena.dev.modular_avatar.core.MergeAnimatorPathMode;
#endif
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Engine
{
    /// <summary>
    /// The object gadget family: the generated things whose subject is an OBJECT in the gimmick
    /// prefab this controller is pinned to, rather than a parameter. One kind so far — a toggle
    /// — and the parts the family shares are deliberately the ones that were hard to get right:
    /// how a target is pointed at, what DaerD may sweep, and where the clips end up. What each
    /// kind BUILDS is its own concrete builder (<see cref="ToggleBuilder"/> for this one).
    ///
    /// <para>THE PIN IS THE FRAME OF REFERENCE.</para>
    /// An animation curve addresses an object by a path, and a path only means something
    /// relative to something. For a gimmick that something is the MA Merge Animator the
    /// controller is pinned to (<see cref="PrefabLinks"/>): Modular Avatar plays the merged
    /// controller with its paths taken from the merge's own object. So every target is saved as
    /// a REFERENCE into that prefab and the path is derived from the pair when a curve needs one
    /// (ADR 0044) — a saved path would be wrong the first time somebody renames an object, and
    /// wrong silently. Nothing is applied without a healthy pin, and every reason for refusing
    /// says which object it is talking about.
    ///
    /// <para>OWNERSHIP.</para>
    /// On the animator side the record holds direct references to what it built — the layer, the
    /// tree, the clips — and sweeping means removing exactly those (ADR 0045). The parameter is
    /// the one thing removed by name, and only when <c>createdParameter</c> says this gadget is
    /// what put it there. Nothing is ever found by name or by shape and deleted for looking like
    /// something DaerD would have made.
    ///
    /// A clip the user supplied is the one thing a record points at without owning (ADR 0046).
    /// DaerD writes its own rows into it, takes those same rows back out when the gadget is
    /// regenerated or swept — the ledger in <c>GraphFrameData.ClipOutput</c> says which they
    /// are — and never deletes the file. A curve there that the ledger does not claim belongs to
    /// somebody else, and finding one is a named refusal rather than an overwrite.
    ///
    /// <para>THE GUARD.</para>
    /// Reading a merge's path mode needs Modular Avatar's type, so that one question sits behind
    /// the same <c>DAERD_MA &amp;&amp; DAERD_VRC</c> pair <c>ParameterStore.MaStore</c> and
    /// <see cref="PrefabLinks"/> use. Without those the pin can never be healthy, so this whole
    /// class refuses politely and by name rather than compiling to something half-alive.
    /// </summary>
    static class ObjectGadgets
    {
        public enum Kind
        {
            Toggle,
            // Appended, and to be appended to: a saved gadget records its kind as this enum's
            // number, so inserting one anywhere else would rename every gadget after it in
            // every controller already built.
        }

        // Must stay in ObjectGadgets.Kind order.
        public static readonly string[] KindLabels = { "Toggle" };

        /// <summary>How the gadget is wired, as the wizard names it. Indexed by
        /// <c>ToggleBuilder.Mode</c>, which is what the record stores as an int.</summary>
        public static readonly string[] ModeLabels = { "Layer (Bool)", "Direct Blend Tree (Float)" };

        public static string KindLabel(GraphFrameData.ObjectGadgetConfig config) =>
            config != null && config.kind >= 0 && config.kind < KindLabels.Length
                ? KindLabels[config.kind] : "?";

        public static string ModeLabel(GraphFrameData.ObjectGadgetConfig config) =>
            config != null && config.mode >= 0 && config.mode < ModeLabels.Length
                ? ModeLabels[config.mode] : "?";

        // ---- the prefab side ---------------------------------------------------

        /// <summary>
        /// The transform every path is derived against: the object carrying the merge this
        /// controller is pinned to, or null when the pin is not healthy. Resolving a reference
        /// and reading a transform — no sweep, no prefab loaded — so a repaint may call it.
        ///
        /// Without Modular Avatar the pin never reads as healthy, so this answers null there
        /// without needing a guard of its own.
        /// </summary>
        public static Transform Root(AnimatorController controller)
        {
            var status = PrefabLinks.Status(controller);
            if (!status.IsHealthy) return null;
            var merge = status.mergeAnimator as Component;
            return merge != null ? merge.transform : null;
        }

        /// <summary>
        /// Where a target sits, as the path a curve wants: relative to the merge, derived now
        /// rather than remembered (ADR 0044). The merge's own object is "" — a gadget that hides
        /// the object the merge lives on is a normal thing to build, and an empty path is what
        /// addresses it.
        ///
        /// Null when the target is outside the merge's subtree, which cannot be expressed as a
        /// relative path at all. <see cref="Validate"/> turns that into a named refusal; this
        /// stays quiet so the wizard can ask the same question while drawing a list.
        /// </summary>
        public static string PathOf(Transform root, GameObject target)
        {
            if (root == null || target == null) return null;
            var transform = target.transform;
            if (transform == root) return string.Empty;
            if (!transform.IsChildOf(root)) return null;
            return AnimationUtility.CalculateTransformPath(transform, root);
        }

        /// <summary>Every object a gadget could name: the merge's own object and everything
        /// under it, which is exactly the set that has a relative path. In hierarchy order, so
        /// a picker built from it reads like the prefab does.</summary>
        public static List<GameObject> Candidates(Transform root)
        {
            var found = new List<GameObject>();
            if (root == null) return found;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                found.Add(transform.gameObject);
            return found;
        }

        // ---- validation --------------------------------------------------------

        /// <summary>
        /// Human-readable reason this gadget can't be built, or null when it can.
        ///
        /// <paramref name="replaces"/> is the record this one is about to replace, and it buys
        /// the same two things <c>AapGadgets.Request.replaces</c> does: what that gadget already
        /// owns does not read as taken, and everything it built is swept just before this one is
        /// built. It is passed rather than looked up by name on purpose — a gadget being renamed
        /// still replaces the record it came from, and a NEW gadget that happens to pick a name
        /// somebody else's gadget uses must be refused rather than quietly demolish it.
        /// </summary>
        public static string Validate(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config,
            GraphFrameData.ObjectGadgetConfig replaces = null)
        {
            if (controller == null) return L.Tr("No controller.");
            if (config == null) return L.Tr("There is no gadget to build.");
            if (string.IsNullOrEmpty(config.name))
                return L.Tr("The gadget needs a name.");
            if (string.IsNullOrEmpty(config.parameter))
                return L.Tr("The parameter needs a name.");

            string refusal = LinkRefusal(controller);
            if (refusal != null) return refusal;

            var root = Root(controller);
            if (root == null)
                return L.Tr("The linked Merge Animator could not be resolved.");

            refusal = TargetRefusal(root, config);
            if (refusal != null) return refusal;

            foreach (var existing in GraphFrameData.GetObjectGadgets(controller))
                if (existing != replaces && existing.parameter == config.parameter)
                    return L.Tr("Another object gadget ('{0}') already drives the parameter '{1}'.",
                        existing.name, config.parameter);

            refusal = ParameterRefusal(controller, config, replaces);
            if (refusal != null) return refusal;

            refusal = ClipRefusal(controller, config, replaces, root);
            if (refusal != null) return refusal;

            // The same question every builder that adds a Direct child asks first, and it is
            // about the layer rather than about the toggle: a layer already carrying states
            // that are not Direct trees would be joined rather than shared.
            return (ToggleBuilder.Mode)config.mode == ToggleBuilder.Mode.DirectBlendTree
                ? DbtBuilder.ValidateLayerChoice(controller, LayerIndexOf(controller, config.layer), "DBT")
                : null;
        }

        /// <summary>
        /// The pin, as the reason nothing can be built from it — or null when it is fine. Every
        /// state names what it is talking about and none of them offers to repair itself: which
        /// of two controllers a re-pointed merge now belongs to is not DaerD's call (the rule the
        /// whole prefab link keeps).
        ///
        /// Public because it is the first thing a surface has to say. The home screen's card and
        /// the wizard both open with it and then offer nothing, rather than showing controls that
        /// would refuse one by one on being pressed.
        /// </summary>
        public static string LinkRefusal(AnimatorController controller)
        {
            var status = PrefabLinks.Status(controller);
            switch (status.state)
            {
                case PrefabLinkState.Healthy:
                    break;
                case PrefabLinkState.PrefabMissing:
                    return L.Tr("The prefab this controller is linked to cannot be found, so there is nothing to animate inside it.");
                case PrefabLinkState.MergeMissing:
                    return L.Tr("The MA Merge Animator this controller was linked to is no longer inside '{0}'.",
                        status.prefab.name);
                case PrefabLinkState.Diverged:
                    return L.Tr("The MA Merge Animator in '{0}' now merges something else, so its objects are no longer this controller's to animate.",
                        status.prefab.name);
                case PrefabLinkState.Unverifiable:
                    return L.Tr("Modular Avatar is not installed, so the prefab link cannot be read and no object gadget can be built from it.");
                default:
                    return L.Tr("This controller is not linked to a gimmick prefab yet — an object gadget animates the objects inside one.");
            }

#if DAERD_MA && DAERD_VRC
            var merge = status.mergeAnimator as MaMergeAnimator;
            if (merge == null)
                return L.Tr("The linked Merge Animator could not be resolved.");
            // Absolute paths address the AVATAR's hierarchy, and a gimmick prefab does not know
            // where in an avatar it will be dropped. There is no approximation to offer here, so
            // the limit is stated rather than worked around (ADR 0008 / 0023).
            if (merge.pathMode != MaPathMode.Relative)
                return L.Tr("The Merge Animator in '{0}' is set to Absolute paths. DaerD only builds object gadgets for Relative merges — with Absolute the prefab alone cannot say where the objects will end up.",
                    status.prefab.name);
#endif
            return null;
        }

        /// <summary>The targets, as the first reason one of them cannot be animated.</summary>
        static string TargetRefusal(Transform root, GraphFrameData.ObjectGadgetConfig config)
        {
            if (config.targets == null || config.targets.Count == 0)
                return L.Tr("Add at least one target object.");

            var seen = new HashSet<GameObject>();
            foreach (var record in config.targets)
            {
                if (record == null || record.target == null)
                    return L.Tr("An object this gadget animates is gone from the prefab. DaerD does not pick a replacement for you — point the gadget at an object again, or delete it.");
                if (!seen.Add(record.target))
                    return L.Tr("Target '{0}' is listed more than once.", record.target.name);
                if (PathOf(root, record.target) == null)
                    return L.Tr("'{0}' is not inside '{1}', which the merge's paths are relative to.",
                        record.target.name, root.name);
                if (!record.toggleActive && (record.bindings == null || record.bindings.Count == 0))
                    return L.Tr("Target '{0}' has nothing to animate — enable Object or add a component binding.",
                        record.target.name);
                if (record.bindings == null) continue;
                foreach (var binding in record.bindings)
                {
                    if (binding == null || string.IsNullOrEmpty(binding.property)
                        || string.IsNullOrEmpty(binding.typeName))
                        return L.Tr("Target '{0}' has an invalid component binding.", record.target.name);
                    if (ToggleBuilder.FindComponentType(binding.typeName) == null)
                        return L.Tr("Target '{0}' animates a '{1}', and this project has no component by that name.",
                            record.target.name, binding.typeName);
                }
            }
            return null;
        }

        /// <summary>The parameter, as the wiring's own rule: a Bool drives the two-state layer,
        /// a Float blends the tree. An existing parameter of the right type is reused rather
        /// than refused, and the one being regenerated does not collide with itself.</summary>
        static string ParameterRefusal(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config, GraphFrameData.ObjectGadgetConfig replaces)
        {
            if (replaces != null && replaces.parameter == config.parameter) return null;
            var existing = DbtBuilder.FindParameter(controller, config.parameter);
            if (existing == null) return null;
            var mode = (ToggleBuilder.Mode)config.mode;
            if (mode == ToggleBuilder.Mode.Layer
                && existing.type != AnimatorControllerParameterType.Bool)
                return L.Tr("Parameter '{0}' exists but is not a Bool.", config.parameter);
            if (mode == ToggleBuilder.Mode.DirectBlendTree
                && existing.type != AnimatorControllerParameterType.Float)
                return L.Tr("Parameter '{0}' exists but is not a Float.", config.parameter);
            return null;
        }

        // ---- the clips somebody else owns --------------------------------------

        /// <summary>Whether this side is a clip the user handed over rather than one DaerD is
        /// about to mint. A slot marked user-provided with nothing in it is not a clip at all —
        /// it means the same as an empty slot, which is "generate one".</summary>
        static bool Provided(GraphFrameData.ClipOutput output) =>
            output != null && output.userProvided && output.clip != null;

        /// <summary>
        /// A supplied clip as the reason a row cannot be written into it, or null.
        ///
        /// DaerD writes its own rows into somebody's clip and leaves everything else alone
        /// (ADR 0046), and the whole contract rests on being able to tell "mine" from "theirs".
        /// The ledger of the last generate is what says which is which: a curve at one of this
        /// gadget's bindings that the ledger does not claim was put there by someone — the
        /// person, or another gadget — and taking it over silently would destroy an edit that
        /// nothing recorded. So it is refused by name, with the clip and the row in the
        /// sentence, and the person decides.
        ///
        /// Two gadgets sharing one clip is not a conflict and is not meant to be: each owns the
        /// rows it wrote, and only the same ROW being claimed twice is refused.
        /// </summary>
        static string ClipRefusal(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config,
            GraphFrameData.ObjectGadgetConfig replaces, Transform root)
        {
            bool on = Provided(config.onClip), off = Provided(config.offClip);
            if (!on && !off) return null;
            // Both sides key the same bindings with different values, so one clip holding both
            // would end up holding whichever side was written last — a toggle that never moves.
            if (on && off && config.onClip.clip == config.offClip.clip)
                return L.Tr("The ON and OFF sides are the same clip ('{0}'). Each side writes the same rows with different values, so one clip cannot hold both.",
                    config.onClip.clip.name);

            var rows = ToggleBuilder.Rows(PlanFor(controller, config, root));
            return Conflict(config.onClip, replaces, rows) ?? Conflict(config.offClip, replaces, rows);
        }

        static string Conflict(GraphFrameData.ClipOutput output,
            GraphFrameData.ObjectGadgetConfig replaces, List<ToggleBuilder.Row> rows)
        {
            if (!Provided(output)) return null;
            var mine = Booked(replaces, output.clip);
            var present = new HashSet<string>();
            foreach (var binding in AnimationUtility.GetCurveBindings(output.clip))
                present.Add(Key(binding.path, binding.type != null ? binding.type.Name : null,
                    binding.propertyName));

            foreach (var row in rows)
            {
                string key = Key(row.binding.path,
                    row.binding.type != null ? row.binding.type.Name : null,
                    row.binding.propertyName);
                if (!present.Contains(key) || mine.Contains(key)) continue;
                return L.Tr("'{0}' already animates '{1}' ({2}), and this gadget did not write that curve. DaerD does not take over rows in a clip you supplied — remove the curve, or point this side at another clip.",
                    output.clip.name,
                    row.binding.path.Length > 0 ? row.binding.path : L.Tr("(the merge's own object)"),
                    (row.binding.type != null ? row.binding.type.Name : "?") + "." + row.binding.propertyName);
            }
            return null;
        }

        /// <summary>The rows the record being replaced says IT wrote into this clip. Read from
        /// whichever of its two sides points at the same clip — a person who swaps the ON and
        /// OFF slots has moved their own rows around, not collided with somebody.</summary>
        static HashSet<string> Booked(GraphFrameData.ObjectGadgetConfig replaces, AnimationClip clip)
        {
            var booked = new HashSet<string>();
            if (replaces == null || clip == null) return booked;
            foreach (var output in new[] { replaces.onClip, replaces.offClip })
            {
                if (output == null || output.clip != clip || output.written == null) continue;
                foreach (var row in output.written)
                    if (row != null) booked.Add(Key(row.path, row.typeName, row.property));
            }
            return booked;
        }

        /// <summary>A curve's identity as the triple that names it. Rows are compared as text
        /// because that is what a record can hold: a System.Type does not serialize, so the
        /// ledger keeps the short name and the comparison has to meet it there.</summary>
        static string Key(string path, string typeName, string property) =>
            (path ?? string.Empty) + "|" + (typeName ?? string.Empty) + "|" + (property ?? string.Empty);

        /// <summary>
        /// Something worth saying that is not a refusal, or null. There is one: a gadget asked to
        /// declare its parameter when the controller has no store to declare it in. Refusing
        /// would be wrong — the gadget works perfectly well, the parameter simply reaches no
        /// avatar — so it is said out loud and built anyway.
        /// </summary>
        public static string Note(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            if (config == null || !config.declare) return null;
            if (ParameterStore.Of(controller) != null) return null;
            return L.Tr("'{0}' will not be declared anywhere: this controller has no parameter store. Link the prefab's MA Parameters on the home screen, or turn Declare off.",
                config.parameter);
        }

        // ---- applying ----------------------------------------------------------

        /// <summary>
        /// Builds the gadget and records it, replacing <paramref name="replaces"/> if it is
        /// given. Returns false when validation refuses, having changed nothing.
        ///
        /// The order is the one ADR 0013 had to learn: validate, then sweep the old gadget, THEN
        /// resolve where the new one goes. A sweep can remove a layer and shift every index
        /// after it, so the host is looked up by reference afterwards rather than carried across
        /// as a number.
        ///
        /// Turning <paramref name="commitSubAssets"/> off leaves the flush to the caller, so a
        /// batch pays one reimport instead of one per gadget (ADR 0011). The flush matters more
        /// than it looks: a clip added with AddObjectToAsset stays invisible in the Project
        /// window — and to anything reading the imported artifact — until the file is saved and
        /// reimported.
        ///
        /// <paramref name="newLayerName"/> only names a Direct blend tree layer that has to be
        /// CREATED — <c>config.layer</c> decides where an existing one is joined. A recipe passes
        /// the name its call carries, so a gimmick ported to a fresh controller rebuilds its
        /// shared layer under the name it had rather than under the wizard's default.
        /// </summary>
        public static bool Apply(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config,
            GraphFrameData.ObjectGadgetConfig replaces = null, bool commitSubAssets = true,
            string newLayerName = null)
        {
            if (Validate(controller, config, replaces) != null) return false;
            var root = Root(controller);

            using (new UndoScope("Object Gadget"))
            {
                Undo.RegisterCompleteObjectUndo(controller, "Object Gadget");
                if (replaces != null) Remove(controller, replaces);

                var plan = PlanFor(controller, config, root, newLayerName);
                var onClip = Output(controller, config.onClip, plan, on: true);
                var offClip = Output(controller, config.offClip, plan, on: false);

                bool created;
                if (plan.mode == ToggleBuilder.Mode.Layer)
                {
                    config.layer = ToggleBuilder.BuildLayer(plan, onClip.clip, offClip.clip, out created);
                    config.tree = null;
                }
                else
                {
                    config.tree = ToggleBuilder.BuildDirectBlendTree(plan, onClip.clip, offClip.clip,
                        out created);
                    config.layer = DbtBuilder.HostingMachine(controller, config.tree);
                }
                config.createdParameter = created;
                config.onClip = onClip;
                config.offClip = offClip;

                GraphFrameData.SaveObjectGadget(controller, config);
                Declare(controller, config);
                EditorUtility.SetDirty(controller);
            }
            if (commitSubAssets) DbtBuilder.CommitSubAssets(controller);
            return true;
        }

        /// <summary>The record as the generators want it: paths derived from the references,
        /// component types resolved from their names, and the host layer as an index — looked up
        /// from the saved reference, so a record whose layer is gone lands on -1 and a new layer
        /// is built.</summary>
        static ToggleBuilder.Plan PlanFor(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config, Transform root, string newLayerName = null)
        {
            var plan = new ToggleBuilder.Plan
            {
                controller = controller,
                mode = (ToggleBuilder.Mode)config.mode,
                name = config.name,
                parameter = config.parameter,
                defaultOn = config.defaultOn,
                layerIndex = LayerIndexOf(controller, config.layer),
                newLayerName = string.IsNullOrEmpty(newLayerName) ? "DBT" : newLayerName,
            };
            foreach (var record in config.targets)
            {
                var target = new ToggleBuilder.Target
                {
                    path = PathOf(root, record.target),
                    activeWhenOn = record.activeWhenOn,
                    toggleActive = record.toggleActive,
                };
                if (record.bindings != null)
                    foreach (var binding in record.bindings)
                        target.bindings.Add(new ToggleBuilder.Binding
                        {
                            type = ToggleBuilder.FindComponentType(binding.typeName),
                            property = binding.property,
                            offValue = binding.offValue,
                            onValue = binding.onValue,
                        });
                plan.targets.Add(target);
            }
            return plan;
        }

        /// <summary>
        /// One side of the toggle written, and the ledger of what went into it.
        ///
        /// Which clip it is depends on the slot the caller handed over: a clip the user supplied
        /// is written in place — DaerD's rows only, the file theirs — and an empty slot mints a
        /// fresh sub-asset of the controller, which is the default and what every gadget did
        /// before ADR 0046. The rows that were written are booked either way: one bookkeeping
        /// whoever the clip belongs to, so a regenerate has one thing to undo (see
        /// <c>GraphFrameData.ClipOutput</c>).
        ///
        /// The previous rows are already gone by the time this runs — <see cref="Remove"/> took
        /// them out of the old record's clips on the way in, which is the same act that destroys
        /// a clip DaerD owned. That is what keeps a renamed target from leaving a row behind:
        /// what is erased is what the ledger says was written, not what the targets imply now.
        /// </summary>
        static GraphFrameData.ClipOutput Output(AnimatorController controller,
            GraphFrameData.ClipOutput slot, ToggleBuilder.Plan plan, bool on)
        {
            var output = new GraphFrameData.ClipOutput();
            if (Provided(slot))
            {
                output.clip = slot.clip;
                output.userProvided = true;
                Undo.RegisterCompleteObjectUndo(output.clip, "Object Gadget");
                ToggleBuilder.Write(output.clip, plan, on);
                EditorUtility.SetDirty(output.clip);
            }
            else
            {
                output.clip = ToggleBuilder.BuildClip(plan, on);
                DbtBuilder.Attach(controller, output.clip);
            }

            foreach (var row in ToggleBuilder.Rows(plan))
                output.written.Add(new GraphFrameData.WrittenRow
                {
                    path = row.binding.path,
                    typeName = row.binding.type != null ? row.binding.type.Name : null,
                    property = row.binding.propertyName,
                });
            return output;
        }

        /// <summary>
        /// Declares the gadget's parameter through the controller's parameter store — the one
        /// route (design: no second path around the store, so NDMF's effective names stay the
        /// store's business). A row somebody already wrote is left exactly as it is.
        ///
        /// Synced, because a toggle nobody else can see is not much of a toggle, and because
        /// that is what the async sync setups do with the parameters they generate. Saved is
        /// left off: whether a gimmick comes back switched on at the next login is a preference,
        /// and the Parameters panel's own columns are one click away.
        /// </summary>
        static void Declare(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            if (!config.declare) return;
            var store = ParameterStore.Of(controller);
            if (store == null || store.Target == null) return;
            if (store.Find(config.parameter) != null) return;

            var parameter = DbtBuilder.FindParameter(controller, config.parameter);
            var mapped = parameter != null ? VrcExpressionParameters.MapType(parameter.type) : null;
            if (mapped == null) return;
            store.Add(new VrcExpressionParameters.Entry
            {
                name = config.parameter,
                valueType = mapped.Value,
                defaultValue = config.defaultOn ? 1f : 0f,
                synced = true,
                saved = false,
            });
        }

        // ---- removing ----------------------------------------------------------

        /// <summary>
        /// Takes one saved gadget back out: the layer it added or the tree it hung, the clips it
        /// generated, the parameter if it was the one that created it, and the record.
        ///
        /// Everything but the parameter is reached through the record's own references, which is
        /// the whole of DaerD's claim (ADR 0045): nothing is searched for by name or by shape.
        /// A clip the user supplied is never destroyed — the file is theirs — and loses exactly
        /// the rows the ledger says this gadget wrote into it, which is the other half of the
        /// same claim (ADR 0046): what is given back is what was taken.
        ///
        /// Sub-assets are left unflushed on purpose: <see cref="Apply"/> calls this on the way to
        /// building the replacement and pays for one reimport, not two. A caller that only
        /// deletes finishes with <c>DbtBuilder.CommitSubAssets</c>.
        /// </summary>
        public static void Remove(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            if (controller == null || config == null) return;
            Undo.RegisterCompleteObjectUndo(controller, "Remove Object Gadget");

            if ((ToggleBuilder.Mode)config.mode == ToggleBuilder.Mode.Layer)
            {
                int index = LayerIndexOf(controller, config.layer);
                if (index >= 0) controller.RemoveLayer(index);
            }
            else
            {
                Detach(controller, config);
                if (config.tree != null) Undo.DestroyObjectImmediate(config.tree);
            }

            Release(config.onClip);
            Release(config.offClip);
            if (config.createdParameter) RemoveParameter(controller, config.parameter);
            GraphFrameData.RemoveObjectGadget(controller, config.parameter);
            EditorUtility.SetDirty(controller);
        }

        /// <summary>Unhooks a tree-wired gadget from the root Direct tree it hangs off. The host
        /// is found through the record's layer reference, and the child is matched by reference
        /// too — a sibling gadget that happens to be built the same way is not this one.</summary>
        static void Detach(AnimatorController controller,
            GraphFrameData.ObjectGadgetConfig config)
        {
            var root = HostRootTree(controller, config.layer);
            if (root == null || config.tree == null) return;
            var kept = new List<ChildMotion>();
            foreach (var child in root.children)
                if (child.motion != config.tree) kept.Add(child);
            if (kept.Count == root.children.Length) return;
            Undo.RegisterCompleteObjectUndo(root, "Remove Object Gadget");
            root.children = kept.ToArray();
            EditorUtility.SetDirty(root);
        }

        /// <summary>The root Direct tree of the layer a saved gadget lives in, or null when the
        /// layer (or its tree) is already gone.</summary>
        static BlendTree HostRootTree(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (machine == null) return null;
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != machine) continue;
                foreach (var child in machine.states)
                    if (child.state != null && child.state.motion is BlendTree root
                        && root.blendType == BlendTreeType.Direct)
                        return root;
            }
            return null;
        }

        /// <summary>Lets go of one side's clip. A clip DaerD minted is destroyed with the rest of
        /// the gadget; a clip the user supplied keeps its file and loses only the rows this
        /// gadget booked — deleting somebody's asset because a gadget that pointed at it went
        /// away is the one thing this must never do.</summary>
        static void Release(GraphFrameData.ClipOutput output)
        {
            if (output == null || output.clip == null) return;
            if (output.userProvided) Erase(output);
            else Undo.DestroyObjectImmediate(output.clip);
        }

        /// <summary>
        /// Takes this gadget's rows back out of a supplied clip, one booked row at a time.
        ///
        /// The ledger is walked rather than the targets, because the two disagree exactly when
        /// it matters: rename a target and the path derived now is not the path that was
        /// written, so a clip cleaned "by what the gadget animates" would keep the old row
        /// forever and the object would stay stuck wherever the stale curve left it.
        ///
        /// A row whose type this project no longer has is skipped: there is nothing to build a
        /// binding from, and guessing would be reaching for curves that are not this gadget's.
        /// </summary>
        static void Erase(GraphFrameData.ClipOutput output)
        {
            if (output == null || output.clip == null || output.written == null) return;
            Undo.RegisterCompleteObjectUndo(output.clip, "Object Gadget");
            foreach (var row in output.written)
            {
                if (row == null || string.IsNullOrEmpty(row.property)) continue;
                var type = ToggleBuilder.FindCurveType(row.typeName);
                if (type == null) continue;
                AnimationUtility.SetEditorCurve(output.clip,
                    EditorCurveBinding.FloatCurve(row.path ?? string.Empty, type, row.property),
                    null);
            }
            EditorUtility.SetDirty(output.clip);
        }

        /// <summary>Drops one parameter by name. Reached only through
        /// <c>createdParameter</c> — a parameter this gadget found rather than made is somebody
        /// else's, and leaving it is what lets two gadgets share a driving parameter.</summary>
        static void RemoveParameter(AnimatorController controller, string name)
        {
            var kept = new List<AnimatorControllerParameter>();
            foreach (var parameter in controller.parameters)
                if (parameter.name != name) kept.Add(parameter);
            if (kept.Count != controller.parameters.Length)
                controller.parameters = kept.ToArray();
        }

        static int LayerIndexOf(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (controller == null || machine == null) return -1;
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].stateMachine == machine) return i;
            return -1;
        }
    }
}
