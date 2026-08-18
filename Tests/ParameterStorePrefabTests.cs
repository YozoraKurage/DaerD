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
    /// Editing the parameter store of a gimmick that is a PREFAB and nothing else, which is the
    /// shape a gimmick has for most of its life. Nothing here is in a scene: the objects are
    /// built, written to a prefab and destroyed.
    ///
    /// The claim pinned here is the one that had to be measured rather than reasoned about: a
    /// write into a component that lives inside a prefab asset reaches the FILE. A dirty asset
    /// nobody saves looks exactly like a saved one until the next domain reload.
    ///
    /// The route to that component is <see cref="ParameterStore.StoreFor"/> — the MA Parameters
    /// above a merge somebody already chose. It used to be a project-wide sweep with a button of
    /// its own; that sweep is gone, and the prefab link's Scan is what names a prefab now.
    ///
    /// Skipped by name where Modular Avatar is absent, so the two runs have the same number of
    /// tests in them.
    /// </summary>
    public class ParameterStorePrefabTests
    {
        const string Folder = "Assets/DDPrefabScan";
        const string ControllerPath = Folder + "/Gimmick.controller";
        const string GimmickPrefab = Folder + "/Gimmick.prefab";

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDPrefabScan");
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(Folder);

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

        /// <summary>The store governing the merge inside a prefab asset, reached the way the
        /// prefab link reaches it once a prefab has been picked.</summary>
        static ParameterStore StoreIn(string prefabPath)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var merge = root == null ? null : root.GetComponentInChildren<MaMergeAnimator>(true);
            return ParameterStore.TryWrap(ParameterStore.StoreFor(merge));
        }

        /// <summary>Whether the FILE holds this text, whichever way the project serialises its
        /// assets — Unity writes string fields as UTF-8 in both modes.</summary>
        static bool FileMentions(string path, string text) =>
            Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(text);
#endif

        [Test]
        public void APrefabbedGimmicksStoreIsReadThroughTheMergeAndNotFromTheScene()
        {
#if DAERD_MA && DAERD_VRC
            var controller = Controller(ControllerPath);
            Prefab(GimmickPrefab, controller, "Hat");

            Assert.IsNull(ParameterStore.DetectFor(controller),
                "nothing built here is in the scene, so the scene search has nothing to find");

            var store = StoreIn(GimmickPrefab);
            Assert.IsNotNull(store, "the merge inside the prefab has MA Parameters above it");
            Assert.AreSame(ParametersIn(GimmickPrefab), store.Target);
            Assert.AreEqual("MA Params", store.Kind);
            Assert.IsNotNull(store.Find("Hat"), "the store reads out of the prefab asset");
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
            var store = StoreIn(GimmickPrefab);

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
