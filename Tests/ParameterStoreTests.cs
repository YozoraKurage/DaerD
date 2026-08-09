using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class ParameterStoreTests
    {
        GameObject _host;

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
        }

        ModularAvatarParameters NewMaComponent()
        {
            _host = new GameObject("Gimmick");
            return _host.AddComponent<ModularAvatarParameters>();
        }

        static ModularAvatarParameters.ParameterConfig Config(string name, int syncType,
            bool localOnly = false, bool saved = false, float defaultValue = 0f, bool isPrefix = false) =>
            new ModularAvatarParameters.ParameterConfig
            {
                nameOrPrefix = name,
                syncType = syncType,
                localOnly = localOnly,
                saved = saved,
                defaultValue = defaultValue,
                isPrefix = isPrefix,
            };

        [Test]
        public void TryWrap_RecognizesBothBackends()
        {
            var vrcAsset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            Assert.AreEqual("VRC Params", ParameterStore.TryWrap(vrcAsset).Kind);

            var component = NewMaComponent();
            Assert.AreEqual("MA Params", ParameterStore.TryWrap(component).Kind);
            // A GameObject carrying the component resolves to the component.
            Assert.AreEqual(component, ParameterStore.TryWrap(_host).Target);

            Assert.IsNull(ParameterStore.TryWrap(null));
            Assert.IsNull(ParameterStore.TryWrap(new AnimationClip()));
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
            var component = NewMaComponent();
            component.parameters.Add(Config("Bool", syncType: 3));
            component.parameters.Add(Config("Int", syncType: 1));
            component.parameters.Add(Config("Local", syncType: 2, localOnly: true));
            component.parameters.Add(Config("Anim", syncType: 0));
            var store = ParameterStore.TryWrap(component);

            // "Local" is unsynced already and "Absent" isn't there — neither counts.
            Assert.AreEqual(2, store.SetSynced(new[] { "Bool", "Int", "Local", "Absent" }, false));
            Assert.IsFalse(store.Find("Bool").synced);
            Assert.IsFalse(store.Find("Int").synced);
            // Unsyncing is written as localOnly: the row keeps its type instead of collapsing
            // to NotSynced, so the parameter stays declared.
            Assert.AreEqual(3, component.parameters[0].syncType);
            Assert.AreEqual(1, component.parameters[1].syncType);
            Assert.IsTrue(component.parameters[0].localOnly);
            Assert.IsTrue(store.Find("Bool").typed);

            // A NotSynced row has no type to sync as — syncing it on is skipped, not guessed.
            Assert.AreEqual(0, store.SetSynced(new[] { "Anim" }, true));
            Assert.AreEqual(0, component.parameters[3].syncType);
            Assert.IsFalse(store.Find("Anim").synced);
        }

        [Test]
        public void MissingEntries_SkipsKnownNamesAndTriggersAndAddsThemAsync()
        {
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
            component.parameters.Add(Config("Int", syncType: 1));
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
        }

        [Test]
        public void MaStore_ReadMapsTypesSyncAndSkipsPrefixRows()
        {
            var component = NewMaComponent();
            component.parameters.Add(Config("Int", syncType: 1, saved: true, defaultValue: 2f));
            component.parameters.Add(Config("Float", syncType: 2));
            component.parameters.Add(Config("Bool", syncType: 3, localOnly: true));
            component.parameters.Add(Config("Anim", syncType: 0));
            component.parameters.Add(Config("Tail", syncType: 2, isPrefix: true));

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
        }

        [Test]
        public void MaStore_AddEditRemoveRename()
        {
            var component = NewMaComponent();
            var store = ParameterStore.TryWrap(component);

            store.Add(new VrcExpressionParameters.Entry
            {
                name = "Hat",
                valueType = VrcExpressionParameters.ValueType.Bool,
                saved = true,
                defaultValue = 1f,
            });
            Assert.AreEqual(3, component.parameters[0].syncType);   // Bool
            Assert.IsFalse(component.parameters[0].localOnly);
            Assert.IsTrue(component.parameters[0].saved);
            Assert.AreEqual(1f, component.parameters[0].defaultValue);
            Assert.IsFalse(component.parameters[0].isPrefix);

            // Duplicate adds are ignored.
            store.Add(new VrcExpressionParameters.Entry { name = "Hat" });
            Assert.AreEqual(1, component.parameters.Count);

            Assert.IsTrue(store.Edit("Hat", e => e.synced = false));
            Assert.IsTrue(component.parameters[0].localOnly);
            Assert.AreEqual(3, component.parameters[0].syncType);   // type kept

            Assert.IsTrue(store.Rename("Hat", "Cap"));
            Assert.AreEqual("Cap", component.parameters[0].nameOrPrefix);

            Assert.IsTrue(store.Remove("Cap"));
            Assert.IsFalse(store.Remove("Cap"));
            Assert.AreEqual(0, component.parameters.Count);
        }

        [Test]
        public void MaStore_WriteAllAppliesAsDiffAndKeepsPrefixRows()
        {
            var component = NewMaComponent();
            component.parameters.Add(Config("Keep", syncType: 2));
            component.parameters.Add(Config("Drop", syncType: 2));
            component.parameters.Add(Config("Tail", syncType: 2, isPrefix: true));
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
        }

        [Test]
        public void MaStore_HasNoOwnBudget()
        {
            var component = NewMaComponent();
            component.parameters.Add(Config("A", syncType: 3));
            var store = ParameterStore.TryWrap(component);
            Assert.AreEqual(-1, store.Capacity());
            Assert.AreEqual(1, store.UsedBits());
        }

        [Test]
        public void Analyze_SkipsTypeCheckForUntypedEntries()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Anim", AnimatorControllerParameterType.Float);

            var component = NewMaComponent();
            component.parameters.Add(Config("Anim", syncType: 0));   // NotSynced → untyped

            var issues = new List<AnalyzerIssue>();
            ParameterStore.TryWrap(component).Analyze(controller, issues);
            Assert.AreEqual(0, issues.Count);
        }
    }
}
