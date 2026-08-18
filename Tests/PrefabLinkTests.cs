using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.PackageManager;
using UnityEngine;
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif
using Yozolab.DaerD.Bridge;

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
        /// <paramref name="merged"/>. With <paramref name="withStore"/> the root also carries an
        /// MA Parameters — the ordinary gimmick shape, where the merge is somewhere inside and
        /// the declaration sits above it.</summary>
        static GameObject Prefab(string path, AnimatorController merged, bool withStore = false)
        {
            var built = new GameObject("Root");
            if (withStore)
                built.AddComponent<nadena.dev.modular_avatar.core.ModularAvatarParameters>();
            var mid = new GameObject("Mid");
            mid.transform.SetParent(built.transform);
            var leaf = new GameObject("Leaf");
            leaf.transform.SetParent(mid.transform);
            leaf.AddComponent<MaMergeAnimator>().animator = merged;
            var saved = PrefabUtility.SaveAsPrefabAsset(built, path);
            Object.DestroyImmediate(built);
            return saved;
        }

        static PrefabLinkCandidate CandidateIn(string prefabPath, AnimatorController controller)
        {
            var found = PrefabLinks.FindCandidatesIn(
                AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), controller);
            Assert.AreEqual(1, found.Count, "the fixture puts exactly one merge in each prefab");
            return found[0];
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

        /// <summary>Whether the FILE holds this text, whichever way the project serialises its
        /// assets — Unity writes string fields as UTF-8 in both modes. A dirty asset nobody
        /// saved looks exactly like a saved one until the next domain reload, so this is the
        /// only question worth asking about a write into a prefab.</summary>
        static bool FileMentions(string path, string text) =>
            Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(text);

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
            int loads = PrefabAssetSweep.Loads;

            Assert.AreEqual(1, PrefabLinks.FindCandidates(controller).Count);
            Assert.AreEqual(scans + 1, PrefabLinks.Scans);
            Assert.AreEqual(loads + 1, PrefabAssetSweep.Loads,
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

        // ---- linking -----------------------------------------------------------

        [Test]
        public void AScanThatFindsNothingHasNothingToAsk()
        {
            var controller = Controller(ControllerPath);
            PrefabLinks.ForgetCandidates();

            var scan = PrefabLinks.ScanFor(controller);

            Assert.AreEqual(PrefabLinkChoice.Nothing, scan.choice);
            Assert.IsEmpty(scan.candidates);
            Assert.IsNull(scan.plan);
        }

        [Test]
        public void AScanThatFindsOneComesWithItsPlanReady()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller, withStore: true);
            PrefabLinks.ForgetCandidates();

            var scan = PrefabLinks.ScanFor(controller);

            Assert.AreEqual(PrefabLinkChoice.One, scan.choice);
            Assert.IsNotNull(scan.plan);
            Assert.AreEqual("Gimmick", scan.plan.candidate.prefab.name);
            Assert.IsTrue(scan.plan.FillsStore,
                "nothing is in the slot yet and the prefab has an MA Parameters to put there");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AScanThatFindsSeveralLeavesTheChoiceToTheUser()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);
            Prefab(SecondPrefab, controller);
            PrefabLinks.ForgetCandidates();

            var scan = PrefabLinks.ScanFor(controller);

            Assert.AreEqual(PrefabLinkChoice.Several, scan.choice);
            Assert.AreEqual(2, scan.candidates.Count);
            Assert.IsNull(scan.plan, "there is no plan until somebody says which one");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void LinkingFillsAnEmptyParameterStoreSlotFromThePrefab()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var prefab = Prefab(GimmickPrefab, controller, withStore: true);
            Assert.IsNull(GraphFrameData.GetParameterStore(controller));

            var plan = PrefabLinks.PlanFor(controller, CandidateIn(GimmickPrefab, controller));
            Assert.IsTrue(plan.FillsStore);
            Assert.IsFalse(plan.StoreDiffers);
            PrefabLinks.Apply(controller, plan);

            Assert.AreEqual(PrefabLinkState.Healthy, PrefabLinks.Status(controller).state);
            var store = ParameterStore.Of(controller);
            Assert.IsNotNull(store, "the slot was empty, so linking filled it");
            Assert.AreEqual("MA Params", store.Kind);
            Assert.AreSame(prefab.GetComponent<
                nadena.dev.modular_avatar.core.ModularAvatarParameters>(), store.Target,
                "the one above the merge, which is where Modular Avatar itself looks");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void LinkingLeavesASlotSomebodyAlreadyFilledAlone()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller, withStore: true);
            Prefab(SecondPrefab, controller, withStore: true);
            // The slot answered by hand, with the OTHER prefab's declaration.
            var chosen = AssetDatabase.LoadAssetAtPath<GameObject>(SecondPrefab)
                .GetComponent<nadena.dev.modular_avatar.core.ModularAvatarParameters>();
            GraphFrameData.SetParameterStore(controller, chosen);

            var plan = PrefabLinks.PlanFor(controller, CandidateIn(GimmickPrefab, controller));
            Assert.IsFalse(plan.FillsStore);
            Assert.IsTrue(plan.StoreDiffers, "so the UI can offer the prefab's own as a button");
            PrefabLinks.Apply(controller, plan);

            Assert.AreEqual(PrefabLinkState.Healthy, PrefabLinks.Status(controller).state);
            Assert.AreSame(chosen, GraphFrameData.GetParameterStore(controller),
                "linking a prefab must not quietly replace an answer somebody gave");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void APrefabWithNoDeclarationLinksAnywayAndLeavesTheSlotEmpty()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller);

            var plan = PrefabLinks.PlanFor(controller, CandidateIn(GimmickPrefab, controller));
            Assert.IsNull(plan.store, "there is no MA Parameters above this merge");
            Assert.IsFalse(plan.FillsStore);
            PrefabLinks.Apply(controller, plan);

            Assert.AreEqual(PrefabLinkState.Healthy, PrefabLinks.Status(controller).state);
            Assert.IsNull(GraphFrameData.GetParameterStore(controller));
            Assert.IsNull(PrefabLinks.StoreOf(PrefabLinks.Status(controller)));
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        // ---- writing the declaration into the prefab ---------------------------

        /// <summary>
        /// The refusals, decided over facts rather than over the project — which is the only way
        /// to check the case that matters most, since a prefab inside a registry package is not
        /// something a test can install on demand.
        /// </summary>
        [Test]
        public void WhoMayBeWrittenToFollowsWhereThePrefabLives()
        {
            Assert.AreEqual(PrefabWriteRefusal.None,
                PrefabWriter.Judge("Assets/Gimmick.prefab", null, false),
                "an asset in the user's own project");
            Assert.AreEqual(PrefabWriteRefusal.None,
                PrefabWriter.Judge("Packages/dev.example.gimmick/Gimmick.prefab",
                    PackageSource.Embedded, false),
                "a VPM project keeps its dependencies as real folders, and a gimmick being "
                + "developed in one is a normal thing to be editing");
            Assert.AreEqual(PrefabWriteRefusal.None,
                PrefabWriter.Judge("Packages/dev.example.gimmick/Gimmick.prefab",
                    PackageSource.Local, false));

            foreach (var source in new[]
                     {
                         PackageSource.Registry, PackageSource.Git,
                         PackageSource.LocalTarball, PackageSource.BuiltIn,
                     })
                Assert.AreEqual(PrefabWriteRefusal.ImmutablePackage,
                    PrefabWriter.Judge("Packages/dev.example.gimmick/Gimmick.prefab", source, false),
                    "a write there is thrown away by the next resolve, and a change that "
                    + "disappears without saying so is worse than one that never happened");

            Assert.AreEqual(PrefabWriteRefusal.NotAPrefabAsset,
                PrefabWriter.Judge(null, null, false));
        }

        [Test]
        public void APrefabOpenWithUnsavedEditsIsRefused()
        {
            Assert.AreEqual(PrefabWriteRefusal.OpenWithUnsavedEdits,
                PrefabWriter.Judge("Assets/Gimmick.prefab", null, true));
        }

        [Test]
        public void APrefabInThisProjectIsWritable()
        {
            var prefab = BarePrefab(GimmickPrefab);
            Assert.AreEqual(PrefabWriteRefusal.None, PrefabWriter.Check(prefab));
            Assert.AreEqual(PrefabWriteRefusal.NotAPrefabAsset, PrefabWriter.Check(null));

            var loose = new GameObject("Loose");
            Assert.AreEqual(PrefabWriteRefusal.NotAPrefabAsset, PrefabWriter.Check(loose),
                "a scene object has no file to write");
            Object.DestroyImmediate(loose);
        }

        [Test]
        public void AddingTheDeclarationChangesNothingElseInThePrefab()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var prefab = Prefab(GimmickPrefab, controller);
            Pin(controller, GimmickPrefab);
            Assert.IsNull(PrefabLinks.StoreOf(PrefabLinks.Status(controller)));

            var added = PrefabWriter.AddParameters(prefab);

            Assert.IsNotNull(added, "the component comes back as it exists in the saved asset");
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab);
            Assert.IsNotNull(saved.transform.Find("Mid/Leaf"), "the objects are where they were");
            Assert.AreSame(controller, saved.GetComponentInChildren<MaMergeAnimator>(true).animator,
                "and the merge still merges what it merged");

            var status = PrefabLinks.Status(controller);
            Assert.AreEqual(PrefabLinkState.Healthy, status.state,
                "writing the file must not break the link that pointed into it");
            Assert.AreSame(added, PrefabLinks.StoreOf(status),
                "on the root, which is where Modular Avatar looks from the merge upwards — so "
                + "a merge three objects down finds it");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AddingTheDeclarationTwiceLeavesTheSecondCallWithNothingToDo()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var prefab = Prefab(GimmickPrefab, controller);

            Assert.IsNotNull(PrefabWriter.AddParameters(prefab));
            Assert.IsNull(PrefabWriter.AddParameters(
                AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab)));

            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab);
            Assert.AreEqual(1, saved.GetComponents<
                nadena.dev.modular_avatar.core.ModularAvatarParameters>().Length);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        /// <summary>
        /// The half of this feature that had to be MEASURED rather than reasoned about: whether
        /// editing the VALUES of a component that lives inside a prefab asset reaches the file,
        /// through the ordinary store path and without any prefab-contents round trip of its own.
        /// It does — the store marks the component dirty and saves the asset it belongs to — so
        /// there are two ways into a prefab and not one: structure through
        /// <see cref="PrefabWriter"/>, values through the store, exactly as they are for a
        /// component in a scene.
        /// </summary>
        [Test]
        public void EditingTheAddedDeclarationReachesThePrefabFile()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var prefab = Prefab(GimmickPrefab, controller);
            var store = ParameterStore.TryWrap(PrefabWriter.AddParameters(prefab));
            Assert.IsNotNull(store);

            store.Add(new VrcExpressionParameters.Entry
            {
                name = "Hat",
                valueType = VrcExpressionParameters.ValueType.Bool,
                typed = true,
                synced = true,
                saved = true,
                defaultValue = 1f,
            });
            Assert.IsTrue(FileMentions(GimmickPrefab, "Hat"),
                "a row added to a prefab's store has to be in the prefab FILE — a dirty asset "
                + "nobody saved is gone at the next domain reload");

            Assert.IsTrue(store.Edit("Hat", entry => entry.synced = false));
            Assert.IsTrue(store.Rename("Hat", "Cap"));

            AssetDatabase.ImportAsset(GimmickPrefab, ImportAssetOptions.ForceUpdate);
            var reread = ParameterStore.TryWrap(
                AssetDatabase.LoadAssetAtPath<GameObject>(GimmickPrefab)
                    .GetComponent<nadena.dev.modular_avatar.core.ModularAvatarParameters>());
            var entry = reread.Find("Cap");
            Assert.IsNotNull(entry, "read back out of the reimported asset, not out of memory");
            Assert.IsFalse(entry.synced);
            Assert.IsTrue(entry.typed, "unsyncing keeps the row's type");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
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
