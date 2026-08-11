using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Matching a running Animator back to the graph. The path format is not documented
    /// anywhere DaerD can check, so it is not asserted against a string DaerD wrote — every
    /// test here builds the path, runs the controller, and holds the two hashes against each
    /// other. Unity is the authority; these tests are the transcript of asking it.
    /// </summary>
    public class AnimatorPlaybackTests
    {
        static AnimatorController NewController(string layerName, out AnimatorStateMachine sm)
        {
            var controller = new AnimatorController();
            controller.AddLayer(layerName);
            sm = controller.layers[0].stateMachine;
            return controller;
        }

        static AnimationClip Spin(string name)
        {
            var clip = new AnimationClip { name = name };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Linear(0f, 0f, 1f, 1f));
            return clip;
        }

        static Animator AnimatorOf(AnimatorRig rig) => rig.Root.GetComponent<Animator>();

        [Test]
        public void FullPathHash_MatchesUnity_ForAStateInTheRootMachine()
        {
            var controller = NewController("FX", out var sm);
            // The layer and its root state machine start with the same name and drift apart the
            // moment either is renamed. Which of the two ends up in the path is the whole
            // question, so the test makes them differ — and the answer is the machine.
            sm.name = "Root Machine";
            sm.AddState("Idle").motion = Spin("Idle");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Step();
                Assert.AreEqual(
                    AnimatorOf(rig).GetCurrentAnimatorStateInfo(0).fullPathHash,
                    AnimatorPlayback.FullPathHash(new List<AnimatorStateMachine> { sm }, "Idle"));
                Assert.AreNotEqual(
                    AnimatorPlayback.FullPathHash(new List<AnimatorStateMachine> { sm }, "Idle"),
                    Animator.StringToHash("FX.Idle"),
                    "the layer's name is not what Unity puts in the path");
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void FullPathHash_MatchesUnity_ForAStateInsideASubStateMachine()
        {
            var controller = NewController("FX", out var sm);
            var idle = sm.AddState("Idle");
            idle.motion = Spin("Idle");
            var child = sm.AddStateMachine("Sub");
            // Same short name as the root's state: the pair that a shortNameHash match would
            // light up together.
            var deep = child.AddState("Idle");
            deep.motion = Spin("Deep");
            child.defaultState = deep;
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var jump = idle.AddTransition(child);
            jump.hasExitTime = false;
            jump.duration = 0f;
            jump.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Step();
                rig.Set("Go", true).Step(4);
                var running = AnimatorOf(rig).GetCurrentAnimatorStateInfo(0);

                Assert.AreEqual(running.fullPathHash,
                    AnimatorPlayback.FullPathHash(
                        new List<AnimatorStateMachine> { sm, child }, "Idle"));
                Assert.AreNotEqual(
                    AnimatorPlayback.FullPathHash(new List<AnimatorStateMachine> { sm }, "Idle"),
                    AnimatorPlayback.FullPathHash(
                        new List<AnimatorStateMachine> { sm, child }, "Idle"),
                    "two 'Idle' states in one layer must not share a hash");
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Read_ReportsTheStateAndHowFarThroughItsClipItIs()
        {
            var controller = NewController("Base", out var sm);
            var idle = sm.AddState("Idle");
            var clip = Spin("Idle");
            clip.wrapMode = WrapMode.Loop;
            idle.motion = clip;

            using (var rig = new AnimatorRig(controller))
            {
                rig.Step();
                var playback = AnimatorPlayback.Read(AnimatorOf(rig), 0);

                Assert.IsTrue(playback.valid);
                Assert.AreEqual(
                    AnimatorPlayback.FullPathHash(new List<AnimatorStateMachine> { sm }, "Idle"),
                    playback.stateHash);
                Assert.IsFalse(playback.inTransition);
                Assert.AreEqual(1f, playback.weight, 1e-4f, "the base layer is always at full weight");

                float first = playback.progress;
                rig.Step(10);
                float later = AnimatorPlayback.Read(AnimatorOf(rig), 0).progress;

                Assert.GreaterOrEqual(first, 0f);
                Assert.LessOrEqual(later, 1f);
                Assert.AreNotEqual(first, later, "the clip is playing, so the position moved");
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Read_DuringATransition_NamesWhereItIsGoing()
        {
            var controller = NewController("Base", out var sm);
            var idle = sm.AddState("Idle");
            idle.motion = Spin("Idle");
            var next = sm.AddState("Next");
            next.motion = Spin("Next");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var cross = idle.AddTransition(next);
            cross.hasExitTime = false;
            cross.duration = 0.5f;   // long enough to be caught mid-blend
            cross.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Step();
                rig.Set("Go", true).Step(2);
                var playback = AnimatorPlayback.Read(AnimatorOf(rig), 0);

                Assert.IsTrue(playback.inTransition, "the crossfade should still be running");
                Assert.AreEqual(
                    AnimatorPlayback.FullPathHash(new List<AnimatorStateMachine> { sm }, "Next"),
                    playback.nextStateHash);
                Assert.IsFalse(playback.fromAnyState);
                Assert.Greater(playback.transitionProgress, 0f);
                Assert.Less(playback.transitionProgress, 1f);
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Read_FromAnyState_SaysSo()
        {
            var controller = NewController("Base", out var sm);
            sm.AddState("Idle").motion = Spin("Idle");
            var alert = sm.AddState("Alert");
            alert.motion = Spin("Alert");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            var fire = sm.AddAnyStateTransition(alert);
            fire.duration = 0.5f;
            fire.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Step();
                rig.Set("Go", true).Step(2);
                var playback = AnimatorPlayback.Read(AnimatorOf(rig), 0);

                Assert.IsTrue(playback.inTransition);
                Assert.IsTrue(playback.fromAnyState,
                    "the running edge leaves the Any State node, not the current state");
            }
            Object.DestroyImmediate(controller);
        }

        [Test]
        public void Read_WithNothingToRead_IsNotValid()
        {
            var controller = NewController("Base", out var sm);
            sm.AddState("Idle").motion = Spin("Idle");

            using (var rig = new AnimatorRig(controller))
            {
                Assert.IsFalse(AnimatorPlayback.Read(null, 0).valid);
                Assert.IsFalse(AnimatorPlayback.Read(AnimatorOf(rig), 3).valid,
                    "the layer does not exist on this animator");
            }
            Object.DestroyImmediate(controller);
        }
    }
}
