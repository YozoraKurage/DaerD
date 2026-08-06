using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class AsyncSyncBuilderTests
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

        static AsyncSyncBuilder.Request NewRequest(AnimatorController controller,
            params string[] targets)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
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
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(null, "F", "B")));
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(controller, "F")));
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(controller, "F", "F")));
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(controller, "F", "Missing")));

            controller.AddParameter("T", AnimatorControllerParameterType.Trigger);
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(controller, "F", "T")));

            var zeroStep = NewRequest(controller, "F", "B");
            zeroStep.stepSeconds = 0f;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(zeroStep));

            var clash = NewController();
            clash.AddParameter("Async/Index", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(clash, "F", "B")));

            Assert.IsNull(AsyncSyncBuilder.Validate(NewRequest(controller, "F", "B", "I")));
        }

        // ---- structure ------------------------------------------------------

        [Test]
        public void Apply_BuildsSendCycleAndDecoder()
        {
            var controller = NewController();
            Assert.IsTrue(AsyncSyncBuilder.Apply(NewRequest(controller, "F", "B", "I")));

            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "IsLocal").type);
            Assert.AreEqual(AnimatorControllerParameterType.Int,
                DbtBuilder.FindParameter(controller, "Async/Index").type);
            Assert.AreEqual(AnimatorControllerParameterType.Float,
                DbtBuilder.FindParameter(controller, "Async/Float").type);
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Async/Bool").type);
            Assert.AreEqual(AnimatorControllerParameterType.Int,
                DbtBuilder.FindParameter(controller, "Async/Int").type);

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
                    Assert.IsTrue(HasCondition(transition, "Async/Index", AnimatorConditionMode.Equals, 1f));
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
            request.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            Assert.IsNull(DbtBuilder.FindParameter(controller, "Async/Index"));
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Async/Index/b0").type);
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Async/Index/b1").type);

            var stateMachine = controller.layers[1].stateMachine;
            var recvI = FindState(stateMachine, "Recv I");   // slot 2 = b0 off, b1 on
            foreach (var transition in stateMachine.anyStateTransitions)
                if (transition.destinationState == recvI)
                {
                    Assert.IsTrue(HasCondition(transition, "Async/Index/b0", AnimatorConditionMode.IfNot, 0f));
                    Assert.IsTrue(HasCondition(transition, "Async/Index/b1", AnimatorConditionMode.If, 0f));
                }
        }

        [Test]
        public void Apply_RegeneratesExistingLayerInPlace()
        {
            var controller = NewController();
            Assert.IsTrue(AsyncSyncBuilder.Apply(NewRequest(controller, "F", "B", "I")));
            Assert.AreEqual(2, controller.layers.Length);
            Assert.AreEqual(7, controller.layers[1].stateMachine.states.Length);

            // Rerun against the same layer with fewer targets: no new layer, rebuilt content.
            var again = NewRequest(controller, "F", "B");
            again.layerIndex = 1;
            Assert.IsTrue(AsyncSyncBuilder.Apply(again));
            Assert.AreEqual(2, controller.layers.Length);
            var stateMachine = controller.layers[1].stateMachine;
            Assert.AreEqual(5, stateMachine.states.Length);   // 2 send + idle + 2 recv
            Assert.AreEqual(2, stateMachine.anyStateTransitions.Length);
            Assert.AreEqual(1, stateMachine.entryTransitions.Length);
        }

        [Test]
        public void Apply_RejectsMissingRegenerationTarget()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B");
            request.layerIndex = 5;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(request));
        }

        [Test]
        public void AsyncSyncConfig_SaveReplacesPerLayerAndPrunesDeadEntries()
        {
            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var layerA = new AnimatorStateMachine { name = "A" };
            var layerB = new AnimatorStateMachine { name = "B" };

            data.SaveAsyncSync(new GraphFrameData.AsyncSyncConfig
            {
                layer = layerA,
                baseName = "One",
                targets = new List<string> { "F" },
            });
            data.SaveAsyncSync(new GraphFrameData.AsyncSyncConfig { layer = layerB, baseName = "Two" });
            // Same layer again → replaces instead of stacking.
            data.SaveAsyncSync(new GraphFrameData.AsyncSyncConfig
            {
                layer = layerA,
                baseName = "One v2",
                targets = new List<string> { "F", "B" },
            });

            var configs = data.AsyncSyncs();
            Assert.AreEqual(2, configs.Count);
            foreach (var config in configs)
                if (config.layer == layerA)
                {
                    Assert.AreEqual("One v2", config.baseName);
                    Assert.AreEqual(2, config.targets.Count);
                }

            // A deleted layer prunes its entry.
            Object.DestroyImmediate(layerB);
            Assert.AreEqual(1, data.AsyncSyncs().Count);
        }

        [Test]
        public void Apply_OnlyCreatesChannelsForPresentTypes()
        {
            var controller = NewController();
            Assert.IsTrue(AsyncSyncBuilder.Apply(NewRequest(controller, "F", "B")));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Async/Float"));
            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Async/Bool"));
            Assert.IsNull(DbtBuilder.FindParameter(controller, "Async/Int"));
        }

        [Test]
        public void Apply_AddsGeneratedParametersToTheStore()
        {
            var controller = NewController();
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            var request = NewRequest(controller, "F", "B");
            request.store = ParameterStore.TryWrap(asset);
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var index = VrcExpressionParameters.Find(asset, "Async/Index");
            Assert.IsNotNull(index);
            Assert.AreEqual(VrcExpressionParameters.ValueType.Int, index.valueType);
            Assert.IsTrue(index.synced);
            Assert.IsFalse(index.saved);
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "Async/Float"));
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "Async/Bool"));
            // The targets themselves are NOT added.
            Assert.IsNull(VrcExpressionParameters.Find(asset, "F"));
        }

        [Test]
        public void CostPreview_ComparesCompressedAgainstDirect()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            // direct: 8 (F) + 1 (B) + 8 (I) = 17; compressed: 8 index + 8 F + 1 B + 8 I = 25
            Assert.AreEqual(17, AsyncSyncBuilder.DirectBits(request));
            Assert.AreEqual(25, AsyncSyncBuilder.CompressedBits(request));

            // Compression pays off as slots share channels: 4 floats direct = 32,
            // compressed = 8 index + 8 channel = 16.
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            controller.AddParameter("F3", AnimatorControllerParameterType.Float);
            controller.AddParameter("F4", AnimatorControllerParameterType.Float);
            var floats = NewRequest(controller, "F", "F2", "F3", "F4");
            Assert.AreEqual(32, AsyncSyncBuilder.DirectBits(floats));
            Assert.AreEqual(16, AsyncSyncBuilder.CompressedBits(floats));

            var bits = NewRequest(controller, "F", "F2", "F3", "F4");
            bits.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            Assert.AreEqual(10, AsyncSyncBuilder.CompressedBits(bits));   // 2 bits + 8
        }

        // ---- sizing the setup to the parameter count ------------------------

        [Test]
        public void ResolveEncoding_Auto_TakesTheCheaperIndex_AndTiesGoToInt()
        {
            var controller = NewController();
            var few = NewRequest(controller, "F", "B", "I");
            few.encoding = AsyncSyncBuilder.IndexEncoding.Auto;
            Assert.AreEqual(AsyncSyncBuilder.IndexEncoding.Bool, AsyncSyncBuilder.ResolveEncoding(few),
                "2 index bits beat a flat 8");

            // At 8 index bits the Bool index costs the same as the Int one; the tie goes to Int
            // because it is one parameter and one condition per route instead of eight.
            var many = NewRequest(controller);
            many.encoding = AsyncSyncBuilder.IndexEncoding.Auto;
            for (int i = 0; i < 200; i++) many.targets.Add("P" + i);
            Assert.AreEqual(AsyncSyncBuilder.IndexEncoding.Int, AsyncSyncBuilder.ResolveEncoding(many));

            var explicitInt = NewRequest(controller, "F", "B");
            Assert.AreEqual(AsyncSyncBuilder.IndexEncoding.Int, AsyncSyncBuilder.ResolveEncoding(explicitInt),
                "an explicit choice is never overridden");
        }

        [Test]
        public void FreeSlots_ReportsTheTailOfTheBoolIndexRange()
        {
            var controller = NewController();
            var three = NewRequest(controller, "F", "B", "I");
            three.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            Assert.AreEqual(1, AsyncSyncBuilder.FreeSlots(three), "2 bits hold 4 slots");

            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var four = NewRequest(controller, "F", "B", "I", "F2");
            four.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            Assert.AreEqual(0, AsyncSyncBuilder.FreeSlots(four), "the next slot needs another bit");

            var asInt = NewRequest(controller, "F", "B", "I");
            Assert.AreEqual(0, AsyncSyncBuilder.FreeSlots(asInt), "an Int index has room either way");
        }

        [Test]
        public void CycleSeconds_IsTheWorstCaseAgeOfAValue()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.stepSeconds = 0.4f;
            Assert.AreEqual(1.2f, AsyncSyncBuilder.CycleSeconds(request), 0.0001f);
        }

        [Test]
        public void Warnings_CallOutASetupThatSavesNothing()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("B1", AnimatorControllerParameterType.Bool);
            controller.AddParameter("B2", AnimatorControllerParameterType.Bool);

            // 2 Bools direct = 2 bits; compressed = 1 index bit + 1 Bool channel = 2.
            var pointless = NewRequest(controller, "B1", "B2");
            pointless.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            Assert.AreEqual(AsyncSyncBuilder.DirectBits(pointless), AsyncSyncBuilder.CompressedBits(pointless));
            Assert.IsTrue(AsyncSyncBuilder.Warnings(pointless).Exists(w => w.Contains("saves nothing")));

            controller.AddParameter("F1", AnimatorControllerParameterType.Float);
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            controller.AddParameter("F3", AnimatorControllerParameterType.Float);
            var worthwhile = NewRequest(controller, "F1", "F2", "F3");
            Assert.IsFalse(AsyncSyncBuilder.Warnings(worthwhile).Exists(w => w.Contains("saves nothing")));
        }

        // ---- Empty clip -----------------------------------------------------

        /// <summary>A clip with an actual length — normalized exit times need one.</summary>
        static AnimationClip NewEmptyClip(float length)
        {
            var clip = new AnimationClip { name = "Empty" };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0f, length, 1f));
            return clip;
        }

        [Test]
        public void Apply_FillsGeneratedStatesWithTheEmptyClip_AndKeepsTheStepInSeconds()
        {
            var controller = NewController();
            var clip = NewEmptyClip(0.5f);
            var request = NewRequest(controller, "F", "B", "I");
            request.emptyClip = clip;
            request.stepSeconds = 0.3f;
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[controller.layers.Length - 1].stateMachine;
            int filled = 0;
            foreach (var child in sm.states)
            {
                Assert.AreSame(clip, child.state.motion, "state '" + child.state.name + "' has no motion");
                filled++;
            }
            Assert.AreEqual(7, filled, "3 send + 3 recv + idle");

            // Exit time is normalized to the motion, so the dwell has to be divided by its length.
            var send = FindState(sm, "Send F");
            Assert.AreEqual(0.3f / 0.5f, send.transitions[0].exitTime, 0.0001f);

            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Apply_WithoutAnEmptyClip_LeavesStatesMotionless_SoExitTimeReadsAsSeconds()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.stepSeconds = 0.45f;
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[controller.layers.Length - 1].stateMachine;
            var send = FindState(sm, "Send F");
            Assert.IsNull(send.motion);
            Assert.AreEqual(0.45f, send.transitions[0].exitTime, 0.0001f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Apply_IgnoresAZeroLengthEmptyClip()
        {
            var controller = NewController();
            var clip = new AnimationClip { name = "Zero" };
            var request = NewRequest(controller, "F", "B");
            request.emptyClip = clip;
            Assert.IsNull(AsyncSyncBuilder.ResolveEmptyClip(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[controller.layers.Length - 1].stateMachine;
            Assert.IsNull(FindState(sm, "Send F").motion);

            Object.DestroyImmediate(clip);
            Object.DestroyImmediate(controller);
        }
    }
}
