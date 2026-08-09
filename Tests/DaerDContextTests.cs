using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class DaerDContextTests
    {
        AnimatorController _controller;
        DaerDContext _context;
        int _homeChanged;
        int _selectionChanged;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _controller.AddLayer("Second");
            _context = new DaerDContext();
            // Subscribed after the controller is set, so the counters only see what a test does.
            _context.SetController(_controller);
            _context.HomeChanged += () => _homeChanged++;
            _context.SelectionChanged += () => _selectionChanged++;
        }

        [TearDown]
        public void TearDown()
        {
            if (_controller != null) Object.DestroyImmediate(_controller);
        }

        [Test]
        public void SelectHome_RaisesBothEvents_AndDropsTheSelection()
        {
            _context.Select(_controller.layers[0].stateMachine);
            _selectionChanged = 0;

            _context.SelectHome();

            Assert.IsTrue(_context.IsHomeSelected);
            Assert.IsNull(_context.Selection);
            Assert.AreEqual(1, _homeChanged);
            Assert.AreEqual(1, _selectionChanged);
        }

        [Test]
        public void SelectHome_KeepsTheLayerUnderneath_AsThePlaceToReturnTo()
        {
            _context.SetLayer(1);

            _context.SelectHome();

            Assert.AreEqual(1, _context.LayerIndex);
            Assert.AreSame(_controller.layers[1].stateMachine, _context.CurrentStateMachine);
        }

        [Test]
        public void SelectHome_IsANoOp_WithoutAController_AndWhenAlreadyHome()
        {
            var bare = new DaerDContext();
            int bareEvents = 0;
            bare.HomeChanged += () => bareEvents++;

            bare.SelectHome();

            Assert.IsFalse(bare.IsHomeSelected);
            Assert.AreEqual(0, bareEvents, "no controller, nothing to show a home screen for");

            _context.SelectHome();
            _homeChanged = 0;
            _selectionChanged = 0;

            _context.SelectHome();

            Assert.AreEqual(0, _homeChanged, "already home");
            Assert.AreEqual(0, _selectionChanged);
        }

        [Test]
        public void SelectHome_PopsBackToTheLayerRoot()
        {
            var root = _controller.layers[0].stateMachine;
            var sub = root.AddStateMachine("Sub");
            var state = root.AddState("Tree");
            var tree = new BlendTree { name = "Tree" };
            state.motion = tree;
            _context.EnterStateMachine(sub);
            _context.EnterBlendTree(state);
            Assert.IsTrue(_context.IsViewingBlendTree);

            _context.SelectHome();

            Assert.IsFalse(_context.IsViewingBlendTree);
            Assert.AreEqual(0, _context.BlendTreePath.Count);
            Assert.AreEqual(1, _context.StateMachinePath.Count, "back at the layer root");
            Assert.AreSame(root, _context.StateMachinePath[0]);

            // In-memory controllers have no asset path, so the tree is nobody's sub-asset.
            Object.DestroyImmediate(tree);
        }

        [Test]
        public void SetLayer_LeavesHome_AndSaysSoOnlyWhenItWasHome()
        {
            _context.SelectHome();
            _homeChanged = 0;

            _context.SetLayer(1);

            Assert.IsFalse(_context.IsHomeSelected);
            Assert.AreEqual(1, _context.LayerIndex);
            Assert.AreEqual(1, _homeChanged, "picking a layer is how home is left");

            _homeChanged = 0;
            _context.SetLayer(0);

            Assert.AreEqual(0, _homeChanged, "layer to layer changes nothing about home");
        }

        [Test]
        public void SetController_ClearsTheHomeFlag()
        {
            _context.SelectHome();

            _context.SetController(_controller);

            Assert.IsFalse(_context.IsHomeSelected);
        }
    }
}
