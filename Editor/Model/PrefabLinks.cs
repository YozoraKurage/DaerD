using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif

namespace Yozolab.DaerD
{
    /// <summary>
    /// What the saved pin (<see cref="GraphFrameData.PrefabLink"/>) resolves to right now.
    /// Every value but <see cref="None"/> and <see cref="Healthy"/> is something to say out loud
    /// with names in it — the pin is the user's statement about their own project, so DaerD
    /// reports what it cannot confirm rather than quietly repairing it.
    /// </summary>
    enum PrefabLinkState
    {
        /// <summary>No pin was ever set (or it was cleared).</summary>
        None,
        /// <summary>Both references resolve and the merge names this very controller.</summary>
        Healthy,
        /// <summary>The prefab asset is gone — deleted, or on a branch that is not checked out.</summary>
        PrefabMissing,
        /// <summary>The prefab is there and the merge inside it is not (deleted component, or an
        /// object the prefab no longer has).</summary>
        MergeMissing,
        /// <summary>The merge is alive and merges a DIFFERENT controller. Somebody re-pointed it,
        /// and guessing which of the two is now the mistake is not DaerD's call.</summary>
        Diverged,
        /// <summary>Modular Avatar is not installed, so the merge cannot be read at all. The saved
        /// pin is untouched and comes back the moment MA does.</summary>
        Unverifiable,
    }

    /// <summary>The pin as resolved for display: the state plus the objects the UI has to name.</summary>
    class PrefabLinkStatus
    {
        public PrefabLinkState state = PrefabLinkState.None;
        /// <summary>The pinned prefab, or null when it no longer resolves.</summary>
        public GameObject prefab;
        /// <summary>The pinned merge, or null when it no longer resolves.</summary>
        public Object mergeAnimator;
        /// <summary>What the merge merges instead, filled only for <see cref="PrefabLinkState.Diverged"/>
        /// — the whole point of that state is being able to name it.</summary>
        public Object mergedController;

        public bool IsHealthy => state == PrefabLinkState.Healthy;
    }

    /// <summary>
    /// One prefab that merges a controller, and the merge inside it that says so — what the
    /// project sweep hands back for the user to pick from.
    /// </summary>
    class PrefabLinkCandidate
    {
        public GameObject prefab;
        public Object mergeAnimator;
    }

    /// <summary>
    /// The prefab side of a controller: reading the saved pin, and finding what could be pinned.
    ///
    /// Split in two on purpose, because the two halves cost wildly different things.
    /// <see cref="Status"/> resolves references and asks the merge one question, so it is cheap
    /// enough for a repaint and is what the tab strip and the home screen draw from.
    /// <see cref="FindCandidates"/> walks the project, so it only ever runs from a button
    /// (ADR 0028) and remembers its answer until an asset import drops it.
    ///
    /// <para>THE GUARD.</para>
    /// Both Modular Avatar and the VRChat SDK are needed to say anything at all here: the merge
    /// component is MA's, and MA's own assembly only defines it where the SDK is present, so the
    /// pair is the same one <c>ParameterStore.MaStore</c>'s merge lookups are written behind.
    /// Without them the saved pin is still read and still shown — as
    /// <see cref="PrefabLinkState.Unverifiable"/>, which is the honest answer — and never written.
    /// </summary>
    static class PrefabLinks
    {
        /// <summary>
        /// The pin resolved. Reference resolution and (where MA is installed) one field read: no
        /// sweep, no prefab loaded, nothing written. That is the contract that lets a repaint call
        /// this — and the reason the "is it really ours" question is asked of the merge that is
        /// already pinned rather than of the project.
        /// </summary>
        public static PrefabLinkStatus Status(AnimatorController controller)
        {
            var status = new PrefabLinkStatus();
            var link = GraphFrameData.GetPrefabLink(controller);
            // Reference emptiness, not Unity's: a pin whose prefab was deleted still HAS a pin,
            // and reporting that as "never linked" is the one answer that loses information.
            if (link == null || (ReferenceEquals(link.prefab, null)
                    && ReferenceEquals(link.mergeAnimator, null)))
                return status;

            status.prefab = link.prefab;
            status.mergeAnimator = link.mergeAnimator;

            if (link.prefab == null)
            {
                status.state = PrefabLinkState.PrefabMissing;
                return status;
            }
            if (link.mergeAnimator == null)
            {
                status.state = PrefabLinkState.MergeMissing;
                return status;
            }

#if DAERD_MA && DAERD_VRC
            var merge = link.mergeAnimator as MaMergeAnimator;
            if (merge == null)
            {
                // MA is installed and the pinned object is not one of its merges. That is a
                // broken pin rather than an unanswerable one — the reference resolves, it just
                // does not point at something that can merge anything.
                status.state = PrefabLinkState.MergeMissing;
                return status;
            }
            status.mergedController = merge.animator;
            status.state = merge.animator == controller
                ? PrefabLinkState.Healthy
                : PrefabLinkState.Diverged;
#else
            status.state = PrefabLinkState.Unverifiable;
#endif
            return status;
        }

