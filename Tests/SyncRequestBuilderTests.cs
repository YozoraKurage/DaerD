using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The per-state Sync Request component: its managed Parameter Driver (through the
    /// <see cref="VRCAvatarParameterDriver"/> stub), the sync layer regeneration it triggers,
    /// and the GraphFrameData records behind the inspector UI.
    /// </summary>
    public class SyncRequestBuilderTests
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

        /// <summary>Builds a sync layer and hands back its config the way GraphFrameData
        /// would for an on-disk controller (in-memory controllers don't persist it).</summary>
        static GraphFrameData.AsyncSyncConfig BuildSetup(AnimatorController controller)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
                encoding = AsyncSyncBuilder.IndexEncoding.Int,
                skipDrivers = true,
            };
            request.targets.AddRange(new[] { "F", "B", "I" });
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));
            return new GraphFrameData.AsyncSyncConfig
            {
                layer = controller.layers[1].stateMachine,
                baseName = "Async",
                encoding = (int)AsyncSyncBuilder.IndexEncoding.Int,
                stepSeconds = 0.3f,
                floatChannels = 1,
                targets = new List<string> { "F", "B", "I" },
            };
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
        public void Validate_RejectsForeignTargets_AndEmptyPicks()
        {
            var controller = NewController();
            var config = BuildSetup(controller);
            var state = controller.layers[0].stateMachine.AddState("S");

            Assert.IsNotNull(SyncRequestBuilder.Validate(controller, config, state,
                new List<string>()));
            Assert.IsNotNull(SyncRequestBuilder.Validate(controller, config, state,
                new List<string> { "Nope" }));
            Assert.IsNotNull(SyncRequestBuilder.Validate(controller, null, state,
                new List<string> { "F" }));
            Assert.IsNull(SyncRequestBuilder.Validate(controller, config, state,
                new List<string> { "F" }));
        }

        [Test]
        public void Apply_CreatesTheDriver_AndTeachesTheSetupToListen()
        {
            var controller = NewController();
            var config = BuildSetup(controller);
            var state = controller.layers[0].stateMachine.AddState("Dress");

            Assert.IsTrue(SyncRequestBuilder.Apply(controller, config, state,
                new List<string> { "B" }));

            // The driver on the state: DaerD-managed name, localOnly, one Set per flag.
            var behaviour = SyncRequestBuilder.FindDriver(state, "Async");
            Assert.IsNotNull(behaviour);
            Assert.AreEqual("Sync Request (Async)", behaviour.name);
            // Read as data, not cast: with the real SDK installed it is the SDK's driver
            // class on the state, since the builder finds the class by name.
            var driver = VrcParameterDriver.ReadSpec(behaviour);
            Assert.IsTrue(driver.localOnly);
            Assert.AreEqual(1, driver.entries.Count);
            Assert.AreEqual("Async/Req/B", driver.entries[0].name);
            Assert.AreEqual(0, driver.entries[0].kind);
            Assert.AreEqual(1f, driver.entries[0].value);

            // "B" was not requestable yet, so the sync layer was regenerated to listen:
            // flag parameter plus redirect routes ahead of the ring.
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Async/Req/B").type);
            var sendF = FindState(controller.layers[1].stateMachine, "Send F");
            Assert.AreEqual(2, sendF.transitions.Length);
            Assert.AreEqual("Send B", sendF.transitions[0].destinationState.name);
        }

        [Test]
        public void Apply_Rewrites_InsteadOfStacking_AndKeepsCycleOrder()
        {
            var controller = NewController();
            var config = BuildSetup(controller);
            var state = controller.layers[0].stateMachine.AddState("Dress");

            Assert.IsTrue(SyncRequestBuilder.Apply(controller, config, state,
                new List<string> { "B" }));
            // What a persisted config would now hold (in-memory controllers don't keep it).
            config.requests = new List<string> { "B" };
            Assert.IsTrue(SyncRequestBuilder.Apply(controller, config, state,
                new List<string> { "I", "F" }));

            int drivers = 0;
            foreach (var behaviour in state.behaviours)
                if (behaviour != null && behaviour.name == "Sync Request (Async)")
                    drivers++;
            Assert.AreEqual(1, drivers, "editing rewrites the managed driver, never stacks");

            var driver = VrcParameterDriver.ReadSpec(SyncRequestBuilder.FindDriver(state, "Async"));
            Assert.AreEqual(2, driver.entries.Count);
            // Stored and driven in the setup's cycle order, not tick order.
            Assert.AreEqual("Async/Req/F", driver.entries[0].name);
            Assert.AreEqual("Async/Req/I", driver.entries[1].name);
        }

        [Test]
        public void Remove_DropsTheManagedDriver()
        {
            var controller = NewController();
            var config = BuildSetup(controller);
            var state = controller.layers[0].stateMachine.AddState("Dress");
            Assert.IsTrue(SyncRequestBuilder.Apply(controller, config, state,
                new List<string> { "B" }));

            SyncRequestBuilder.Remove(controller, state, "Async");
            Assert.IsNull(SyncRequestBuilder.FindDriver(state, "Async"));
            Assert.AreEqual(0, state.behaviours.Length);
        }

        [Test]
        public void SyncRequestRecords_ReplacePerStateAndSetup_AndPruneDeadStates()
        {
            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var stateA = new AnimatorState { name = "A" };
            var stateB = new AnimatorState { name = "B" };

            data.SaveSyncRequest(new GraphFrameData.SyncRequest
            { state = stateA, baseName = "Async", targets = new List<string> { "F" } });
            data.SaveSyncRequest(new GraphFrameData.SyncRequest
            { state = stateB, baseName = "Async", targets = new List<string> { "B" } });
            // Same (state, setup) pair again → replaced, not stacked.
            data.SaveSyncRequest(new GraphFrameData.SyncRequest
            { state = stateA, baseName = "Async", targets = new List<string> { "F", "I" } });
            // Same state against another setup → its own record.
            data.SaveSyncRequest(new GraphFrameData.SyncRequest
            { state = stateA, baseName = "Other", targets = new List<string> { "F" } });

            Assert.AreEqual(3, data.SyncRequests().Count);
            foreach (var entry in data.SyncRequests())
                if (entry.state == stateA && entry.baseName == "Async")
                    CollectionAssert.AreEqual(new[] { "F", "I" }, entry.targets);

            data.RemoveSyncRequest(stateA, "Other");
            Assert.AreEqual(2, data.SyncRequests().Count);

            // A deleted state prunes its record.
            Object.DestroyImmediate(stateB);
            Assert.AreEqual(1, data.SyncRequests().Count);

            Object.DestroyImmediate(stateA);
            Object.DestroyImmediate(data);
        }
    }
}
