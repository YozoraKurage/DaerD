using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Which node an arrow key moves to. The graph's Y axis grows downwards, so Up is negative;
    /// the cases here are the layouts where "nearest" and "in that direction" disagree.
    /// </summary>
    public class GraphNavigationTests
    {
        static readonly Vector2 Up = new Vector2(0f, -1f);
        static readonly Vector2 Down = new Vector2(0f, 1f);
        static readonly Vector2 Right = new Vector2(1f, 0f);
        static readonly Vector2 Left = new Vector2(-1f, 0f);

        static int Pick(Vector2 from, Vector2 direction, params Vector2[] centres) =>
            AnimatorGraphView.PickNeighbour(from, new List<Vector2>(centres), direction);

        [Test]
        public void NothingInThatDirection_IsNoMove()
        {
            Assert.AreEqual(-1, Pick(Vector2.zero, Up, new Vector2(0f, 200f)));
            Assert.AreEqual(-1, Pick(Vector2.zero, Left, new Vector2(200f, 0f)));
            Assert.AreEqual(-1, Pick(Vector2.zero, Down));
        }

        [Test]
        public void TheNearestOneAhead_Wins()
        {
            Assert.AreEqual(1, Pick(Vector2.zero, Down,
                new Vector2(0f, 400f),
                new Vector2(0f, 100f)));
        }

        [Test]
        public void ANearNeighbourSlightlyOffAxis_BeatsADistantOneDeadAhead()
        {
            // A column of states rarely lines up to the pixel; insisting on dead ahead would
            // make the arrow keys skip the state that is obviously next.
            Assert.AreEqual(0, Pick(Vector2.zero, Down,
                new Vector2(30f, 120f),
                new Vector2(0f, 400f)));
        }

        [Test]
        public void SomethingMostlySideways_LosesToAnythingProperlyAhead()
        {
            // Nearer by raw distance, but it is beside rather than below.
            Assert.AreEqual(1, Pick(Vector2.zero, Down,
                new Vector2(300f, 60f),
                new Vector2(20f, 220f)));
        }

        [Test]
        public void SomethingMostlySideways_IsStillTakenWhenNothingElseIsThatWay()
        {
            // Otherwise an arrow key in a wide layout would simply do nothing.
            Assert.AreEqual(0, Pick(Vector2.zero, Down, new Vector2(300f, 60f)));
        }

        [Test]
        public void MovingRight_ReadsTheOtherAxisTheSameWay()
        {
            Assert.AreEqual(1, Pick(Vector2.zero, Right,
                new Vector2(500f, 20f),
                new Vector2(150f, 40f)));
        }
    }
}
