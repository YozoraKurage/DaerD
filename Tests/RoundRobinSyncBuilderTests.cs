using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class RoundRobinSyncBuilderTests
    {
        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);
            return controller;
        }

        static RoundRobinSyncBuilder.Request NewRequest(AnimatorController controller,
            params string[] targets)
        {
            var request = new RoundRobinSyncBuilder.Request
            {
                controller = controller,
                baseName = "RR",
                skipDrivers = true,
            };
            request.targets.AddRange(targets);
            return request;
        }

        static bool HasCondition(AnimatorTransitionBase transition, string parameter,
            AnimatorConditionMode mode, float threshold)
        {
            foreach (var condition in transition.conditions)
                if (condition.parameter == parameter && condition.mode == mode
                    && Mathf.Approximately(condition.threshold, threshold))
                    return true;
            return false;
        }

        // ---- validation -----------------------------------------------------

        [Test]
        public void Validate_RejectsBadRequests()
        {
            var controller = NewController();
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(NewRequest(null, "F", "B")));
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(NewRequest(controller, "F")));
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(NewRequest(controller, "F", "F")));
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(NewRequest(controller, "F", "Missing")));

            controller.AddParameter("T", AnimatorControllerParameterType.Trigger);
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(NewRequest(controller, "F", "T")));

            var zeroStep = NewRequest(controller, "F", "B");
            zeroStep.stepSeconds = 0f;
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(zeroStep));

            var clash = NewController();
            clash.AddParameter("RR/Index", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(RoundRobinSyncBuilder.Validate(NewRequest(clash, "F", "B")));

            Assert.IsNull(RoundRobinSyncBuilder.Validate(NewRequest(controller, "F", "B", "I")));
        }

        // ---- structure ------------------------------------------------------

        [Test]
        public void Apply_BuildsSendCycleAndDecoder()
        {
            var controller = NewController();
            Assert.IsTrue(RoundRobinSyncBuilder.Apply(NewRequest(controller, "F", "B", "I")));

            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "IsLocal").type);
            Assert.AreEqual(AnimatorControllerParameterType.Int,
                DbtBuilder.FindParameter(controller, "RR/Index").type);
            Assert.AreEqual(AnimatorControllerParameterType.Float,
                DbtBuilder.FindParameter(controller, "RR/Float").type);
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "RR/Bool").type);
            Assert.AreEqual(AnimatorControllerParameterType.Int,
                DbtBuilder.FindParameter(controller, "RR/Int").type);

            Assert.AreEqual(2, controller.layers.Length);
            var stateMachine = controller.layers[1].stateMachine;
            // 3 send + idle + 3 recv
            Assert.AreEqual(7, stateMachine.states.Length);

            // The send cycle: Send F → Send B → Send I → Send F, exit-time only.
            var sendF = FindState(stateMachine, "Send F");
            var sendB = FindState(stateMachine, "Send B");
            var sendI = FindState(stateMachine, "Send I");
            Assert.AreEqual(sendB, sendF.transitions[0].destinationState);
            Assert.AreEqual(sendI, sendB.transitions[0].destinationState);
            Assert.AreEqual(sendF, sendI.transitions[0].destinationState);
            Assert.IsTrue(sendF.transitions[0].hasExitTime);
            Assert.AreEqual(0.3f, sendF.transitions[0].exitTime);
            Assert.AreEqual(0f, sendF.transitions[0].duration);
            Assert.AreEqual(0, sendF.transitions[0].conditions.Length);

            // Locals fall through to the first slot; remotes branch to Idle.
            Assert.AreEqual(sendF, stateMachine.defaultState);
            Assert.AreEqual(1, stateMachine.entryTransitions.Length);
            Assert.AreEqual("Remote Idle", stateMachine.entryTransitions[0].destinationState.name);
            Assert.IsTrue(HasCondition(stateMachine.entryTransitions[0], "IsLocal",
                AnimatorConditionMode.IfNot, 0f));

            // The decoder: one Any-State route per slot, keyed on the index.
            Assert.AreEqual(3, stateMachine.anyStateTransitions.Length);
            foreach (var transition in stateMachine.anyStateTransitions)
            {
                Assert.IsFalse(transition.canTransitionToSelf);
                Assert.IsFalse(transition.hasExitTime);
                Assert.IsTrue(HasCondition(transition, "IsLocal", AnimatorConditionMode.IfNot, 0f));
            }
            var recvB = FindState(stateMachine, "Recv B");
            foreach (var transition in stateMachine.anyStateTransitions)
                if (transition.destinationState == recvB)
                    Assert.IsTrue(HasCondition(transition, "RR/Index", AnimatorConditionMode.Equals, 1f));
        }

        static AnimatorState FindState(AnimatorStateMachine stateMachine, string name)
        {
            foreach (var child in stateMachine.states)
                if (child.state != null && child.state.name == name)
                    return child.state;
            Assert.Fail("State '" + name + "' not found.");
            return null;
        }

        [Test]
        public void Apply_BoolEncodingCreatesIndexBits()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");   // 3 slots -> 2 bits
            request.encoding = RoundRobinSyncBuilder.IndexEncoding.Bool;
            Assert.IsTrue(RoundRobinSyncBuilder.Apply(request));

            Assert.IsNull(DbtBuilder.FindParameter(controller, "RR/Index"));
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "RR/Index/b0").type);
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "RR/Index/b1").type);

            var stateMachine = controller.layers[1].stateMachine;
            var recvI = FindState(stateMachine, "Recv I");   // slot 2 = b0 off, b1 on
            foreach (var transition in stateMachine.anyStateTransitions)
                if (transition.destinationState == recvI)
                {
                    Assert.IsTrue(HasCondition(transition, "RR/Index/b0", AnimatorConditionMode.IfNot, 0f));
                    Assert.IsTrue(HasCondition(transition, "RR/Index/b1", AnimatorConditionMode.If, 0f));
                }
        }

        [Test]
        public void Apply_OnlyCreatesChannelsForPresentTypes()
        {
            var controller = NewController();
            Assert.IsTrue(RoundRobinSyncBuilder.Apply(NewRequest(controller, "F", "B")));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "RR/Float"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "RR/Bool"));
            Assert.IsNull(DbtBuilder.FindParameter(controller, "RR/Int"));
        }

        [Test]
        public void Apply_AddsGeneratedParametersToTheStore()
        {
            var controller = NewController();
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            var request = NewRequest(controller, "F", "B");
            request.store = ParameterStore.TryWrap(asset);
            Assert.IsTrue(RoundRobinSyncBuilder.Apply(request));

            var index = VrcExpressionParameters.Find(asset, "RR/Index");
            Assert.IsNotNull(index);
            Assert.AreEqual(VrcExpressionParameters.ValueType.Int, index.valueType);
            Assert.IsTrue(index.synced);
            Assert.IsFalse(index.saved);
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "RR/Float"));
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "RR/Bool"));
            // The targets themselves are NOT added.
            Assert.IsNull(VrcExpressionParameters.Find(asset, "F"));
        }

        [Test]
        public void CostPreview_ComparesCompressedAgainstDirect()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            // direct: 8 (F) + 1 (B) + 8 (I) = 17; compressed: 8 index + 8 F + 1 B + 8 I = 25
            Assert.AreEqual(17, RoundRobinSyncBuilder.DirectBits(request));
            Assert.AreEqual(25, RoundRobinSyncBuilder.CompressedBits(request));

            // Compression pays off as slots share channels: 4 floats direct = 32,
            // compressed = 8 index + 8 channel = 16.
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            controller.AddParameter("F3", AnimatorControllerParameterType.Float);
            controller.AddParameter("F4", AnimatorControllerParameterType.Float);
            var floats = NewRequest(controller, "F", "F2", "F3", "F4");
            Assert.AreEqual(32, RoundRobinSyncBuilder.DirectBits(floats));
            Assert.AreEqual(16, RoundRobinSyncBuilder.CompressedBits(floats));

            var bits = NewRequest(controller, "F", "F2", "F3", "F4");
            bits.encoding = RoundRobinSyncBuilder.IndexEncoding.Bool;
            Assert.AreEqual(10, RoundRobinSyncBuilder.CompressedBits(bits));   // 2 bits + 8
        }
    }
}
