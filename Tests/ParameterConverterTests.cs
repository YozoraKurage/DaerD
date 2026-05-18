using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class ParameterConverterTests
    {
        [Test]
        public void BoolIf_ToInt_BecomesEqualsOne()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.If, 0f, AnimatorControllerParameterType.Int);
            Assert.AreEqual(AnimatorConditionMode.Equals, result.mode);
            Assert.AreEqual(1f, result.threshold, 1e-4f);
        }

        [Test]
        public void BoolIfNot_ToInt_BecomesEqualsZero()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.IfNot, 0f, AnimatorControllerParameterType.Int);
            Assert.AreEqual(AnimatorConditionMode.Equals, result.mode);
            Assert.AreEqual(0f, result.threshold, 1e-4f);
        }

        [Test]
        public void BoolIf_ToFloat_BecomesGreaterHalf()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.If, 0f, AnimatorControllerParameterType.Float);
            Assert.AreEqual(AnimatorConditionMode.Greater, result.mode);
            Assert.AreEqual(0.5f, result.threshold, 1e-4f);
        }

        [Test]
        public void FloatGreater_ToBool_BecomesIf_AndIsLossy()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.Greater, 3f, AnimatorControllerParameterType.Bool);
            Assert.AreEqual(AnimatorConditionMode.If, result.mode);
            Assert.IsTrue(result.lossy);
        }

        [Test]
        public void FloatLess_ToBool_BecomesIfNot()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.Less, 3f, AnimatorControllerParameterType.Bool);
            Assert.AreEqual(AnimatorConditionMode.IfNot, result.mode);
        }

        [Test]
        public void IntEquals_ToFloat_BecomesGreater_AndIsLossy()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.Equals, 2f, AnimatorControllerParameterType.Float);
            Assert.AreEqual(AnimatorConditionMode.Greater, result.mode);
            Assert.IsTrue(result.lossy);
        }

        [Test]
        public void IntGreater_ToFloat_IsUnchanged()
        {
            var result = ParameterConverter.ConvertCondition(
                AnimatorConditionMode.Greater, 2f, AnimatorControllerParameterType.Float);
            Assert.AreEqual(AnimatorConditionMode.Greater, result.mode);
            Assert.IsFalse(result.lossy);
        }

        [Test]
        public void ComputeAndApply_BoolToInt_RewritesCondition()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var sm = controller.layers[0].stateMachine;
            var a = sm.AddState("A");
            var b = sm.AddState("B");
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);
            var transition = a.AddTransition(b);
            transition.AddCondition(AnimatorConditionMode.If, 0f, "P");

            var plan = ParameterConverter.ComputeConversion(controller, "P", AnimatorControllerParameterType.Int);
            Assert.AreEqual(1, plan.conditionChanges.Count);

            ParameterConverter.Apply(plan);

            Assert.AreEqual(AnimatorControllerParameterType.Int, controller.parameters[0].type);
            Assert.AreEqual(AnimatorConditionMode.Equals, transition.conditions[0].mode);
            Assert.AreEqual(1f, transition.conditions[0].threshold, 1e-4f);

            Object.DestroyImmediate(controller);
        }
    }
}
