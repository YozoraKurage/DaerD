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

            var issues = new List<ControllerAnalyzer.Issue>();
            ParameterStore.TryWrap(component).Analyze(controller, issues);
            Assert.AreEqual(0, issues.Count);
        }
    }
}
