using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif

namespace Yozolab.DaerD.Bridge
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

    /// <summary>What a scan turned up, as the three answers the UI has to tell apart.</summary>
    enum PrefabLinkChoice
    {
        /// <summary>Nothing merges this controller — say so by name, ask nothing.</summary>
        Nothing,
        /// <summary>Exactly one, so there is nothing to choose: confirm it and link.</summary>
        One,
        /// <summary>Several, and picking between them is the user's call, never DaerD's.</summary>
        Several,
    }

    /// <summary>
    /// Everything linking to one candidate would do, worked out before anything is written.
    ///
    /// It exists so the confirmation dialog and the write are the same decision: a dialog that
    /// described the write in its own words would be a second implementation of it, free to
    /// drift, and this one is about to touch two saved fields at once. It is also what makes the
    /// decision testable without a dialog on screen.
    /// </summary>
    class PrefabLinkPlan
    {
        public PrefabLinkCandidate candidate;
        /// <summary>The MA Parameters that governs the candidate's merge, or null when the
        /// prefab has none (or MA is not installed).</summary>
        public Object store;
        /// <summary>What the controller's parameter store slot holds right now.</summary>
        public Object currentStore;

        /// <summary>Whether linking also fills the store slot. Only ever true for an EMPTY slot:
        /// a slot somebody filled by hand is an answer, and silently replacing it while they
        /// pressed a button labelled "link a prefab" would be DaerD editing something it was
        /// not asked about.</summary>
        public bool FillsStore => store != null && currentStore == null;

        /// <summary>The slot is filled with something other than what this prefab offers. Not an
        /// error and not a thing to fix — the store is offered as a button instead.</summary>
        public bool StoreDiffers => store != null && currentStore != null && currentStore != store;
    }

    /// <summary>A scan and what to do with it, so the branch is decided in one place rather
    /// than by whichever caller counted the list.</summary>
    class PrefabLinkScan
    {
        public List<PrefabLinkCandidate> candidates = new List<PrefabLinkCandidate>();
        public PrefabLinkChoice choice = PrefabLinkChoice.Nothing;
        /// <summary>Filled only for <see cref="PrefabLinkChoice.One"/> — with several, the plan
        /// cannot be made until the user has said which.</summary>
        public PrefabLinkPlan plan;
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

        // ---- linking -----------------------------------------------------------

        /// <summary>What linking to <paramref name="candidate"/> would do. Reads both saved
        /// fields; writes neither.</summary>
        public static PrefabLinkPlan PlanFor(AnimatorController controller,
            PrefabLinkCandidate candidate)
        {
            if (candidate == null) return null;
            return new PrefabLinkPlan
            {
                candidate = candidate,
                store = ParameterStore.StoreFor(candidate.mergeAnimator),
                currentStore = GraphFrameData.GetParameterStore(controller),
            };
        }

        /// <summary>
        /// Writes the plan: the pin, and the store slot when the plan says it fills it. One call
        /// rather than two at the call site, because the second write is conditional and the
        /// condition is the plan's — a caller that decided it again could decide it differently
        /// from the dialog the user just read.
        /// </summary>
        public static void Apply(AnimatorController controller, PrefabLinkPlan plan)
        {
            if (controller == null || plan == null || plan.candidate == null) return;
            GraphFrameData.SetPrefabLink(controller, plan.candidate.prefab,
                plan.candidate.mergeAnimator);
            if (plan.FillsStore)
                GraphFrameData.SetParameterStore(controller, plan.store);
        }

        /// <summary>The project scan, as the branch its caller has to draw.</summary>
        public static PrefabLinkScan ScanFor(AnimatorController controller) =>
            Decide(controller, FindCandidates(controller));

        /// <summary>The same for one prefab the user has already named (a drag-and-drop).</summary>
        public static PrefabLinkScan ScanIn(GameObject prefab, AnimatorController controller) =>
            Decide(controller, FindCandidatesIn(prefab, controller));

        static PrefabLinkScan Decide(AnimatorController controller,
            List<PrefabLinkCandidate> candidates)
        {
            var scan = new PrefabLinkScan { candidates = candidates };
            if (candidates.Count == 1)
            {
                scan.choice = PrefabLinkChoice.One;
                scan.plan = PlanFor(controller, candidates[0]);
            }
            else if (candidates.Count > 1)
            {
                scan.choice = PrefabLinkChoice.Several;
            }
            return scan;
        }

        /// <summary>The MA Parameters that governs the merge this controller is pinned to, or
        /// null. What the home screen offers as a button when the store slot holds something
        /// else — offered, never applied on its own (design: the slot is the user's answer).
        /// </summary>
        public static Object StoreOf(PrefabLinkStatus status) =>
            status != null && status.IsHealthy ? ParameterStore.StoreFor(status.mergeAnimator) : null;

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
        /// Nested prefabs are found as themselves. A prefab that merely CONTAINS the one carrying
        /// the merge does not depend on the controller directly, so the dependency table skips it
        /// — and the inner prefab is in the sweep on its own account anyway, which is the one
        /// somebody would want to link to.
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

    /// <summary>
    /// The sweep's one blind spot: prefabs change on disk without any code of DaerD's running.
    /// A pull can add the Merge Animator that would have been the answer, or take away the prefab
    /// that was. Dropping every candidate list on any import costs a button press to refill and
    /// is the only invalidation that cannot be wrong about which import mattered.
    ///
    /// It used to drop the parameter store's project sweep as well, which was the second thing
    /// that walked these prefabs. That sweep is gone — the link finds the prefab and the store
    /// comes from the merge it names — so there is one memory left to invalidate and it lives
    /// beside it.
    /// </summary>
    class PrefabLinkImportWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted,
            string[] moved, string[] movedFrom) => PrefabLinks.ForgetCandidates();
    }
}
