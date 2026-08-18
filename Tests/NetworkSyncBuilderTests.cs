using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Tests
{
    public class NetworkSyncBuilderTests
    {
        /// <summary>Base layer plus a target layer with <paramref name="stateCount"/> states
        /// chained A→B→C… so the IsLocal fencing has transitions to act on.</summary>
        static AnimatorController NewController(int stateCount)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Target");
            var stateMachine = controller.layers[1].stateMachine;
            AnimatorState previous = null;
            for (int i = 0; i < stateCount; i++)
            {
                var state = stateMachine.AddState("S" + i, new Vector3(0f, i * 100f, 0f));
                if (previous != null)
                {
                    var transition = previous.AddTransition(state);
                    transition.AddCondition(AnimatorConditionMode.If, 0f, EnsureBool(controller, "Go" + i));
                }
                previous = state;
            }
            return controller;
        }

        static string EnsureBool(AnimatorController controller, string name)
        {
            if (DbtBuilder.FindParameter(controller, name) == null)
                controller.AddParameter(name, AnimatorControllerParameterType.Bool);
            return name;
        }

        static NetworkSyncBuilder.Request NewRequest(AnimatorController controller) =>
            new NetworkSyncBuilder.Request
            {
                controller = controller,
                layerIndex = 1,
                syncParameter = "Target/Sync",
                packIntoSubMachine = false,
                skipDrivers = true,
            };

        static List<AnimatorState> Mirrors(AnimatorController controller, string prefix = "[Net] ")
        {
            var mirrors = new List<AnimatorState>();
            foreach (var child in controller.layers[1].stateMachine.states)
                if (child.state != null && child.state.name.StartsWith(prefix))
                    mirrors.Add(child.state);
            return mirrors;
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
            Assert.IsNotNull(NetworkSyncBuilder.Validate(NewRequest(null)));

            var single = new AnimatorController();
            single.AddLayer("Base");
            single.AddLayer("Target");
            single.layers[1].stateMachine.AddState("Only");
            Assert.IsNotNull(NetworkSyncBuilder.Validate(NewRequest(single)));

            var controller = NewController(3);
            var noName = NewRequest(controller);
            noName.syncParameter = string.Empty;
            Assert.IsNotNull(NetworkSyncBuilder.Validate(noName));

            var noPrefix = NewRequest(controller);
            noPrefix.remotePrefix = string.Empty;
            Assert.IsNotNull(NetworkSyncBuilder.Validate(noPrefix));

            var clash = NewController(3);
            clash.AddParameter("Target/Sync", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(NetworkSyncBuilder.Validate(NewRequest(clash)));

            var isLocalClash = NewController(3);
            isLocalClash.AddParameter("IsLocal", AnimatorControllerParameterType.Float);
            Assert.IsNotNull(NetworkSyncBuilder.Validate(NewRequest(isLocalClash)));
        }

        [Test]
        public void Validate_AcceptsMatchingExistingIntParameter()
        {
            var controller = NewController(3);
            controller.AddParameter("Target/Sync", AnimatorControllerParameterType.Int);
            Assert.IsNull(NetworkSyncBuilder.Validate(NewRequest(controller)));
        }

        // ---- int encoding, AnyState wiring -----------------------------------

        [Test]
        public void Apply_Int_AnyState_BuildsMirrorsAndRouting()
        {
            var controller = NewController(3);
            Assert.IsTrue(NetworkSyncBuilder.Apply(NewRequest(controller)));

            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "IsLocal").type);
            Assert.AreEqual(AnimatorControllerParameterType.Int,
                DbtBuilder.FindParameter(controller, "Target/Sync").type);

            var mirrors = Mirrors(controller);
            Assert.AreEqual(3, mirrors.Count);
            Assert.AreEqual("[Net] S0", mirrors[0].name);

            var stateMachine = controller.layers[1].stateMachine;
            int routed = 0;
            foreach (var transition in stateMachine.anyStateTransitions)
            {
                Assert.IsFalse(transition.canTransitionToSelf);
                Assert.IsFalse(transition.hasExitTime);
                Assert.AreEqual(0f, transition.duration);
                Assert.IsTrue(HasCondition(transition, "IsLocal", AnimatorConditionMode.IfNot, 0f));
                int index = mirrors.IndexOf(transition.destinationState);
                Assert.GreaterOrEqual(index, 0);
                Assert.IsTrue(HasCondition(transition, "Target/Sync", AnimatorConditionMode.Equals, index));
                routed++;
            }
            Assert.AreEqual(3, routed);
        }

        [Test]
        public void Apply_FencesExistingLocalTransitionsWithIsLocal()
        {
            var controller = NewController(3);
            Assert.IsTrue(NetworkSyncBuilder.Apply(NewRequest(controller)));

            foreach (var child in controller.layers[1].stateMachine.states)
            {
                if (child.state == null || child.state.name.StartsWith("[Net] ")) continue;
                foreach (var transition in child.state.transitions)
                {
                    int isLocalConditions = 0;
                    foreach (var condition in transition.conditions)
                        if (condition.parameter == "IsLocal") isLocalConditions++;
                    Assert.AreEqual(1, isLocalConditions);
                    Assert.IsTrue(HasCondition(transition, "IsLocal", AnimatorConditionMode.If, 0f));
                }
            }
        }

        [Test]
        public void Apply_EntryBranchesToRemoteDefault()
        {
            var controller = NewController(3);
            Assert.IsTrue(NetworkSyncBuilder.Apply(NewRequest(controller)));

            var stateMachine = controller.layers[1].stateMachine;
            Assert.AreEqual("S0", stateMachine.defaultState.name);   // local fallback untouched
            Assert.AreEqual(1, stateMachine.entryTransitions.Length);
            var entry = stateMachine.entryTransitions[0];
            Assert.AreEqual("[Net] S0", entry.destinationState.name);
            Assert.IsTrue(HasCondition(entry, "IsLocal", AnimatorConditionMode.IfNot, 0f));
        }

        [Test]
        public void Apply_MirrorsCopyStateFields()
        {
            var controller = NewController(2);
            var original = controller.layers[1].stateMachine.states[0].state;
            var clip = new AnimationClip { name = "M" };
            original.motion = clip;
            original.speed = 2.5f;
            original.writeDefaultValues = false;
            Assert.IsTrue(NetworkSyncBuilder.Apply(NewRequest(controller)));

            var mirror = Mirrors(controller)[0];
            Assert.AreEqual(clip, mirror.motion);
            Assert.AreEqual(2.5f, mirror.speed);
            Assert.IsFalse(mirror.writeDefaultValues);
        }

        // ---- bool encoding ----------------------------------------------------

        [Test]
        public void Apply_Bool_EncodesBitsLsbFirst()
        {
            var controller = NewController(3);   // 3 states -> 2 bits
            var request = NewRequest(controller);
            request.encoding = NetworkSyncBuilder.Encoding.Bool;
            Assert.IsTrue(NetworkSyncBuilder.Apply(request));

            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Target/Sync/b0").type);
            Assert.AreEqual(AnimatorControllerParameterType.Bool,
                DbtBuilder.FindParameter(controller, "Target/Sync/b1").type);
            Assert.IsNull(DbtBuilder.FindParameter(controller, "Target/Sync/b2"));

            var mirrors = Mirrors(controller);
            foreach (var transition in controller.layers[1].stateMachine.anyStateTransitions)
            {
                int index = mirrors.IndexOf(transition.destinationState);
                Assert.IsTrue(HasCondition(transition, "Target/Sync/b0",
                    (index & 1) == 1 ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f));
                Assert.IsTrue(HasCondition(transition, "Target/Sync/b1",
                    (index & 2) == 2 ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0f));
            }
        }

        [Test]
        public void BitsRequired_CoversEdgeCases()
        {
            Assert.AreEqual(1, NetworkSyncBuilder.BitsRequired(2));
            Assert.AreEqual(2, NetworkSyncBuilder.BitsRequired(3));
            Assert.AreEqual(2, NetworkSyncBuilder.BitsRequired(4));
            Assert.AreEqual(3, NetworkSyncBuilder.BitsRequired(5));
            Assert.AreEqual(8, NetworkSyncBuilder.BitsRequired(256));
        }

        // ---- all-to-all wiring -------------------------------------------------

        [Test]
        public void Apply_AllToAll_WiresEveryMirrorPair()
        {
            var controller = NewController(3);
            var request = NewRequest(controller);
            request.wiring = NetworkSyncBuilder.RemoteWiring.AllToAll;
            Assert.IsTrue(NetworkSyncBuilder.Apply(request));

            var stateMachine = controller.layers[1].stateMachine;
            Assert.AreEqual(0, stateMachine.anyStateTransitions.Length);

            var mirrors = Mirrors(controller);
            int total = 0;
            foreach (var mirror in mirrors)
            {
                foreach (var transition in mirror.transitions)
                {
                    int index = mirrors.IndexOf(transition.destinationState);
                    Assert.GreaterOrEqual(index, 0);
                    Assert.IsTrue(HasCondition(transition, "Target/Sync", AnimatorConditionMode.Equals, index));
                    Assert.IsTrue(HasCondition(transition, "IsLocal", AnimatorConditionMode.IfNot, 0f));
                    total++;
                }
            }
            Assert.AreEqual(3 * 2, total);
        }

        // ---- packing -------------------------------------------------------------

        [Test]
        public void Apply_PackMovesMirrorsIntoNetworkSubMachine()
        {
            var controller = NewController(3);
            var request = NewRequest(controller);
            request.packIntoSubMachine = true;
            Assert.IsTrue(NetworkSyncBuilder.Apply(request));

            var stateMachine = controller.layers[1].stateMachine;
            Assert.AreEqual(0, Mirrors(controller).Count);   // no longer at root level
            Assert.AreEqual(1, stateMachine.stateMachines.Length);
            var packed = stateMachine.stateMachines[0].stateMachine;
            Assert.AreEqual("Network", packed.name);
            Assert.AreEqual(3, packed.states.Length);
        }

        // ---- options ---------------------------------------------------------------

        [Test]
        public void Apply_PreserveTimingCopiesDurationButNotExitTime()
        {
            var controller = NewController(2);
            var original = controller.layers[1].stateMachine.states[0].state;
            var source = original.transitions[0];
            source.hasExitTime = true;
            source.duration = 0.5f;
            var request = NewRequest(controller);
            request.preserveTransitionProperties = true;
            Assert.IsTrue(NetworkSyncBuilder.Apply(request));

            foreach (var transition in controller.layers[1].stateMachine.anyStateTransitions)
            {
                if (transition.destinationState != Mirrors(controller)[0]) continue;
                Assert.AreEqual(0.5f, transition.duration);
                Assert.IsFalse(transition.hasExitTime);
            }
        }
    }
}
