using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA
using MaParameters = nadena.dev.modular_avatar.core.ModularAvatarParameters;
#endif
#if DAERD_MA && DAERD_VRC
using MaMergeAnimator = nadena.dev.modular_avatar.core.ModularAvatarMergeAnimator;
#endif

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Finding — and editing — the parameter store of a gimmick that is a PREFAB and nothing
    /// else, which is the shape a gimmick has for most of its life. Nothing here is in a scene:
    /// the objects are built, written to a prefab and destroyed, so a scene search would find
    /// none of it and the only thing that can answer is the project sweep.
    ///
    /// Three claims are pinned rather than remembered, because all three are about somebody
    /// else's machinery and all three would fail silently:
    /// <list type="bullet">
    /// <item>the dependency table is enough of a filter — a prefab that does not reference the
    /// controller is never opened, which is the only reason a sweep over every prefab in the
    /// project is affordable at all;</item>
    /// <item>an answer is remembered until an asset changes, and an import drops it;</item>
    /// <item>a write into a component that lives inside a prefab asset reaches the FILE. This
    /// is the one that had to be measured rather than reasoned about — a dirty asset nobody
    /// saves looks exactly like a saved one until the next domain reload.</item>
    /// </list>
    ///
    /// Skipped by name where Modular Avatar is absent, so the two runs have the same number of
    /// tests in them.
    /// </summary>
    public class ParameterStorePrefabTests
    {
        const string Folder = "Assets/DDPrefabScan";
        const string ControllerPath = Folder + "/Gimmick.controller";
        const string OtherControllerPath = Folder + "/Other.controller";
        const string GimmickPrefab = Folder + "/Gimmick.prefab";
        const string DecoyPrefab = Folder + "/Decoy.prefab";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDPrefabScan");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            ParameterStore.ForgetPrefabScan();
        }

        static AnimatorController Controller(string path)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddLayer("Base");
            return controller;
        }

#if DAERD_MA && DAERD_VRC
        /// <summary>A gimmick prefab of the ordinary shape: one object carrying both the merge
        /// that names the controller and the parameters that declare what it uses.</summary>
        static GameObject Prefab(string path, AnimatorController merged, string parameter = null)
        {
            var built = new GameObject(Path.GetFileNameWithoutExtension(path));
            var parameters = built.AddComponent<MaParameters>();
            if (parameter != null)
                parameters.parameters.Add(new nadena.dev.modular_avatar.core.ParameterConfig
                {
                    nameOrPrefix = parameter,
                    syncType = nadena.dev.modular_avatar.core.ParameterSyncType.Bool,
                });
            built.AddComponent<MaMergeAnimator>().animator = merged;
            var saved = PrefabUtility.SaveAsPrefabAsset(built, path);
            Object.DestroyImmediate(built);
            return saved;
        }

        static MaParameters ParametersIn(string prefabPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return root == null ? null : root.GetComponentInChildren<MaParameters>(true);
        }

        /// <summary>Whether the FILE holds this text, whichever way the project serialises its
        /// assets — Unity writes string fields as UTF-8 in both modes.</summary>
        static bool FileMentions(string path, string text) =>
            Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(text);
