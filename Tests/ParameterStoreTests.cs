using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
#if DAERD_MA
using MaConfig = nadena.dev.modular_avatar.core.ParameterConfig;
using MaParameters = nadena.dev.modular_avatar.core.ModularAvatarParameters;
using MaSyncType = nadena.dev.modular_avatar.core.ParameterSyncType;
#endif

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The two parameter-store backends, and what a project with no Modular Avatar gets.
    ///
    /// The MA half used to run against a stand-in of the tests' own — same type name, same
    /// serialized fields — because the store reached MA through SerializedObject and could not
    /// tell the difference. It reaches MA through MA's types now, so the stand-in is gone and
    /// these drive the real component; where MA is absent they skip themselves by name, so the
    /// number of tests is the same in both projects and a test that vanished cannot be mistaken
    /// for one that passed.
    /// </summary>
    public class ParameterStoreTests
    {
        GameObject _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
        }

#if DAERD_MA
        MaParameters NewMaComponent()
        {
            _host = new GameObject("Gimmick");
            return _host.AddComponent<MaParameters>();
        }

        static MaConfig Config(string name, MaSyncType syncType,
            bool localOnly = false, bool saved = false, float defaultValue = 0f, bool isPrefix = false) =>
            new MaConfig
            {
                nameOrPrefix = name,
                syncType = syncType,
                localOnly = localOnly,
                saved = saved,
                defaultValue = defaultValue,
                isPrefix = isPrefix,
            };
