using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    class StateClipboardTestBehaviour : StateMachineBehaviour
    {
        public string payload;
    }

    public class StateClipboardTests
    {
        AnimatorController _controller;
        AnimatorStateMachine _smA;
        AnimatorStateMachine _smB;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _controller.AddLayer("Other");
            _controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            _smA = _controller.layers[0].stateMachine;
            _smB = _controller.layers[1].stateMachine;
        }

        [TearDown]
        public void TearDown()
        {
            // Static clipboard: clearing also destroys the detached behaviour copies it holds.
            StateClipboard.Clear();
            Object.DestroyImmediate(_controller);
        }

        /// <summary>A → B on "Go", with a behaviour on A. Positions are supplied by the caller,
        /// mirroring how the graph feeds node positions in.</summary>
        void BuildSourceStates(out AnimatorState a, out AnimatorState b,
            out Dictionary<AnimatorState, Vector2> positions)
        {
            a = _smA.AddState("A", new Vector3(100f, 100f, 0f));
            b = _smA.AddState("B", new Vector3(300f, 160f, 0f));
            var transition = a.AddTransition(b);
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            transition.duration = 0.25f;

            var behaviour = (StateClipboardTestBehaviour)a.AddStateMachineBehaviour(
                typeof(StateClipboardTestBehaviour));
            behaviour.payload = "hello";

            positions = new Dictionary<AnimatorState, Vector2>
            {
                [a] = new Vector2(100f, 100f),
                [b] = new Vector2(300f, 160f),
            };
        }

        Vector2 PositionOf(Dictionary<AnimatorState, Vector2> positions, AnimatorState state) =>
            positions.TryGetValue(state, out var position) ? position : Vector2.zero;

        [Test]
        public void PastesIntoAnotherLayer_WithTransitionsBehavioursAndLayout()
        {
            BuildSourceStates(out var a, out var b, out var positions);

            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s), null, _controller);
            var created = StateClipboard.Paste(_smB, new Vector2(0f, 0f), _controller);

            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(2, _smB.states.Length);
            Assert.AreEqual(2, _smA.states.Length, "the source layer is left alone");

            // The top-left of the copied group lands on the paste position; B keeps its offset.
            var byName = new Dictionary<string, Vector3>();
            foreach (var child in _smB.states) byName[child.state.name] = child.position;
            Assert.AreEqual(new Vector3(0f, 0f, 0f), byName["A"]);
            Assert.AreEqual(new Vector3(200f, 60f, 0f), byName["B"]);

            // The transition between the two copied states travels with them.
            var pastedA = created[0];
            Assert.AreEqual("A", pastedA.name);
            Assert.AreEqual(1, pastedA.transitions.Length);
            Assert.AreSame(created[1], pastedA.transitions[0].destinationState);
            Assert.AreEqual(1, pastedA.transitions[0].conditions.Length);
            Assert.AreEqual("Go", pastedA.transitions[0].conditions[0].parameter);
            Assert.AreEqual(0.25f, pastedA.transitions[0].duration, 0.0001f);

            // Behaviours are recreated, not shared with the original state.
            Assert.AreEqual(1, pastedA.behaviours.Length);
            var pastedBehaviour = pastedA.behaviours[0] as StateClipboardTestBehaviour;
            Assert.IsNotNull(pastedBehaviour);
            Assert.AreEqual("hello", pastedBehaviour.payload);
            Assert.AreNotSame(a.behaviours[0], pastedBehaviour);
        }

        /// <summary>Exit / Entry / AnyState are singletons of the state machine, so the copies
        /// have to be wired to the destination layer's own nodes.</summary>
        [Test]
        public void EntryExitAndAnyStateLinks_TravelWithTheCopy()
        {
            BuildSourceStates(out var a, out var b, out var positions);

            var toExit = a.AddExitTransition();
            toExit.AddCondition(AnimatorConditionMode.IfNot, 0f, "Go");
            var fromEntry = _smA.AddEntryTransition(b);
            fromEntry.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            var fromAny = _smA.AddAnyStateTransition(a);
            fromAny.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            fromAny.canTransitionToSelf = false;

            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s), null, _controller, _smA);
            var created = StateClipboard.Paste(_smB, Vector2.zero, _controller);

            var pastedA = created[0];
            var pastedB = created[1];

            int exits = 0;
            foreach (var t in pastedA.transitions)
                if (t.isExit) exits++;
            Assert.AreEqual(1, exits, "state → Exit");

            Assert.AreEqual(1, _smB.entryTransitions.Length, "Entry → state");
            Assert.AreSame(pastedB, _smB.entryTransitions[0].destinationState);
            Assert.AreEqual("Go", _smB.entryTransitions[0].conditions[0].parameter);

            Assert.AreEqual(1, _smB.anyStateTransitions.Length, "AnyState → state");
            Assert.AreSame(pastedA, _smB.anyStateTransitions[0].destinationState);
            Assert.IsFalse(_smB.anyStateTransitions[0].canTransitionToSelf, "settings come along too");

            Assert.AreEqual(1, _smA.entryTransitions.Length, "the source layer is left alone");
            Assert.AreEqual(1, _smA.anyStateTransitions.Length);
        }

        [Test]
        public void WithoutTheSourceStateMachine_OnlyTheStatesOwnLinksTravel()
        {
            BuildSourceStates(out var a, out var b, out var positions);
            a.AddExitTransition();
            _smA.AddEntryTransition(b);
            _smA.AddAnyStateTransition(a);

            // No state machine passed: a state knows its outgoing transitions, but Entry /
            // AnyState transitions live on the machine and can't be found from here.
            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s), null, _controller);
            var created = StateClipboard.Paste(_smB, Vector2.zero, _controller);

            int exits = 0;
            foreach (var t in created[0].transitions)
                if (t.isExit) exits++;
            Assert.AreEqual(1, exits);
            Assert.AreEqual(0, _smB.entryTransitions.Length);
            Assert.AreEqual(0, _smB.anyStateTransitions.Length);
        }

        [Test]
        public void NamesSurviveTheMove_UnlessTheDestinationAlreadyUsesThem()
        {
            BuildSourceStates(out var a, out var b, out var positions);
            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s), null, _controller, _smA);

            // Empty destination: the names cross unchanged.
            var created = StateClipboard.Paste(_smB, Vector2.zero, _controller);
            Assert.AreEqual("A", created[0].name);
            Assert.AreEqual("B", created[1].name);

            // Second paste into the same layer: the names are taken, so the copies get a suffix
            // rather than two states answering to "A".
            var again = StateClipboard.Paste(_smB, new Vector2(500f, 0f), _controller);
            Assert.AreEqual("A 1", again[0].name);
            Assert.AreEqual("B 1", again[1].name);
            Assert.AreSame(again[1], again[0].transitions[0].destinationState,
                "transitions follow the objects, not the names");
        }

        [Test]
        public void PastingIntoAnotherController_RecreatesReferencedParameters()
        {
            BuildSourceStates(out var a, out var b, out var positions);
            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s), null, _controller);

            var destination = new AnimatorController();
            destination.AddLayer("Base");
            Assert.AreEqual(0, destination.parameters.Length);

            StateClipboard.Paste(destination.layers[0].stateMachine, Vector2.zero, destination);

            Assert.AreEqual(1, destination.parameters.Length);
            Assert.AreEqual("Go", destination.parameters[0].name);
            Assert.AreEqual(AnimatorControllerParameterType.Bool, destination.parameters[0].type);

            Object.DestroyImmediate(destination);
        }

        [Test]
        public void PastingTwice_DoesNotDuplicateExistingParameters()
        {
            BuildSourceStates(out var a, out var b, out var positions);
            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s), null, _controller);

            StateClipboard.Paste(_smB, Vector2.zero, _controller);
            StateClipboard.Paste(_smB, new Vector2(400f, 0f), _controller);

            Assert.AreEqual(1, _controller.parameters.Length, "'Go' is already there");
            Assert.AreEqual(4, _smB.states.Length);
        }

        [Test]
        public void CopyWithoutController_StillPastes_JustWithoutParameters()
        {
            BuildSourceStates(out var a, out var b, out var positions);
            StateClipboard.Copy(new[] { a, b }, s => PositionOf(positions, s));

            var destination = new AnimatorController();
            destination.AddLayer("Base");
            var created = StateClipboard.Paste(destination.layers[0].stateMachine, Vector2.zero, destination);

            Assert.AreEqual(2, created.Count);
            Assert.AreEqual(0, destination.parameters.Length,
                "nothing was captured to recreate — the copy didn't know the source controller");

            Object.DestroyImmediate(destination);
        }
    }
}
