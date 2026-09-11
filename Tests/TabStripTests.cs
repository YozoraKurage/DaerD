using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>Which tab a sideways Shift+scroll lands on.</summary>
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
    }
}
