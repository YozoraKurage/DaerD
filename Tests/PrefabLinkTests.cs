using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The pin that says which gimmick prefab a controller belongs to: that it is saved, that it
    /// survives what a prefab goes through in a real project, and that DaerD reports what it
    /// cannot confirm instead of tidying it away.
    ///
    /// Two claims are worth more than the rest and are the reason several of these tests write
    /// prefabs to disk rather than building objects in memory:
    /// <list type="bullet">
    /// <item>a reference INTO a prefab survives being renamed, reparented and re-saved, so the
    /// pin can be a reference and the path can be derived from it — a saved path string would be
    /// wrong after the first rename;</item>
    /// <item>a reference that stops resolving is left exactly as it is. That one cannot be
    /// checked by reading the code, because the failure mode is a well-meaning normalization
    /// somewhere else writing null over a pin that was only temporarily unreadable.</item>
    /// </list>
    ///
    /// Whatever needs a Merge Animator to exist is skipped by name where Modular Avatar is
    /// absent, so both runs have the same number of tests in them.
    /// </summary>
    public class PrefabLinkTests
    {
        const string Folder = "Assets/DDPrefabLink";
        const string ControllerPath = Folder + "/Gimmick.controller";
        const string OtherControllerPath = Folder + "/Other.controller";
        const string GimmickPrefab = Folder + "/Gimmick.prefab";
        const string SecondPrefab = Folder + "/Second.prefab";
        const string DecoyPrefab = Folder + "/Decoy.prefab";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDPrefabLink");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            PrefabLinks.ForgetCandidates();
            GraphFrameData.ForgetHolders();
        }

        static AnimatorController Controller(string path)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddLayer("Base");
            return controller;
        }

        /// <summary>A prefab asset with one object under the root, so a test has something to
        /// rename and reparent. Nothing here is in a scene: the objects are built, written and
        /// destroyed.</summary>
        static GameObject BarePrefab(string path)
        {
            var built = new GameObject("Root");
            var mid = new GameObject("Mid");
            mid.transform.SetParent(built.transform);
            var leaf = new GameObject("Leaf");
            leaf.transform.SetParent(mid.transform);
            var saved = PrefabUtility.SaveAsPrefabAsset(built, path);
            Object.DestroyImmediate(built);
            return saved;
        }

#if DAERD_MA && DAERD_VRC
        /// <summary>The same shape with an MA Merge Animator on the leaf, naming
        /// <paramref name="merged"/>.</summary>
        static GameObject Prefab(string path, AnimatorController merged)
        {
            var built = new GameObject("Root");
            var mid = new GameObject("Mid");
            mid.transform.SetParent(built.transform);
            var leaf = new GameObject("Leaf");
            leaf.transform.SetParent(mid.transform);
            leaf.AddComponent<MaMergeAnimator>().animator = merged;
            var saved = PrefabUtility.SaveAsPrefabAsset(built, path);
            Object.DestroyImmediate(built);
            return saved;
        }

        static MaMergeAnimator MergeIn(string prefabPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return root == null ? null : root.GetComponentInChildren<MaMergeAnimator>(true);
        }

        /// <summary>Pins the one merge in <paramref name="prefabPath"/> as the controller's home.</summary>
        static void Pin(AnimatorController controller, string prefabPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            GraphFrameData.SetPrefabLink(controller, root, MergeIn(prefabPath));
        }
