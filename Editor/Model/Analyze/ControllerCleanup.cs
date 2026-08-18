using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>
    /// Cleanup helpers for a controller asset: an index of every AnimationClip the controller
    /// references (with the states that use it), and detection / deletion of leftover sub-assets
    /// that are stored inside the .controller file but are no longer reachable from any
    /// controller in that file (blend trees, clips or states orphaned by past edits).
    /// </summary>
    static class ControllerCleanup
    {
        /// <summary>One place a clip is used; carries the drill path for <see cref="DaerDContext.NavigateTo"/>.</summary>
        public class ClipUsage
        {
            /// <summary>List label, e.g. "Base / Locomotion / Walk — MoveTree".</summary>
            public string label;
            public int layerIndex;
            public List<AnimatorStateMachine> stateMachinePath;
            public AnimatorState state;
        }

        public class ClipEntry
        {
            public AnimationClip clip;
            /// <summary>True when the clip is stored inside the .controller file itself.</summary>
            public bool embedded;
            public List<ClipUsage> usages = new List<ClipUsage>();
        }

        // ---- clip index ------------------------------------------------------

        /// <summary>Every clip the controller references, sorted by name, with one usage per
        /// (clip, state) pair — a clip filling several slots of one blend tree lists once.</summary>
        public static List<ClipEntry> CollectClipUsages(AnimatorController controller)
        {
            var ordered = new List<ClipEntry>();
            if (controller == null) return ordered;
            var entries = new Dictionary<AnimationClip, ClipEntry>();
            string controllerPath = AssetDatabase.GetAssetPath(controller);

            var layers = controller.layers;
            for (int li = 0; li < layers.Length; li++)
            {
                var layer = layers[li];
                // A synced layer has no state machine of its own — it re-plays the source
                // layer's states with per-state override motions. Navigation therefore
                // targets the source layer, where the states actually live.
                bool synced = layer.syncedLayerIndex >= 0;
                int sourceIndex = synced ? layer.syncedLayerIndex : li;
                if (sourceIndex < 0 || sourceIndex >= layers.Length) continue;
                var root = layers[sourceIndex].stateMachine;
                if (root == null) continue;

                string layerLabel = synced ? layer.name + " (sync)" : layer.name;
                var path = new List<AnimatorStateMachine> { root };
                Walk(root, path, layer, synced, sourceIndex, layerLabel, controllerPath, entries, ordered);
            }

            ordered.Sort((a, b) => string.Compare(a.clip.name, b.clip.name, System.StringComparison.OrdinalIgnoreCase));
            return ordered;
        }

        static void Walk(AnimatorStateMachine sm, List<AnimatorStateMachine> path,
            AnimatorControllerLayer layer, bool synced, int navLayerIndex, string layerLabel,
            string controllerPath, Dictionary<AnimationClip, ClipEntry> entries, List<ClipEntry> ordered)
        {
            string pathLabel = path.PathLabel(layerLabel);
            foreach (var cs in sm.states)
            {
                var state = cs.state;
                if (state == null) continue;
                var motion = synced ? layer.GetOverrideMotion(state) : state.motion;
                AddMotionUsages(motion, null, state, pathLabel, navLayerIndex, path,
                    controllerPath, entries, ordered, new HashSet<Motion>());
            }
            foreach (var child in sm.stateMachines)
            {
                var childSm = child.stateMachine;
                if (childSm == null || path.Contains(childSm)) continue;
                path.Add(childSm);
                Walk(childSm, path, layer, synced, navLayerIndex, layerLabel, controllerPath, entries, ordered);
                path.RemoveAt(path.Count - 1);
            }
        }

        static void AddMotionUsages(Motion motion, BlendTree owner, AnimatorState state,
            string pathLabel, int layerIndex, List<AnimatorStateMachine> path, string controllerPath,
            Dictionary<AnimationClip, ClipEntry> entries, List<ClipEntry> ordered, HashSet<Motion> seenInState)
        {
            if (motion == null || !seenInState.Add(motion)) return;
            if (motion is AnimationClip clip)
            {
                if (!entries.TryGetValue(clip, out var entry))
                {
                    entry = new ClipEntry
                    {
                        clip = clip,
                        embedded = !string.IsNullOrEmpty(controllerPath)
                            && AssetDatabase.GetAssetPath(clip) == controllerPath,
                    };
                    entries[clip] = entry;
                    ordered.Add(entry);
                }
                string label = pathLabel + " / " + state.name;
                if (owner != null) label += " — " + owner.name;
                entry.usages.Add(new ClipUsage
                {
                    label = label,
                    layerIndex = layerIndex,
                    // Snapshot — the walker mutates the list as it recurses.
                    stateMachinePath = new List<AnimatorStateMachine>(path),
                    state = state,
                });
            }
            else if (motion is BlendTree tree)
            {
                foreach (var child in tree.children)
                    AddMotionUsages(child.motion, tree, state, pathLabel, layerIndex, path,
                        controllerPath, entries, ordered, seenInState);
            }
        }

        // ---- clip replacement ------------------------------------------------

        /// <summary>
        /// Replaces every reference to <paramref name="from"/> in the controller — state
        /// motions, blend tree child slots (any depth) and synced-layer override motions —
        /// with <paramref name="to"/>, as one undoable step. Returns the number of
        /// references replaced.
        /// </summary>
        public static int ReplaceClip(AnimatorController controller, AnimationClip from, AnimationClip to)
        {
            if (controller == null || from == null || to == null || from == to) return 0;
            int replaced = 0;
            using (new UndoScope("Replace Animation Clip"))
            {
                foreach (var state in controller.AllStates())
                {
                    if (state.motion != from) continue;
                    Undo.RegisterCompleteObjectUndo(state, "Replace Animation Clip");
                    state.motion = to;
                    EditorUtility.SetDirty(state);
                    replaced++;
                }

                // Trees hanging off synced-layer overrides are not reachable through state
                // motions, so collect from both places rather than using AllBlendTrees.
                var trees = new HashSet<BlendTree>();
                foreach (var state in controller.AllStates())
                    CollectTrees(state.motion, trees);
                var layers = controller.layers;
                foreach (var layer in layers)
                {
                    if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= layers.Length) continue;
                    var source = layers[layer.syncedLayerIndex].stateMachine;
                    foreach (var sm in source.SelfAndDescendants())
                        foreach (var cs in sm.states)
                            if (cs.state != null)
                                CollectTrees(layer.GetOverrideMotion(cs.state), trees);
                }

                foreach (var tree in trees)
                {
                    var children = tree.children;
                    bool changed = false;
                    for (int i = 0; i < children.Length; i++)
                    {
                        if (children[i].motion != from) continue;
                        children[i].motion = to;
                        changed = true;
                        replaced++;
                    }
                    if (!changed) continue;
                    Undo.RegisterCompleteObjectUndo(tree, "Replace Animation Clip");
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                }

                // layers is a copy — mutate it fully, then write it back once if anything changed.
                bool layersChanged = false;
                foreach (var layer in layers)
                {
                    if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= layers.Length) continue;
                    var source = layers[layer.syncedLayerIndex].stateMachine;
                    foreach (var sm in source.SelfAndDescendants())
                        foreach (var cs in sm.states)
                        {
                            if (cs.state == null) continue;
                            if (!ReferenceEquals(layer.GetOverrideMotion(cs.state), from)) continue;
                            layer.SetOverrideMotion(cs.state, to);
                            layersChanged = true;
                            replaced++;
                        }
                }
                if (layersChanged)
                {
                    Undo.RegisterCompleteObjectUndo(controller, "Replace Animation Clip");
                    controller.layers = layers;
                    EditorUtility.SetDirty(controller);
                }
            }
            return replaced;
        }

        static void CollectTrees(Motion motion, HashSet<BlendTree> trees)
        {
            // The set doubles as the cycle guard for self-nested trees.
            if (!(motion is BlendTree tree) || !trees.Add(tree)) return;
            foreach (var child in tree.children)
                CollectTrees(child.motion, trees);
        }

        // ---- leftover sub-assets ---------------------------------------------

        /// <summary>Sub-assets of the controller's file that no controller in it reaches.</summary>
        public static List<Object> FindLeftoverSubAssets(AnimatorController controller)
        {
            if (controller == null) return new List<Object>();
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return new List<Object>();
            return FindLeftovers(controller, AssetDatabase.LoadAllAssetsAtPath(path));
        }

        /// <summary>
        /// Core of the scan, separated from the AssetDatabase so tests can pass a synthetic
        /// sub-asset list. Anything in <paramref name="allAssets"/> not reachable from a
        /// controller in the list is a leftover. Null entries (missing-script stubs) are
        /// skipped — there is no object reference to delete safely.
        /// </summary>
        public static List<Object> FindLeftovers(AnimatorController controller, Object[] allAssets)
        {
            var reachable = new HashSet<Object>();
            CollectReachable(controller, reachable);
            // A file can hold more than one controller (rare but legal); their graphs are
            // not garbage, so mark everything they reach too.
            foreach (var asset in allAssets)
                if (asset is AnimatorController other)
                    CollectReachable(other, reachable);

            // The designated Empty clip may live inside the file while only DaerD's data
            // object references it — that's a kept setting, not garbage.
            foreach (var asset in allAssets)
                if (asset is GraphFrameData data && data.emptyClip != null)
                    reachable.Add(data.emptyClip);

            var leftovers = new List<Object>();
            foreach (var asset in allAssets)
            {
                if (asset == null) continue;
                if (asset is AnimatorController) continue;
                if (asset is GraphFrameData) continue;   // DaerD's own hidden storage
                if (reachable.Contains(asset)) continue;
                leftovers.Add(asset);
            }
            return leftovers;
        }

        static void CollectReachable(AnimatorController controller, HashSet<Object> reachable)
        {
            if (controller == null || !reachable.Add(controller)) return;

            var visitedTrees = new HashSet<Motion>();
            foreach (var layer in controller.layers)
                if (layer.avatarMask != null) reachable.Add(layer.avatarMask);

            foreach (var sm in controller.AllStateMachines())
            {
                reachable.Add(sm);
                AddBehaviours(sm.behaviours, reachable);
            }
            foreach (var state in controller.AllStates())
            {
                reachable.Add(state);
                AddBehaviours(state.behaviours, reachable);
                AddMotion(state.motion, reachable, visitedTrees);
            }
            foreach (var t in controller.AllTransitions())
                reachable.Add(t);

            // Synced layers carry their own per-state override motions and behaviours.
            var layers = controller.layers;
            foreach (var layer in layers)
            {
                if (layer.syncedLayerIndex < 0 || layer.syncedLayerIndex >= layers.Length) continue;
                var source = layers[layer.syncedLayerIndex].stateMachine;
                foreach (var sm in source.SelfAndDescendants())
                    foreach (var cs in sm.states)
                    {
                        if (cs.state == null) continue;
                        AddMotion(layer.GetOverrideMotion(cs.state), reachable, visitedTrees);
                        AddBehaviours(layer.GetOverrideBehaviours(cs.state), reachable);
                    }
            }
        }

        static void AddBehaviours(StateMachineBehaviour[] behaviours, HashSet<Object> reachable)
        {
            if (behaviours == null) return;
            foreach (var b in behaviours)
                if (b != null) reachable.Add(b);
        }

        static void AddMotion(Motion motion, HashSet<Object> reachable, HashSet<Motion> visited)
        {
            // The visited set guards against self-nested (cyclic) blend trees.
            if (motion == null || !visited.Add(motion)) return;
            reachable.Add(motion);
            if (motion is BlendTree tree)
                foreach (var child in tree.children)
                    AddMotion(child.motion, reachable, visited);
        }

        // ---- exposed sub-assets ----------------------------------------------

        /// <summary>
        /// Sub-assets that Unity keeps hidden but which are showing up in the Project window
        /// under the .controller. These are usually StateMachineBehaviours (a VRC Parameter
        /// Driver and friends) whose hide flags were cleared by a copy/paste at some point:
        /// they are in use, so the leftover scan won't touch them, yet they clutter the file's
        /// contents and can't be selected for anything useful.
        /// </summary>
        public static List<Object> FindExposedSubAssets(AnimatorController controller)
        {
            if (controller == null) return new List<Object>();
            string path = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(path)) return new List<Object>();
            return FindExposed(AssetDatabase.LoadAllAssetsAtPath(path));
        }

        /// <summary>
        /// Core of the scan, split from the AssetDatabase for testing. Only the object kinds
        /// Unity itself stores hidden are considered — an AnimationClip authored into the
        /// controller is meant to be visible, and so is the controller itself.
        /// </summary>
        public static List<Object> FindExposed(Object[] allAssets)
        {
            var exposed = new List<Object>();
            if (allAssets == null) return exposed;
            foreach (var asset in allAssets)
            {
                if (asset == null) continue;
                if (!IsHiddenByUnity(asset)) continue;
                if ((asset.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
                exposed.Add(asset);
            }
            return exposed;
        }

        static bool IsHiddenByUnity(Object asset) =>
            asset is StateMachineBehaviour
            || asset is AnimatorState
            || asset is AnimatorStateMachine
            || asset is AnimatorTransitionBase
            || asset is BlendTree;

        /// <summary>
        /// Re-hides the given sub-assets. Nothing is deleted and no reference changes — the
        /// objects stay exactly where they are, they just stop being listed under the asset.
        /// </summary>
        public static int HideSubAssets(AnimatorController controller, IEnumerable<Object> assets)
        {
            if (controller == null || assets == null) return 0;
            int hidden = 0;
            using (new UndoScope("Hide Sub-Assets"))
            {
                foreach (var asset in assets)
                {
                    if (asset == null) continue;
                    Undo.RegisterCompleteObjectUndo(asset, "Hide Sub-Assets");
                    asset.hideFlags |= HideFlags.HideInHierarchy;
                    EditorUtility.SetDirty(asset);
                    hidden++;
                }
            }
            if (hidden > 0)
            {
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                // The Project window lists sub-assets from the imported artifact, not from the
                // objects in memory — without a forced reimport the rows stay on screen even
                // though the file on disk is already correct.
                string path = AssetDatabase.GetAssetPath(controller);
                if (!string.IsNullOrEmpty(path))
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            return hidden;
        }

        /// <summary>Deletes the given sub-assets as one undoable step and saves so the file shrinks.</summary>
        public static int DeleteSubAssets(AnimatorController controller, IEnumerable<Object> assets)
        {
            if (controller == null || assets == null) return 0;
            int deleted = 0;
            using (new UndoScope("Delete Leftover Sub-Assets"))
            {
                foreach (var asset in assets)
                {
                    if (asset == null) continue;
                    Undo.DestroyObjectImmediate(asset);
                    deleted++;
                }
            }
            if (deleted > 0)
            {
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
            }
            return deleted;
        }

        /// <summary>"BlendTree 'OldTree'" style label for the leftover list.</summary>
        public static string Describe(Object asset)
        {
            if (asset == null) return "(null)";
            string name = string.IsNullOrEmpty(asset.name) ? "(unnamed)" : asset.name;
            return asset.GetType().Name + " '" + name + "'";
        }
    }
}