#endif

        [Test]
        public void APrefabbedGimmickIsFoundByTheProjectSweepAndNotByTheSceneOne()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller, "Hat");

            Assert.IsNull(ParameterStore.DetectFor(controller),
                "nothing built here is in the scene, so the scene search has nothing to find");

            var found = ParameterStore.DetectInPrefabs(controller);
            Assert.IsNotNull(found, "the prefab's merge names this controller");
            Assert.AreSame(ParametersIn(GimmickPrefab), found);

            var store = ParameterStore.TryWrap(found);
            Assert.AreEqual("MA Params", store.Kind);
            Assert.IsNotNull(store.Find("Hat"), "the store reads out of the prefab asset");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void OnlyPrefabsThatAlreadyReferenceTheControllerAreOpened()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            var other = Controller(OtherControllerPath);
            Prefab(GimmickPrefab, controller, "Hat");
            Prefab(DecoyPrefab, other, "Bag");

            ParameterStore.ForgetPrefabScan();
            int loads = ParameterStore.PrefabLoads;
            var found = ParameterStore.DetectInPrefabs(controller);

            Assert.AreSame(ParametersIn(GimmickPrefab), found);
            Assert.AreEqual(loads + 1, ParameterStore.PrefabLoads,
                "the decoy carries MA Parameters too, and the sweep must never have opened it "
                + "to know that — the dependency table already said it names another controller");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void TheAnswerIsRememberedUntilSomethingInTheProjectChanges()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller, "Hat");

            ParameterStore.ForgetPrefabScan();
            int scans = ParameterStore.PrefabScans;

            Assert.IsNotNull(ParameterStore.DetectInPrefabs(controller));
            Assert.AreEqual(scans + 1, ParameterStore.PrefabScans);
            Assert.IsNotNull(ParameterStore.DetectInPrefabs(controller));
            Assert.AreEqual(scans + 1, ParameterStore.PrefabScans,
                "pressing the button twice must not sweep the project twice");

            // The one thing a remembered answer cannot survive: a prefab that has just started
            // (or stopped) referencing the controller. The import itself is what drops it.
            Prefab(DecoyPrefab, controller, "Bag");
            Assert.IsNotNull(ParameterStore.DetectInPrefabs(controller));
            Assert.AreEqual(scans + 2, ParameterStore.PrefabScans,
                "an asset import drops the memory");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void AControllerThatIsNotAFileIsAnsweredForRatherThanSwept()
        {
#if DAERD_MA && DAERD_VRC
            var loose = new AnimatorController();
            loose.AddLayer("Base");
            ParameterStore.ForgetPrefabScan();
            int scans = ParameterStore.PrefabScans;

            Assert.IsNull(ParameterStore.DetectInPrefabs(loose));
            Assert.IsNull(ParameterStore.DetectInPrefabs(null));
            Assert.AreEqual(scans, ParameterStore.PrefabScans,
                "a controller with no path cannot be any prefab's dependency, so there is "
                + "nothing to sweep for");
            Object.DestroyImmediate(loose);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void WritingThroughAPrefabbedStoreReachesTheFile()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller, "Hat");
            var store = ParameterStore.TryWrap(ParameterStore.DetectInPrefabs(controller));

            store.Add(new VrcExpressionParameters.Entry
            {
                name = "Cape",
                valueType = VrcExpressionParameters.ValueType.Bool,
                synced = true,
                saved = true,
                defaultValue = 1f,
            });
            Assert.IsTrue(FileMentions(GimmickPrefab, "Cape"),
                "a row added to a prefab's store has to be in the prefab FILE — a dirty asset "
                + "nobody saved is gone at the next domain reload");

            Assert.IsTrue(store.Edit("Hat", entry => entry.synced = false));
            Assert.IsTrue(store.Rename("Hat", "Cap"));
            Assert.IsTrue(FileMentions(GimmickPrefab, "Cap"));

            Assert.IsTrue(store.Remove("Cape"));
            Assert.IsFalse(FileMentions(GimmickPrefab, "Cape"), "and a removal is written too");

            // The whole round trip, read back out of the reimported asset rather than out of
            // the objects the writes touched.
            AssetDatabase.ImportAsset(GimmickPrefab, ImportAssetOptions.ForceUpdate);
            var reread = ParameterStore.TryWrap(ParametersIn(GimmickPrefab));
            Assert.AreEqual(1, reread.Read().Count);
            var entry = reread.Find("Cap");
            Assert.IsNotNull(entry);
            Assert.IsFalse(entry.synced);
            Assert.IsTrue(entry.typed, "unsyncing keeps the row's type");
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }
    }
}
