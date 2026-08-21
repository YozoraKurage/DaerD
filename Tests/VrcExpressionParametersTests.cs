using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Analyze;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Stand-in for the VRC SDK asset: same type name and serialized field layout, so the
    /// SerializedObject-based accessor works against it without the SDK installed.
    /// </summary>
    class VRCExpressionParameters : ScriptableObject
    {
        public const int MAX_PARAMETER_COST = 256;

        [System.Serializable]
        public class Parameter
        {
            public string name;
            public int valueType;
            public bool saved = true;
            public float defaultValue;
            public bool networkSynced = true;
        }

        public Parameter[] parameters = new Parameter[0];
    }

    public class VrcExpressionParametersTests
    {
        static VRCExpressionParameters NewAsset(params VrcExpressionParameters.Entry[] entries)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            VrcExpressionParameters.WriteAll(asset, entries);
            return asset;
        }

        static VrcExpressionParameters.Entry Entry(string name,
            VrcExpressionParameters.ValueType type, bool synced = true, bool saved = true,
            float defaultValue = 0f) =>
            new VrcExpressionParameters.Entry
            {
                name = name,
                valueType = type,
                synced = synced,
                saved = saved,
                defaultValue = defaultValue,
            };

        [Test]
        public void ReadWrite_RoundTripsEntries()
        {
            var asset = NewAsset(
                Entry("A", VrcExpressionParameters.ValueType.Bool, synced: true, saved: false, defaultValue: 1f),
                Entry("B", VrcExpressionParameters.ValueType.Float, synced: false));

            var read = VrcExpressionParameters.Read(asset);
            Assert.AreEqual(2, read.Count);
            Assert.AreEqual("A", read[0].name);
            Assert.AreEqual(VrcExpressionParameters.ValueType.Bool, read[0].valueType);
            Assert.IsFalse(read[0].saved);
            Assert.IsTrue(read[0].synced);
            Assert.AreEqual(1f, read[0].defaultValue);
            Assert.IsFalse(read[1].synced);
        }

        [Test]
        public void AddRemoveRenameEdit_Work()
        {
            var asset = NewAsset(Entry("A", VrcExpressionParameters.ValueType.Int));

            VrcExpressionParameters.Add(asset, Entry("B", VrcExpressionParameters.ValueType.Bool));
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "B"));

            // Duplicate adds are ignored.
            VrcExpressionParameters.Add(asset, Entry("B", VrcExpressionParameters.ValueType.Int));
            Assert.AreEqual(2, VrcExpressionParameters.Read(asset).Count);

            Assert.IsTrue(VrcExpressionParameters.Rename(asset, "A", "A2"));
            Assert.IsNull(VrcExpressionParameters.Find(asset, "A"));
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "A2"));

            Assert.IsTrue(VrcExpressionParameters.Edit(asset, "B", e => e.saved = false));
            Assert.IsFalse(VrcExpressionParameters.Find(asset, "B").saved);

            Assert.IsTrue(VrcExpressionParameters.Remove(asset, "B"));
            Assert.IsFalse(VrcExpressionParameters.Remove(asset, "B"));
            Assert.AreEqual(1, VrcExpressionParameters.Read(asset).Count);
        }

        [Test]
        public void UsedBits_CountsSyncedEntriesOnly()
        {
            var asset = NewAsset(
                Entry("Bool", VrcExpressionParameters.ValueType.Bool),
                Entry("Int", VrcExpressionParameters.ValueType.Int),
                Entry("Float", VrcExpressionParameters.ValueType.Float),
                Entry("Local", VrcExpressionParameters.ValueType.Float, synced: false));
            Assert.AreEqual(1 + 8 + 8, VrcExpressionParameters.UsedBits(asset));
            Assert.AreEqual(256, VrcExpressionParameters.Capacity(asset));
        }

        [Test]
        public void MapType_TriggerHasNoEquivalent()
        {
            Assert.AreEqual(VrcExpressionParameters.ValueType.Int,
                VrcExpressionParameters.MapType(AnimatorControllerParameterType.Int));
            Assert.AreEqual(VrcExpressionParameters.ValueType.Float,
                VrcExpressionParameters.MapType(AnimatorControllerParameterType.Float));
            Assert.AreEqual(VrcExpressionParameters.ValueType.Bool,
                VrcExpressionParameters.MapType(AnimatorControllerParameterType.Bool));
            Assert.IsNull(VrcExpressionParameters.MapType(AnimatorControllerParameterType.Trigger));
        }

        [Test]
        public void Analyze_FlagsMismatchesAndOrphans()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Match", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Mismatched", AnimatorControllerParameterType.Float);

            var asset = NewAsset(
                Entry("Match", VrcExpressionParameters.ValueType.Bool),
                Entry("Mismatched", VrcExpressionParameters.ValueType.Bool),
                Entry("Orphan", VrcExpressionParameters.ValueType.Bool));

            var issues = new List<AnalyzerIssue>();
            ParameterStore.TryWrap(asset).Analyze(controller, issues);

            // Type mismatches are a supported VRChat technique (parameter mismatching) —
            // both findings are informational, never errors.
            Assert.AreEqual(2, issues.Count);
            foreach (var issue in issues)
            {
                Assert.AreEqual(IssueKind.VrcParameters, issue.kind);
                Assert.AreEqual(IssueSeverity.Info, issue.severity);
            }
        }
    }

    public class PhysBoneSiblingsTests
    {
        [Test]
        public void PrefixOf_DetectsKnownSuffixes()
        {
            Assert.AreEqual("Tail", PhysBoneSiblings.PrefixOf("Tail_IsGrabbed"));
            Assert.AreEqual("Tail", PhysBoneSiblings.PrefixOf("Tail_Angle"));
            Assert.IsNull(PhysBoneSiblings.PrefixOf("Tail"));
            Assert.IsNull(PhysBoneSiblings.PrefixOf("_Angle"));   // suffix alone is no family
            Assert.IsNull(PhysBoneSiblings.PrefixOf(null));
        }

        [Test]
        public void Siblings_FindsSamePrefixOtherSuffixes()
        {
            var controller = new AnimatorController();
            controller.AddParameter("Tail_IsGrabbed", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Tail_Angle", AnimatorControllerParameterType.Float);
            controller.AddParameter("Ear_Angle", AnimatorControllerParameterType.Float);
            controller.AddParameter("Tail", AnimatorControllerParameterType.Bool);

            var siblings = PhysBoneSiblings.Siblings(controller, "Tail_IsGrabbed");
            Assert.AreEqual(1, siblings.Count);
            Assert.AreEqual("Tail_Angle", siblings[0]);
        }

        [Test]
        public void MissingFamily_SeedsFromAMemberName_AndSkipsWhatExists()
        {
            var controller = new AnimatorController();
            controller.AddParameter("Tail_IsGrabbed", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Tail_Angle", AnimatorControllerParameterType.Float);

            var missing = PhysBoneSiblings.MissingFamily(controller, "Tail_Angle");

            Assert.AreEqual(3, missing.Count);
            Assert.Contains(("Tail_IsPosed", AnimatorControllerParameterType.Bool), missing);
            Assert.Contains(("Tail_Stretch", AnimatorControllerParameterType.Float), missing);
            Assert.Contains(("Tail_Squish", AnimatorControllerParameterType.Float), missing);
        }

        [Test]
        public void MissingFamily_TreatsAnUnsuffixedNameAsThePrefix()
        {
            var controller = new AnimatorController();
            controller.AddParameter("Tail", AnimatorControllerParameterType.Float);

            var missing = PhysBoneSiblings.MissingFamily(controller, "Tail");

            Assert.AreEqual(5, missing.Count);
            Assert.Contains(("Tail_IsGrabbed", AnimatorControllerParameterType.Bool), missing);
            Assert.Contains(("Tail_Angle", AnimatorControllerParameterType.Float), missing);
        }

        [Test]
        public void RenamedSibling_CarriesThePrefixChange()
        {
            Assert.AreEqual("Tail2_Angle",
                PhysBoneSiblings.RenamedSibling("Tail_Angle", "Tail_IsGrabbed", "Tail2_IsGrabbed"));
            // Rename that doesn't change the family prefix (or leaves the family) → no carry.
            Assert.IsNull(PhysBoneSiblings.RenamedSibling("Tail_Angle", "Tail_IsGrabbed", "Tail_IsPosed"));
            Assert.IsNull(PhysBoneSiblings.RenamedSibling("Ear_Angle", "Tail_IsGrabbed", "Tail2_IsGrabbed"));
            Assert.IsNull(PhysBoneSiblings.RenamedSibling("Tail_Angle", "Tail_IsGrabbed", "PlainName"));
        }
    }
}
