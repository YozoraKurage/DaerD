using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.Edit;

namespace Yozolab.DaerD.Tests
{
    public class FrameInheritanceTests
    {
        [Test]
        public void CarryOver_ClonesFramesFromMappedStateMachines_ToTheirCopies()
        {
            var sourceSm = new AnimatorStateMachine();
            var copySm = new AnimatorStateMachine();
            var unmappedSm = new AnimatorStateMachine();

            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            data.frames.Add(new GraphFrameData.Frame
            {
                title = "Group A",
                color = new Color(0.2f, 0.6f, 0.9f, 1f),
                bounds = new Rect(10f, 20f, 100f, 80f),
                locked = true,
                moveNodesWithFrame = false,
                stateMachine = sourceSm,
            });
            data.frames.Add(new GraphFrameData.Frame
            {
                title = "Other",
                bounds = new Rect(0f, 0f, 50f, 50f),
                stateMachine = unmappedSm,
            });

            var map = new Dictionary<AnimatorStateMachine, AnimatorStateMachine> { [sourceSm] = copySm };
            FrameInheritance.CarryOver(data, map);

            // Three entries: original frame on sourceSm, original frame on unmappedSm,
            // and the new cloned frame on copySm.
            Assert.AreEqual(3, data.frames.Count);
            GraphFrameData.Frame clone = null;
            foreach (var f in data.frames)
                if (f.stateMachine == copySm) clone = f;
            Assert.IsNotNull(clone, "Expected a frame to be cloned onto the mapped copy SM");
            Assert.AreEqual("Group A", clone.title);
            Assert.AreEqual(new Rect(10f, 20f, 100f, 80f), clone.bounds);
            Assert.IsTrue(clone.locked);
            Assert.IsFalse(clone.moveNodesWithFrame);

            // The frame whose state machine isn't in the map stays put with no clone.
            int otherCount = 0;
            foreach (var f in data.frames)
                if (f.stateMachine == unmappedSm) otherCount++;
            Assert.AreEqual(1, otherCount);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(sourceSm);
            Object.DestroyImmediate(copySm);
            Object.DestroyImmediate(unmappedSm);
        }

        [Test]
        public void CarryOver_ClonesNotes()
        {
            var sourceSm = new AnimatorStateMachine();
            var copySm = new AnimatorStateMachine();

            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            data.notes.Add(new GraphFrameData.Note
            {
                text = "memo",
                color = new Color(0.9f, 0.8f, 0.5f, 1f),
                fontSize = 16,
                bounds = new Rect(5f, 5f, 60f, 40f),
                stateMachine = sourceSm,
            });

            var map = new Dictionary<AnimatorStateMachine, AnimatorStateMachine> { [sourceSm] = copySm };
            FrameInheritance.CarryOver(data, map);

            Assert.AreEqual(2, data.notes.Count);
            GraphFrameData.Note clone = null;
            foreach (var n in data.notes)
                if (n.stateMachine == copySm) clone = n;
            Assert.IsNotNull(clone);
            Assert.AreEqual("memo", clone.text);
            Assert.AreEqual(16, clone.fontSize);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(sourceSm);
            Object.DestroyImmediate(copySm);
        }

        [Test]
        public void CarryOver_EmptyMap_IsNoOp()
        {
            var sm = new AnimatorStateMachine();
            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            data.frames.Add(new GraphFrameData.Frame { title = "F", stateMachine = sm });

            FrameInheritance.CarryOver(data, new Dictionary<AnimatorStateMachine, AnimatorStateMachine>());

            Assert.AreEqual(1, data.frames.Count);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(sm);
        }
    }
}
