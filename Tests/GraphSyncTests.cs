using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// GraphSync driven headlessly: a graph view that is never attached to a panel, rebuilt by
    /// calling <see cref="GraphSync.Rebuild"/> directly (the scheduled rebuild never runs here).
    /// </summary>
    public class GraphSyncTests
    {
        AnimatorController _controller;
        DaerDContext _context;
        AnimatorStateMachine _root, _loco;
        AnimatorState _idle, _walk;
        AnimatorStateTransition _leaving;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _context = new DaerDContext();
            _context.SetController(_controller);
            _root = _context.CurrentStateMachine;
            _idle = _root.AddState("Idle", new Vector3(0f, 0f, 0f));
            _loco = _root.AddStateMachine("Loco", new Vector3(0f, 200f, 0f));
            _walk = _loco.AddState("Walk", new Vector3(0f, 0f, 0f));
            _leaving = _walk.AddTransition(_idle);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_controller);
        }

        static SpecialNode FindUpNode(AnimatorGraphView view)
        {
            SpecialNode found = null;
            view.nodes.ForEach(node =>
            {
                if (node is SpecialNode spn && spn.Kind == SpecialNodeKind.Up) found = spn;
            });
            return found;
        }

        [Test]
        public void InsideASubStateMachine_ATransitionLeavingIt_LandsOnTheUpNode()
        {
            _context.EnterStateMachine(_loco);
            var view = new AnimatorGraphView(_context);
            view.Sync.Rebuild();

            var up = FindUpNode(view);
            Assert.IsNotNull(up, "a sub-state machine shows its parent");

            var edge = view.Sync.FindEdge(_leaving);
            Assert.IsNotNull(edge, "the leaving transition is drawn");
            Assert.AreSame(up, edge.input.node);
            Assert.IsTrue(view.Sync.CanReplicateEdge(edge), "each copy goes where its original goes");
        }

        [Test]
        public void InsideASubStateMachine_ReplicatingTheUpEdge_CopiesTowardTheRealDestination()
        {
            _context.EnterStateMachine(_loco);
            var view = new AnimatorGraphView(_context);
            view.Sync.Rebuild();

            view.Sync.ReplicateEdge(view.Sync.FindEdge(_leaving));

            Assert.AreEqual(2, _walk.transitions.Length);
            Assert.AreSame(_idle, _walk.transitions[1].destinationState);
        }

        [Test]
        public void InsideASubStateMachine_AddingLikeALeavingTransition_GoesWhereItGoes()
        {
            _context.EnterStateMachine(_loco);
            var view = new AnimatorGraphView(_context);
            view.Sync.Rebuild();

            var created = view.Sync.CreateTransitionLike(_leaving);

            Assert.IsNotNull(created);
            Assert.AreEqual(2, _walk.transitions.Length);
            Assert.AreSame(_idle, created.destinationState);
        }

        [Test]
        public void AtTheRoot_ThereIsNoUpNode()
        {
            var view = new AnimatorGraphView(_context);
            view.Sync.Rebuild();

            Assert.IsNull(FindUpNode(view));
        }
    }
}
