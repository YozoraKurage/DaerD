using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class AsyncSyncBuilderTests
    {
        DaerDLanguage _savedLanguage;

        // Warning assertions match English substrings; pin the language so the tests pass
        // on a Japanese editor too.
        [OneTimeSetUp]
        public void ForceEnglish()
        {
            _savedLanguage = L.Language;
            L.Language = DaerDLanguage.English;
        }

        [OneTimeTearDown]
        public void RestoreLanguage() => L.Language = _savedLanguage;

        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);
            return controller;
        }

        /// <summary>Explicit Int encoding: the Request default is Auto, and these tests
        /// assert exact structure, so they pin the encoding they mean.</summary>
        static AsyncSyncBuilder.Request NewRequest(AnimatorController controller,
            params string[] targets)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
                encoding = AsyncSyncBuilder.IndexEncoding.Int,
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

        // ---- default base name ----------------------------------------------

        [Test]
        public void DefaultBaseName_FollowsTheAssetGuid_AndDodgesSetupsAlreadyOnTheController()
        {
            const string guid = "0123456789abcdef0123456789abcdef";
            Assert.AreEqual("DD012345", AsyncSyncBuilder.DefaultBaseName(guid, new List<string>()));

            // A second setup on the same controller can't answer to the first one's name.
            Assert.AreEqual("DD012345_2",
                AsyncSyncBuilder.DefaultBaseName(guid, new List<string> { "DD012345" }));
            Assert.AreEqual("DD012345_3",
                AsyncSyncBuilder.DefaultBaseName(guid, new List<string> { "DD012345", "DD012345_2" }));

            // No GUID: the historical name, so nothing that ran before this existed changes.
            Assert.AreEqual("Async", AsyncSyncBuilder.DefaultBaseName(string.Empty, new List<string>()));
            Assert.AreEqual("Async", AsyncSyncBuilder.DefaultBaseName(null, new List<string>()));

            var controller = NewController();
            Assert.AreEqual("Async", AsyncSyncBuilder.DefaultBaseName(controller),
                "an in-memory controller has no asset GUID to derive from");
            Object.DestroyImmediate(controller);
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
            // because it is one parameter and one condition per route instead of eight. The
            // parameters must exist: slots are built from the controller, not from the list.
            var many = NewRequest(controller);
            many.encoding = AsyncSyncBuilder.IndexEncoding.Auto;
            for (int i = 0; i < 200; i++)
            {
                controller.AddParameter("P" + i, AnimatorControllerParameterType.Bool);
                many.targets.Add("P" + i);
            }
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

        [Test]
        public void Warnings_CallOutATargetAnimationWrites()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            Assert.IsFalse(AsyncSyncBuilder.Warnings(request).Exists(w => w.Contains("(AAP")));

            // A clip animating the parameter on the Animator itself — a DBT gadget output.
            var clip = new AnimationClip { name = "F AAP" };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "F"),
                AnimationCurve.Constant(0f, 1f, 1f));
            controller.layers[0].stateMachine.AddState("Write").motion = clip;

            Assert.IsTrue(AsyncSyncBuilder.Warnings(request)
                .Exists(w => w.Contains("(AAP") && w.Contains("'F'")));
            // Said, not refused: the scan can't yet tell a clip that plays from one that
            // merely exists, so it must not block a setup that may be fine.
            Assert.IsNull(AsyncSyncBuilder.Validate(request));

            Object.DestroyImmediate(controller);
        }

        // ---- float channels ---------------------------------------------------

        [Test]
        public void RequestDefaults_AutoEncoding_OneChannelPerType()
        {
            var request = new AsyncSyncBuilder.Request();
            Assert.AreEqual(AsyncSyncBuilder.IndexEncoding.Auto, request.encoding);
            Assert.AreEqual(1, request.floatChannels);
            Assert.AreEqual(1, request.boolChannels);
        }

        [Test]
        public void FloatChannels_BatchFloatsIntoSharedSlots()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            controller.AddParameter("F3", AnimatorControllerParameterType.Float);
            controller.AddParameter("F4", AnimatorControllerParameterType.Float);

            var request = NewRequest(controller, "F", "F2", "F3", "F4");
            request.floatChannels = 2;

            var slots = AsyncSyncBuilder.BuildSlots(request);
            Assert.AreEqual(2, slots.Count, "4 floats over 2 channels = 2 slots");
            CollectionAssert.AreEqual(new[] { "F", "F2" }, slots[0].targets);
            CollectionAssert.AreEqual(new[] { "F3", "F4" }, slots[1].targets);

            // 8 index + 2 × 8 float channels = 24; direct would be 32.
            Assert.AreEqual(24, AsyncSyncBuilder.CompressedBits(request));
            Assert.AreEqual(2, AsyncSyncBuilder.FloatChannelsUsed(request));

            var generated = AsyncSyncBuilder.GeneratedParameters(request);
            Assert.IsTrue(generated.Exists(g => g.name == "Async/Float"));
            Assert.IsTrue(generated.Exists(g => g.name == "Async/Float2"));

            // The cycle shortens with the slot count: 2 steps instead of 4.
            Assert.AreEqual(2f * request.stepSeconds, AsyncSyncBuilder.CycleSeconds(request), 0.0001f);
        }

        [Test]
        public void FloatChannels_UnusedChannelsAreNotCreated()
        {
            var controller = NewController();
            // One Float and one Bool: no batch ever carries 2 floats, so channel 2 of 4
            // requested would be dead weight — it must be neither generated nor billed.
            var request = NewRequest(controller, "F", "B");
            request.floatChannels = 4;

            Assert.AreEqual(1, AsyncSyncBuilder.FloatChannelsUsed(request));
            Assert.IsFalse(AsyncSyncBuilder.GeneratedParameters(request)
                .Exists(g => g.name == "Async/Float2"));
        }

        [Test]
        public void Apply_WithFloatChannels_SendsAndDecodesTheBatch()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "F", "F2", "B");
            request.floatChannels = 2;
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            // 2 slots ({F,F2}, {B}) -> 2 send + idle + 2 recv.
            Assert.AreEqual(5, sm.states.Length);
            Assert.IsNotNull(FindState(sm, "Send F +1"));
            Assert.IsNotNull(FindState(sm, "Recv F +1"));
            Assert.AreEqual(2, sm.anyStateTransitions.Length);

            Assert.AreEqual(AnimatorControllerParameterType.Float,
                DbtBuilder.FindParameter(controller, "Async/Float2").type);
        }

        // ---- bool channels ----------------------------------------------------

        static AnimatorController BoolController(int count)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            for (int i = 1; i <= count; i++)
                controller.AddParameter("B" + i, AnimatorControllerParameterType.Bool);
            return controller;
        }

        static string[] BoolNames(int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = "B" + (i + 1);
            return names;
        }

        [Test]
        public void BoolChannels_BatchBoolsIntoSharedSlots()
        {
            var controller = BoolController(5);
            var request = NewRequest(controller, BoolNames(5));
            request.boolChannels = 2;

            var slots = AsyncSyncBuilder.BuildSlots(request);
            Assert.AreEqual(3, slots.Count, "5 bools over 2 channels = 3 slots");
            CollectionAssert.AreEqual(new[] { "B1", "B2" }, slots[0].targets);
            CollectionAssert.AreEqual(new[] { "B3", "B4" }, slots[1].targets);
            CollectionAssert.AreEqual(new[] { "B5" }, slots[2].targets);

            Assert.AreEqual(2, AsyncSyncBuilder.BoolChannelsUsed(request));
        }

        [Test]
        public void BoolChannels_TradeOneSyncedBitForAShorterPass()
        {
            var controller = BoolController(16);
            var slow = NewRequest(controller, BoolNames(16));
            slow.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            // 16 slots -> 4 index bits + 1 channel; one pass is 16 steps.
            Assert.AreEqual(5, AsyncSyncBuilder.CompressedBits(slow));
            Assert.AreEqual(16, AsyncSyncBuilder.BuildSchedule(AsyncSyncBuilder.BuildSlots(slow)).Count);

            var fast = NewRequest(controller, BoolNames(16));
            fast.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            fast.boolChannels = 4;
            // 4 slots -> 2 index bits + 4 channels: one more bit, a quarter of the pass.
            Assert.AreEqual(6, AsyncSyncBuilder.CompressedBits(fast));
            Assert.AreEqual(4, AsyncSyncBuilder.BuildSchedule(AsyncSyncBuilder.BuildSlots(fast)).Count);
        }

        [Test]
        public void BoolChannels_Channel0_KeepsTheNameOlderSetupsAlreadySync()
        {
            var controller = BoolController(4);
            var request = NewRequest(controller, BoolNames(4));
            request.boolChannels = 2;

            var generated = AsyncSyncBuilder.GeneratedParameters(request);
            // Renaming channel 0 would strand the store entry an existing setup syncs.
            Assert.IsTrue(generated.Exists(g => g.name == "Async/Bool"));
            Assert.IsTrue(generated.Exists(g => g.name == "Async/Bool2"));
            Assert.AreEqual(AsyncSyncBuilder.ChannelParameter("Async", AnimatorControllerParameterType.Bool),
                AsyncSyncBuilder.BoolChannelParameter("Async", 0));
        }

        [Test]
        public void BoolChannels_UnusedChannelsAreNotCreated()
        {
            var controller = NewController();
            var request = NewRequest(controller, "B", "F", "I");
            request.boolChannels = 4;

            Assert.AreEqual(1, AsyncSyncBuilder.BoolChannelsUsed(request));
            Assert.IsFalse(AsyncSyncBuilder.GeneratedParameters(request)
                .Exists(g => g.name == "Async/Bool2"));
        }

        [Test]
        public void BoolChannels_DoNotBatchAcrossRatesOrTypes()
        {
            var controller = BoolController(3);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);
            controller.AddParameter("I2", AnimatorControllerParameterType.Int);
            var request = NewRequest(controller, "B1", "B2", "B3", "I", "I2");
            request.boolChannels = 4;
            request.rates["B3"] = 2;    // a different rate is a different batch

            var slots = AsyncSyncBuilder.BuildSlots(request);
            CollectionAssert.AreEqual(new[] { "B1", "B2" }, slots[0].targets);
            CollectionAssert.AreEqual(new[] { "B3" }, slots[1].targets);
            // Ints never batch: one channel, one target.
            CollectionAssert.AreEqual(new[] { "I" }, slots[2].targets);
            CollectionAssert.AreEqual(new[] { "I2" }, slots[3].targets);
        }

        [Test]
        public void Apply_WithBoolChannels_SendsAndDecodesTheBatch()
        {
            var controller = BoolController(4);
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "B1", "B2", "B3", "F");
            request.boolChannels = 2;
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            // 3 slots ({B1,B2}, {B3}, {F}) -> 3 send + idle + 3 recv.
            Assert.AreEqual(7, sm.states.Length);
            Assert.IsNotNull(FindState(sm, "Send B1 +1"));
            Assert.IsNotNull(FindState(sm, "Recv B1 +1"));
            Assert.AreEqual(3, sm.anyStateTransitions.Length);

            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Async/Bool2").type);
        }

        [Test]
        public void BoolChannels_SurviveTheSavedSetup()
        {
            var controller = BoolController(4);
            var config = new GraphFrameData.AsyncSyncConfig
            {
                baseName = "Async",
                boolChannels = 2,
                targets = new List<string>(BoolNames(4)),
            };
            Assert.AreEqual(2, AsyncSyncBuilder.FromConfig(controller, config).boolChannels);

            // Setups saved before the field existed deserialize to 0 and must read as 1.
            var legacy = new GraphFrameData.AsyncSyncConfig { boolChannels = 0 };
            Assert.AreEqual(1, legacy.BoolChannelsOrDefault);
            Assert.AreEqual(1, AsyncSyncBuilder.FromConfig(controller, legacy).boolChannels);
        }

        [Test]
        public void BoolChannels_OutOfRange_IsRefused()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B");
            request.boolChannels = 0;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(request));
            request.boolChannels = 9;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(request));
            request.boolChannels = 8;
            Assert.IsNull(AsyncSyncBuilder.Validate(request));
        }

        // ---- rate scheduling --------------------------------------------------

        [Test]
        public void BuildSchedule_SpreadsARateTwoSlotToOppositeEndsOfThePass()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.rates["F"] = 2;

            var slots = AsyncSyncBuilder.BuildSlots(request);
            var schedule = AsyncSyncBuilder.BuildSchedule(slots);

            // Weights (2,1,1) over 4 steps: F claims 0 and 2 → F B F I.
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 }, schedule);
            for (int i = 0; i < schedule.Count; i++)
                Assert.AreNotEqual(schedule[i], schedule[(i + 1) % schedule.Count],
                    "no slot may occupy adjacent steps — the decoder would not re-trigger");

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            Assert.AreEqual(2f * request.stepSeconds, intervals["F"], 0.0001f);
            Assert.AreEqual(4f * request.stepSeconds, intervals["B"], 0.0001f);
            Assert.AreEqual(4f * request.stepSeconds, AsyncSyncBuilder.CycleSeconds(request), 0.0001f);
        }

        [Test]
        public void BuildSchedule_HonorsTheTargetOrder_AsTheCycleOrder()
        {
            var controller = NewController();
            // Same parameters, different listed order — the cycle follows the list.
            var schedule = AsyncSyncBuilder.BuildSchedule(
                AsyncSyncBuilder.BuildSlots(NewRequest(controller, "I", "F", "B")));
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, schedule);
            var slots = AsyncSyncBuilder.BuildSlots(NewRequest(controller, "I", "F", "B"));
            Assert.AreEqual("I", slots[0].targets[0]);
            Assert.AreEqual("F", slots[1].targets[0]);
            Assert.AreEqual("B", slots[2].targets[0]);
        }

        [Test]
        public void BuildSchedule_NormalizesSharedFactors_AndCapsUnseparableRates()
        {
            var controller = NewController();
            // All ×2 is the same cycle as all ×1 — the common factor drops out.
            var all = NewRequest(controller, "F", "B", "I");
            all.rates["F"] = 2; all.rates["B"] = 2; all.rates["I"] = 2;
            CollectionAssert.AreEqual(new[] { 0, 1, 2 },
                AsyncSyncBuilder.BuildSchedule(AsyncSyncBuilder.BuildSlots(all)));
            Assert.IsFalse(AsyncSyncBuilder.Warnings(all)
                .Exists(w => w.Contains("effectively runs")), "normalization is not a cap");

            // ×4 against a single other slot can never be separated: capped to alternation.
            var capped = NewRequest(controller, "F", "B");
            capped.rates["F"] = 4;
            CollectionAssert.AreEqual(new[] { 0, 1 },
                AsyncSyncBuilder.BuildSchedule(AsyncSyncBuilder.BuildSlots(capped)));
            Assert.IsTrue(AsyncSyncBuilder.Warnings(capped)
                .Exists(w => w.Contains("effectively runs")), "the cap is called out");
        }

        [Test]
        public void BuildSchedule_HeavierMixes_StayAdjacencyFree()
        {
            var controller = NewController();
            controller.AddParameter("G", AnimatorControllerParameterType.Float);
            controller.AddParameter("H", AnimatorControllerParameterType.Bool);
            var request = NewRequest(controller, "F", "G", "B", "H");
            request.rates["F"] = 4;   // weights (4,2,1,1): F every other step, G twice
            request.rates["G"] = 2;

            var slots = AsyncSyncBuilder.BuildSlots(request);
            var schedule = AsyncSyncBuilder.BuildSchedule(slots);

            Assert.AreEqual(8, schedule.Count);
            int f = 0, g = 0;
            for (int i = 0; i < schedule.Count; i++)
            {
                Assert.AreNotEqual(schedule[i], schedule[(i + 1) % schedule.Count]);
                if (schedule[i] == 0) f++;
                if (schedule[i] == 1) g++;
            }
            Assert.AreEqual(4, f);
            Assert.AreEqual(2, g);

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            Assert.AreEqual(2f * request.stepSeconds, intervals["F"], 0.0001f);
            Assert.AreEqual(4f * request.stepSeconds, intervals["G"], 0.0001f);
            Assert.AreEqual(8f * request.stepSeconds, intervals["B"], 0.0001f);
        }

        [Test]
        public void Apply_WithARate_BuildsOneSendStatePerScheduleStep()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.rates["F"] = 2;
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            // Schedule F B F I -> 4 send states (F twice, uniquely named) + idle + 3 recv.
            Assert.AreEqual(8, sm.states.Length);
            var first = FindState(sm, "Send F");
            var second = FindState(sm, "Send F (2)");
            Assert.AreEqual(3, sm.anyStateTransitions.Length, "the decoder stays one state per slot");

            // The ring: Send F → Send B → Send F (2) → Send I → Send F.
            Assert.AreEqual("Send B", first.transitions[0].destinationState.name);
            Assert.AreEqual("Send I", second.transitions[0].destinationState.name);
        }

        /// <summary>Regression: ticking the FIRST parameter in the wizard used to freeze
        /// Unity — Warnings runs on every repaint and EffectiveWeights spun forever on a
        /// single slot ("1 > 0 others" capped to 1, a no-op flagged as a change).</summary>
        [Test]
        public void SingleTarget_WarningsAndWeights_Terminate()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F");

            Assert.DoesNotThrow(() => AsyncSyncBuilder.Warnings(request));

            var slots = AsyncSyncBuilder.BuildSlots(request);
            CollectionAssert.AreEqual(new[] { 1 }, AsyncSyncBuilder.EffectiveWeights(slots));
            CollectionAssert.AreEqual(new[] { 0 }, AsyncSyncBuilder.BuildSchedule(slots));

            // Same degenerate shape with a rate on the lone slot.
            request.rates["F"] = 4;
            CollectionAssert.AreEqual(new[] { 1 },
                AsyncSyncBuilder.EffectiveWeights(AsyncSyncBuilder.BuildSlots(request)));

            // And with no targets at all (the wizard's initial state).
            Assert.DoesNotThrow(() => AsyncSyncBuilder.Warnings(NewRequest(controller)));
        }

        [Test]
        public void Validate_RejectsOutOfRangeRates_AndSingleSlotSetups()
        {
            var controller = NewController();
            var zero = NewRequest(controller, "F", "B");
            zero.rates["F"] = 0;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(zero));
            zero.rates["F"] = AsyncSyncBuilder.MaxRate + 1;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(zero));

            // Two floats over two channels collapse into one slot: the index would never
            // change and remotes would decode exactly once.
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var oneSlot = NewRequest(controller, "F", "F2");
            oneSlot.floatChannels = 2;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(oneSlot));
            oneSlot.floatChannels = 1;
            Assert.IsNull(AsyncSyncBuilder.Validate(oneSlot));
        }

        // ---- reserved parameters ---------------------------------------------

        [Test]
        public void Validate_RejectsMachineryParametersAsTargets()
        {
            var controller = NewController();
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(controller, "IsLocal", "F")),
                "IsLocal belongs to the machinery");

            controller.AddParameter("Async/Value", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(AsyncSyncBuilder.Validate(NewRequest(controller, "Async/Value", "F")),
                "the request's own namespace is reserved");

            var badChannels = NewRequest(controller, "F", "B");
            badChannels.floatChannels = 0;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(badChannels));
            badChannels.floatChannels = 9;
            Assert.IsNotNull(AsyncSyncBuilder.Validate(badChannels));
        }

        // ---- slots that mix types ---------------------------------------------

        /// <summary>A slot the automatic batching can never produce: it groups by type, so
        /// only a hand-written grid puts a Float, a Bool and an Int in one step. Everything
        /// that numbers channels has to count each type on its own, and these pin that.</summary>
        static AsyncSyncBuilder.Slot MixedSlot(params string[] targets)
        {
            var slot = new AsyncSyncBuilder.Slot();
            slot.targets.AddRange(targets);
            return slot;
        }

        [Test]
        public void ChannelsInSlot_CountsEachTypeSeparately()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "F", "F2", "B", "I");
            var slot = MixedSlot("F", "B", "F2", "I");

            // Reading the slot's size would have said 4 Float channels, and billed for them.
            Assert.AreEqual(2, AsyncSyncCost.ChannelsInSlot(request, slot,
                AnimatorControllerParameterType.Float));
            Assert.AreEqual(1, AsyncSyncCost.ChannelsInSlot(request, slot,
                AnimatorControllerParameterType.Bool));
            Assert.AreEqual(1, AsyncSyncCost.ChannelsInSlot(request, slot,
                AnimatorControllerParameterType.Int));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AddChannelCopies_NumbersEachTypesChannelsFromZero()
        {
            if (!VrcParameterDriver.SdkAvailable)
                Assert.Ignore("Reading the copy entries needs the Parameter Driver behaviour.");

            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "F", "F2", "B", "I");
            var slot = MixedSlot("F", "B", "F2", "I");
            var machine = controller.layers[0].stateMachine;

            var send = VrcParameterDriver.AddTo(machine.AddState("Send", Vector3.zero));
            AsyncSyncApplier.AddChannelCopies(send, request, slot, toChannels: true);
            var entries = VrcParameterDriver.ReadSpec(send).entries;
            Assert.AreEqual(4, entries.Count);
            // B sits second in the slot but is the FIRST Bool, and F2 the second Float.
            CollectionAssert.AreEqual(new[] { "F", "B", "F2", "I" },
                entries.ConvertAll(e => e.source));
            CollectionAssert.AreEqual(
                new[] { "Async/Float", "Async/Bool", "Async/Float2", "Async/Int" },
                entries.ConvertAll(e => e.name));

            // The decoder is the same call with the arrow reversed, so one numbering serves
            // both — a per-type counter that only fixed the send side would desync the pair.
            var recv = VrcParameterDriver.AddTo(machine.AddState("Recv", Vector3.zero));
            AsyncSyncApplier.AddChannelCopies(recv, request, slot, toChannels: false);
            var back = VrcParameterDriver.ReadSpec(recv).entries;
            CollectionAssert.AreEqual(
                new[] { "Async/Float", "Async/Bool", "Async/Float2", "Async/Int" },
                back.ConvertAll(e => e.source));
            CollectionAssert.AreEqual(new[] { "F", "B", "F2", "I" },
                back.ConvertAll(e => e.name));

            Object.DestroyImmediate(controller);
        }

        // ---- slot breaks ------------------------------------------------------

        static AnimatorController FloatController(int count)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            for (int i = 1; i <= count; i++)
                controller.AddParameter("F" + i, AnimatorControllerParameterType.Float);
            return controller;
        }

        [Test]
        public void SlotBreaks_LetABatchedTargetTakeAStepOfItsOwn()
        {
            var controller = FloatController(4);
            var request = NewRequest(controller, "F1", "F2", "F3", "F4");
            request.floatChannels = 2;
            CollectionAssert.AreEqual(new[] { "F1", "F2" },
                AsyncSyncBuilder.BuildSlots(request)[0].targets);

            // F2 declines the batch F1 opened — and opens one F3 may still join, so the
            // author says where the groups begin rather than giving batching up entirely.
            request.slotBreaks.Add("F2");
            var slots = AsyncSyncBuilder.BuildSlots(request);
            Assert.AreEqual(3, slots.Count);
            CollectionAssert.AreEqual(new[] { "F1" }, slots[0].targets);
            CollectionAssert.AreEqual(new[] { "F2", "F3" }, slots[1].targets);
            CollectionAssert.AreEqual(new[] { "F4" }, slots[2].targets);

            // A name that is not multiplexed is ignored, same contract as a stale rate.
            request.slotBreaks.Add("Gone");
            Assert.AreEqual(3, AsyncSyncBuilder.BuildSlots(request).Count);
        }

        [Test]
        public void SlotBreaks_OnEveryTarget_LeaveTheSpareChannelUnbilled()
        {
            var controller = FloatController(4);
            var request = NewRequest(controller, "F1", "F2", "F3", "F4");
            request.floatChannels = 2;
            request.slotBreaks.AddRange(new[] { "F2", "F3", "F4" });

            Assert.AreEqual(4, AsyncSyncBuilder.BuildSlots(request).Count);
            // Nothing batches any more, so the second channel is neither generated nor paid
            // for — splitting costs steps, not synced bits.
            Assert.AreEqual(1, AsyncSyncBuilder.FloatChannelsUsed(request));
            Assert.IsFalse(AsyncSyncBuilder.GeneratedParameters(request)
                .Exists(g => g.name == "Async/Float2"));
        }

        [Test]
        public void SlotBreaks_SurviveTheSavedSetup()
        {
            var controller = FloatController(2);
            var restored = AsyncSyncBuilder.FromConfig(controller,
                new GraphFrameData.AsyncSyncConfig
                {
                    baseName = "Async",
                    targets = new List<string> { "F1", "F2" },
                    slotBreaks = new List<string> { "F2" },
                });
            CollectionAssert.AreEqual(new[] { "F2" }, restored.slotBreaks);

            Object.DestroyImmediate(controller);
        }

        // ---- explicit schedule -----------------------------------------------

        [Test]
        public void ScheduleOverride_IsUsedVerbatim_AndDrivesTheIntervals()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.scheduleOverride.AddRange(new[] { "F", "B", "F", "I" });

            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            // 4 schedule steps (F twice) + idle + 3 recv.
            Assert.AreEqual(8, sm.states.Length);
            Assert.IsNotNull(FindState(sm, "Send F (2)"));

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            Assert.AreEqual(2f * request.stepSeconds, intervals["F"], 0.0001f);
            Assert.AreEqual(4f * request.stepSeconds, intervals["B"], 0.0001f);
        }

        [Test]
        public void ScheduleOverride_RejectsBrokenSchedules()
        {
            var controller = NewController();

            var unknown = NewRequest(controller, "F", "B");
            unknown.scheduleOverride.AddRange(new[] { "F", "Nope" });
            Assert.IsNotNull(AsyncSyncBuilder.Validate(unknown));

            var uncovered = NewRequest(controller, "F", "B", "I");
            uncovered.scheduleOverride.AddRange(new[] { "F", "B" });
            Assert.IsNotNull(AsyncSyncBuilder.Validate(uncovered), "'I' is never visited");

            var adjacent = NewRequest(controller, "F", "B", "I");
            adjacent.scheduleOverride.AddRange(new[] { "F", "F", "B", "I" });
            Assert.IsNotNull(AsyncSyncBuilder.Validate(adjacent));

            var wrap = NewRequest(controller, "F", "B", "I");
            wrap.scheduleOverride.AddRange(new[] { "F", "B", "I", "F" });
            Assert.IsNotNull(AsyncSyncBuilder.Validate(wrap),
                "the last and first step are adjacent too — the cycle wraps");
        }

        [Test]
        public void RefreshIntervals_ReportTheWorstGap_NotTheAverageOne()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            // F sits at steps 0 and 2 of six: 2 steps one way round, 4 the other. Averaging
            // would call that "every 3 steps" and hide the wait that actually happens.
            request.scheduleOverride.AddRange(new[] { "F", "B", "F", "I", "B", "I" });
            Assert.IsNull(AsyncSyncBuilder.Validate(request));

            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            Assert.AreEqual(4f * request.stepSeconds, intervals["F"], 0.0001f);
            Assert.AreEqual(3f * request.stepSeconds, intervals["B"], 0.0001f);
            Assert.AreEqual(4f * request.stepSeconds, intervals["I"], 0.0001f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ScheduleOverride_SurvivesTheSavedSetup()
        {
            var controller = NewController();
            var config = new GraphFrameData.AsyncSyncConfig
            {
                baseName = "Async",
                targets = new List<string> { "F", "B", "I" },
                schedule = new List<string> { "F", "B", "F", "I" },
            };

            var request = AsyncSyncBuilder.FromConfig(controller, config);
            CollectionAssert.AreEqual(config.schedule, request.scheduleOverride);

            // The regression this closes: SyncRequestBuilder rebuilds a layer through
            // FromConfig, and a schedule that did not survive the trip meant adding one sync
            // request silently re-timed a hand-written (or recipe-written) cycle to the rates.
            var slots = AsyncSyncBuilder.BuildSlots(request);
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 2 },
                AsyncSyncBuilder.EffectiveSchedule(request, slots));

            // Saved before the field existed: no schedule, so the pass comes from the rates.
            var legacy = AsyncSyncBuilder.FromConfig(controller, new GraphFrameData.AsyncSyncConfig
            {
                baseName = "Async",
                targets = new List<string> { "F", "B", "I" },
            });
            Assert.AreEqual(0, legacy.scheduleOverride.Count);
            CollectionAssert.AreEqual(
                AsyncSyncBuilder.BuildSchedule(AsyncSyncBuilder.BuildSlots(legacy)),
                AsyncSyncBuilder.EffectiveSchedule(legacy, AsyncSyncBuilder.BuildSlots(legacy)));

            Object.DestroyImmediate(controller);
        }

        // ---- explicit grid ----------------------------------------------------

        static GraphFrameData.AsyncSyncConfig.StepSpec GridStep(params string[] targets)
        {
            var step = new GraphFrameData.AsyncSyncConfig.StepSpec();
            step.targets.AddRange(targets);
            return step;
        }

        static void Sends(AsyncSyncBuilder.Request request, params string[] targets) =>
            request.steps.Add(GridStep(targets));

        /// <summary>A grid is the only way to put two types in one step, and the cost model
        /// has to charge it one channel per type rather than one per target.</summary>
        [Test]
        public void Steps_MixedStep_BillsOneChannelOfEachType()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "F", "F2", "B", "I");
            Sends(request, "F", "B");
            Sends(request, "F2", "I");

            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.AreEqual(1, AsyncSyncBuilder.FloatChannelsUsed(request));
            Assert.AreEqual(1, AsyncSyncBuilder.BoolChannelsUsed(request));
            // 8 index + 8 Float channel + 1 Bool channel + 8 Int channel.
            Assert.AreEqual(25, AsyncSyncBuilder.CompressedBits(request));
            Assert.IsFalse(AsyncSyncBuilder.GeneratedParameters(request)
                .Exists(g => g.name == "Async/Float2"));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Apply_WithSteps_SendsEachStepAndDecodesOneStatePerSet()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "F", "F2", "B", "I");
            request.skipDrivers = false;
            Sends(request, "F", "B");
            Sends(request, "F2", "I");
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            // 2 steps -> 2 send + idle + 2 recv, and one Any-State route per set.
            Assert.AreEqual(5, sm.states.Length);
            Assert.AreEqual(2, sm.anyStateTransitions.Length);

            var driver = FindState(sm, "Send F +1").behaviours[0] as VRCAvatarParameterDriver;
            Assert.IsNotNull(driver);
            // The Bool rides Bool channel 0 even though it sits second in the step.
            Assert.AreEqual("F", driver.parameters[0].source);
            Assert.AreEqual("Async/Float", driver.parameters[0].name);
            Assert.AreEqual("B", driver.parameters[1].source);
            Assert.AreEqual("Async/Bool", driver.parameters[1].name);
            Assert.AreEqual("Async/Index", driver.parameters[2].name);
            Assert.AreEqual(0f, driver.parameters[2].value);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Steps_Validate_RefusesWhatTheDecoderCouldNotRun()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);

            var empty = NewRequest(controller, "F", "B");
            Sends(empty, "F");
            Sends(empty);
            Assert.IsNotNull(AsyncSyncBuilder.Validate(empty), "an empty step carries nothing");

            var overrun = NewRequest(controller, "F", "F2", "B");
            Sends(overrun, "F", "F2");
            Sends(overrun, "B");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(overrun),
                "two Floats in one step need two Float channels");
            overrun.floatChannels = 2;
            Assert.IsNull(AsyncSyncBuilder.Validate(overrun));

            var uncovered = NewRequest(controller, "F", "B", "I");
            Sends(uncovered, "F");
            Sends(uncovered, "B");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(uncovered), "'I' is never sent");

            var repeat = NewRequest(controller, "F", "B", "I");
            Sends(repeat, "F");
            Sends(repeat, "F");
            Sends(repeat, "B");
            Sends(repeat, "I");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(repeat));

            var wrap = NewRequest(controller, "F", "B", "I");
            Sends(wrap, "F");
            Sends(wrap, "B");
            Sends(wrap, "I");
            Sends(wrap, "F");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(wrap),
                "the last and first step are adjacent too — the cycle wraps");

            var single = NewRequest(controller, "F", "B");
            single.floatChannels = 1;
            Sends(single, "F", "B");
            Sends(single, "B", "F");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(single),
                "one set spelled twice is still one index");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Steps_OverlappingSets_GetStateNamesOfTheirOwn()
        {
            var controller = FloatController(3);
            var request = NewRequest(controller, "F1", "F2", "F3");
            request.floatChannels = 2;
            // Both slots lead with F1 and hold two targets, so both want the same name.
            Sends(request, "F1", "F2");
            Sends(request, "F1", "F3");

            var expected = AsyncSyncApplier.ExpectedStateNames(request);
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var built = new List<string>();
            foreach (var child in controller.layers[1].stateMachine.states)
                built.Add(child.state.name);
            CollectionAssert.Contains(built, "Send F1 +1");
            CollectionAssert.Contains(built, "Send F1 +1 #2");
            CollectionAssert.Contains(built, "Recv F1 +1 #2");
            Assert.AreEqual(built.Count, new HashSet<string>(built).Count,
                "two states of one machine must not answer to one name");

            // And the export's recognition rule has to name them the same way, or a grid
            // would come back as raw states with a warning.
            expected.Sort(System.StringComparer.Ordinal);
            built.Sort(System.StringComparer.Ordinal);
            CollectionAssert.AreEqual(built, expected);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Steps_SurviveTheSavedSetup()
        {
            var controller = NewController();
            var saved = new GraphFrameData.AsyncSyncConfig
            {
                baseName = "Async",
                targets = new List<string> { "F", "B", "I" },
            };
            saved.steps.Add(GridStep("F", "B"));
            saved.steps.Add(GridStep("I"));

            // The regression this closes for the older cycle applies here too: a sync request
            // rebuilds the layer through FromConfig, and a grid that did not survive the trip
            // would re-time the layer to the rates behind the user's back.
            var restored = AsyncSyncBuilder.FromConfig(controller, saved);
            Assert.AreEqual(2, restored.steps.Count);
            CollectionAssert.AreEqual(new[] { "F", "B" }, restored.steps[0].targets);
            CollectionAssert.AreEqual(new[] { 0, 1 },
                AsyncSyncBuilder.EffectiveSchedule(restored,
                    AsyncSyncBuilder.BuildSlots(restored)));

            // Editing the restored grid must not reach back into what was saved: the wizard
            // rewrites this list on every click, and the panel it came from is still open.
            restored.steps[0].targets.Clear();
            CollectionAssert.AreEqual(new[] { "F", "B" }, saved.steps[0].targets);

            // Saved before the field existed: no grid, so the slots are batched as before.
            var legacy = AsyncSyncBuilder.FromConfig(controller,
                new GraphFrameData.AsyncSyncConfig
                {
                    baseName = "Async",
                    targets = new List<string> { "F", "B", "I" },
                });
            Assert.AreEqual(0, legacy.steps.Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Steps_LetOneTargetRunTwoStepsInARow()
        {
            var controller = FloatController(3);
            var request = NewRequest(controller, "F1", "F2", "F3");
            request.floatChannels = 2;
            // {F1,F2} then {F1,F3}: the sets differ, so the index changes and the decoder
            // fires — while F1 is refreshed by both steps. The automatic batching cannot
            // express this, and neither could the older step-by-step cycle.
            Sends(request, "F1", "F2");
            Sends(request, "F1", "F3");

            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            var intervals = AsyncSyncBuilder.RefreshIntervals(request);
            Assert.AreEqual(request.stepSeconds, intervals["F1"], 0.0001f);
            Assert.AreEqual(2f * request.stepSeconds, intervals["F2"], 0.0001f);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ExpectedStateNames_MatchWhatApplyReallyBuilds()
        {
            var controller = NewController();
            controller.AddParameter("F2", AnimatorControllerParameterType.Float);
            var request = NewRequest(controller, "F", "F2", "B", "I");
            request.floatChannels = 2;
            request.rates["B"] = 2;

            // The export decides whether a layer may be rewritten as one AsyncSync call by
            // comparing it against these names, so a rule that drifted from the builder would
            // quietly rewrite a layer somebody had edited by hand.
            var expected = AsyncSyncApplier.ExpectedStateNames(request);
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var built = new List<string>();
            foreach (var child in controller.layers[1].stateMachine.states)
                built.Add(child.state.name);

            expected.Sort(System.StringComparer.Ordinal);
            built.Sort(System.StringComparer.Ordinal);
            CollectionAssert.AreEqual(built, expected);

            Object.DestroyImmediate(controller);
        }

        // ---- clock phase ------------------------------------------------------

        /// <summary>The index value the decoder's Any-State route to this state fires on.</summary>
        static void AssertDecodes(AnimatorStateMachine stateMachine, string state, int index)
        {
            var destination = FindState(stateMachine, state);
            foreach (var transition in stateMachine.anyStateTransitions)
                if (transition.destinationState == destination)
                {
                    Assert.IsTrue(
                        HasCondition(transition, "Async/Index", AnimatorConditionMode.Equals, index),
                        "'" + state + "' should decode index " + index);
                    return;
                }
            Assert.Fail("No Any-State route to '" + state + "'.");
        }

        [Test]
        public void Clock_LetsOneSlotHoldTwoStepsInARow()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.allowRepeatSteps = true;
            Sends(request, "F");
            Sends(request, "F");
            Sends(request, "B");
            Sends(request, "I");

            Assert.IsNull(AsyncSyncBuilder.Validate(request),
                "the clock is exactly what pays for the repeated step");
            var expected = AsyncSyncApplier.ExpectedStateNames(request);
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            // 4 send + idle + 3 recv, and a second Recv for the slot that repeats.
            Assert.AreEqual(9, sm.states.Length);
            Assert.AreEqual(4, sm.anyStateTransitions.Length);

            // The two steps of F land on two states, on two index values — which is the whole
            // mechanism: the route is refused when it would re-enter the state already active.
            AssertDecodes(sm, "Recv F", 0);
            AssertDecodes(sm, "Recv F (2)", 1);
            AssertDecodes(sm, "Recv B", 2);
            AssertDecodes(sm, "Recv I", 3);

            // The exporter decides whether a layer may be rewritten as one AsyncSync call by
            // comparing it against these names, and the clock adds states it has to know about.
            var built = new List<string>();
            foreach (var child in sm.states) built.Add(child.state.name);
            expected.Sort(System.StringComparer.Ordinal);
            built.Sort(System.StringComparer.Ordinal);
            CollectionAssert.AreEqual(built, expected);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Clock_SendsTheTwoStepsOfOneSlotOnDifferentIndices()
        {
            if (!VrcParameterDriver.SdkAvailable)
                Assert.Ignore("Reading the index the send driver writes needs the Parameter Driver behaviour.");

            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.allowRepeatSteps = true;
            request.skipDrivers = false;
            Sends(request, "F");
            Sends(request, "F");
            Sends(request, "B");
            Sends(request, "I");
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            var first = FindState(sm, "Send F").behaviours[0] as VRCAvatarParameterDriver;
            var second = FindState(sm, "Send F (2)").behaviours[0] as VRCAvatarParameterDriver;
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            // Same copy, different index: the payload repeats and the index does not.
            Assert.AreEqual("F", first.parameters[0].source);
            Assert.AreEqual("F", second.parameters[0].source);
            Assert.AreEqual("Async/Index", first.parameters[1].name);
            Assert.AreEqual(0f, first.parameters[1].value);
            Assert.AreEqual("Async/Index", second.parameters[1].name);
            Assert.AreEqual(1f, second.parameters[1].value);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Clock_CostsNothingUntilThePassActuallyRepeats()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.allowRepeatSteps = true;

            // Nothing repeats, so no slot needs a second phase: the index still runs 0,1,2 and
            // the layer is the one the same setup builds with the clock off.
            Assert.AreEqual(3, AsyncSyncBuilder.IndexValues(request));
            Assert.AreEqual(25, AsyncSyncBuilder.CompressedBits(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            Assert.AreEqual(7, sm.states.Length);
            Assert.AreEqual(3, sm.anyStateTransitions.Length);
            AssertDecodes(sm, "Recv B", 1);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Clock_IsFreeUnderTheIntIndex_AndWidensTheBoolOneOnlyOnOverflow()
        {
            var controller = FloatController(4);
            var request = NewRequest(controller, "F1", "F2", "F3", "F4");
            request.encoding = AsyncSyncBuilder.IndexEncoding.Bool;
            request.allowRepeatSteps = true;
            Sends(request, "F1");
            Sends(request, "F2");
            Sends(request, "F3");
            Sends(request, "F4");
            // 4 slots, 4 index values: two bits, and one Float channel.
            Assert.AreEqual(4, AsyncSyncBuilder.IndexValues(request));
            Assert.AreEqual(2 + 8, AsyncSyncBuilder.CompressedBits(request));

            // A fifth step repeating F4 gives that slot a second value, and five values need
            // a third bit — the tail of the range being free is what makes this the exception.
            Sends(request, "F4");
            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.AreEqual(5, AsyncSyncBuilder.IndexValues(request));
            Assert.AreEqual(3 + 8, AsyncSyncBuilder.CompressedBits(request));

            // The Int index holds 255 values however they are shared out, so it pays nothing.
            request.encoding = AsyncSyncBuilder.IndexEncoding.Int;
            Assert.AreEqual(8 + 8, AsyncSyncBuilder.CompressedBits(request));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Clock_RefusesAPassOfOneSlotWithAnOddNumberOfSteps()
        {
            var controller = NewController();
            var odd = NewRequest(controller, "F", "B");
            odd.allowRepeatSteps = true;
            Sends(odd, "F", "B");
            Sends(odd, "F", "B");
            Sends(odd, "F", "B");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(odd),
                "three steps of one slot close the alternation into a ring that can't alternate");

            var even = NewRequest(controller, "F", "B");
            even.allowRepeatSteps = true;
            Sends(even, "F", "B");
            Sends(even, "F", "B");
            Assert.IsNull(AsyncSyncBuilder.Validate(even));
            Assert.IsTrue(AsyncSyncBuilder.Apply(even));

            var sm = controller.layers[1].stateMachine;
            // One slot, two phases: 2 send + idle + 2 recv.
            Assert.AreEqual(5, sm.states.Length);
            Assert.AreEqual(2, sm.anyStateTransitions.Length);
            AssertDecodes(sm, "Recv F +1", 0);
            AssertDecodes(sm, "Recv F +1 (2)", 1);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Clock_MakesASingleSlotSetupBuildable_AndSaysWhatItCosts()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            for (int i = 1; i <= 4; i++)
                controller.AddParameter("B" + i, AnimatorControllerParameterType.Bool);

            var request = NewRequest(controller, "B1", "B2", "B3", "B4");
            request.boolChannels = 4;
            Assert.AreEqual(1, AsyncSyncBuilder.BuildSlots(request).Count);
            Assert.IsNotNull(AsyncSyncBuilder.Validate(request),
                "without a clock the index would never change");

            request.allowRepeatSteps = true;
            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));
            Assert.AreEqual(5, controller.layers[1].stateMachine.states.Length,
                "2 send + idle + 2 recv: the one slot alternates against itself");

            Assert.IsTrue(AsyncSyncBuilder.Warnings(request)
                    .Exists(w => w.Contains("every step sends every target")),
                "one slot is direct sync wearing a cycle, and that has to be said");

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Clock_SurvivesTheSavedSetup()
        {
            // Regenerating outside the wizard — a state's sync request, the layer panel —
            // starts from the saved setup, and one that lost the clock would rebuild a layer
            // whose own grid it then refuses. The setup only persists on a controller that has
            // an asset to hold it, hence the file.
            const string path = "Assets/DaerDAsyncSyncClockTest.controller";
            AssetDatabase.CreateAsset(new AnimatorController(), path);
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                controller.AddLayer("Base");
                controller.AddParameter("F", AnimatorControllerParameterType.Float);
                controller.AddParameter("B", AnimatorControllerParameterType.Bool);

                var request = NewRequest(controller, "F", "B");
                request.allowRepeatSteps = true;
                Sends(request, "F");
                Sends(request, "F");
                Sends(request, "B");
                Assert.IsTrue(AsyncSyncBuilder.Apply(request));

                var configs = GraphFrameData.GetAsyncSyncs(controller);
                Assert.AreEqual(1, configs.Count);
                Assert.IsTrue(configs[0].allowRepeatSteps);

                var restored = AsyncSyncBuilder.FromConfig(controller, configs[0]);
                Assert.IsTrue(restored.allowRepeatSteps);
                restored.skipDrivers = true;
                Assert.IsNull(AsyncSyncBuilder.Validate(restored),
                    "the restored setup still describes the layer that was built");

                // Data saved before the field existed deserializes to false, which is the pass
                // those setups already had.
                Assert.IsFalse(AsyncSyncBuilder.FromConfig(controller,
                    new GraphFrameData.AsyncSyncConfig { baseName = "Async" }).allowRepeatSteps);
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void Clock_LetsAnExplicitScheduleRepeatASlot()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.scheduleOverride.AddRange(new[] { "F", "F", "B", "I" });
            Assert.IsNotNull(AsyncSyncBuilder.Validate(request), "unclocked, this is refused");

            request.allowRepeatSteps = true;
            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            Assert.AreEqual(9, sm.states.Length);
            AssertDecodes(sm, "Recv F", 0);
            AssertDecodes(sm, "Recv F (2)", 1);

            Object.DestroyImmediate(controller);
        }

        // ---- sync requests ---------------------------------------------------

        [Test]
        public void RequestableTargets_FollowCycleOrder_AndIgnoreStaleEntries()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.requestTargets.AddRange(new[] { "I", "B", "B", "Gone" });

            CollectionAssert.AreEqual(new[] { "B", "I" },
                AsyncSyncBuilder.RequestableTargets(request));
            Assert.IsNull(AsyncSyncBuilder.Validate(request),
                "a stale saved entry must not block regeneration");
            Assert.IsTrue(AsyncSyncBuilder.Warnings(request)
                .Exists(w => w.Contains("not multiplexed")), "but it is called out");

            // An existing parameter of another type under the flag's name blocks.
            controller.AddParameter("Async/Req/B", AnimatorControllerParameterType.Float);
            var collision = NewRequest(controller, "F", "B");
            collision.requestTargets.Add("B");
            Assert.IsNotNull(AsyncSyncBuilder.Validate(collision));
        }

        [Test]
        public void Apply_WithRequests_AddsRedirectRoutesAheadOfTheRing()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.requestTargets.Add("B");
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Async/Req/B").type);

            var sm = controller.layers[1].stateMachine;
            var sendF = FindState(sm, "Send F");
            var sendB = FindState(sm, "Send B");
            var sendI = FindState(sm, "Send I");

            // Every OTHER step gets a redirect to Send B ahead of its ring transition —
            // same step timing, gated on the flag; the ring stays the unconditional fallback.
            Assert.AreEqual(2, sendF.transitions.Length);
            var redirect = sendF.transitions[0];
            Assert.AreEqual(sendB, redirect.destinationState);
            Assert.IsTrue(redirect.hasExitTime);
            Assert.AreEqual(0.3f, redirect.exitTime);
            Assert.AreEqual(0f, redirect.duration);
            Assert.IsTrue(HasCondition(redirect, "Async/Req/B", AnimatorConditionMode.If, 0f));
            Assert.AreEqual(0, sendF.transitions[1].conditions.Length);

            Assert.AreEqual(2, sendI.transitions.Length);
            Assert.AreEqual(sendB, sendI.transitions[0].destinationState);

            // No self-redirect: back-to-back sends of one slot are invisible to the decoder
            // (canTransitionToSelf is off there); the next step picks the flag up instead.
            Assert.AreEqual(1, sendB.transitions.Length);
            Assert.AreEqual(sendI, sendB.transitions[0].destinationState);
        }

        /// <summary>
        /// Requests queue instead of interrupting. Every redirect carries the ring's exit time,
        /// so the running step always spends its full dwell and the jump happens at the step
        /// boundary; at that boundary the routes are tried in cycle order, so exactly one
        /// pending request is served and the others keep their flag raised for the next one.
        /// </summary>
        [Test]
        public void Apply_WithMultipleRequests_QueuesOnePerStepBoundary()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.requestTargets.AddRange(new[] { "B", "I" });
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sm = controller.layers[1].stateMachine;
            var sendF = FindState(sm, "Send F");
            var sendB = FindState(sm, "Send B");
            var sendI = FindState(sm, "Send I");

            // With both flags up, the transition order decides — and it is the cycle order.
            Assert.AreEqual(3, sendF.transitions.Length);
            Assert.AreEqual(sendB, sendF.transitions[0].destinationState);
            Assert.IsTrue(HasCondition(sendF.transitions[0], "Async/Req/B",
                AnimatorConditionMode.If, 0f));
            Assert.AreEqual(sendI, sendF.transitions[1].destinationState);
            Assert.IsTrue(HasCondition(sendF.transitions[1], "Async/Req/I",
                AnimatorConditionMode.If, 0f));
            Assert.AreEqual(sendB, sendF.transitions[2].destinationState);
            Assert.AreEqual(0, sendF.transitions[2].conditions.Length, "the ring is the fallback");

            // No redirect shortens a step: the values just sent still need their sync window,
            // so a request raised mid-step waits out the dwell like the ring does.
            foreach (var state in new[] { sendF, sendB, sendI })
                foreach (var transition in state.transitions)
                {
                    Assert.IsTrue(transition.hasExitTime, state.name + " leaves before its dwell");
                    Assert.AreEqual(0.3f, transition.exitTime, 0.0001f);
                    Assert.AreEqual(0f, transition.duration);
                }

            // The step that just served B routes only to the other request; its own flag is
            // already down, and a repeat of the same index would be invisible to the decoder.
            Assert.AreEqual(2, sendB.transitions.Length);
            Assert.AreEqual(sendI, sendB.transitions[0].destinationState);
            Assert.IsTrue(HasCondition(sendB.transitions[0], "Async/Req/I",
                AnimatorConditionMode.If, 0f));
            Assert.AreEqual(0, sendB.transitions[1].conditions.Length);
        }

        [Test]
        public void Apply_RequestFlags_AreCreatedButNeverSynced()
        {
            var controller = NewController();
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            var request = NewRequest(controller, "F", "B");
            request.requestTargets.Add("F");
            request.store = ParameterStore.TryWrap(asset);
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            Assert.IsNotNull(DbtBuilder.FindParameter(controller, "Async/Req/F"));
            Assert.IsNotNull(VrcExpressionParameters.Find(asset, "Async/Index"));
            Assert.IsNull(VrcExpressionParameters.Find(asset, "Async/Req/F"),
                "request flags are local machinery — they must not cost synced bits");
        }

        /// <summary>Driver contents via the test stub: the serving state clears its own flag
        /// after copying the value and setting the index.</summary>
        [Test]
        public void Apply_WithDrivers_ServingAStepClearsItsRequestFlag()
        {
            var controller = NewController();
            var request = NewRequest(controller, "F", "B", "I");
            request.skipDrivers = false;
            request.requestTargets.Add("B");
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));

            var sendB = FindState(controller.layers[1].stateMachine, "Send B");
            var driver = sendB.behaviours[0] as VRCAvatarParameterDriver;
            Assert.IsNotNull(driver);
            Assert.IsTrue(driver.localOnly);

            Assert.AreEqual(3, driver.parameters.Count);
            Assert.AreEqual(3, driver.parameters[0].type);   // Copy: B -> channel
            Assert.AreEqual("B", driver.parameters[0].source);
            Assert.AreEqual("Async/Index", driver.parameters[1].name);
            Assert.AreEqual(1f, driver.parameters[1].value);
            Assert.AreEqual(0, driver.parameters[2].type);   // Set: flag down
            Assert.AreEqual("Async/Req/B", driver.parameters[2].name);
            Assert.AreEqual(0f, driver.parameters[2].value);

            // States that don't serve the slot don't touch the flag.
            var sendF = FindState(controller.layers[1].stateMachine, "Send F");
            var sendFDriver = sendF.behaviours[0] as VRCAvatarParameterDriver;
            Assert.IsFalse(sendFDriver.parameters.Exists(p => p.name == "Async/Req/B"));
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

        [Test]
        public void Apply_WithNoDesignatedClip_CreatesTheEmptyClipInsideTheController()
        {
            const string path = "Assets/DaerDAsyncSyncEmptyClipTest.controller";
            AssetDatabase.CreateAsset(new AnimatorController(), path);
            try
            {
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                controller.AddLayer("Base");
                controller.AddParameter("F", AnimatorControllerParameterType.Float);
                controller.AddParameter("B", AnimatorControllerParameterType.Bool);
                controller.AddParameter("I", AnimatorControllerParameterType.Int);

                var request = NewRequest(controller, "F", "B", "I");
                request.stepSeconds = 0.3f;
                Assert.IsTrue(AsyncSyncBuilder.Apply(request));

                var created = GraphFrameData.GetEmptyClip(controller);
                Assert.IsNotNull(created, "nothing was designated, so applying should create the clip");
                Assert.Greater(created.length, 0f, "exit times are normalized to the motion");
                Assert.AreEqual(AssetDatabase.GetAssetPath(controller), AssetDatabase.GetAssetPath(created),
                    "the clip lives inside the .controller");

                var sm = controller.layers[controller.layers.Length - 1].stateMachine;
                foreach (var child in sm.states)
                    Assert.AreSame(created, child.state.motion,
                        "state '" + child.state.name + "' has no motion");
                // The generated clip is 1 s long, so the normalized dwell reads as the step itself.
                Assert.AreEqual(0.3f / 1f, FindState(sm, "Send F").transitions[0].exitTime, 0.0001f);

                // Regenerating reuses the designated clip instead of stacking one clip per run.
                var again = NewRequest(controller, "F", "B", "I");
                again.layerIndex = controller.layers.Length - 1;
                Assert.IsTrue(AsyncSyncBuilder.Apply(again));
                Assert.AreSame(created, GraphFrameData.GetEmptyClip(controller));
                int clips = 0;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (asset is AnimationClip) clips++;
                Assert.AreEqual(1, clips, "the second run reused the clip");
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }
    }
}
