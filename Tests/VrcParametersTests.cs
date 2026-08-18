using System.Collections.Generic;
using NUnit.Framework;
using Yozolab.DaerD.Bridge;

namespace Yozolab.DaerD.Tests
{
    public class VrcParametersTests
    {
        [Test]
        public void All_HaveUniqueNonEmptyNamesAndCategories()
        {
            var seen = new HashSet<string>();
            foreach (var def in VrcParameters.All)
            {
                Assert.IsFalse(string.IsNullOrEmpty(def.name), "parameter name must not be empty");
                Assert.IsFalse(string.IsNullOrEmpty(def.category), "parameter '" + def.name + "' needs a category");
                Assert.IsTrue(seen.Add(def.name), "duplicate VRChat parameter name: " + def.name);
            }
        }

        [Test]
        public void All_NamesAreTrimmed_NoStrayWhitespace()
        {
            foreach (var def in VrcParameters.All)
                Assert.AreEqual(def.name.Trim(), def.name, "name has stray whitespace: '" + def.name + "'");
        }

        [Test]
        public void All_CoversTheWellKnownCoreParameters()
        {
            var names = new HashSet<string>();
            foreach (var def in VrcParameters.All) names.Add(def.name);

            foreach (var expected in new[]
            {
                "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight",
                "GestureLeftWeight", "GestureRightWeight", "AngularY",
                "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude",
                "Upright", "Grounded", "Seated", "AFK", "TrackingType", "VRMode",
                "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList", "AvatarVersion",
                "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "AdjustedScaleFactor",
            })
                CollectionAssert.Contains(names, expected);
        }

        [Test]
        public void KnownParameters_HaveTheCorrectType()
        {
            var byName = new Dictionary<string, VrcParameters.ParamType>();
            foreach (var def in VrcParameters.All) byName[def.name] = def.type;

            Assert.AreEqual(VrcParameters.ParamType.Bool, byName["IsLocal"]);
            Assert.AreEqual(VrcParameters.ParamType.Int, byName["Viseme"]);
            Assert.AreEqual(VrcParameters.ParamType.Float, byName["Voice"]);
            Assert.AreEqual(VrcParameters.ParamType.Int, byName["GestureLeft"]);
            Assert.AreEqual(VrcParameters.ParamType.Float, byName["GestureLeftWeight"]);
            Assert.AreEqual(VrcParameters.ParamType.Float, byName["VelocityZ"]);
            Assert.AreEqual(VrcParameters.ParamType.Float, byName["Upright"]);
            Assert.AreEqual(VrcParameters.ParamType.Bool, byName["Grounded"]);
            Assert.AreEqual(VrcParameters.ParamType.Float, byName["ScaleFactor"]);
        }

        [Test]
        public void GestureLabel_MapsTheEightIndices()
        {
            Assert.AreEqual("Neutral", VrcParameters.GestureLabel(0f));
            Assert.AreEqual("Fist", VrcParameters.GestureLabel(1f));
            Assert.AreEqual("HandOpen", VrcParameters.GestureLabel(2f));
            Assert.AreEqual("ThumbsUp", VrcParameters.GestureLabel(7f));
        }

        [Test]
        public void GestureLabel_RejectsFractionalAndOutOfRangeValues()
        {
            Assert.IsNull(VrcParameters.GestureLabel(1.5f));
            Assert.IsNull(VrcParameters.GestureLabel(-1f));
            Assert.IsNull(VrcParameters.GestureLabel(8f));
        }

        [Test]
        public void IsGestureParameter_MatchesOnlyTheTwoGestureInts()
        {
            Assert.IsTrue(VrcParameters.IsGestureParameter("GestureLeft"));
            Assert.IsTrue(VrcParameters.IsGestureParameter("GestureRight"));
            Assert.IsFalse(VrcParameters.IsGestureParameter("GestureLeftWeight"));
            Assert.IsFalse(VrcParameters.IsGestureParameter("gestureleft"));
        }
    }
}
