using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class GraphFrameDataTests
    {
        [Test]
        public void AddFrame_StoresIt_AndFramesInFiltersByStateMachine()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Other");
            var smA = controller.layers[0].stateMachine;
            var smB = controller.layers[1].stateMachine;

            var data = GraphFrameData.GetOrCreate(controller);
            var frameA = data.AddFrame(smA, new Rect(10f, 20f, 300f, 200f), "Group A");
            data.AddFrame(smB, new Rect(0f, 0f, 100f, 100f), "Group B");

            Assert.AreEqual(2, data.frames.Count);
            var inA = data.FramesIn(smA);
            Assert.AreEqual(1, inA.Count);
            Assert.AreSame(frameA, inA[0]);
            Assert.AreEqual("Group A", inA[0].title);
            Assert.AreEqual(new Rect(10f, 20f, 300f, 200f), inA[0].bounds);

            data.RemoveFrame(frameA);
            Assert.AreEqual(0, data.FramesIn(smA).Count);
            Assert.AreEqual(1, data.FramesIn(smB).Count);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        /// <summary>Regression: regenerating a layer from a recipe destroys and recreates
        /// its state machine — every machine-keyed record (async-sync setup and its SYNC
        /// badge, frames, notes, C# ownership) must follow to the successor, matched by the
        /// old instance ID even after the machine object is destroyed.</summary>
        [Test]
        public void RemapMachineReferences_MovesRecordsToTheSuccessor_EvenAfterDestroy()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Zip");
            controller.AddLayer("Other");
            var old = controller.layers[0].stateMachine;
            var other = controller.layers[1].stateMachine;
            int oldId = old.GetInstanceID();

            var data = GraphFrameData.GetOrCreate(controller);
            data.AddFrame(old, new Rect(0f, 0f, 100f, 100f), "OnZip");
            data.AddNote(old, new Rect(0f, 0f, 50f, 50f));
            var foreignFrame = data.AddFrame(other, new Rect(0f, 0f, 10f, 10f), "Elsewhere");
            data.asyncSyncs.Add(new GraphFrameData.AsyncSyncConfig { layer = old, baseName = "Zip" });
            data.codeOwned.Add(new GraphFrameData.CodeOwnedLayer { layer = old, recipe = controller });

            // The real-world sequence: the old machine dies before the remap runs.
            Object.DestroyImmediate(old);
            var successor = new AnimatorStateMachine { name = "Zip" };

            Assert.IsTrue(data.RemapMachineReferences(oldId, successor));
            Assert.AreSame(successor, data.frames[0].stateMachine);
            Assert.AreSame(successor, data.notes[0].stateMachine);
            Assert.AreSame(successor, data.asyncSyncs[0].layer);
            Assert.AreSame(successor, data.codeOwned[0].layer);
            Assert.AreSame(other, foreignFrame.stateMachine, "records of other layers stay put");

            Assert.IsFalse(data.RemapMachineReferences(123456789, successor),
                "an unknown id must not touch anything");

            Object.DestroyImmediate(successor);
            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Find_ReturnsNull_ForInMemoryControllers()
        {
            var controller = new AnimatorController();
            Assert.IsNull(GraphFrameData.Find(controller));
            Object.DestroyImmediate(controller);
        }
    }
}
