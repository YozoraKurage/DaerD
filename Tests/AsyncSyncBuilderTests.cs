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

        // ---- float channels ---------------------------------------------------

        [Test]
        public void RequestDefaults_AutoEncoding_OneFloatChannel()
        {
            var request = new AsyncSyncBuilder.Request();
            Assert.AreEqual(AsyncSyncBuilder.IndexEncoding.Auto, request.encoding);
            Assert.AreEqual(1, request.floatChannels);
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
