using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class GraphClipboardTests
    {
        AnimatorController _controller;
        DaerDContext _context;
        FrameCommands _frames;
        GraphClipboard _clipboard;
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
            _context = new DaerDContext();
            _context.SetController(_controller);
            _frames = new FrameCommands(_context);
            _clipboard = new GraphClipboard(_context, new EdgeCommands(_context), _frames);
        }

        [TearDown]
        public void TearDown()
        {
            // All three clipboards are static, so anything left in them leaks into the next test.
            StateClipboard.Clear();
            FrameNoteClipboard.Clear();
            TransitionClipboard.Clear();
            if (_frames.Data != null) Object.DestroyImmediate(_frames.Data);
            Object.DestroyImmediate(_controller);
        }

        static Vector2 NoPosition(AnimatorState state) => Vector2.zero;

        // Frames and notes built directly rather than through FrameCommands: an in-memory
        // controller cannot own the holder as a sub-asset, so every find-or-create hands out a
        // fresh one. Only the paste path needs a holder, and it makes exactly one.
        GraphFrameData.Frame NewFrame(string title, Rect bounds) =>
            new GraphFrameData.Frame { title = title, bounds = bounds, stateMachine = _smA };

        GraphFrameData.Note NewNote(Rect bounds) =>
            new GraphFrameData.Note { bounds = bounds, stateMachine = _smA };

        [Test]
        public void CopyStates_ThenPasteIntoAnotherLayer_CarriesTheNamesAndTransitions()
        {
            var a = _smA.AddState("A", new Vector3(0f, 0f, 0f));
            var b = _smA.AddState("B", new Vector3(100f, 0f, 0f));
            a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0f, "Go");

            _clipboard.CopyStates(new List<AnimatorState> { a, b }, NoPosition);
            _context.SetLayer(1);
            Assert.IsTrue(_clipboard.PasteStates(new Vector2(50f, 60f)));

            Assert.AreEqual(2, _smB.states.Length);
            var names = new List<string>();
            foreach (var child in _smB.states) names.Add(child.state.name);
            CollectionAssert.AreEquivalent(new[] { "A", "B" }, names,
                "an empty destination has no clash, so the names cross unchanged");
        }

        [Test]
        public void CopyStates_DropsAnyCopiedFramesAndNotes()
        {
            _clipboard.CopyFrame(NewFrame("Group A", new Rect(0f, 0f, 100f, 100f)));
            Assert.IsTrue(FrameNoteClipboard.HasData);

            _clipboard.CopyStates(new List<AnimatorState> { _smA.AddState("A") }, NoPosition);

            Assert.IsTrue(StateClipboard.HasData);
            Assert.IsFalse(FrameNoteClipboard.HasData,
                "the two paste together, so a fresh state copy has to drop the frame copy");
        }

        [Test]
        public void PasteStates_WithAnEmptyClipboard_ReportsThatNothingRan()
        {
            Assert.IsFalse(_clipboard.PasteStates(Vector2.zero));
        }

        [Test]
        public void CopyElements_SharesOneAnchorAcrossStatesFramesAndNotes()
        {
            var a = _smA.AddState("A", new Vector3(200f, 200f, 0f));
            var frame = NewFrame("Group A", new Rect(40f, 90f, 100f, 100f));
            var note = NewNote(new Rect(300f, 10f, 50f, 50f));

            _clipboard.CopyElements(new List<AnimatorState> { a },
                new List<GraphFrameData.Frame> { frame },
                new List<GraphFrameData.Note> { note },
                0, s => new Vector2(200f, 200f));

            Assert.IsTrue(StateClipboard.HasData);
            Assert.IsTrue(FrameNoteClipboard.HasData);
            // Lowest x over the states/frames/notes (40) and lowest y (10) — one shared corner,
            // so the mixed copy keeps its relative layout when it is pasted.
            Assert.AreEqual(new Vector2(40f, 10f), FrameNoteClipboard.Anchor);
        }

        [Test]
        public void CopyElements_WithNothingSelected_LeavesTheClipboardsAlone()
        {
            _clipboard.CopyElements(new List<AnimatorState>(), new List<GraphFrameData.Frame>(),
                new List<GraphFrameData.Note>(), 0, NoPosition);

            Assert.IsFalse(StateClipboard.HasData);
            Assert.IsFalse(FrameNoteClipboard.HasData);
        }

        [Test]
        public void CopyFrame_ThenPasteLandsInTheLayerNowOnScreen()
        {
            _clipboard.CopyFrame(NewFrame("Group A", new Rect(10f, 10f, 320f, 220f)));
            _context.SetLayer(1);
            var created = _clipboard.PasteFramesAndNotes(new Vector2(-40f, 5f));

            Assert.IsNotNull(created);
            Assert.AreEqual(1, created.Count);
            var pasted = _frames.Data.FramesIn(_smB)[0];
            Assert.AreEqual("Group A", pasted.title);
            Assert.AreEqual(new Rect(-40f, 5f, 320f, 220f), pasted.bounds);
        }

        [Test]
        public void CopyNote_DropsCopiedStates()
        {
            _clipboard.CopyStates(new List<AnimatorState> { _smA.AddState("A") }, NoPosition);

            _clipboard.CopyNote(NewNote(new Rect(0f, 0f, 200f, 100f)));

            Assert.IsFalse(StateClipboard.HasData);
            Assert.AreEqual(1, FrameNoteClipboard.NoteCount);
        }

        [Test]
        public void PasteFramesAndNotes_WithAnEmptyClipboard_ReportsThatNothingRan()
        {
            Assert.IsNull(_clipboard.PasteFramesAndNotes(Vector2.zero));
        }

        [Test]
        public void CopyTransitions_ThenPasteWithAStateAsTheNewSource()
        {
            var a = _smA.AddState("A");
            var b = _smA.AddState("B");
            var c = _smA.AddState("C");
            a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0f, "Go");

            CopyTransitionsOf(TransitionEnd.Of(a), a.transitions);
            var created = _clipboard.PasteTransitionsWithStateAsSource(c);

            Assert.IsNotNull(created);
            Assert.AreEqual(1, created.Count);
            Assert.AreEqual(1, c.transitions.Length);
            Assert.AreEqual(b, c.transitions[0].destinationState, "the recorded destination is reused");
            Assert.AreEqual("Go", c.transitions[0].conditions[0].parameter);
        }

        [Test]
        public void CopyTransitions_ThenPasteWithAStateAsTheNewDestination()
        {
            var a = _smA.AddState("A");
            var b = _smA.AddState("B");
            var c = _smA.AddState("C");
            a.AddTransition(b);

            CopyTransitionsOf(TransitionEnd.Of(a), a.transitions);
            var created = _clipboard.PasteTransitionsWithStateAsDestination(c);

            Assert.IsNotNull(created);
            Assert.AreEqual(1, created.Count);
            Assert.AreEqual(2, a.transitions.Length, "the recorded source gains a second transition");
            Assert.AreEqual(c, a.transitions[1].destinationState);
        }

        [Test]
        public void PasteTransitions_WithAnEmptyClipboard_ReportsThatNothingRan()
        {
            var a = _smA.AddState("A");
            Assert.IsNull(_clipboard.PasteTransitionsWithStateAsSource(a));
            Assert.IsNull(_clipboard.PasteTransitionsWithStateAsDestination(a));
            Assert.IsNull(_clipboard.PasteTransitionsWithStateAsSource(null));
        }

        [Test]
        public void PasteTransitionSettingsOnto_StampsTheFirstSnapshotOverEveryTransition()
        {
            var a = _smA.AddState("A");
            var b = _smA.AddState("B");
            var source = a.AddTransition(b);
            source.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            source.duration = 0.75f;
            var target = b.AddTransition(a);

            CopyTransitionsOf(TransitionEnd.Of(a), new AnimatorStateTransition[] { source });

            Assert.IsTrue(_clipboard.PasteTransitionSettingsOnto(new List<AnimatorTransitionBase> { target }));
            Assert.AreEqual(0.75f, target.duration, 0.0001f);
            Assert.AreEqual(1, target.conditions.Length);
            Assert.AreEqual("Go", target.conditions[0].parameter);
        }

        [Test]
        public void PasteTransitionSettingsOnto_WithNothingToTouch_ReportsFalse()
        {
            var a = _smA.AddState("A");
            var b = _smA.AddState("B");
            CopyTransitionsOf(TransitionEnd.Of(a), new AnimatorStateTransition[] { a.AddTransition(b) });

            Assert.IsFalse(_clipboard.PasteTransitionSettingsOnto(new List<AnimatorTransitionBase>()));
        }

        [Test]
        public void PasteTransitionsAsNewOn_AddsOnePerSnapshotPerPair()
        {
            var a = _smA.AddState("A");
            var b = _smA.AddState("B");
            var c = _smA.AddState("C");
            a.AddTransition(b).AddCondition(AnimatorConditionMode.If, 0f, "Go");

            CopyTransitionsOf(TransitionEnd.Of(a), a.transitions);
            var pairs = new List<(TransitionEnd, TransitionEnd)>
            {
                (TransitionEnd.Of(b), TransitionEnd.Of(c)),
            };

            Assert.IsTrue(_clipboard.PasteTransitionsAsNewOn(pairs, out var last));
            Assert.IsNotNull(last);
            Assert.AreEqual(1, b.transitions.Length);
            Assert.AreEqual(c, b.transitions[0].destinationState);
            Assert.AreEqual("Go", b.transitions[0].conditions[0].parameter);
        }

        [Test]
        public void PasteTransitionsAsNewOn_WithAnEmptyClipboard_ReportsThatNothingRan()
        {
            Assert.IsFalse(_clipboard.PasteTransitionsAsNewOn(
                new List<(TransitionEnd, TransitionEnd)>(), out var last));
            Assert.IsNull(last);
        }

        void CopyTransitionsOf(TransitionEnd source, IEnumerable<AnimatorStateTransition> transitions)
        {
            var list = new List<AnimatorTransitionBase>();
            foreach (var t in transitions) list.Add(t);
            _clipboard.CopyTransitions(new List<(TransitionEnd, IList<AnimatorTransitionBase>)>
            {
                (source, list),
            });
        }
    }
}
