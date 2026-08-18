using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Authoring
{
    /// <summary>
    /// Converts a controller (or a subset of its layers) into recipe source code. The
    /// exporter never writes C# by hand: it drives a real <see cref="ControllerBuilder"/>
    /// with a <see cref="RecipeScript"/> recorder attached, so the emitted text is the exact
    /// call sequence whose result can be diffed against the original — that replayed builder
    /// comes back in the result for the tests to verify. Assets become [SerializeField]
    /// fields (pre-assigned on the generated .asset), never GUIDs in code.
    ///
    /// Two files come out, halves of one partial class: the generated one, rewritten whole on
    /// every export, and a hand half written only when it doesn't exist yet. The split is what
    /// makes the round trip survivable — export, reshape the code, Generate, export again —
    /// since the re-export lands next to the reshaped half instead of on top of it.
    /// </summary>
    static class RecipeExporter
    {
        public class Result
        {
            /// <summary>The generated half ("&lt;Name&gt;.Generated.cs") — always rewritten.</summary>
            public string code;
            /// <summary>The hand half ("&lt;Name&gt;.cs") — only written when it doesn't exist yet.</summary>
            public string handHalf;
            public string className;
            public readonly List<FieldRef> fields = new List<FieldRef>();
            public readonly List<string> warnings = new List<string>();
            /// <summary>The builder the recording run drove — its IR is what the code builds.</summary>
            internal ControllerBuilder replayed;
        }

        public class FieldRef
        {
            public string fieldName;
            public string fieldType;
            public Object asset;
        }

        /// <summary>
        /// <paramref name="layerNames"/> null exports the whole controller (an exclusive
        /// recipe); a subset exports those layers plus only the parameters they reference.
        /// </summary>
        public static Result Export(AnimatorController controller, ICollection<string> layerNames,
            string className, string namespaceName)
        {
            var result = new Result { className = className };
            if (controller == null) return result;

            var full = ControllerIR.Parse(controller);
            var ir = full;
            if (layerNames != null)
            {
                ir = full.FilterTo(layerNames, ReferencedParameters(controller, layerNames));
                // Synced indices refer to the FULL layer list; remap them into the subset
                // (or to -1, which the driver reports as an unexportable sync source).
                foreach (var layer in ir.layers)
                {
                    if (layer.syncedLayerIndex < 0) continue;
                    string sourceName = layer.syncedLayerIndex < full.layers.Count
                        ? full.layers[layer.syncedLayerIndex].name : null;
                    layer.syncedLayerIndex = -1;
                    for (int i = 0; i < ir.layers.Count; i++)
                        if (ir.layers[i].name == sourceName && ir.layers[i].machine != null)
                            layer.syncedLayerIndex = i;
                }
            }

            var claims = new ChildClaims();
            var gadgets = PlanGadgets(controller, layerNames, claims);
            // The multiplexed targets need no help reaching ReferencedParameters above: the
            // send ring names them in its Parameter Drivers, and CollectParameterNames reads
            // those, so a partial export of the sync layer alone still declares them.
            var asyncSyncs = PlanAsyncSyncs(controller, layerNames, result.warnings);
            var objects = PlanObjects(controller, layerNames, result.warnings, claims);
            // Both planners have had their say by now, which is what the order warning and the
            // strip below both need: whether a layer still has children nobody claimed is only
            // known once every planner has claimed its own.
            WarnAboutRebuildOrder(controller, layerNames, gadgets, objects, claims, result.warnings);
            claims.Strip(ir);

            var script = new RecipeScript();
            var builder = new ControllerBuilder { Script = script };
            script.RegisterRoot(builder);
            result.replayed = builder;

            RegisterAssets(ir, script, result, gadgets, asyncSyncs, objects, claims);
            new RecipeDriver(builder, ir, result.warnings, gadgets, asyncSyncs, objects, claims).Run();
            result.warnings.AddRange(builder.Bake());

            result.code = ComposeGenerated(script, className, namespaceName, controller, result);
            result.handHalf = ComposeHandHalf(className, namespaceName);
            return result;
        }

        /// <summary>Only the parameters the exported layers actually use travel with a
        /// partial export.</summary>
        static HashSet<string> ReferencedParameters(AnimatorController controller,
            ICollection<string> layerNames)
        {
            var referenced = new HashSet<string>();
            foreach (var layer in controller.layers)
                if (layerNames.Contains(layer.name) && layer.stateMachine != null)
                    referenced.UnionWith(LayerClipboard.CollectParameterNames(layer.stateMachine));
            return referenced;
        }

        // ---- claiming the children of a shared tree ----------------------------

        /// <summary>
        /// Which children of a Direct-blend-tree layer have a call to stand for them.
        ///
        /// <para>WHY THE CLAIM IS PER CHILD AND NOT PER LAYER.</para>
        /// One Direct tree layer hosts whatever wants a per-frame slot: AAP gadgets, tree-wired
        /// object toggles, and whatever somebody hung there by hand. Asking "does EVERY child of
        /// this layer have a call?" made one hand-built child disqualify the whole layer, and a
        /// disqualified layer is exported as raw states — which puts it in the recipe's
        /// DECLARATION. The next Generate then rebuilds that machinery from scratch, the saved
        /// records point at a state machine and a tree that no longer exist, and they are pruned:
        /// the toggles keep working as states and DaerD forgets it ever made them.
        ///
        /// So each planner claims only the children it can write back, and the layer is declared
        /// as a raw tree holding only what nobody claimed. Nothing claimed at all means the layer
        /// is declared exactly as before; everything claimed means it is not declared at all and
        /// the calls rebuild it whole, which is what already happened.
        ///
        /// <para>WHAT IT COSTS.</para>
        /// A split layer loses its child ORDER: the declared remainder is built first and each
        /// post step appends its own children after it. Direct children sum, so the order only
        /// matters between two children writing the same parameter — which is a chain, and a
        /// chain lives entirely inside one planner's claim, where the order is kept.
        ///
        /// Children are identified by index. The IR holds a copy of the tree, parsed child by
        /// child from the live one, so position is the one thing the two are guaranteed to agree
        /// on — a reference match is meaningless across the copy, and a name match would be a
        /// guess.
        /// </summary>
        internal class ChildClaims
        {
            readonly Dictionary<string, HashSet<int>> _claimed = new Dictionary<string, HashSet<int>>();
            readonly Dictionary<string, int> _total = new Dictionary<string, int>();

            /// <summary>Records that the call for one planner rebuilds child
            /// <paramref name="index"/> of this layer's root tree.</summary>
            public void Claim(string layerName, int index, int childCount)
            {
                if (!_claimed.TryGetValue(layerName, out var indices))
                    _claimed[layerName] = indices = new HashSet<int>();
                indices.Add(index);
                _total[layerName] = childCount;
            }

            /// <summary>Whether this layer still holds children no call accounts for — the
            /// question that decides whether it is declared as well as called.</summary>
            public bool HasLeftovers(string layerName) =>
                layerName != null && _claimed.TryGetValue(layerName, out var indices)
                && _total.TryGetValue(layerName, out int total) && indices.Count < total;

            /// <summary>
            /// Takes the claimed children out of the IR, so what is left to declare is the
            /// remainder and nothing else. A layer whose children are all claimed keeps an empty
            /// tree, which the driver never emits — its calls are the whole of it.
            ///
            /// A tree whose child count no longer matches what was surveyed is left alone: the
            /// indices would be pointing at other children, and declaring one child too many is
            /// recoverable where deleting the wrong one is not.
            /// </summary>
            public void Strip(ControllerIR ir)
            {
                foreach (var layer in ir.layers)
                {
                    if (!_claimed.TryGetValue(layer.name, out var indices)) continue;
                    var tree = RootTree(layer);
                    if (tree == null || tree.children.Count != _total[layer.name]) continue;
                    for (int i = tree.children.Count - 1; i >= 0; i--)
                        if (indices.Contains(i)) tree.children.RemoveAt(i);
                }
            }

            /// <summary>IR-side twin of <see cref="GadgetRootTree"/>: the root Direct tree of a
            /// layer shaped like one state playing it, or null.</summary>
            static ControllerIR.Tree RootTree(ControllerIR.Layer layer)
            {
                var machine = layer.machine;
                if (machine == null || machine.machines.Count > 0 || machine.states.Count != 1)
                    return null;
                var tree = machine.states[0].tree;
                return tree != null && tree.type == BlendTreeType.Direct ? tree : null;
            }
        }

        /// <summary>
        /// The post steps append their layers at the end of the controller, so an ordinary layer
        /// that sat after one of them changes place. A layer the export ALSO declares is exempt:
        /// the declaration rebuilds it where it was and the post step only adds children to it.
        /// </summary>
        static void WarnAboutRebuildOrder(AnimatorController controller,
            ICollection<string> layerNames, GadgetPlan gadgets, ObjectPlan objects,
            ChildClaims claims, List<string> warnings)
        {
            var layers = controller.layers;
            var movedGadgets = RebuiltWhole(gadgets.layers.Keys, claims);
            if (movedGadgets.Count > 0
                && FollowedByOrdinaryLayers(layers, layerNames, movedGadgets, gadgets.supporting))
                warnings.Add(L.Tr("Gadget layers are regenerated at the end of the controller; the layer order will differ from the original."));

            var movedObjects = RebuiltWhole(objects.layers.Keys, claims);
            if (movedObjects.Count > 0
                && FollowedByOrdinaryLayers(layers, layerNames, movedObjects, null))
                warnings.Add(L.Tr("Object gadget layers are regenerated at the end of the controller; the layer order will differ from the original."));
        }

        /// <summary>The layer names a post step rebuilds outright. A layer with a raw remainder
        /// is declared as well as called, and a declared layer keeps its index.</summary>
        static List<string> RebuiltWhole(ICollection<string> names, ChildClaims claims)
        {
            var whole = new List<string>();
            foreach (var name in names)
                if (!claims.HasLeftovers(name)) whole.Add(name);
            return whole;
        }

        // ---- gadget layers -----------------------------------------------------

        /// <summary>
        /// The layers a controller can have written back as <c>c.Gadgets(…)</c> calls instead
        /// of as the tree they expanded into, plus what that implies for the rest of the export.
        /// </summary>
        internal class GadgetPlan
        {
            /// <summary>Layer name → its gadgets, in the order the root tree holds them, which
            /// is the order they were built in and the order they have to be rebuilt in.</summary>
            public readonly Dictionary<string, List<AapGadgets.Request>> layers =
                new Dictionary<string, List<AapGadgets.Request>>();

            /// <summary>Layers a covered gadget brings along and regenerates by itself
            /// (FrameTime's clock). Exporting their states as well would only add a second
            /// copy under a numbered name.</summary>
            public readonly HashSet<string> supporting = new HashSet<string>();

            /// <summary>Whether a covered gadget owns this parameter — the output, or the
            /// namespace under it. Its call recreates them, so declaring them up top is noise
            /// the next Generate overwrites anyway. Shared machinery (a smoothing amount, the
            /// constant One) lives outside that namespace and stays declared.</summary>
            public bool Owns(string parameter)
            {
                foreach (var pair in layers)
                    foreach (var request in pair.Value)
                        if (!string.IsNullOrEmpty(request.output)
                            && (parameter == request.output
                                || parameter.StartsWith(request.output + "/",
                                    System.StringComparison.Ordinal)))
                            return true;
                return false;
            }
        }

        /// <summary>
        /// Works out which layers qualify. The check runs on the live controller rather than on
        /// the IR: the IR holds copies of the trees, and the saved configs point at the real
        /// ones, so a reference match is only meaningful here.
        ///
        /// The gadgets claim the children of the root Direct tree that have a config to stand for
        /// them, and only those (see <see cref="ChildClaims"/>): a child added by hand, or one an
        /// object toggle owns, has no gadget call that would rebuild it and stays in the raw tree
        /// the layer is also declared as.
        /// </summary>
        static GadgetPlan PlanGadgets(AnimatorController controller, ICollection<string> layerNames,
            ChildClaims claims)
        {
            var plan = new GadgetPlan();
            var configs = GraphFrameData.GetGadgets(controller);
            if (configs.Count == 0) return plan;

            foreach (var layer in controller.layers)
            {
                if (layerNames != null && !layerNames.Contains(layer.name)) continue;
                var root = GadgetRootTree(layer);
                if (root == null) continue;

                var covered = new List<AapGadgets.Request>();
                for (int i = 0; i < root.children.Length; i++)
                {
                    var config = FindGadget(configs, layer.stateMachine, root.children[i].motion);
                    if (config == null) continue;
                    covered.Add(AapGadgets.ToRequest(config, controller));
                    claims.Claim(layer.name, i, root.children.Length);
                }
                if (covered.Count == 0) continue;

                plan.layers[layer.name] = covered;
                foreach (var request in covered)
                    foreach (var name in AapGadgets.SupportingLayerNames(request))
                        plan.supporting.Add(name);
            }
            return plan;
        }

        /// <summary>The root Direct tree of a gadget-shaped layer — one state, playing a Direct
        /// blend tree, and nothing else in the machine — or null. Live-side twin of the IR check
        /// the driver makes before it points at the gadget API.</summary>
        static BlendTree GadgetRootTree(AnimatorControllerLayer layer)
        {
            var machine = layer.stateMachine;
            if (machine == null || machine.stateMachines.Length > 0 || machine.states.Length != 1)
                return null;
            var state = machine.states[0].state;
            return state != null && state.motion is BlendTree root
                && root.blendType == BlendTreeType.Direct ? root : null;
        }

        static GraphFrameData.AapGadgetConfig FindGadget(
            List<GraphFrameData.AapGadgetConfig> configs, AnimatorStateMachine machine, Motion child)
        {
            foreach (var config in configs)
                if (config.layer == machine && config.tree == child)
                    return config;
            return null;
        }

        /// <summary>True when an ordinary layer sits after one that a post step rebuilds —
        /// the post step appends its layer at the end, so the original order is not kept.
        /// Shared by both post steps; <paramref name="brought"/> is null for the one that
        /// brings no extra layers along.</summary>
        static bool FollowedByOrdinaryLayers(AnimatorControllerLayer[] layers,
            ICollection<string> layerNames, ICollection<string> rebuilt, ICollection<string> brought)
        {
            bool seenRebuilt = false;
            foreach (var layer in layers)
            {
                if (layerNames != null && !layerNames.Contains(layer.name)) continue;
                if (rebuilt.Contains(layer.name) || (brought != null && brought.Contains(layer.name)))
                    seenRebuilt = true;
                else if (seenRebuilt) return true;
            }
            return false;
        }

        // ---- async sync layers --------------------------------------------------

        /// <summary>
        /// The layers a controller can have written back as a <c>c.AsyncSync(…)</c> call
        /// instead of as the send ring and decoder they expanded into. Same shape as
        /// <see cref="GadgetPlan"/>, because it answers the same two questions: which layers
        /// stop being states, and which parameters stop being declarations.
        /// </summary>
        internal class AsyncSyncPlan
        {
            /// <summary>Layer name → the setup that layer is, rebuilt from its saved config.</summary>
            public readonly Dictionary<string, AsyncSyncBuilder.Request> layers =
                new Dictionary<string, AsyncSyncBuilder.Request>();

            /// <summary>Layers a planned setup also rebuilds — today the Ready watcher. They
            /// have no call of their own: the AsyncSync call that owns them puts them back,
            /// and emitting their states would build the machinery a second time.</summary>
            public readonly HashSet<string> supporting = new HashSet<string>();

            /// <summary>
            /// Whether a planned setup generated this parameter — the index, the value
            /// channels, the request flags, all of which live under its base name. The call
            /// recreates them, so declaring them up top only restates what the next Generate
            /// rebuilds. IsLocal is deliberately not owned: it is shared machinery other
            /// layers read, exactly like the gadgets' constant One.
            /// </summary>
            public bool Owns(string parameter)
            {
                if (string.IsNullOrEmpty(parameter)) return false;
                foreach (var pair in layers)
                {
                    string prefix = pair.Value.baseName + "/";
                    if (!string.IsNullOrEmpty(pair.Value.baseName)
                        && parameter.StartsWith(prefix, System.StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Works out which layers qualify. Like <see cref="PlanGadgets"/> this runs on the live
        /// controller rather than on the IR, because the saved configs point at the real state
        /// machines and a reference match is only meaningful against those.
        ///
        /// A layer qualifies only when it still holds exactly the states the saved setup would
        /// build. Anything added, removed or renamed by hand has no call to stand for it, and
        /// rewriting the layer as one call would drop it without a word.
        /// </summary>
        static AsyncSyncPlan PlanAsyncSyncs(AnimatorController controller,
            ICollection<string> layerNames, List<string> warnings)
        {
            var plan = new AsyncSyncPlan();
            var configs = GraphFrameData.GetAsyncSyncs(controller);
            if (configs.Count == 0) return plan;

            foreach (var layer in controller.layers)
            {
                if (layerNames != null && !layerNames.Contains(layer.name)) continue;
                if (layer.stateMachine == null) continue;
                var config = FindAsyncSync(configs, layer.stateMachine);
                if (config == null) continue;

                var request = AsyncSyncBuilder.FromConfig(controller, config);
                if (!MatchesGeneratedShape(layer.stateMachine, request))
                {
                    warnings.Add(L.Tr("Layer '{0}' no longer holds the states its saved async-sync setup would build; exported as raw states instead of an AsyncSync call.",
                        layer.name));
                    continue;
                }
                plan.layers[layer.name] = request;
                var owned = new HashSet<AnimatorStateMachine>();
                if (config.readyLayer != null) owned.Add(config.readyLayer);
                if (config.staleLayer != null) owned.Add(config.staleLayer);
                if (config.groups != null)
                    foreach (var group in config.groups)
                        if (group?.layer != null) owned.Add(group.layer);
                foreach (var other in controller.layers)
                    if (other.stateMachine != null && owned.Contains(other.stateMachine))
                        plan.supporting.Add(other.name);
            }

            // Same post-step caveat the gadget layers carry: this one is rebuilt at the end of
            // the controller, so anything ordinary after it changes place.
            if (plan.layers.Count > 0
                && FollowedByOrdinaryLayers(controller.layers, layerNames, plan.layers.Keys, null))
                warnings.Add(L.Tr("Async sync layers are regenerated at the end of the controller; the layer order will differ from the original."));
            return plan;
        }

        static GraphFrameData.AsyncSyncConfig FindAsyncSync(
            List<GraphFrameData.AsyncSyncConfig> configs, AnimatorStateMachine machine)
        {
            foreach (var config in configs)
                if (config.layer == machine)
                    return config;
            return null;
        }

        /// <summary>Every state the setup would build, and nothing else. Names carry the slot
        /// and the visit, so this catches a renamed state as well as an added one.</summary>
        static bool MatchesGeneratedShape(AnimatorStateMachine machine,
            AsyncSyncBuilder.Request request)
        {
            if (machine.stateMachines.Length > 0) return false;
            var expected = AsyncSyncApplier.ExpectedStateNames(request);
            if (expected.Count == 0 || machine.states.Length != expected.Count) return false;

            var remaining = new List<string>(expected);
            foreach (var child in machine.states)
            {
                if (child.state == null) return false;
                int at = remaining.IndexOf(child.state.name);
                if (at < 0) return false;
                remaining.RemoveAt(at);
            }
            return remaining.Count == 0;
        }

        // ---- object gadget layers ----------------------------------------------

        /// <summary>
        /// The layers a controller can have written back as <c>c.Objects()</c> calls instead of
        /// as the states, trees and clips they expanded into. Same shape and same two questions
        /// as <see cref="GadgetPlan"/>: which layers stop being states, and which parameters
        /// stop being declarations.
        /// </summary>
        internal class ObjectPlan
        {
            /// <summary>Layer name → the object gadgets that layer is, in the order they have to
            /// be rebuilt in (the root tree's child order, where there is one).</summary>
            public readonly Dictionary<string, List<GraphFrameData.ObjectGadgetConfig>> layers =
                new Dictionary<string, List<GraphFrameData.ObjectGadgetConfig>>();

            /// <summary>The merge every target's path is derived against, resolved once here so
            /// the emission works from the same frame of reference the plan checked against.
            /// Null when nothing qualified, which is also when nothing asks.</summary>
            public Transform root;

            /// <summary>The layer the tree-wired toggles hang in, so the call can name it and a
            /// replay lands in the same one instead of minting a "DBT" beside it. Null when no
            /// toggle is tree-wired. One name for the whole plan because one recipe builds one
            /// shared layer — a controller whose wizard-made toggles are spread over two Direct
            /// layers exports as calls that gather them into the first.</summary>
            public string treeLayer;

            /// <summary>Whether a planned toggle is what created this parameter. Its call
            /// creates it again, so declaring it up top only restates what the next Generate
            /// rebuilds. A parameter the gadget merely borrowed belongs to somebody else and
            /// stays declared — that is the whole meaning of <c>createdParameter</c>.</summary>
            public bool Owns(string parameter)
            {
                if (string.IsNullOrEmpty(parameter)) return false;
                foreach (var pair in layers)
                    foreach (var config in pair.Value)
                        if (config.createdParameter && config.parameter == parameter)
                            return true;
                return false;
            }
        }

        /// <summary>
        /// Works out which layers qualify, on the live controller rather than on the IR — the
        /// records point at the real machines and trees, so a reference match is only meaningful
        /// here (the same reason <see cref="PlanGadgets"/> gives).
        ///
        /// A record qualifies only when it can still be written down: the pin has to be healthy
        /// and every target still in the prefab, because what an exported call carries is the
        /// target's DERIVED PATH and there is no path to derive from a reference that resolves to
        /// nothing. A layer-wired toggle also has to still hold the two states it built, since
        /// nothing else in that layer is anybody's. Anything else falls back to the raw states,
        /// with a named warning — a call that describes less than the layer holds would drop the
        /// difference silently.
        ///
        /// A tree-wired toggle claims its own child of the shared Direct tree and nothing else
        /// (see <see cref="ChildClaims"/>). What sits beside it — a DBT gadget, a child added by
        /// hand — is somebody else's business, not a reason to give up on the layer.
        /// </summary>
        static ObjectPlan PlanObjects(AnimatorController controller,
            ICollection<string> layerNames, List<string> warnings, ChildClaims claims)
        {
            var plan = new ObjectPlan();
            var configs = GraphFrameData.GetObjectGadgets(controller);
            if (configs.Count == 0) return plan;
            var root = ObjectGadgets.Root(controller);
            plan.root = root;

            foreach (var layer in controller.layers)
            {
                if (layerNames != null && !layerNames.Contains(layer.name)) continue;
                if (layer.stateMachine == null) continue;
                var mine = new List<GraphFrameData.ObjectGadgetConfig>();
                foreach (var config in configs)
                    if (config.layer == layer.stateMachine) mine.Add(config);
                if (mine.Count == 0) continue;

                string problem = Unaccounted(layer, mine, root);
                if (problem != null)
                {
                    warnings.Add(problem);
                    continue;
                }
                plan.layers[layer.name] = InTreeOrder(layer, mine);

                var tree = GadgetRootTree(layer);
                if (tree == null) continue;
                if (plan.treeLayer == null) plan.treeLayer = layer.name;
                for (int i = 0; i < tree.children.Length; i++)
                    foreach (var config in mine)
                        if (config.tree == tree.children[i].motion)
                            claims.Claim(layer.name, i, tree.children.Length);
            }
            return plan;
        }

        /// <summary>Why these gadgets cannot be written back as calls, or null when they can.
        /// Only what the gadgets themselves built is asked about: a shared Direct tree layer is
        /// judged one child at a time, and the children nobody claims are exported as the raw
        /// tree they are rather than disqualifying the toggle beside them.</summary>
        static string Unaccounted(AnimatorControllerLayer layer,
            List<GraphFrameData.ObjectGadgetConfig> mine, Transform root)
        {
            if (root == null)
                return L.Tr("Layer '{0}' holds object gadgets, but this controller is not linked to a healthy gimmick prefab any more, so their target paths cannot be worked out; exported as raw states.",
                    layer.name);
            foreach (var config in mine)
                foreach (var record in config.targets)
                    if (record == null || record.target == null
                        || ObjectGadgets.PathOf(root, record.target) == null)
                        return L.Tr("Object gadget '{0}' animates an object that is no longer in the linked prefab, so layer '{1}' is exported as raw states instead of an Objects call.",
                            config.name, layer.name);

            var machine = layer.stateMachine;
            if ((ToggleBuilder.Mode)mine[0].mode == ToggleBuilder.Mode.Layer)
            {
                if (mine.Count != 1)
                    return L.Tr("Layer '{0}' is claimed by {1} object gadgets at once; exported as raw states.",
                        layer.name, mine.Count);
                // The two states the toggle builds, by the names it gives them: this catches a
                // renamed state and an added one alike, which is what MatchesGeneratedShape does
                // for a sync layer.
                var expected = new List<string> { mine[0].name + " OFF", mine[0].name + " ON" };
                if (machine.stateMachines.Length > 0 || machine.states.Length != expected.Count)
                    return Reshaped(mine[0], layer);
                foreach (var child in machine.states)
                    if (child.state == null || !expected.Remove(child.state.name))
                        return Reshaped(mine[0], layer);
                return null;
            }

            // Reshaped is still asked, but only about the gadgets' OWN machinery: a tree-wired
            // toggle whose layer is no longer a Direct tree layer at all has nothing left to
            // claim a child of, and a call for it would describe a shape that is gone.
            return GadgetRootTree(layer) == null ? Reshaped(mine[0], layer) : null;
        }

        static string Reshaped(GraphFrameData.ObjectGadgetConfig config,
            AnimatorControllerLayer layer) =>
            L.Tr("Layer '{0}' no longer holds what the object gadget '{1}' would build; exported as raw states instead of an Objects call.",
                layer.name, config.name);

        /// <summary>The records in the order their trees hang off the layer's root, which is the
        /// order they were built in. A layer-wired gadget is alone in its layer and has no
        /// order to keep.</summary>
        static List<GraphFrameData.ObjectGadgetConfig> InTreeOrder(AnimatorControllerLayer layer,
            List<GraphFrameData.ObjectGadgetConfig> mine)
        {
            var tree = GadgetRootTree(layer);
            if (tree == null || mine.Count < 2) return mine;
            var ordered = new List<GraphFrameData.ObjectGadgetConfig>();
            foreach (var child in tree.children)
                foreach (var config in mine)
                    if (config.tree == child.motion && !ordered.Contains(config))
                        ordered.Add(config);
            foreach (var config in mine)
                if (!ordered.Contains(config)) ordered.Add(config);
            return ordered;
        }

        // ---- asset fields ------------------------------------------------------

        /// <summary>Walks the IR in emission order so field declarations come out in a
        /// stable, readable order.</summary>
        static void RegisterAssets(ControllerIR ir, RecipeScript script, Result result,
            GadgetPlan gadgets, AsyncSyncPlan asyncSyncs, ObjectPlan objects, ChildClaims claims)
        {
            void Register(Object asset)
            {
                if (asset == null || script.Assets.ContainsKey(asset)) return;
                string name = script.RegisterAsset(asset, asset.name);
                result.fields.Add(new FieldRef
                {
                    fieldName = name,
                    fieldType = asset is AnimationClip ? "AnimationClip"
                        : asset is AvatarMask ? "AvatarMask" : "Motion",
                    asset = asset,
                });
            }

            void Tree(ControllerIR.Tree tree)
            {
                if (tree == null) return;
                foreach (var child in tree.children)
                {
                    Register(child.motionAsset);
                    Tree(child.tree);
                }
            }

            void Machine(ControllerIR.Machine machine)
            {
                if (machine == null) return;
                foreach (var state in machine.states)
                {
                    Register(state.motionAsset);
                    Tree(state.tree);
                }
                foreach (var child in machine.machines)
                    Machine(child.machine);
            }

            foreach (var layer in ir.layers)
            {
                // A layer with a raw remainder is still declared, and the remainder's clips are
                // the user's — the strip above already took the claimed children out, so what is
                // walked here is exactly what the declaration will need fields for.
                bool remainder = claims.HasLeftovers(layer.name);
                // A layer the gadget calls rebuild contributes no fields: every clip in it is
                // minted by those calls, and a field for one would refer to nothing.
                if (!remainder && (gadgets.layers.ContainsKey(layer.name)
                    || gadgets.supporting.Contains(layer.name))) continue;
                // Same for a sync layer: its states play the controller's Empty clip, which the
                // call resolves (or creates) at Generate time rather than through a field.
                if (asyncSyncs.layers.ContainsKey(layer.name)
                    || asyncSyncs.supporting.Contains(layer.name)) continue;
                // And for an object gadget layer: the clips are keyed from the prefab by the
                // call, or named by asset path where they are the user's (ADR 0046).
                if (!remainder && objects.layers.ContainsKey(layer.name)) continue;
                Register(layer.mask);
                Machine(layer.machine);
                foreach (var entry in layer.syncedMotions)
                    Register(entry.motion);
            }
        }

        /// <summary>"---- text ----…" divider padded to a steady width.</summary>
        internal static string Header(string text)
        {
            const int width = 72;
            string lead = "---- " + text + " ";
            return lead.Length >= width ? lead.TrimEnd() : lead + new string('-', width - lead.Length);
        }

        // ---- composing the file --------------------------------------------------

        const string CheatSheet =
@"// AnimatorAsCode-style API (Yozolab.DaerD.Authoring), quick reference:
//   Parameters   var go = c.BoolParameter(""Go"");   var x = c.FloatParameter(""X"", 0.5f);
//                c.IntParameter(""N"");   c.TriggerParameter(""Fire"");
//   Layers       var fx = c.Layer(""Name"").WithWeight(1).Additive().WithAvatarMask(mask);
//                c.SyncedLayer(""Mirror"", ""Name"").Override(""StatePath"", clip);
//   States       var s = fx.NewState(""Idle"").WithAnimation(clip).At(260, 60)
//                    .WithWriteDefaultsSetTo(false).WithSpeedSetTo(2).WithMotionTime(x)
//                    .WithTag(""t"").Default();
//   Sub-machines var sub = fx.NewSubStateMachine(""Sub"").At(500, 50);  sub.NewState(...);
//   Transitions  s.TransitionsTo(other) / s.Exits() / fx.AnyTransitionsTo(s)
//                    / fx.EntryTransitionsTo(s) / sub.TransitionsTo(s), then chain:
//                .When(go.IsTrue()).And(x.IsGreaterThan(0.5f))      // conditions AND together
//                .AfterAnimationFinishes() .AfterAnimationIsAtLeastAtNormalized(0.9f)
//                .WithTransitionDurationSeconds(0.15f) .WithTransitionToSelf()
//                .WithInterruption(TransitionInterruptionSource.Destination)
//   Blend trees  var t = c.NewBlendTree(""Move"").Simple1D(x)
//                    .WithAnimation(idleClip, 0).WithAnimation(runClip, 1);
//                s.WithAnimation(t);   2D: .FreeformDirectional2D(x, y) + .WithAnimation(clip, 0, 1)
//                Direct: .Direct() + .WithAnimation(clip, weightParam);  extras: t.LastChild.TimeScale(2)
//   Drivers      s.Drives(n, 1).DrivingIncreases(x, 0.1f).DrivingCopies(a, b).DrivingLocally()
//                    .DrivingRemaps(a, 0, 1, b, -1, 1).DrivingRandomizes(x, 0, 1);
//   Gadgets      c.Gadgets(""DBT"").Multiply(a, b, ""A*B"").Remap(x, ""X01"", -1, 1, 0, 1)
//                    .Smooth(x, ""X/Smoothed"", ""X/Smoothing"").Buffer(x, ""X/Late"", 2);
//                (the per-frame float math from the Add menu; its layer is rebuilt each time)
//   Objects      c.Objects().Toggle(""Hat"").Shows(""Head/Hat"").Enables(""Head/Hat/Light"", ""Light"")
//                    .Toggle(""Cape"").AsTree().Hides(""Body/Cape"").DefaultOn();
//                (toggles for the pinned gimmick prefab; paths are read from it, never written;
//                 c.Objects(""Name"") names the layer the tree-wired ones share)
//   Async sync   c.AsyncSync().Targets(""Hue"", ""Outfit"").Rate(""Hue"", 2).Requestable(""Hue"")
//                    .Schedule(""Hue"", ""Outfit"", ""Hue"");            // the cycle, step by step
//                    .Sends(""Hue"", ""Outfit"").Sends(""Hue"");   // or what each step carries
//                    .AllowRepeats();       // and then a step may send what the one before did
//   Fallbacks    s.BehaviourJson(typeName, json);   c.Raw(controller => { /* full API */ });
// Assets are the [SerializeField] fields below — assign them on the recipe asset.
// A build body is ordinary C#: loops, helpers and interpolation all work in your half.";

        /// <summary>
        /// The exporter's half: fields and BuildGenerated, rewritten whole on every export.
        /// It is deliberately the file nobody edits — that is what lets the other half be
        /// reshaped freely, and what makes its own git diff a clean report of what changed in
        /// the controller since the last export.
        /// </summary>
        static string ComposeGenerated(RecipeScript script, string className, string namespaceName,
            AnimatorController controller, Result result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated> Exported from \"" + controller.name
                + "\" by DaerD. </auto-generated>");
            sb.AppendLine("// DO NOT EDIT — every export overwrites this file. Your half is "
                + className + ".cs:");
            sb.AppendLine("// its Build() is what Generate runs, and DaerD never touches it. After a re-export,");
            sb.AppendLine("// diff this file, carry what changed into yours, then press Compare on the recipe");
            sb.AppendLine("// asset — it passes when both halves declare the same controller.");
            sb.AppendLine(CheatSheet);
            sb.AppendLine();
            sb.AppendLine("using UnityEditor.Animations;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using Yozolab.DaerD.Authoring;");
            sb.AppendLine();

            bool hasNamespace = !string.IsNullOrEmpty(namespaceName);
            string indent = hasNamespace ? "    " : string.Empty;
            if (hasNamespace)
            {
                sb.AppendLine("namespace " + namespaceName);
                sb.AppendLine("{");
            }

            sb.AppendLine(indent + "public partial class " + className + " : ControllerRecipe");
            sb.AppendLine(indent + "{");
            foreach (var field in result.fields)
                sb.AppendLine(indent + "    [SerializeField] " + field.fieldType + " "
                    + field.fieldName + ";");
            if (result.fields.Count > 0) sb.AppendLine();

            sb.AppendLine(indent + "    protected override void BuildGenerated(ControllerBuilder c)");
            sb.AppendLine(indent + "    {");
            var body = StripUnusedVariables(script.Lines);
            while (body.Count > 0 && body[0].Length == 0) body.RemoveAt(0);
            while (body.Count > 0 && body[body.Count - 1].Length == 0) body.RemoveAt(body.Count - 1);
            foreach (var line in body)
                sb.AppendLine(line.Length == 0 ? string.Empty : indent + "        " + line);
            sb.AppendLine(indent + "    }");
            sb.AppendLine(indent + "}");
            if (hasNamespace) sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>First line of a hand half, so the exporter can tell one from a recipe
        /// written before the split (which carries the fields and Build the generated half
        /// now owns, and would collide with it).</summary>
        public const string HandHalfMarker = "// <daerd-recipe>";

        /// <summary>
        /// Your half: a Build that delegates to the generated one, and nothing else. Written
        /// once, at the first export, and never overwritten afterwards — whatever it grows
        /// into (loops, helpers, an AI's reshaping) is yours to keep. Delegating is the honest
        /// starting point: a fresh export generates the right controller before anyone has
        /// touched anything.
        /// </summary>
        static string ComposeHandHalf(string className, string namespaceName)
        {
            var sb = new StringBuilder();
            sb.AppendLine(HandHalfMarker + " Hand half of " + className
                + " — DaerD never overwrites this file. </daerd-recipe>");
            sb.AppendLine("// " + className + ".Generated.cs is the exporter's half: rewritten on every export,");
            sb.AppendLine("// with an API cheat sheet at the top. Shape this Build() however you like — loops,");
            sb.AppendLine("// helpers, your own names — and press Compare on the recipe asset to check that it");
            sb.AppendLine("// still declares the same controller as the export it came from.");
            sb.AppendLine();
            sb.AppendLine("using UnityEditor.Animations;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine("using Yozolab.DaerD.Authoring;");
            sb.AppendLine();

            bool hasNamespace = !string.IsNullOrEmpty(namespaceName);
            string indent = hasNamespace ? "    " : string.Empty;
            if (hasNamespace)
            {
                sb.AppendLine("namespace " + namespaceName);
                sb.AppendLine("{");
            }

            sb.AppendLine(indent + "public partial class " + className);
            sb.AppendLine(indent + "{");
            sb.AppendLine(indent + "    protected override void Build(ControllerBuilder c) => BuildGenerated(c);");
            sb.AppendLine(indent + "}");
            if (hasNamespace) sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Drops "var t = " from declarations nothing refers back to (one-shot
        /// transitions), leaving plain fluent statements.</summary>
        internal static List<string> StripUnusedVariables(IReadOnlyList<string> lines)
        {
            var counts = new Dictionary<string, int>();
            foreach (var line in lines)
                foreach (Match token in Regex.Matches(line, @"[A-Za-z_][A-Za-z0-9_]*"))
                    counts[token.Value] = counts.TryGetValue(token.Value, out var n) ? n + 1 : 1;

            var output = new List<string>(lines.Count);
            foreach (var line in lines)
            {
                var declaration = Regex.Match(line, @"^var ([A-Za-z_][A-Za-z0-9_]*) = (.*)$");
                output.Add(declaration.Success && counts[declaration.Groups[1].Value] == 1
                    ? declaration.Groups[2].Value
                    : line);
            }
            return output;
        }
    }
}
