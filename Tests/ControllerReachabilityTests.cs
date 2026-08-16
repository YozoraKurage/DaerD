using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The forward walk from a layer's Entry. Every case here is one where "does something
    /// point at this state?" gives the wrong answer — an island whose members all have
    /// incoming transitions, a sub-state machine nobody enters, an Any State that belongs to
    /// one.
    /// </summary>
    public class ControllerReachabilityTests
    {
        static AnimatorController NewController(out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        static List<string> Names(HashSet<AnimatorState> states)
        {
            var names = new List<string>();
            foreach (var state in states) names.Add(state.name);
            names.Sort(System.StringComparer.Ordinal);
            return names;
        }

        [Test]
        public void AnIslandOfWiredStates_IsStillUnreachable()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var b = sm.AddState("B");
            var c = sm.AddState("C");
            var d = sm.AddState("D");
            a.AddTransition(b);
            c.AddTransition(d);         // C and D point at each other and nothing else...
            d.AddTransition(c);         // ...so both have an incoming transition, and neither runs

            CollectionAssert.AreEqual(new[] { "A", "B" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void EntryTransitions_AreWaysIn_BesideTheDefaultState()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");           // default
            var b = sm.AddState("B");
            sm.AddEntryTransition(b);

            CollectionAssert.AreEqual(new[] { "A", "B" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ASubStateMachineNothingTransitionsInto_IsUnreachable()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.defaultState = p;
            p.AddTransition(a);         // P leads out, but there is no way in

            CollectionAssert.AreEqual(new[] { "A" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ATransitionIntoASubStateMachine_EntersThroughItsDefaultState()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            var q = child.AddState("Q");
            child.defaultState = p;
            a.AddTransition(child);
            p.AddTransition(q);

            CollectionAssert.AreEqual(new[] { "A", "P", "Q" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ExitFromASubStateMachine_FollowsTheTransitionTheParentDrewOutOfIt()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var b = sm.AddState("B");   // only reachable through the sub-machine's Exit
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.defaultState = p;
            a.AddTransition(child);
            p.AddExitTransition();
            sm.AddStateMachineTransition(child, b);

            CollectionAssert.AreEqual(new[] { "A", "B", "P" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ExitWithNothingDrawnOutOfTheSubMachine_RisesUntilItRestartsTheLayer()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.defaultState = p;
            a.AddTransition(child);
            p.AddExitTransition();      // nothing leaves Child, so this reaches the layer's Exit

            // The layer restarting at Entry is a cycle in the walk; the test is as much about
            // it terminating as about the answer.
            CollectionAssert.AreEqual(new[] { "A", "P" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnyStateInTheRoot_ReachesWhateverItPointsAt()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");           // default
            var z = sm.AddState("Z");
            sm.AddAnyStateTransition(z);

            CollectionAssert.AreEqual(new[] { "A", "Z" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnyStateBelongingToASubMachineNobodyEnters_ReachesNothing()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");           // default
            var z = sm.AddState("Z");
            var child = sm.AddStateMachine("Child");
            child.AddState("P");
            child.AddAnyStateTransition(z);   // scoped to Child, which never runs

            CollectionAssert.AreEqual(new[] { "A" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ATransitionStraightIntoASubMachinesState_MakesThatMachineActive()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var z = sm.AddState("Z");
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            var q = child.AddState("Q");
            child.defaultState = q;     // never entered — nothing goes through Child's Entry
            a.AddTransition(p);         // ...but a transition crosses straight into P
            child.AddAnyStateTransition(z);

            CollectionAssert.AreEqual(new[] { "A", "P", "Z" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        /// <summary>
        /// Why the walk has no "this layer never starts" answer: you cannot build one. A root
        /// holding no state of its own adopts the sub-machine's default state as its own — a
        /// default state that does not live in the machine that names it — and Unity puts it
        /// back when you clear it. Worth pinning, because the walk starts from that property.
        /// </summary>
        [Test]
        public void ARootWithNoStateOfItsOwn_TakesTheSubMachinesDefaultStateAsItsWayIn()
        {
            var controller = NewController(out var sm);
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.AddState("Q");        // nothing points at Q

            foreach (var t in sm.entryTransitions) sm.RemoveEntryTransition(t);
            sm.defaultState = null;     // ignored

            Assert.AreSame(p, sm.defaultState);
            CollectionAssert.AreEqual(new[] { "P" },
                Names(ControllerReachability.ReachableStates(sm)));

            Object.DestroyImmediate(controller);
        }

        /// <summary>
        /// Passing the root's Exit is its own question: it is what puts the layer back at Entry
        /// and so the only way a root's Entry conditions are ever read a second time. Walked the
        /// same way as the states — conditions unevaluated — so a route nothing satisfies still
        /// counts as reaching Exit.
        /// </summary>
        [Test]
        public void ALayerWithNothingLeadingToExit_NeverPassesIt()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var b = sm.AddState("B");
            a.AddTransition(b);
            b.AddTransition(a);         // a main loop that never ends

            Assert.IsFalse(ControllerReachability.ReachesExit(sm));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnExitTransitionOnAStateTheLayerCanEnter_PassesExit()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            a.AddExitTransition();

            Assert.IsTrue(ControllerReachability.ReachesExit(sm));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnExitInsideASubMachineWithNothingDrawnOutOfIt_RisesToTheRootsExit()
        {
            var controller = NewController(out var sm);
            var a = sm.AddState("A");   // default
            var child = sm.AddStateMachine("Child");
            var p = child.AddState("P");
            child.defaultState = p;
            a.AddTransition(child);
            p.AddExitTransition();      // nothing leaves Child, so this rises to the layer's Exit

            Assert.IsTrue(ControllerReachability.ReachesExit(sm));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnExitOnAStateNothingCanEnter_IsNotAWayOut()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");           // default
            var island = sm.AddState("Island");
            island.AddExitTransition();

            Assert.IsFalse(ControllerReachability.ReachesExit(sm));

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void AnEmptyLayer_IsAnEmptyAnswer()
        {
            var controller = NewController(out var sm);

            Assert.AreEqual(0, ControllerReachability.ReachableStates(sm).Count);

            Object.DestroyImmediate(controller);
        }

        [Test]
        public void ASyncedLayerPlaysTheSourceLayersMachine()
        {
            var controller = NewController(out var sm);
            sm.AddState("A");
            controller.AddLayer("Mirror");
            var layers = controller.layers;
            layers[1].syncedLayerIndex = 0;
            controller.layers = layers;

            Assert.AreSame(sm, ControllerReachability.PlayedMachine(controller, 1));

            Object.DestroyImmediate(controller);
        }
    }
}
