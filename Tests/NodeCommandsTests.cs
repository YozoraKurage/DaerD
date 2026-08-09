using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class NodeCommandsTests
    {
        readonly List<Object> _cleanup = new List<Object>();
        AnimatorController _controller;
        DaerDContext _context;
        NodeCommands _nodes;
        AnimatorStateMachine _sm;

        [SetUp]
        public void SetUp()
        {
            _controller = new AnimatorController();
            _controller.AddLayer("Base");
            _cleanup.Add(_controller);
            _context = new DaerDContext();
            _context.SetController(_controller);
            _sm = _context.CurrentStateMachine;
            _nodes = new NodeCommands(_context);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup)
                if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        [Test]
        public void CreateState_AddsToTheCurrentMachine_WithAUniqueNameAndPosition()
        {
            var first = _nodes.CreateState(new Vector2(10f, 20f), null);
            var second = _nodes.CreateState(new Vector2(30f, 40f), null);

            Assert.IsNotNull(first);
            Assert.AreEqual("New State", first.name);
            Assert.AreEqual("New State 1", second.name, "the name is taken, so the copy gets a suffix");
            Assert.AreEqual(2, _sm.states.Length);
            Assert.AreEqual(new Vector3(10f, 20f, 0f), _sm.states[0].position);
            Assert.AreEqual(new Vector3(30f, 40f, 0f), _sm.states[1].position);
        }

        [Test]
        public void CreateState_BlendTreeMode_PutsABlendTreeInTheMotionSlot()
        {
            var state = _nodes.CreateState(Vector2.zero, "state-blendtree");

            Assert.IsInstanceOf<BlendTree>(state.motion);
            Assert.AreEqual("Blend Tree", state.motion.name);
            // In-memory controllers have no asset path, so the tree is never attached as a
            // sub-asset and nothing else owns it.
            _cleanup.Add(state.motion);
        }

        [Test]
        public void CreateState_WithoutAStateMachine_DoesNothing()
        {
            var bare = new DaerDContext();
            Assert.IsNull(new NodeCommands(bare).CreateState(Vector2.zero, null));
        }

        [Test]
        public void CreateStateWithMotion_NamesTheStateAfterTheMotion()
        {
            var clip = new AnimationClip { name = "Wave" };
            _cleanup.Add(clip);

            var state = _nodes.CreateStateWithMotion(new Vector2(5f, 6f), clip);

            Assert.AreEqual("Wave", state.name);
            Assert.AreEqual(clip, state.motion);
            Assert.IsNull(_nodes.CreateStateWithMotion(Vector2.zero, null), "no motion, no state");
        }

        [Test]
        public void AssignMotion_ReplacesTheMotion()
        {
            var state = _nodes.CreateState(Vector2.zero, null);
            var clip = new AnimationClip { name = "Wave" };
            _cleanup.Add(clip);

            _nodes.AssignMotion(state, clip);

            Assert.AreEqual(clip, state.motion);
        }

        [Test]
        public void CreateSubStateMachine_AddsAChildMachine()
        {
            var child = _nodes.CreateSubStateMachine(new Vector2(1f, 2f));

            Assert.IsNotNull(child);
            Assert.AreEqual("New Sub-State Machine", child.name);
            Assert.AreEqual(1, _sm.stateMachines.Length);
            Assert.AreEqual(new Vector3(1f, 2f, 0f), _sm.stateMachines[0].position);
        }

        [Test]
        public void SetDefaultState_ReportsWhetherThereWasAnythingToDo()
        {
            _nodes.CreateState(Vector2.zero, null);              // becomes the layer default
            var second = _nodes.CreateState(new Vector2(100f, 0f), null);

            Assert.IsTrue(_nodes.SetDefaultState(second));
            Assert.AreEqual(second, _sm.defaultState);
            Assert.IsFalse(_nodes.SetDefaultState(null), "nothing to do, so the caller skips its rebuild");
        }

        [Test]
        public void PackStates_MovesThemIntoAChild_AndUnpackBringsThemBack()
        {
            var a = _nodes.CreateState(new Vector2(0f, 0f), null);
            var b = _nodes.CreateState(new Vector2(100f, 0f), null);

            var child = _nodes.PackStates(new List<AnimatorState> { a, b });

            Assert.IsNotNull(child);
            Assert.AreEqual(0, _sm.states.Length, "both states moved into the child");
            Assert.AreEqual(1, _sm.stateMachines.Length);
            Assert.AreEqual(2, child.states.Length);

            Assert.IsTrue(_nodes.UnpackSubStateMachine(child));
            Assert.AreEqual(2, _sm.states.Length);
            Assert.AreEqual(0, _sm.stateMachines.Length);
        }

        [Test]
        public void PackStates_WithNothingToPack_ReturnsNull()
        {
            Assert.IsNull(_nodes.PackStates(null));
            Assert.IsNull(_nodes.PackStates(new List<AnimatorState>()));
        }

        [Test]
        public void UnpackSubStateMachine_WithoutAChild_ReportsNothingToDo()
        {
            Assert.IsFalse(_nodes.UnpackSubStateMachine(null));
        }
    }
}
