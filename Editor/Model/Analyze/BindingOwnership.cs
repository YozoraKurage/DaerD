using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Analyze
{
    /// <summary>One animated property, identified the way a clip identifies it.</summary>
    readonly struct BindingKey : IEquatable<BindingKey>, IComparable<BindingKey>
    {
        public readonly string Path;
        public readonly Type Type;
        public readonly string Property;

        public BindingKey(string path, Type type, string property)
        {
            Path = path ?? string.Empty;
            Type = type;
            Property = property ?? string.Empty;
        }

        public static BindingKey Of(EditorCurveBinding binding) =>
            new BindingKey(binding.path, binding.type, binding.propertyName);

        /// <summary>The one-line form the UI and the analyzer messages both show:
        /// <c>Body:SkinnedMeshRenderer.blendShape.Smile</c>. The animated root has no path of
        /// its own, so it is spelled <c>(root)</c> rather than left blank.</summary>
        public string Display =>
            (Path.Length == 0 ? "(root)" : Path) + ":" + (Type != null ? Type.Name : "?") + "." + Property;

        public bool Equals(BindingKey other) =>
            string.Equals(Path, other.Path, StringComparison.Ordinal)
            && Type == other.Type
            && string.Equals(Property, other.Property, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is BindingKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Path.GetHashCode();
                hash = (hash * 397) ^ (Type != null ? Type.GetHashCode() : 0);
                return (hash * 397) ^ Property.GetHashCode();
            }
        }

        public int CompareTo(BindingKey other)
        {
            int byPath = string.CompareOrdinal(Path, other.Path);
            if (byPath != 0) return byPath;
            string mine = Type != null ? Type.Name : string.Empty;
            string theirs = other.Type != null ? other.Type.Name : string.Empty;
            int byType = string.CompareOrdinal(mine, theirs);
            if (byType != 0) return byType;
            return string.CompareOrdinal(Property, other.Property);
        }

        public override string ToString() => Display;
    }

    /// <summary>
    /// What one layer does with the controller's bindings: which of them it writes, in which of
    /// its reachable states, and how many reachable states it has to write them in.
    /// </summary>
    sealed class LayerBindings
    {
        public int LayerIndex;
        public string LayerName;
        public AnimatorLayerBlendingMode BlendingMode;
        public float DefaultWeight;

        /// <summary>Reachable states that have a motion. A clip with only zero-key curves
        /// counts — it is playing, it just writes nothing — and so does a blend tree.</summary>
        public int PlayingStates;

        /// <summary>Reachable states with <c>Motion == null</c>. They assert nothing, which is
        /// why they cannot be part of an ownership.</summary>
        public int SilentStates;

        /// <summary>Nothing this layer can enter plays anything, so it never asserts a value —
        /// a parameter-only layer, as far as Write Defaults is concerned.</summary>
        public bool IsSilent => PlayingStates == 0;

        /// <summary>The reachable states, in walk order — what a cell has to subtract the
        /// writers from to say which states do NOT write the binding.</summary>
        public readonly List<AnimatorState> States = new List<AnimatorState>();

        /// <summary>Reachable states whose motion writes the key. A BlendTree contributes the
        /// union of every leaf clip it reaches.</summary>
        public readonly Dictionary<BindingKey, List<AnimatorState>> Writers =
            new Dictionary<BindingKey, List<AnimatorState>>();

        public bool Touches(BindingKey key) => Writers.ContainsKey(key);

        /// <summary>Every reachable state of this layer writes the key, so whatever the layer
        /// is doing the binding is asserted — the shape that makes Write Defaults irrelevant
        /// for it. A single silent (Motion=None) reachable state is enough to lose this.</summary>
        public bool Owns(BindingKey key) =>
            PlayingStates > 0 && Writers.TryGetValue(key, out var writers)
            && writers.Count == PlayingStates + SilentStates;
    }

    /// <summary>
    /// Which layer, if any, guarantees that a binding is written every frame.
    ///
    /// <para>The model (Unity 2022.3, measured).</para>
    /// Write Defaults decides one thing: what a contributing layer does with a binding its
    /// current state does NOT write. ON leaves it unasserted — the layers below show through,
    /// and if nobody writes it the Animator's bind-time default fills it. OFF asserts the
    /// layer's buffer value, which inside one controller is whatever the layers below wrote
    /// this frame, or else the last value anyone wrote (stale). A controller therefore behaves
    /// the same all-ON as all-OFF exactly when every binding in its union is written every
    /// frame by somebody, and the static shape that guarantees that is an OWNER: a layer whose
    /// every reachable state writes the binding. A binding with no owner is the
    /// Write-Defaults-dependent case — the one that breaks when a merge tool (Modular Avatar's
    /// "match write defaults", VRCFury) flips the merged controller's WD.
    ///
    /// <para>The binding set is the union over EVERY clip the controller references</para>
    /// — unreachable states, weight-0 layers and blend-tree children included. A clip parked on
    /// a state nothing leads to still declares a binding the Animator binds at build time, so
    /// leaving it out would hide the frames in which nothing writes it. The per-layer walk is
    /// the opposite: only reachable states can write, so only those fill <see cref="Writers"/>.
    ///
    /// <para>Two layers writing the same binding is NOT by itself the problem.</para>
    /// The problem is a frame in which nobody writes it; cross-layer overlap only matters as
    /// the source of the stale value that WD OFF then asserts.
    ///
    /// <para>Excluded: Animator-typed bindings at the root path</para>
    /// (<c>binding.type == typeof(Animator) &amp;&amp; path == ""</c>) — humanoid muscles, root
    /// motion and AAP parameters. Muscles are Write-Defaults-independent (they snap to the
    /// muscle-0 pose either way), and AAPs are <see cref="AapWriteScan"/>'s business.
    ///
    /// <para>NOT modelled, deliberately.</para>
    /// Layer weights: a weight-0 layer asserts nothing, but weights are driven at runtime by
    /// behaviours this scan cannot see, so it reads structure only and counts every layer.
    /// Motion=None freezing is <see cref="IssueKind.MissingMotion"/>'s finding, not this one's.
    /// AvatarMask is ignored, so a masked-out binding is still counted as written. Blend-tree
    /// child weights are ignored too: a child sitting at weight 0 counts as writing — a
    /// conservative choice, and an unmeasured one.
    /// </summary>
    sealed class BindingOwnership
    {
        /// <summary>Every binding the controller's clips declare, sorted, Animator-root ones
        /// excluded.</summary>
        public readonly List<BindingKey> Union = new List<BindingKey>();

        /// <summary>Every layer, in index order — silent ones included, so an index into this
        /// list is a layer index.</summary>
        public readonly List<LayerBindings> Layers = new List<LayerBindings>();

        static readonly LayerBindings[] None = new LayerBindings[0];

        public IReadOnlyList<LayerBindings> Owners(BindingKey key)
        {
            List<LayerBindings> found = null;
            foreach (var layer in Layers)
                if (layer.Owns(key)) (found ?? (found = new List<LayerBindings>())).Add(layer);
            return (IReadOnlyList<LayerBindings>)found ?? None;
        }

        public IReadOnlyList<LayerBindings> Touchers(BindingKey key)
        {
            List<LayerBindings> found = null;
            foreach (var layer in Layers)
                if (layer.Touches(key)) (found ?? (found = new List<LayerBindings>())).Add(layer);
            return (IReadOnlyList<LayerBindings>)found ?? None;
        }

        public static BindingOwnership Collect(AnimatorController controller)
        {
            var result = new BindingOwnership();
            if (controller == null) return result;

            // One clip is read once however many states and trees reach it; GetCurveBindings
            // is the expensive part of this walk.
            var cache = new Dictionary<AnimationClip, List<BindingKey>>();

            var union = new HashSet<BindingKey>();
            var seenMotions = new HashSet<Motion>();
            foreach (var state in controller.AllStates())
                Collect(state.motion, union, cache, seenMotions);

            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var entry = new LayerBindings
                {
                    LayerIndex = i,
                    LayerName = layers[i].name,
                    BlendingMode = layers[i].blendingMode,
                    DefaultWeight = layers[i].defaultWeight,
                };
                result.Layers.Add(entry);

                var root = ControllerReachability.PlayedMachine(controller, i);
                if (root == null) continue;   // no state machine at all: zero states, silent
                var reachable = ControllerReachability.ReachableStates(root);
                bool synced = layers[i].syncedLayerIndex >= 0;

                foreach (var sm in root.SelfAndDescendants())
                    foreach (var cs in sm.states)
                    {
                        var state = cs.state;
                        if (state == null || !reachable.Contains(state)) continue;
                        // A synced layer replays the source layer's states, each of which may
                        // carry an override motion and falls back to the source's when it does not.
                        var motion = synced ? layers[i].GetOverrideMotion(state) ?? state.motion : state.motion;
                        entry.States.Add(state);
                        if (motion == null)
                        {
                            entry.SilentStates++;
                            continue;
                        }
                        entry.PlayingStates++;

                        var written = new HashSet<BindingKey>();
                        seenMotions.Clear();
                        Collect(motion, written, cache, seenMotions);
                        foreach (var key in written)
                        {
                            if (!entry.Writers.TryGetValue(key, out var states))
                                entry.Writers[key] = states = new List<AnimatorState>();
                            states.Add(state);
                        }
                        // The walk over AllStates above saw every state's own motion; a synced
                        // layer's override motions hang off the layer, not the state, so they
                        // reach the union only through here.
                        union.UnionWith(written);
                    }
            }

            result.Union.AddRange(union);
            result.Union.Sort();
            return result;
        }

        static void Collect(Motion motion, HashSet<BindingKey> into,
            Dictionary<AnimationClip, List<BindingKey>> cache, HashSet<Motion> visited)
        {
            if (motion == null || !visited.Add(motion)) return;
            if (motion is BlendTree tree)
            {
                foreach (var child in tree.children) Collect(child.motion, into, cache, visited);
                return;
            }
            if (!(motion is AnimationClip clip)) return;
            foreach (var key in BindingsOf(clip, cache)) into.Add(key);
        }

        static List<BindingKey> BindingsOf(AnimationClip clip,
            Dictionary<AnimationClip, List<BindingKey>> cache)
        {
            if (cache.TryGetValue(clip, out var found)) return found;

            var keys = new List<BindingKey>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (IsAnimatorRoot(binding)) continue;
                // A curve with no keys is bound but writes nothing — same skip ClipDigest makes.
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;
                keys.Add(BindingKey.Of(binding));
            }
            // Material and mesh swaps are bindings too, and a layer that swaps a material in
            // one state only has exactly the problem this scan is about.
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (IsAnimatorRoot(binding)) continue;
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                if (curve == null || curve.Length == 0) continue;
                keys.Add(BindingKey.Of(binding));
            }

            cache[clip] = keys;
            return keys;
        }

        /// <summary>Humanoid muscles, root motion and AAPs — see the class doc for why they are
        /// not part of the question.</summary>
        static bool IsAnimatorRoot(EditorCurveBinding binding) =>
            binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path);
    }
}
