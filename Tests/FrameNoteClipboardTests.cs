using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Tests
{
    public class FrameNoteClipboardTests
    {
        AnimatorController _controller;
        GraphFrameData _data;
        AnimatorStateMachine _smA;
        AnimatorStateMachine _smB;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _controller.AddLayer("Other");
            _smA = _controller.layers[0].stateMachine;
            _smB = _controller.layers[1].stateMachine;
            _data = GraphFrameData.GetOrCreate(_controller);
        }

        [TearDown]
        public void TearDown()
        {
            // The clipboard is static: leaving data in it would leak into the next test.
            FrameNoteClipboard.Clear();
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_controller);
        }

        [Test]
        public void Frame_PastesIntoAnotherLayer_KeepingLook_AtThePastePosition()
        {
            var frame = _data.AddFrame(_smA, new Rect(100f, 50f, 320f, 220f), "Group A");
            frame.color = new Color(0.1f, 0.2f, 0.3f, 1f);
            frame.moveNodesWithFrame = false;
            frame.locked = true;

            FrameNoteClipboard.Copy(new[] { frame }, null);
            var created = FrameNoteClipboard.Paste(_data, _smB, new Vector2(-40f, 10f));

            Assert.AreEqual(1, created.Count);
            var pasted = _data.FramesIn(_smB)[0];
            Assert.AreEqual("Group A", pasted.title, "another layer has no clash, so the title carries over");
            Assert.AreEqual(new Rect(-40f, 10f, 320f, 220f), pasted.bounds);
            Assert.AreEqual(frame.color, pasted.color);
            Assert.IsFalse(pasted.moveNodesWithFrame, "the original had it off, so the copy has it off");
            Assert.IsFalse(pasted.locked, "a pasted frame must be draggable, whatever the original was");
            Assert.AreEqual(1, _data.FramesIn(_smA).Count, "the source layer is untouched");
        }

        [Test]
        public void Note_PastesIntoAnotherLayer_KeepingTextAndStyle()
        {
            var note = _data.AddNote(_smA, new Rect(0f, 0f, 200f, 100f));
            note.text = "remember this";
            note.fontSize = 16;
            note.color = new Color(0.9f, 0.8f, 0.5f, 0.6f);

            FrameNoteClipboard.Copy(null, new[] { note });
            FrameNoteClipboard.Paste(_data, _smB, new Vector2(30f, 30f));

            var pasted = _data.NotesIn(_smB)[0];
            Assert.AreEqual("remember this", pasted.text);
            Assert.AreEqual(16, pasted.fontSize);
            Assert.AreEqual(note.color, pasted.color);
            Assert.AreEqual(new Rect(30f, 30f, 200f, 100f), pasted.bounds);
            Assert.AreSame(_smB, pasted.stateMachine);
        }

        [Test]
        public void MixedSelection_KeepsRelativeLayout()
        {
            var frame = _data.AddFrame(_smA, new Rect(100f, 100f, 300f, 200f), "Group");
            var note = _data.AddNote(_smA, new Rect(150f, 140f, 120f, 80f));

            FrameNoteClipboard.Copy(new[] { frame }, new[] { note });
            FrameNoteClipboard.Paste(_data, _smB, Vector2.zero);

            // The frame is the group's top-left corner, so it lands on the paste position and the
            // note keeps its offset from it.
            Assert.AreEqual(new Rect(0f, 0f, 300f, 200f), _data.FramesIn(_smB)[0].bounds);
            Assert.AreEqual(new Rect(50f, 40f, 120f, 80f), _data.NotesIn(_smB)[0].bounds);
        }

        [Test]
        public void AnchorOverride_ShiftsTheWholeGroup()
        {
            var frame = _data.AddFrame(_smA, new Rect(100f, 100f, 300f, 200f), "Group");

            // A state sitting 40 units up-left of the frame was copied in the same gesture, so it
            // is the anchor — the frame has to keep that offset when pasted.
            FrameNoteClipboard.Copy(new[] { frame }, null, new Vector2(60f, 60f));
            FrameNoteClipboard.Paste(_data, _smB, Vector2.zero);

            Assert.AreEqual(new Rect(40f, 40f, 300f, 200f), _data.FramesIn(_smB)[0].bounds);
        }

        [Test]
        public void PastingIntoTheSameLayer_UniquifiesTheTitle()
        {
            var frame = _data.AddFrame(_smA, new Rect(0f, 0f, 300f, 200f), "Group");

            FrameNoteClipboard.Copy(new[] { frame }, null);
            FrameNoteClipboard.Paste(_data, _smA, new Vector2(40f, 40f));
            FrameNoteClipboard.Paste(_data, _smA, new Vector2(80f, 80f));

            var titles = _data.FramesIn(_smA).ConvertAll(f => f.title);
            CollectionAssert.AreEquivalent(new[] { "Group", "Group 1", "Group 2" }, titles);
        }

        [Test]
        public void Clear_EmptiesTheClipboard()
        {
            var frame = _data.AddFrame(_smA, new Rect(0f, 0f, 300f, 200f), "Group");
            FrameNoteClipboard.Copy(new[] { frame }, null);
            Assert.IsTrue(FrameNoteClipboard.HasData);

            FrameNoteClipboard.Clear();

            Assert.IsFalse(FrameNoteClipboard.HasData);
            Assert.AreEqual(0, FrameNoteClipboard.Paste(_data, _smB, Vector2.zero).Count);
            Assert.AreEqual(0, _data.FramesIn(_smB).Count);
        }
    }
}
