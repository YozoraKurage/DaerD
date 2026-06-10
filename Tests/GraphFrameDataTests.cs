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

        [Test]
        public void Find_ReturnsNull_ForInMemoryControllers()
        {
            var controller = new AnimatorController();
            Assert.IsNull(GraphFrameData.Find(controller));
            Object.DestroyImmediate(controller);
        }
    }
}
