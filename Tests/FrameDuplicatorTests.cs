using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    public class FrameDuplicatorTests
    {
        static (AnimatorController, AnimatorStateMachine) NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            return (controller, controller.layers[0].stateMachine);
        }

        [Test]
        public void Duplicate_CopiesFrameAndStatesAndInternalTransitions()
        {
            var (controller, sm) = NewController();
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);

            var a = sm.AddState("A", new Vector3(50f, 50f, 0f));
            var b = sm.AddState("B", new Vector3(200f, 50f, 0f));
            var outside = sm.AddState("Outside", new Vector3(500f, 50f, 0f));
            var aToB = a.AddTransition(b);
            aToB.AddCondition(AnimatorConditionMode.If, 0f, "P");
            aToB.duration = 0.7f;
            a.AddTransition(outside);   // crosses the duplicated set boundary, should NOT be copied

            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame
            {
                title = "Group",
                color = new Color(0.1f, 0.2f, 0.3f, 1f),
                bounds = new Rect(0f, 0f, 400f, 400f),
                stateMachine = sm,
            };
            data.frames.Add(frame);

            var newFrame = FrameDuplicator.Duplicate(data, controller, sm, frame, new[] { a, b },
                System.Array.Empty<GraphFrameData.Note>());

            Assert.IsNotNull(newFrame);
            Assert.AreEqual("Group 1", newFrame.title);
            Assert.AreEqual(2, data.frames.Count);
            // Duplicated states exist.
            Assert.AreEqual(5, sm.states.Length);

            AnimatorState aCopy = null, bCopy = null;
            foreach (var cs in sm.states)
            {
                if (cs.state == a || cs.state == b || cs.state == outside) continue;
                if (cs.state.name.StartsWith("A")) aCopy = cs.state;
                else if (cs.state.name.StartsWith("B")) bCopy = cs.state;
            }
            Assert.IsNotNull(aCopy, "Expected duplicate of A");
            Assert.IsNotNull(bCopy, "Expected duplicate of B");

            // Critical: internal transition A→B is reproduced as A'→B', and the external A→Outside
            // is NOT carried over (the duplicate is self-contained).
            Assert.AreEqual(1, aCopy.transitions.Length,
                "duplicate of A should have exactly one transition (the internal A→B clone)");
            Assert.AreEqual(bCopy, aCopy.transitions[0].destinationState);
            Assert.AreEqual(0.7f, aCopy.transitions[0].duration, 1e-4f);
            Assert.AreEqual(1, aCopy.transitions[0].conditions.Length);
            Assert.AreEqual("P", aCopy.transitions[0].conditions[0].parameter);
            Assert.AreEqual(0, bCopy.transitions.Length);

            // Originals are untouched.
            Assert.AreEqual(2, a.transitions.Length);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_FrameWithoutStates_StillCopiesTheFrameBox()
        {
            var (controller, sm) = NewController();
            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame { title = "Empty", bounds = new Rect(0, 0, 100, 100), stateMachine = sm };
            data.frames.Add(frame);

            var newFrame = FrameDuplicator.Duplicate(data, controller, sm, frame,
                System.Array.Empty<AnimatorState>(), System.Array.Empty<GraphFrameData.Note>());

            Assert.IsNotNull(newFrame);
            Assert.AreEqual(2, data.frames.Count);
            Assert.AreEqual(0, sm.states.Length);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_CopiesNotesInsideTheFrame()
        {
            var (controller, sm) = NewController();
            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame { title = "F", bounds = new Rect(0, 0, 100, 100), stateMachine = sm };
            data.frames.Add(frame);

            var note = new GraphFrameData.Note
            {
                text = "memo",
                color = new Color(0.5f, 0.7f, 0.9f, 1f),
                fontSize = 16,
                bounds = new Rect(20f, 20f, 50f, 30f),
                stateMachine = sm,
            };
            data.notes.Add(note);

            FrameDuplicator.Duplicate(data, controller, sm, frame,
                System.Array.Empty<AnimatorState>(), new[] { note });

            Assert.AreEqual(2, data.notes.Count);
            var copy = data.notes[1];
            Assert.AreEqual("memo", copy.text);
            Assert.AreEqual(16, copy.fontSize);
            // Note bounds offset matches the frame's.
            Assert.AreEqual(60f, copy.bounds.x, 1e-4f);
            Assert.AreEqual(60f, copy.bounds.y, 1e-4f);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_ReplicatesExitTransitionsFromInsideStates()
        {
            var (controller, sm) = NewController();
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);

            var a = sm.AddState("A", new Vector3(50f, 50f, 0f));
            var aToExit = a.AddExitTransition();
            aToExit.AddCondition(AnimatorConditionMode.If, 0f, "P");
            aToExit.duration = 0.3f;

            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame
                { title = "F", bounds = new Rect(0, 0, 200, 200), stateMachine = sm };
            data.frames.Add(frame);

            FrameDuplicator.Duplicate(data, controller, sm, frame, new[] { a },
                System.Array.Empty<GraphFrameData.Note>());

            AnimatorState aCopy = null;
            foreach (var cs in sm.states)
                if (cs.state != a) aCopy = cs.state;
            Assert.IsNotNull(aCopy, "Expected duplicate of A");

            // The duplicate inherits the same outgoing-to-Exit behaviour.
            Assert.AreEqual(1, aCopy.transitions.Length);
            Assert.IsTrue(aCopy.transitions[0].isExit,
                "duplicate of A should have an exit transition cloned from the original");
            Assert.AreEqual(0.3f, aCopy.transitions[0].duration, 1e-4f);
            Assert.AreEqual(1, aCopy.transitions[0].conditions.Length);
            Assert.AreEqual("P", aCopy.transitions[0].conditions[0].parameter);
            // The original is untouched.
            Assert.AreEqual(1, a.transitions.Length);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_ReplicatesEntryTransitionsTargetingInsideStates()
        {
            var (controller, sm) = NewController();
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);

            var a = sm.AddState("A", new Vector3(50f, 50f, 0f));
            var outside = sm.AddState("Outside", new Vector3(500f, 50f, 0f));
            var entryToA = sm.AddEntryTransition(a);
            entryToA.AddCondition(AnimatorConditionMode.If, 0f, "P");
            sm.AddEntryTransition(outside);   // crosses the boundary, must NOT be cloned

            int entryBefore = sm.entryTransitions.Length;

            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame
                { title = "F", bounds = new Rect(0, 0, 200, 200), stateMachine = sm };
            data.frames.Add(frame);

            FrameDuplicator.Duplicate(data, controller, sm, frame, new[] { a },
                System.Array.Empty<GraphFrameData.Note>());

            AnimatorState aCopy = null;
            foreach (var cs in sm.states)
                if (cs.state != a && cs.state != outside) aCopy = cs.state;
            Assert.IsNotNull(aCopy, "Expected duplicate of A");

            // Exactly one new entry transition added, targeting the duplicate.
            Assert.AreEqual(entryBefore + 1, sm.entryTransitions.Length);
            bool foundClone = false;
            foreach (var t in sm.entryTransitions)
            {
                if (t.destinationState != aCopy) continue;
                foundClone = true;
                Assert.AreEqual(1, t.conditions.Length);
                Assert.AreEqual("P", t.conditions[0].parameter);
            }
            Assert.IsTrue(foundClone, "Entry → A' should exist with cloned conditions");

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_ReplicatesAnyStateTransitionsTargetingInsideStates()
        {
            var (controller, sm) = NewController();
            controller.AddParameter("P", AnimatorControllerParameterType.Bool);

            var a = sm.AddState("A", new Vector3(50f, 50f, 0f));
            var anyToA = sm.AddAnyStateTransition(a);
            anyToA.AddCondition(AnimatorConditionMode.If, 0f, "P");
            anyToA.duration = 0.4f;

            int anyBefore = sm.anyStateTransitions.Length;

            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame
                { title = "F", bounds = new Rect(0, 0, 200, 200), stateMachine = sm };
            data.frames.Add(frame);

            FrameDuplicator.Duplicate(data, controller, sm, frame, new[] { a },
                System.Array.Empty<GraphFrameData.Note>());

            AnimatorState aCopy = null;
            foreach (var cs in sm.states)
                if (cs.state != a) aCopy = cs.state;
            Assert.IsNotNull(aCopy, "Expected duplicate of A");

            Assert.AreEqual(anyBefore + 1, sm.anyStateTransitions.Length);
            bool foundClone = false;
            foreach (var t in sm.anyStateTransitions)
            {
                if (t.destinationState != aCopy) continue;
                foundClone = true;
                Assert.AreEqual(0.4f, t.duration, 1e-4f);
                Assert.AreEqual(1, t.conditions.Length);
            }
            Assert.IsTrue(foundClone, "AnyState → A' should exist with cloned settings");

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Duplicate_OffsetsTheNewFrameAndPreservesProperties()
        {
            var (controller, sm) = NewController();
            var data = ScriptableObject.CreateInstance<GraphFrameData>();
            var frame = new GraphFrameData.Frame
            {
                title = "Group",
                bounds = new Rect(100f, 200f, 320f, 220f),
                color = new Color(0.7f, 0.2f, 0.4f, 1f),
                moveNodesWithFrame = false,
                locked = true,
                stateMachine = sm,
            };
            data.frames.Add(frame);

            var newFrame = FrameDuplicator.Duplicate(data, controller, sm, frame,
                System.Array.Empty<AnimatorState>(), System.Array.Empty<GraphFrameData.Note>());

            Assert.AreEqual(140f, newFrame.bounds.x, 1e-4f);
            Assert.AreEqual(240f, newFrame.bounds.y, 1e-4f);
            Assert.AreEqual(320f, newFrame.bounds.width, 1e-4f);
            Assert.AreEqual(220f, newFrame.bounds.height, 1e-4f);
            Assert.AreEqual(frame.color, newFrame.color);
            Assert.AreEqual(false, newFrame.moveNodesWithFrame);
            // The duplicate starts unlocked, even when the original is locked, so the user can
            // immediately edit it.
            Assert.IsFalse(newFrame.locked);

            Object.DestroyImmediate(data);
            Object.DestroyImmediate(controller);
        }
    }
}
