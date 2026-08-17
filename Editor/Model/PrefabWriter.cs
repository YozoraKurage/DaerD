using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
// UnityEditor has a PackageInfo of its own (the asset-store kind), so the package manager's has
// to be named outright.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
#if DAERD_MA
using MaParameters = nadena.dev.modular_avatar.core.ModularAvatarParameters;
#endif

namespace Yozolab.DaerD
{
    /// <summary>Why a prefab cannot be written to. Every value is something to say by name —
    /// none of them is a condition to work around.</summary>
    enum PrefabWriteRefusal
    {
        None,
        /// <summary>Not a prefab asset at all (a scene object, or an object with no file).</summary>
        NotAPrefabAsset,
        /// <summary>Inside a package the package manager owns. Writing there either fails or is
        /// undone by the next resolve, and either way it is not the user's file.</summary>
        ImmutablePackage,
        /// <summary>Open in prefab mode with edits nobody has saved. Writing the file underneath
        /// an open stage means one of the two sets of changes is about to lose.</summary>
        OpenWithUnsavedEdits,
    }

    /// <summary>
    /// The one way DaerD changes the STRUCTURE of a prefab: refuse first, then load the whole
    /// prefab, make every change, save once.
    ///
    /// <para>WHY IT IS SHAPED LIKE THIS.</para>
    /// Saving an asset cannot be undone. There is no Ctrl+Z that takes a component back out of a
    /// prefab file, so the substitute is that the user is told what is about to be added before
    /// it happens, and that a refusal is decided BEFORE anything is opened rather than discovered
    /// halfway through. Loading the prefab's contents, changing them and saving once is the same
    /// discipline in the other direction: a half-applied change is not a state this can end in.
    ///
    /// <para>WHY THE DECISION IS SEPARATE FROM THE WRITE.</para>
    /// <see cref="Judge"/> takes facts and returns a verdict, so what DaerD refuses can be tested
    /// without a package it is not allowed to write to actually being installed — which is the
    /// case nobody can reproduce on demand and exactly the one worth pinning.
    ///
    /// Values inside components are not this class's business: an MA Parameters row is edited
    /// through <c>ParameterStore</c>, which writes into the component where it lives and saves
    /// the asset it belongs to. Measured, and pinned by a test: that reaches the prefab file.
    /// </summary>
    static class PrefabWriter
    {
        /// <summary>The verdict for a prefab, with the project's facts collected for
        /// <see cref="Judge"/>.</summary>
        public static PrefabWriteRefusal Check(GameObject prefab)
        {
            if (prefab == null) return PrefabWriteRefusal.NotAPrefabAsset;
            var path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path) || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                return PrefabWriteRefusal.NotAPrefabAsset;

            var package = PackageInfo.FindForAssetPath(path);
            // Only the stage that is open right now is asked about. Drilling into a nested
            // prefab stacks stages and this sees the innermost — stated rather than worked
            // around, because the case it guards is the common one (the prefab being edited is
            // the prefab being written to) and a wrong answer here is a refusal, not a loss.
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            bool unsaved = stage != null && stage.assetPath == path && stage.scene.isDirty;

            return Judge(path, package?.source, unsaved);
        }

        /// <summary>
        /// The decision itself, over facts rather than over the project.
        ///
        /// Embedded and Local packages are writable and are refused by nobody: a VPM project
        /// keeps its dependencies as real folders under <c>Packages/</c>, and a gimmick being
        /// developed in one is a normal thing to be editing. Everything the package manager
        /// fetched for itself — a registry version, a git dependency, a tarball — is refused:
        /// the write either fails outright or is thrown away by the next resolve, and a change
        /// that disappears without saying so is worse than one that never happened.
        /// A null source is an asset under <c>Assets/</c>, which is the user's own project.
        /// </summary>
        public static PrefabWriteRefusal Judge(string assetPath, PackageSource? source,
            bool unsavedInPrefabMode)
        {
            if (string.IsNullOrEmpty(assetPath)) return PrefabWriteRefusal.NotAPrefabAsset;
            if (source.HasValue && source.Value != PackageSource.Embedded
                && source.Value != PackageSource.Local)
                return PrefabWriteRefusal.ImmutablePackage;
            if (unsavedInPrefabMode) return PrefabWriteRefusal.OpenWithUnsavedEdits;
            return PrefabWriteRefusal.None;
        }

        /// <summary>
        /// Adds an MA Parameters component to the prefab's ROOT and saves the file, returning
        /// the component as it exists in the saved asset (the one made during the edit dies with
        /// the loaded contents). Null where Modular Avatar is absent, where the prefab already
        /// has one on its root, or where the file could not be loaded.
        ///
        /// <para>WHY THE ROOT.</para>
        /// Modular Avatar reads a merge's parameters from the merge's own object or the nearest
        /// parent that has them, so a component on the root is the one place every Merge Animator
        /// in the prefab can see — including ones added later, and including a merge that lives
        /// several objects down. Putting it beside the merge instead would declare parameters for
        /// that merge alone and leave the next one to be asked about again.
        ///
        /// The caller is responsible for having refused first (<see cref="Check"/>) and for
        /// having asked the user: this writes the file.
        /// </summary>
        public static Object AddParameters(GameObject prefab)
        {
#if DAERD_MA
            if (prefab == null) return null;
            var path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path)) return null;

            var contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null) return null;
            try
            {
                if (contents.GetComponent<MaParameters>() != null) return null;
                contents.AddComponent<MaParameters>();
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                // In a finally so a throw between the load and the save cannot leave the loaded
                // copy — an invisible scene of its own — behind for the rest of the session.
                PrefabUtility.UnloadPrefabContents(contents);
            }

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return saved != null ? saved.GetComponent<MaParameters>() : null;
#else
            return null;
#endif
        }
    }
}