#endif

        /// <summary>Writes the controller (and the holder inside it) out and reads it back off
        /// disk, which is the only way to tell a saved field from a remembered one.</summary>
        static AnimatorController Reimported()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ControllerPath, ImportAssetOptions.ForceUpdate);
            GraphFrameData.ForgetHolders();
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        }

        // ---- storage ----------------------------------------------------------

        [Test]
        public void ThePinIsSavedInTheControllerAndComesBackOffDisk()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);

            var reloaded = Reimported();
            var link = GraphFrameData.GetPrefabLink(reloaded);
            Assert.IsNotNull(link);
            Assert.AreSame(AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab), link.prefab);
            Assert.AreSame(MergeIn(GimmickPrefab), link.mergeAnimator,
                "the merge is stored as a reference INTO the prefab, not as a path to it");
            Assert.AreEqual(PrefabLinkState.Healthy, PrefabLinks.Status(reloaded).state);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void ClearingThePinEmptiesItAndSurvivesTheRoundTrip()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);
            GraphFrameData.ClearPrefabLink(controller);

            Assert.AreEqual(PrefabLinkState.None, PrefabLinks.Status(controller).state);

            var reloaded = Reimported();
            var link = GraphFrameData.GetPrefabLink(reloaded);
            Assert.IsNull(link.prefab);
            Assert.IsNull(link.mergeAnimator);
            Assert.AreEqual(PrefabLinkState.None, PrefabLinks.Status(reloaded).state);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void ClearingAControllerThatWasNeverPinnedDoesNotGiveItAHolder()
        {
            var controller = Controller(ControllerPath);
            Assert.IsNull(GraphFrameData.Find(controller), "nothing has been stored on it yet");

            GraphFrameData.ClearPrefabLink(controller);

            Assert.IsNull(GraphFrameData.Find(controller),
                "clearing a pin that does not exist must not add a sub-asset to the controller");
            Assert.AreEqual(PrefabLinkState.None, PrefabLinks.Status(controller).state);
        }

        /// <summary>
        /// The slot takes any Object at all, and this is what that buys: the record is written
        /// and read back by code that does not know or care what Modular Avatar's types are. A
        /// component that is obviously not a merge stands in for the case this really guards —
        /// the same controller opened on a machine where MA is not installed, where the merge
        /// resolves to something DaerD cannot interpret and must not touch.
        /// </summary>
        [Test]
        public void APinPointingAtSomethingUnreadableIsKeptWordForWord()
        {
            var controller = Controller(ControllerPath);
            var prefab = BarePrefab(GimmickPrefab);
            var standIn = prefab.transform.Find("Mid");
            GraphFrameData.SetPrefabLink(controller, prefab, standIn);

            // Resolving the pin is a read. Asking repeatedly is how a normalization would show
            // up: the second answer would differ from the first.
            for (int i = 0; i < 3; i++)
            {
                var status = PrefabLinks.Status(controller);
                Assert.AreNotEqual(PrefabLinkState.None, status.state,
                    "something IS pinned, whether or not this project can read it");
                Assert.AreNotEqual(PrefabLinkState.Healthy, status.state);
            }

            var reloaded = Reimported();
            var link = GraphFrameData.GetPrefabLink(reloaded);
            Assert.AreSame(AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab), link.prefab);
            Assert.IsNotNull(link.mergeAnimator, "the reference is still there after a round trip");
        }

        // ---- state -------------------------------------------------------------

        [Test]
        public void AMergeThatNamesAnotherControllerReadsAsDiverged()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var other = Controller(OtherControllerPath);
            Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);

            var merge = MergeIn(GimmickPrefab);
            merge.animator = other;

            var status = PrefabLinks.Status(controller);
            Assert.AreEqual(PrefabLinkState.Diverged, status.state);
            Assert.AreSame(other, status.mergedController,
                "the state exists so the UI can name what it merges instead");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void ADeletedPrefabReadsAsMissingAndTheRecordIsLeftAlone()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);

            AssetDatabase.DeleteAsset(GimmickPrefab);

            Assert.AreEqual(PrefabLinkState.PrefabMissing, PrefabLinks.Status(controller).state);
            var link = GraphFrameData.GetPrefabLink(controller);
            Assert.IsFalse(ReferenceEquals(link.prefab, null),
                "the reference is unresolvable, not absent — writing null over it would turn "
                + "'I cannot see this right now' into 'there was never one'");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AMergeDeletedOutOfTheStillLivingPrefabReadsAsMergeMissing()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);

            var contents = PrefabUtility.LoadPrefabContents(GimmickPrefab);
            Object.DestroyImmediate(contents.GetComponentInChildren<MaMergeAnimator>(true));
            PrefabUtility.SaveAsPrefabAsset(contents, GimmickPrefab);
            PrefabUtility.UnloadPrefabContents(contents);

            var status = PrefabLinks.Status(controller);
            Assert.IsNotNull(status.prefab, "the prefab itself is still there");
            Assert.AreEqual(PrefabLinkState.MergeMissing, status.state);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// The reason the pin is a pair of references and not a pair of paths. Measured in a
        /// headless probe before any of this was written and pinned here as a regression: the
        /// object carrying the merge is renamed, moved to a different parent and the prefab is
        /// saved over itself, and the link is still the same link — with the path it reports
        /// following the object rather than describing where it used to be.
        /// </summary>
        [Test]
        public void RenamingAndReparentingTheMergeInsideThePrefabKeepsTheLink()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);
            Assert.AreEqual("Mid/Leaf",
                PrefabLinks.PathIn(GraphFrameData.GetPrefabLink(controller).prefab,
                    GraphFrameData.GetPrefabLink(controller).mergeAnimator));

            var contents = PrefabUtility.LoadPrefabContents(GimmickPrefab);
            var leaf = contents.GetComponentInChildren<MaMergeAnimator>(true).transform;
            leaf.name = "LeafRenamed";
            leaf.SetParent(contents.transform);
            PrefabUtility.SaveAsPrefabAsset(contents, GimmickPrefab);
            PrefabUtility.UnloadPrefabContents(contents);

            var status = PrefabLinks.Status(controller);
            Assert.AreEqual(PrefabLinkState.Healthy, status.state,
                "a reference into a prefab survives a rename, a reparent and a re-save");
            Assert.AreEqual("LeafRenamed", PrefabLinks.PathIn(status.prefab, status.mergeAnimator),
                "and the path is derived from it, so it follows the object");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AMergeOnTheRootIsReportedByThePrefabsOwnName()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var built = new GameObject("Root");
            built.AddComponent<MaMergeAnimator>().animator = controller;
            PrefabUtility.SaveAsPrefabAsset(built, GimmickPrefab);
            Object.DestroyImmediate(built);
            Pin(controller, GimmickPrefab);

            var status = PrefabLinks.Status(controller);
            Assert.AreEqual(PrefabLinkState.Healthy, status.state);
            // Saving takes the file's name, so the root of this asset is called "Gimmick"
            // whatever the object it was built from was called.
            Assert.AreEqual("Gimmick", PrefabLinks.PathIn(status.prefab, status.mergeAnimator),
                "an empty path would be an empty label");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- the sweep ---------------------------------------------------------

        [Test]
        public void TheSweepListsEveryPrefabThatMergesThisControllerAndNothingElse()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var other = Controller(OtherControllerPath);
            Prefab(GimmickPrefab, controller);
            Prefab(SecondPrefab, controller);
            Prefab(DecoyPrefab, other);

            var candidates = PrefabLinks.FindCandidates(controller);
            Assert.AreEqual(2, candidates.Count,
                "both prefabs merge this controller, and the choice between them is the "
                + "user's — a sweep that stopped at the first would hide the question");
            var names = new System.Collections.Generic.List<string>();
            foreach (var candidate in candidates)
            {
                names.Add(candidate.prefab.name);
                Assert.IsNotNull(candidate.mergeAnimator);
            }
            CollectionAssert.Contains(names, "Gimmick");
            CollectionAssert.Contains(names, "Second");
            CollectionAssert.DoesNotContain(names, "Decoy");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TheSweepIsRememberedAndOnlyOpensPrefabsTheDependencyTableNames()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var other = Controller(OtherControllerPath);
            Prefab(GimmickPrefab, controller);
            Prefab(DecoyPrefab, other);

            PrefabLinks.ForgetCandidates();
            int scans = PrefabLinks.Scans;
            int loads = ParameterStore.PrefabLoads;

            Assert.AreEqual(1, PrefabLinks.FindCandidates(controller).Count);
            Assert.AreEqual(scans + 1, PrefabLinks.Scans);
            Assert.AreEqual(loads + 1, ParameterStore.PrefabLoads,
                "the decoy carries a merge too, and the sweep must never have opened it to "
                + "know that — the dependency table already said it names another controller");

            Assert.AreEqual(1, PrefabLinks.FindCandidates(controller).Count);
            Assert.AreEqual(scans + 1, PrefabLinks.Scans,
                "pressing the button twice must not sweep the project twice");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AControllerThatIsNotAFileIsAnsweredForRatherThanSwept()
        {
            var loose = new AnimatorController();
            loose.AddLayer("Base");
            PrefabLinks.ForgetCandidates();
            int scans = PrefabLinks.Scans;

            Assert.IsEmpty(PrefabLinks.FindCandidates(loose));
            Assert.IsEmpty(PrefabLinks.FindCandidates(null));
            Assert.AreEqual(scans, PrefabLinks.Scans,
                "a controller with no path cannot be any prefab's dependency");
            Object.DestroyImmediate(loose);
        }

        [Test]
        public void TheSinglePrefabSweepLooksOnlyInsideThePrefabItWasGiven()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Prefab(SecondPrefab, controller);

            PrefabLinks.ForgetCandidates();
            int scans = PrefabLinks.Scans;

            var found = PrefabLinks.FindCandidatesIn(
                AssetDatabase.LoadAssetAtPath<GameObject>(SecondPrefab), controller);

            Assert.AreEqual(1, found.Count);
            Assert.AreEqual("Second", found[0].prefab.name);
            Assert.AreEqual(scans, PrefabLinks.Scans,
                "the user already said which prefab, so there is nothing to sweep for");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }
    }
}
