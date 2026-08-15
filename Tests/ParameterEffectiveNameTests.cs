using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;
#if DAERD_MA
using MaConfig = nadena.dev.modular_avatar.core.ParameterConfig;
using MaParameters = nadena.dev.modular_avatar.core.ModularAvatarParameters;
using MaSyncType = nadena.dev.modular_avatar.core.ParameterSyncType;
#endif

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The one place where a parameter's name in the editor and its name on the wire are not
    /// the same string: an MA Parameters row declared INTERNAL is renamed on the way into the
    /// avatar, so that two copies of the same gimmick do not fight over one name.
    ///
    /// What is asserted here is deliberately not the renaming RULE — that belongs to NDMF and
    /// Modular Avatar, and pinning the shape of the generated name would be this file claiming
    /// to own somebody else's decision. What is pinned is that DaerD asks and uses the answer:
    /// the built name differs from the written one, an untouched row is absent from the map
    /// rather than mapped to itself, and the list DD DynamicAnalyze puts on the wire carries
    /// built names — the same names filling from the build produces, which is the whole reason
    /// the two are worth comparing.
    ///
    /// Needs NDMF as well as MA, which is why the guard names both. Skipped by name otherwise,
    /// so both project shapes run the same number of tests.
    /// </summary>
    public class ParameterEffectiveNameTests
    {
        const string Folder = "Assets/DDBuiltName";
        const string ControllerPath = Folder + "/Gimmick.controller";

        GameObject _host;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets", "DDBuiltName");
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
            AssetDatabase.DeleteAsset(Folder);
        }

        static AnimatorController Controller()
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            controller.AddLayer("Base");
            controller.AddParameter("Hat", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Cape", AnimatorControllerParameterType.Bool);
            return controller;
        }

        [Test]
        public void AStoreWhoseNamesAreFinalRenamesNothing()
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            VrcExpressionParameters.WriteAll(asset, new[]
            {
                new VrcExpressionParameters.Entry
                { name = "Hat", valueType = VrcExpressionParameters.ValueType.Bool },
            });
            // The avatar's own expression parameters are the built names, so there is nothing
            // for this to say — and saying "Hat maps to Hat" would put a row in every UI that
            // shows the map.
            CollectionAssert.IsEmpty(ParameterStore.TryWrap(asset).EffectiveNames());
        }

#if DAERD_MA
        MaParameters Gimmick()
        {
            _host = new GameObject("Gimmick");
            return _host.AddComponent<MaParameters>();
        }

        static MaConfig Config(string name, bool internalParameter = false, string remapTo = null) =>
            new MaConfig
            {
                nameOrPrefix = name,
                syncType = MaSyncType.Bool,
                internalParameter = internalParameter,
                remapTo = remapTo,
            };
#endif

        [Test]
        public void AnInternalRowIsCarriedUnderTheNameTheBuildGivesIt()
        {
#if DAERD_MA && DAERD_NDMF && DAERD_VRC
            var component = Gimmick();
            component.parameters.Add(Config("Hat", internalParameter: true));
            component.parameters.Add(Config("Cape"));
            component.parameters.Add(Config("Bag", remapTo: "Pouch"));

            var built = ParameterStore.TryWrap(component).EffectiveNames();

            Assert.IsTrue(built.TryGetValue("Hat", out var hat),
                "an internal row is renamed, so the store's name is not what travels");
            Assert.AreNotEqual("Hat", hat);
            StringAssert.StartsWith("Hat", hat, "the generated name is still recognisably the row's");

            Assert.IsFalse(built.ContainsKey("Cape"),
                "a row nobody renames is absent rather than mapped to itself");

            Assert.AreEqual("Pouch", built["Bag"],
                "an explicit remap is a rename too, and comes from the same answer");
#else
            Assert.Ignore("Modular Avatar or NDMF is not installed in this project.");
#endif
        }

        [Test]
        public void TheRunsSyncedListIsFilledWithBuiltNames()
        {
#if DAERD_MA && DAERD_NDMF && DAERD_VRC
            var controller = Controller();
            var component = Gimmick();
            component.parameters.Add(Config("Hat", internalParameter: true));
            component.parameters.Add(Config("Cape"));
            // Declared but kept at home: not on the wire, so not in the list either.
            var quiet = Config("Quiet");
            quiet.localOnly = true;
            component.parameters.Add(quiet);
            GraphFrameData.SetParameterStore(controller, component);

            var synced = DynamicAnalyzeWindow.SyncedFromStore(controller);
            Assert.AreEqual(2, synced.Count);
            CollectionAssert.Contains(synced, "Cape");
            CollectionAssert.DoesNotContain(synced, "Hat",
                "the wire would carry a name the built avatar does not have");
            CollectionAssert.DoesNotContain(synced, "Quiet");
            Assert.AreEqual(ParameterStore.TryWrap(component).EffectiveNames()["Hat"],
                synced.Find(name => name != "Cape"));
#else
            Assert.Ignore("Modular Avatar or NDMF is not installed in this project.");
#endif
        }

        [Test]
        public void AControllerWithNoStoreIsAnsweredWithNothingToCompareAgainst()
        {
            var controller = Controller();
            Assert.IsNull(DynamicAnalyzeWindow.SyncedFromStore(controller),
                "null and an empty list are different answers — nothing to ask is not a store "
                + "that syncs nothing");
            Assert.IsNull(DynamicAnalyzeWindow.SyncedFromStore(null));
        }
    }
}
