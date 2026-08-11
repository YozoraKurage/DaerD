using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What "+ Add Condition" starts from. The rule only pays off on numeric parameters, where
    /// the second condition is nearly always the other end of a range.
    /// </summary>
    public class ConditionEditingTests
    {
        static readonly string[] Names = { "Alpha", "Count", "On", "Fire" };

        static Dictionary<string, AnimatorControllerParameterType> Types() =>
            new Dictionary<string, AnimatorControllerParameterType>
            {
                { "Alpha", AnimatorControllerParameterType.Float },
                { "Count", AnimatorControllerParameterType.Int },
                { "On", AnimatorControllerParameterType.Bool },
                { "Fire", AnimatorControllerParameterType.Trigger },
            };

        static List<TransitionClipboard.ConditionData> Working(params TransitionClipboard.ConditionData[] rows) =>
            new List<TransitionClipboard.ConditionData>(rows);

        static TransitionClipboard.ConditionData Row(string parameter, AnimatorConditionMode mode,
            float threshold = 0f) =>
            new TransitionClipboard.ConditionData { parameter = parameter, mode = mode, threshold = threshold };

        [Test]
        public void TheFirstCondition_TakesTheFirstParameter()
        {
            var added = TransitionInspector.NextCondition(Working(), Names, Types());

            Assert.AreEqual("Alpha", added.parameter);
            Assert.AreEqual(AnimatorConditionMode.Greater, added.mode);
        }

        [Test]
        public void AfterAGreater_TheNextOneClosesTheRange()
        {
            var added = TransitionInspector.NextCondition(
                Working(Row("Alpha", AnimatorConditionMode.Greater, 0.1f)), Names, Types());

            Assert.AreEqual("Alpha", added.parameter);
            Assert.AreEqual(AnimatorConditionMode.Less, added.mode);
        }

        [Test]
        public void AfterALess_TheNextOneClosesTheOtherEnd()
        {
            var added = TransitionInspector.NextCondition(
                Working(Row("Count", AnimatorConditionMode.Less, 5f)), Names, Types());

            Assert.AreEqual("Count", added.parameter);
            Assert.AreEqual(AnimatorConditionMode.Greater, added.mode);
        }

        [Test]
        public void AfterAnEquals_TheParameterCarriesButTheModeDoesNot()
        {
            // Equals has no opposite half to pair with, so only the parameter is worth keeping.
            var added = TransitionInspector.NextCondition(
                Working(Row("Count", AnimatorConditionMode.Equals, 3f)), Names, Types());

            Assert.AreEqual("Count", added.parameter);
            Assert.AreEqual(AnimatorConditionMode.Greater, added.mode);
        }

        [Test]
        public void AfterABool_TheParameterDoesNotCarry()
        {
            // A second condition on the same Bool can only contradict the first.
            var added = TransitionInspector.NextCondition(
                Working(Row("On", AnimatorConditionMode.If)), Names, Types());

            Assert.AreEqual("Alpha", added.parameter);
        }

        [Test]
        public void AfterAParameterThatIsGone_TheParameterDoesNotCarry()
        {
            var added = TransitionInspector.NextCondition(
                Working(Row("Deleted", AnimatorConditionMode.Greater, 1f)), Names, Types());

            Assert.AreEqual("Alpha", added.parameter);
            Assert.AreEqual(AnimatorConditionMode.Greater, added.mode);
        }

        // ---- wheel over a condition row ---------------------------------------

        [Test]
        public void AWheelNotchOnAnIntThreshold_MovesItByOne()
        {
            Assert.AreEqual(4f, ConditionGui.Stepped(3f, 1, AnimatorControllerParameterType.Int, fine: false));
            Assert.AreEqual(2f, ConditionGui.Stepped(3f, -1, AnimatorControllerParameterType.Int, fine: false));

            // A threshold no Int can sit on is snapped rather than carried along, and Ctrl does
            // not offer a finer step for a value that has to stay whole.
            Assert.AreEqual(3f, ConditionGui.Stepped(2.5f, 1, AnimatorControllerParameterType.Int, fine: false));
            Assert.AreEqual(4f, ConditionGui.Stepped(3f, 1, AnimatorControllerParameterType.Int, fine: true));
        }

        [Test]
        public void AWheelNotchOnAFloatThreshold_MovesItByATenth()
        {
            Assert.AreEqual(0.6f, ConditionGui.Stepped(0.5f, 1, AnimatorControllerParameterType.Float, fine: false), 1e-5f);
            Assert.AreEqual(0.51f, ConditionGui.Stepped(0.5f, 1, AnimatorControllerParameterType.Float, fine: true), 1e-5f);
        }

        [Test]
        public void TenNotchesOfATenth_LandOnOne()
        {
            float value = 0f;
            for (int i = 0; i < 10; i++)
                value = ConditionGui.Stepped(value, 1, AnimatorControllerParameterType.Float, fine: false);

            // Without the rounding this is 0.99999994, and the field shows it.
            Assert.AreEqual(1f, value);
        }

        [Test]
        public void TheModeWrapsRatherThanStoppingAtTheEnds()
        {
            // Two entries (true / false, or Greater / Less) — a notch either way is the flip.
            Assert.AreEqual(1, ConditionGui.Wrap(0 + 1, 2));
            Assert.AreEqual(0, ConditionGui.Wrap(1 + 1, 2));
            Assert.AreEqual(1, ConditionGui.Wrap(0 - 1, 2));

            // Four (an Int's comparisons) cycles instead.
            Assert.AreEqual(3, ConditionGui.Wrap(0 - 1, 4));
            Assert.AreEqual(0, ConditionGui.Wrap(4, 4));
        }
    }
}
