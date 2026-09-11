using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>How the window reads a Shift+wheel, and which tab a Ctrl+Shift+wheel lands on.</summary>
    public class TabStripTests
    {
        AnimatorController _a, _b, _c;
        TabStrip _strip;

        [SetUp]
        public void SetUp()
        {
            _a = new AnimatorController { name = "A" };
            _b = new AnimatorController { name = "B" };
            _c = new AnimatorController { name = "C" };
            _strip = new TabStrip(new List<AnimatorController>(), new List<int>(), _ => { }, _ => { });
            _strip.Add(_a);
            _strip.Add(_b);
            _strip.Add(_c);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_a);
            Object.DestroyImmediate(_b);
            Object.DestroyImmediate(_c);
        }

        [Test]
        public void Right_IsTheNextTab()
        {
            Assert.AreSame(_b, _strip.Neighbour(_a, 1));
            Assert.AreSame(_c, _strip.Neighbour(_b, 1));
        }

        [Test]
        public void Left_IsThePreviousTab()
        {
            Assert.AreSame(_b, _strip.Neighbour(_c, -1));
            Assert.AreSame(_a, _strip.Neighbour(_b, -1));
        }

        [Test]
        public void AtEitherEnd_ItStops_RatherThanWrapping()
        {
            // A trackpad swipe is a burst of events; wrapping would carry it round the strip.
            Assert.IsNull(_strip.Neighbour(_c, 1));
            Assert.IsNull(_strip.Neighbour(_a, -1));
        }

        [Test]
        public void AControllerThatIsNotOpen_HasNoNeighbour()
        {
            var stranger = new AnimatorController();
            try
            {
                Assert.IsNull(_strip.Neighbour(stranger, 1));
                Assert.IsNull(_strip.Neighbour(null, 1));
            }
            finally
            {
                Object.DestroyImmediate(stranger);
            }
        }

        static DaerDWindow.WheelWalk Read(bool shift, bool ctrl, float x, float y, out int step)
            => DaerDWindow.ReadShiftWheel(shift, ctrl, new Vector2(x, y), out step);

        [Test]
        public void ShiftWheel_ArrivingSideways_StillWalksTheLayers()
        {
            // Windows and macOS hand a Shift+wheel over as a sideways scroll. Reading the axis as
            // "tabs" is what turned every layer step into a tab switch there.
            Assert.AreEqual(DaerDWindow.WheelWalk.Layer, Read(true, false, 3f, 0f, out int step));
            Assert.AreEqual(1, step);
            Assert.AreEqual(DaerDWindow.WheelWalk.Layer, Read(true, false, -3f, 0f, out step));
            Assert.AreEqual(-1, step);
        }

        [Test]
        public void ShiftWheel_Down_IsTheNextLayer()
        {
            Assert.AreEqual(DaerDWindow.WheelWalk.Layer, Read(true, false, 0f, 3f, out int step));
            Assert.AreEqual(1, step);
            Assert.AreEqual(DaerDWindow.WheelWalk.Layer, Read(true, false, 0f, -3f, out step));
            Assert.AreEqual(-1, step);
        }

        [Test]
        public void CtrlShiftWheel_WalksTheTabs_OnEitherAxis()
        {
            Assert.AreEqual(DaerDWindow.WheelWalk.Tab, Read(true, true, 0f, 3f, out int step));
            Assert.AreEqual(1, step);
            Assert.AreEqual(DaerDWindow.WheelWalk.Tab, Read(true, true, -3f, 0f, out step));
            Assert.AreEqual(-1, step);
        }

        [Test]
        public void WithoutShift_NothingIsClaimed()
        {
            // Ctrl + wheel alone stays whatever the panel under the pointer makes of it.
            Assert.AreEqual(DaerDWindow.WheelWalk.None, Read(false, true, 0f, 3f, out int step));
            Assert.AreEqual(0, step);
            Assert.AreEqual(DaerDWindow.WheelWalk.None, Read(false, false, 3f, 0f, out step));
        }

        [Test]
        public void AShiftWheelThatDoesNotMove_IsStillClaimed()
        {
            // Claimed with no step, so the graph does not zoom on it either.
            Assert.AreEqual(DaerDWindow.WheelWalk.Layer, Read(true, false, 0f, 0f, out int step));
            Assert.AreEqual(0, step);
        }
    }
}