#endif

        /// <summary>
        /// The one fact the rest of this file rests on: <c>DAERD_MA</c> is on when — and only
        /// when — Modular Avatar is in the project. It comes from the .asmdef's versionDefines
        /// rather than from anything a person sets, so the failure it catches is the silent
        /// one: a package renamed upstream leaves the define off, every <c>#if</c> block
        /// disappears, and MA support quietly stops existing in a project that has MA.
        /// </summary>
        [Test]
        public void TheDefineIsOnExactlyWhenModularAvatarIsInstalled()
        {
            bool ma = Loaded("nadena.dev.modular-avatar.core");
#if DAERD_MA
            Assert.IsTrue(ma, "DAERD_MA is defined but Modular Avatar's assembly is not loaded");
#else
            Assert.IsFalse(ma,
                "Modular Avatar is installed and DAERD_MA is not defined — the versionDefine's "
                + "package name has stopped matching the package");
#endif
        }

        static bool Loaded(string assembly)
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                if (loaded.GetName().Name == assembly) return true;
            return false;
        }

        [Test]
        public void TryWrap_RecognizesBothBackends()
        {
            var vrcAsset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            Assert.AreEqual("VRC Params", ParameterStore.TryWrap(vrcAsset).Kind);

            Assert.IsNull(ParameterStore.TryWrap(null));
            Assert.IsNull(ParameterStore.TryWrap(new AnimationClip()));
            // A GameObject with nothing on it is not a store either — the MA lookup below is
            // the only reason a GameObject is ever accepted.
            _host = new GameObject("Plain");
            Assert.IsNull(ParameterStore.TryWrap(_host));

#if DAERD_MA
            var component = _host.AddComponent<MaParameters>();
            Assert.AreEqual("MA Params", ParameterStore.TryWrap(component).Kind);
            // A GameObject carrying the component resolves to the component.
            Assert.AreEqual(component, ParameterStore.TryWrap(_host).Target);
#endif
        }

        static ParameterStore NewVrcStore(params VrcExpressionParameters.Entry[] entries)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            VrcExpressionParameters.WriteAll(asset, entries);
            return ParameterStore.TryWrap(asset);
        }

        [Test]
        public void SetSynced_ChangesListedEntriesAndCountsOnlyRealChanges()
        {
            var store = NewVrcStore(
                new VrcExpressionParameters.Entry
                { name = "A", valueType = VrcExpressionParameters.ValueType.Bool },
                new VrcExpressionParameters.Entry
                { name = "B", valueType = VrcExpressionParameters.ValueType.Int },
                new VrcExpressionParameters.Entry
                { name = "Keep", valueType = VrcExpressionParameters.ValueType.Bool });

            // The name the store doesn't hold is skipped rather than counted.
            Assert.AreEqual(2, store.SetSynced(new[] { "A", "B", "Absent" }, false));
            Assert.IsFalse(store.Find("A").synced);
            Assert.IsFalse(store.Find("B").synced);
            Assert.IsTrue(store.Find("Keep").synced);
            Assert.AreEqual(1, store.UsedBits());   // only Keep still costs bits

            Assert.AreEqual(0, store.SetSynced(new[] { "A", "B" }, false), "nothing left to change");
            Assert.AreEqual(2, store.SetSynced(new[] { "A", "B" }, true));
            Assert.IsTrue(store.Find("A").synced);
        }

        [Test]
        public void SetSynced_MaUnsyncKeepsTheRowType()
        {
#if DAERD_MA
            var component = NewMaComponent();
            component.parameters.Add(Config("Bool", MaSyncType.Bool));
            component.parameters.Add(Config("Int", MaSyncType.Int));
            component.parameters.Add(Config("Local", MaSyncType.Float, localOnly: true));
            component.parameters.Add(Config("Anim", MaSyncType.NotSynced));
            var store = ParameterStore.TryWrap(component);

            // "Local" is unsynced already and "Absent" isn't there — neither counts.
            Assert.AreEqual(2, store.SetSynced(new[] { "Bool", "Int", "Local", "Absent" }, false));
            Assert.IsFalse(store.Find("Bool").synced);
            Assert.IsFalse(store.Find("Int").synced);
            // Unsyncing is written as localOnly: the row keeps its type instead of collapsing
            // to NotSynced, so the parameter stays declared.
            Assert.AreEqual(MaSyncType.Bool, component.parameters[0].syncType);
            Assert.AreEqual(MaSyncType.Int, component.parameters[1].syncType);
            Assert.IsTrue(component.parameters[0].localOnly);
            Assert.IsTrue(store.Find("Bool").typed);

            // A NotSynced row has no type to sync as — syncing it on is skipped, not guessed.
            Assert.AreEqual(0, store.SetSynced(new[] { "Anim" }, true));
            Assert.AreEqual(MaSyncType.NotSynced, component.parameters[3].syncType);
            Assert.IsFalse(store.Find("Anim").synced);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void MissingEntries_SkipsKnownNamesAndTriggersAndAddsThemAsync()
        {
#if DAERD_MA
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter(new AnimatorControllerParameter
            { name = "Float", type = AnimatorControllerParameterType.Float, defaultFloat = 0.25f });
            controller.AddParameter(new AnimatorControllerParameter
            { name = "Int", type = AnimatorControllerParameterType.Int, defaultInt = 3 });
            controller.AddParameter(new AnimatorControllerParameter
            { name = "Bool", type = AnimatorControllerParameterType.Bool, defaultBool = true });
            controller.AddParameter("Trig", AnimatorControllerParameterType.Trigger);

            var component = NewMaComponent();
            component.parameters.Add(Config("Int", MaSyncType.Int));
            var store = ParameterStore.TryWrap(component);

            var missing = ParameterStore.MissingEntries(controller, store);
            var names = new List<string>();
            foreach (var entry in missing) names.Add(entry.name);
            // Controller order, without the row the store already has and without the Trigger.
            CollectionAssert.AreEqual(new[] { "Float", "Bool" }, names);

            Assert.AreEqual(VrcExpressionParameters.ValueType.Float, missing[0].valueType);
            Assert.AreEqual(0.25f, missing[0].defaultValue);
            Assert.AreEqual(VrcExpressionParameters.ValueType.Bool, missing[1].valueType);
            Assert.AreEqual(1f, missing[1].defaultValue);   // Bool default true
            foreach (var entry in missing)
            {
                Assert.IsFalse(entry.synced, "the bulk add declares parameters, it doesn't sync them");
                Assert.IsFalse(entry.saved);
            }

            // Int carries the controller's default too, once nothing shadows it.
            var all = ParameterStore.MissingEntries(controller, null);
            Assert.AreEqual(3, all.Count);
            Assert.AreEqual("Int", all[1].name);
            Assert.AreEqual(3f, all[1].defaultValue);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void MaStore_ReadMapsTypesSyncAndSkipsPrefixRows()
        {
#if DAERD_MA
            var component = NewMaComponent();
            component.parameters.Add(Config("Int", MaSyncType.Int, saved: true, defaultValue: 2f));
            component.parameters.Add(Config("Float", MaSyncType.Float));
            component.parameters.Add(Config("Bool", MaSyncType.Bool, localOnly: true));
            component.parameters.Add(Config("Anim", MaSyncType.NotSynced));
            component.parameters.Add(Config("Tail", MaSyncType.Float, isPrefix: true));

            var entries = ParameterStore.TryWrap(component).Read();
            Assert.AreEqual(4, entries.Count);   // the prefix row is preserved but hidden

            Assert.AreEqual(VrcExpressionParameters.ValueType.Int, entries[0].valueType);
            Assert.IsTrue(entries[0].synced);
            Assert.IsTrue(entries[0].saved);
            Assert.AreEqual(2f, entries[0].defaultValue);

            Assert.AreEqual(VrcExpressionParameters.ValueType.Float, entries[1].valueType);
            Assert.IsFalse(entries[2].synced);    // localOnly
            Assert.IsTrue(entries[2].typed);
            Assert.IsFalse(entries[3].synced);    // NotSynced
            Assert.IsFalse(entries[3].typed);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void MaStore_AddEditRemoveRename()
        {
#if DAERD_MA
            var component = NewMaComponent();
            var store = ParameterStore.TryWrap(component);

            store.Add(new VrcExpressionParameters.Entry
            {
                name = "Hat",
                valueType = VrcExpressionParameters.ValueType.Bool,
                saved = true,
                defaultValue = 1f,
            });
            Assert.AreEqual(MaSyncType.Bool, component.parameters[0].syncType);
            Assert.IsFalse(component.parameters[0].localOnly);
            Assert.IsTrue(component.parameters[0].saved);
            Assert.AreEqual(1f, component.parameters[0].defaultValue);
            Assert.IsFalse(component.parameters[0].isPrefix);

            // Duplicate adds are ignored.
            store.Add(new VrcExpressionParameters.Entry { name = "Hat" });
            Assert.AreEqual(1, component.parameters.Count);

            Assert.IsTrue(store.Edit("Hat", e => e.synced = false));
            Assert.IsTrue(component.parameters[0].localOnly);
            Assert.AreEqual(MaSyncType.Bool, component.parameters[0].syncType);   // type kept

            Assert.IsTrue(store.Rename("Hat", "Cap"));
            Assert.AreEqual("Cap", component.parameters[0].nameOrPrefix);

            Assert.IsTrue(store.Remove("Cap"));
            Assert.IsFalse(store.Remove("Cap"));
            Assert.AreEqual(0, component.parameters.Count);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void MaStore_WriteAllAppliesAsDiffAndKeepsPrefixRows()
        {
#if DAERD_MA
            var component = NewMaComponent();
            component.parameters.Add(Config("Keep", MaSyncType.Float));
            component.parameters.Add(Config("Drop", MaSyncType.Float));
            component.parameters.Add(Config("Tail", MaSyncType.Float, isPrefix: true));
            var store = ParameterStore.TryWrap(component);

            store.WriteAll(new List<VrcExpressionParameters.Entry>
            {
                new VrcExpressionParameters.Entry
                {
                    name = "Keep",
                    valueType = VrcExpressionParameters.ValueType.Int,
                },
                new VrcExpressionParameters.Entry
                {
                    name = "New",
                    valueType = VrcExpressionParameters.ValueType.Bool,
                },
            });

            var names = new List<string>();
            foreach (var config in component.parameters) names.Add(config.nameOrPrefix);
            CollectionAssert.Contains(names, "Keep");
            CollectionAssert.Contains(names, "New");
            CollectionAssert.Contains(names, "Tail");   // prefix row untouched
            CollectionAssert.DoesNotContain(names, "Drop");
            Assert.AreEqual(1, store.Find("Keep") != null
                && store.Find("Keep").valueType == VrcExpressionParameters.ValueType.Int ? 1 : 0);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void MaStore_HasNoOwnBudget()
        {
#if DAERD_MA
            var component = NewMaComponent();
            component.parameters.Add(Config("A", MaSyncType.Bool));
            var store = ParameterStore.TryWrap(component);
            Assert.AreEqual(-1, store.Capacity());
            Assert.AreEqual(1, store.UsedBits());
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }

        [Test]
        public void Analyze_SkipsTypeCheckForUntypedEntries()
        {
#if DAERD_MA
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Anim", AnimatorControllerParameterType.Float);

            var component = NewMaComponent();
            component.parameters.Add(Config("Anim", MaSyncType.NotSynced));   // NotSynced → untyped

            var issues = new List<AnalyzerIssue>();
            ParameterStore.TryWrap(component).Analyze(controller, issues);
            Assert.AreEqual(0, issues.Count);
#else
            Assert.Ignore("Modular Avatar is not installed in this project.");
#endif
        }
    }
}
