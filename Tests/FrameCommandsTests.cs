using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class FrameCommandsTests
    {
        readonly List<Object> _cleanup = new List<Object>();
        AnimatorController _controller;
        DaerDContext _context;
        FrameCommands _frames;
        AnimatorStateMachine _sm;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _cleanup.Add(_controller);
            _context = new DaerDContext();
            _context.SetController(_controller);
            _sm = _context.CurrentStateMachine;
            _frames = new FrameCommands(_context);
        }

        [TearDown]
        public void TearDown()
        {
            // The holder is a ScriptableObject; an in-memory controller cannot own it as a
            // sub-asset, so the test has to destroy it itself.
            if (_frames.Data != null) _cleanup.Add(_frames.Data);
            foreach (var o in _cleanup)
                if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        [Test]
        public void CreateFrame_StoresItOnTheHolderForTheCurrentMachine()
        {
            var frame = _frames.CreateFrame(new Rect(10f, 20f, 320f, 220f));

            Assert.IsNotNull(frame);
            Assert.IsNotNull(_frames.Data, "creating a frame is what brings the holder into being");
            Assert.AreEqual(new Rect(10f, 20f, 320f, 220f), frame.bounds);
            Assert.AreEqual(_sm, frame.stateMachine);
            Assert.AreEqual(1, _frames.Data.FramesIn(_sm).Count);
        }

        [Test]
        public void CreateFrame_WithoutAStateMachine_DoesNothing()
        {
            var bare = new FrameCommands(new DaerDContext());
            Assert.IsNull(bare.CreateFrame(new Rect(0f, 0f, 10f, 10f)));
            Assert.IsNull(bare.Data);
        }

        [Test]
        public void FrameEdits_RoundTripThroughTheHolder()
        {
            var frame = _frames.CreateFrame(new Rect(0f, 0f, 100f, 100f));

            Assert.IsTrue(_frames.RenameFrame(frame, "Group A"));
            Assert.AreEqual("Group A", frame.title);
            Assert.IsFalse(_frames.RenameFrame(frame, ""), "an empty title is not a rename");
            Assert.AreEqual("Group A", frame.title);

            var color = new Color(0.1f, 0.2f, 0.3f, 1f);
            Assert.IsTrue(_frames.SetFrameColor(frame, color));
            Assert.AreEqual(color, frame.color);

            Assert.IsTrue(_frames.ResizeFrame(frame, new Rect(0f, 0f, 400f, 300f)));
            Assert.AreEqual(new Rect(0f, 0f, 400f, 300f), frame.bounds);

            Assert.IsTrue(_frames.FitFrame(frame, new Rect(5f, 5f, 50f, 50f)));
            Assert.AreEqual(new Rect(5f, 5f, 50f, 50f), frame.bounds);

            bool moveNodes = frame.moveNodesWithFrame;
            Assert.IsTrue(_frames.ToggleFrameMoveNodes(frame));
            Assert.AreEqual(!moveNodes, frame.moveNodesWithFrame);
        }

        [Test]
        public void ToggleFrameLock_FlipsTheFlag_AndLockedFramesResistDeletion()
        {
            var frame = _frames.CreateFrame(new Rect(0f, 0f, 100f, 100f));
            Assert.IsFalse(frame.locked);

            Assert.IsTrue(_frames.ToggleFrameLock(frame));
            Assert.IsTrue(frame.locked);
            Assert.IsFalse(_frames.DeleteFrame(frame), "a locked frame is not deletable");
            Assert.AreEqual(1, _frames.Data.FramesIn(_sm).Count);

            Assert.IsTrue(_frames.ToggleFrameLock(frame));
            Assert.IsFalse(frame.locked);
            Assert.IsTrue(_frames.DeleteFrame(frame));
            Assert.AreEqual(0, _frames.Data.FramesIn(_sm).Count);
        }

        [Test]
        public void NoteEdits_RoundTripThroughTheHolder()
        {
            var note = _frames.CreateNote(new Rect(4f, 5f, 200f, 100f));

            Assert.IsNotNull(note);
            Assert.AreEqual(_sm, note.stateMachine);

            Assert.IsTrue(_frames.SetNoteText(note, "hello"));
            Assert.AreEqual("hello", note.text);
            Assert.IsTrue(_frames.SetNoteText(note, null));
            Assert.AreEqual(string.Empty, note.text, "a null text lands as empty, never null");

            var color = new Color(0.9f, 0.8f, 0.5f, 1f);
            Assert.IsTrue(_frames.SetNoteColor(note, color));
            Assert.AreEqual(color, note.color);

            Assert.IsTrue(_frames.SetNoteFontSize(note, 20));
            Assert.AreEqual(20, note.fontSize);

            Assert.IsTrue(_frames.ResizeNote(note, new Rect(4f, 5f, 300f, 150f)));
            Assert.AreEqual(new Rect(4f, 5f, 300f, 150f), note.bounds);

            Assert.IsTrue(_frames.DeleteNote(note));
            Assert.AreEqual(0, _frames.Data.NotesIn(_sm).Count);
        }

        [Test]
        public void Edits_WithoutAHolder_ReportNothingToDo()
        {
            // Nothing has created the holder yet, so every edit is a no-op the caller can skip.
            var frame = new GraphFrameData.Frame();
            var note = new GraphFrameData.Note();

            Assert.IsFalse(_frames.RenameFrame(frame, "Group"));
            Assert.IsFalse(_frames.ToggleFrameLock(frame));
            Assert.IsFalse(_frames.SetFrameColor(frame, Color.red));
            Assert.IsFalse(_frames.DeleteFrame(frame));
            Assert.IsFalse(_frames.SetNoteText(note, "x"));
            Assert.IsFalse(_frames.DeleteNote(note));

            Assert.IsFalse(_frames.RenameFrame(null, "Group"));
            Assert.IsFalse(_frames.SetNoteText(null, "x"));
        }

        [Test]
        public void DuplicateFrame_CopiesTheBoxAndTheStatesInsideIt()
        {
            var a = _sm.AddState("A", new Vector3(20f, 20f, 0f));
            var b = _sm.AddState("B", new Vector3(60f, 20f, 0f));
            a.AddTransition(b);
            var frame = _frames.CreateFrame(new Rect(0f, 0f, 200f, 200f));
            _frames.RenameFrame(frame, "Group A");
            // An in-memory controller cannot own the holder as a sub-asset, so every
            // find-or-create hands out a fresh one; the first has to be destroyed by hand.
            _cleanup.Add(_frames.Data);

            var copy = _frames.DuplicateFrame(frame, new List<AnimatorState> { a, b },
                new List<GraphFrameData.Note>());

            Assert.IsNotNull(copy);
            Assert.AreNotSame(frame, copy);
            Assert.AreEqual("Group A", copy.title);
            Assert.AreEqual(frame.color, copy.color);
            Assert.IsFalse(copy.locked, "a fresh duplicate is editable even if the original was locked");
            Assert.AreEqual(_sm, copy.stateMachine);
            Assert.AreEqual(4, _sm.states.Length, "the two states inside were duplicated too");
        }
    }
}
