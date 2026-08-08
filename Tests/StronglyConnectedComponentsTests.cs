using System.Collections.Generic;
using NUnit.Framework;

namespace Yozolab.DaerD.Tests
{
    public class StronglyConnectedComponentsTests
    {
        /// <summary>Adjacency list from one int[] of successors per node.</summary>
        static List<int>[] Graph(params int[][] successors)
        {
            var edges = new List<int>[successors.Length];
            for (int i = 0; i < successors.Length; i++)
                edges[i] = new List<int>(successors[i]);
            return edges;
        }

        [Test]
        public void LinearChain_GivesEveryNodeItsOwnComponent_NumberedFromTheSink()
        {
            // 0 -> 1 -> 2, no cycles. Tarjan closes a component only once every node it can
            // reach is done, so ids come out in reverse topological order: the sink is 0.
            var comp = StronglyConnectedComponents.Compute(
                Graph(new[] { 1 }, new[] { 2 }, new int[0]), out int count);

            Assert.AreEqual(3, count);
            CollectionAssert.AreEqual(new[] { 2, 1, 0 }, comp);
        }

        [Test]
        public void IsolatedNodes_AreEachReportedAsASingletonComponent()
        {
            var comp = StronglyConnectedComponents.Compute(
                Graph(new int[0], new int[0], new int[0]), out int count);

            Assert.AreEqual(3, count);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, comp);
        }

        [Test]
        public void TwoNodeCycle_CollapsesIntoOneComponent()
        {
            var comp = StronglyConnectedComponents.Compute(
                Graph(new[] { 1 }, new[] { 0 }), out int count);

            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(new[] { 0, 0 }, comp);
        }

        [Test]
        public void TwoDisjointCycles_AreTwoComponents()
        {
            // 0 <-> 1 and 2 <-> 3, with nothing connecting the pairs.
            var comp = StronglyConnectedComponents.Compute(
                Graph(new[] { 1 }, new[] { 0 }, new[] { 3 }, new[] { 2 }), out int count);

            Assert.AreEqual(2, count);
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 1 }, comp);
        }

        [Test]
        public void SelfLoop_IsASingleComponentOfOneNode()
        {
            var comp = StronglyConnectedComponents.Compute(Graph(new[] { 0 }), out int count);

            Assert.AreEqual(1, count);
            CollectionAssert.AreEqual(new[] { 0 }, comp);
        }

        [Test]
        public void CycleWithATail_KeepsTheEntryNodeOutOfTheCycleComponent()
        {
            // 0 -> 1 -> 2 -> 1: the 1/2 loop closes first, then 0 becomes its own component.
            var comp = StronglyConnectedComponents.Compute(
                Graph(new[] { 1 }, new[] { 2 }, new[] { 1 }), out int count);

            Assert.AreEqual(2, count);
            CollectionAssert.AreEqual(new[] { 1, 0, 0 }, comp);
        }

        [Test]
        public void EmptyGraph_HasNoComponents()
        {
            var comp = StronglyConnectedComponents.Compute(Graph(), out int count);

            Assert.AreEqual(0, count);
            Assert.AreEqual(0, comp.Length);
        }
    }
}