        /// <summary>Where the merge sits inside its prefab, as a path from the root — derived
        /// from the reference every time it is shown rather than saved, because a saved path dies
        /// the first time somebody renames an object (measured in a headless probe; the reference
        /// survives renames, reparenting and re-saves). The prefab's own name for a merge on the
        /// root, so the answer is never an empty label.</summary>
        public static string PathIn(GameObject prefab, Object mergeAnimator)
        {
            var component = mergeAnimator as Component;
            if (prefab == null || component == null) return string.Empty;
            if (component.transform == prefab.transform) return prefab.name;
            if (!component.transform.IsChildOf(prefab.transform)) return component.name;
            return AnimationUtility.CalculateTransformPath(component.transform, prefab.transform);
        }

        // ---- the project sweep ------------------------------------------------

        /// <summary>Candidates per controller GUID, including "none" — a sweep that found nothing
        /// is the expensive one and the one most likely to be repeated. The same bargain
        /// <c>ParameterStore.MaStore</c> makes, dropped by the same import watcher.</summary>
        static readonly Dictionary<string, List<PrefabLinkCandidate>> s_candidates =
            new Dictionary<string, List<PrefabLinkCandidate>>();

        /// <summary>How many project sweeps have run since the editor started, so a test can
        /// assert that the answer is remembered rather than recomputed.</summary>
        internal static int Scans { get; private set; }

        internal static void ForgetCandidates() => s_candidates.Clear();

        /// <summary>
        /// Every prefab in the project whose MA Merge Animator names this controller, with the
        /// merge that names it. Unlike the parameter store's sweep this does not stop at the
        /// first hit: the point of the list is that a person picks from it, and a sweep that
        /// stopped early would hide the second prefab precisely when the question matters.
        ///
        /// Nested prefabs are found as themselves, for the reason
        /// <see cref="ParameterStore.DetectInPrefabs"/> gives: a prefab that merely CONTAINS the
        /// one carrying the merge does not depend on the controller directly, and the inner
        /// prefab is in the sweep on its own account anyway.
        /// </summary>
        public static List<PrefabLinkCandidate> FindCandidates(AnimatorController controller)
        {
            if (controller == null) return new List<PrefabLinkCandidate>();
            var path = AssetDatabase.GetAssetPath(controller);
            // A controller that is not a file cannot be any prefab's dependency, so there is
            // nothing to match on — and no key to remember it under.
            if (string.IsNullOrEmpty(path)) return new List<PrefabLinkCandidate>();
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (!s_candidates.TryGetValue(guid, out var known))
                s_candidates[guid] = known = Sweep(controller, path);
            // A copy: the caller sorts and filters its own list, and the cache is the answer.
            return new List<PrefabLinkCandidate>(known);
        }

        static List<PrefabLinkCandidate> Sweep(AnimatorController controller, string controllerPath)
        {
            Scans++;
            var found = new List<PrefabLinkCandidate>();
#if DAERD_MA && DAERD_VRC
            foreach (var root in PrefabAssetSweep.Depending(controllerPath))
                Collect(root, controller, found);
#endif
            return found;
        }

        /// <summary>The same question asked of ONE prefab, for a drag-and-drop: the user has
        /// already said which prefab, so there is nothing to sweep for and nothing to remember.
        /// </summary>
        public static List<PrefabLinkCandidate> FindCandidatesIn(GameObject prefab,
            AnimatorController controller)
        {
            var found = new List<PrefabLinkCandidate>();
            if (prefab == null || controller == null) return found;
#if DAERD_MA && DAERD_VRC
            Collect(prefab, controller, found);
#endif
            return found;
        }

#if DAERD_MA && DAERD_VRC
        static void Collect(GameObject root, AnimatorController controller,
            List<PrefabLinkCandidate> into)
        {
            if (root == null) return;
            foreach (var merge in root.GetComponentsInChildren<MaMergeAnimator>(true))
            {
                if (merge == null || merge.animator != controller) continue;
                into.Add(new PrefabLinkCandidate { prefab = root, mergeAnimator = merge });
            }
        }
#endif
    }

    /// <summary>
    /// The two-stage walk over the project's prefabs, shared by everything that has to ask "which
    /// prefabs mention this asset": the parameter store's sweep and the prefab link's.
    ///
    /// <c>FindAssets</c> gives every prefab in the project and <c>GetDependencies</c> answers out
    /// of the import database's own table, so the first stage costs a lookup each and opens
    /// nothing. Only a prefab whose table already names the asset is loaded, and loading a prefab
    /// pulls its meshes, materials and textures in with it — which is the whole reason this is
    /// not simply a loop over <c>LoadAssetAtPath</c>.
    ///
    /// Direct dependencies only. A prefab reaches a controller through a component field, which is
    /// a direct reference; asking recursively would drag in every controller referenced by every
    /// nested prefab and material, and put prefabs through stage two that cannot possibly match.
    ///
    /// Lazy, and that is load-bearing rather than tidy: a caller that stops at the first match
    /// stops the loading with it, which is what keeps the store's sweep as cheap as it was before
    /// this was shared, and what <see cref="Loads"/> is counted for.
    /// </summary>
    static class PrefabAssetSweep
    {
        /// <summary>How many prefabs the sweeps have opened since the editor started. Exists so a
        /// test can assert the claim above: that the dependency table keeps prefabs which have
        /// nothing to do with the asset from ever being loaded.</summary>
        public static int Loads { get; private set; }

        public static IEnumerable<GameObject> Depending(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) yield break;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                if (System.Array.IndexOf(AssetDatabase.GetDependencies(path, false), assetPath) < 0)
                    continue;

                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) continue;
                Loads++;
                yield return root;
            }
        }
    }
}
