using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// DD DynamicAnalyze's engine, which is the part that can be wrong: the clock, the client
    /// that steps a real Animator, the drivers it applies for VRChat, and the trace all of it
    /// comes back as. No window is involved — the window is a viewer over the same trace these
    /// tests read, which is the whole point of the trace being the product.
    /// </summary>
    public class DynamicAnalyzeTests
    {
        /// <summary>Idle → On when "Go" goes up, and On drives N to 5 on the way in.</summary>
        static AnimatorController NewController(bool withDriver = false, bool localOnly = false)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("N", AnimatorControllerParameterType.Int);
            controller.AddParameter("X", AnimatorControllerParameterType.Float);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;

            var transition = idle.AddTransition(on);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            if (withDriver)
            {
                var driver = VrcParameterDriver.AddTo(on, "Test");
                Assert.IsNotNull(driver, "the driver behaviour (or its stub) has to be present");
                VrcParameterDriver.SetLocalOnly(driver, localOnly);
                VrcParameterDriver.AddSetEntry(driver, "N", 5f);
            }
            return controller;
        }

        static SimClock Clock(float seconds = 0.5f, float jitter = 0f) =>
            new SimClock { fps = 60f, seconds = seconds, jitter = jitter, seed = 7 };

        // ---- the clock ------------------------------------------------------

        [Test]
        public void Clock_IsTheSameRunTwice_AndJitterMovesLengthsNotCount()
        {
            var even = Clock().Steps();
            Assert.AreEqual(30, even.Length);
            foreach (var step in even) Assert.AreEqual(1f / 60f, step, 1e-6f);

            var noisy = Clock(jitter: 0.4f);
            var first = noisy.Steps();
            var second = noisy.Steps();
            Assert.AreEqual(even.Length, first.Length, "jitter varies lengths, not the count");
            CollectionAssert.AreEqual(first, second, "same seed, same run");

            bool moved = false;
            foreach (var step in first)
            {
                Assert.Greater(step, 0f, "a frame of no length is not a frame");
                if (!Mathf.Approximately(step, 1f / 60f)) moved = true;
            }
            Assert.IsTrue(moved);

            noisy.seed = 8;
            CollectionAssert.AreNotEqual(first, noisy.Steps(), "a new seed is a new question");
        }

        // ---- the run --------------------------------------------------------

        [Test]
        public void Run_RecordsEveryParameterAndEveryLayer()
        {
            var trace = Simulation.Run(NewController(), Clock(0.1f));
            Assert.AreEqual(6, trace.Frames);

            Assert.AreEqual(SignalKind.Bool, trace.Find("Local", "Go").kind);
            Assert.AreEqual(SignalKind.Int, trace.Find("Local", "N").kind);
            Assert.AreEqual(SignalKind.Float, trace.Find("Local", "X").kind);

            var state = trace.Find("Local", "Base/state");
            Assert.AreEqual(SignalKind.State, state.kind);
            CollectionAssert.AreEqual(new[] { "Idle", "On" }, state.labels);
            Assert.AreEqual("Idle", state.TextAt(0));
            Assert.IsNotNull(trace.Find("Local", "Base/transition"));

            // The clock is in the trace too, so a jittered run can be read back.
            Assert.AreEqual(1f / 60f, trace.StepAt(0), 1e-6f);
            Assert.AreEqual(6f / 60f, trace.Duration, 1e-5f);
        }

        [Test]
        public void Run_TakesTheTransition_TheStimulusAsksFor()
        {
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var trace = Simulation.Run(NewController(), Clock(0.2f), stimulus);

            var go = trace.Find("Local", "Go");
            var state = trace.Find("Local", "Base/state");
            // 0.05 s at 60 fps is the fourth frame: the first whose start has reached it.
            Assert.AreEqual(0f, go.At(2));
            Assert.AreEqual(1f, go.At(3));
            Assert.AreEqual("Idle", state.TextAt(2));
            Assert.AreEqual("On", state.TextAt(trace.Frames - 1));
            Assert.IsTrue(state.ChangedAt(state.Frames - 1) || state.ChangedAt(3));
        }

        [Test]
        public void Run_AppliesADriverWhenItsStateIsEntered()
        {
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var trace = Simulation.Run(NewController(withDriver: true), Clock(0.2f), stimulus);

            var n = trace.Find("Local", "N");
            Assert.AreEqual(0f, n.At(0), "nothing has entered On yet");
            Assert.AreEqual(5f, n.At(trace.Frames - 1));

            // The driver lands with the state, not before it and not a pass later.
            var state = trace.Find("Local", "Base/state");
            int entered = -1, driven = -1;
            for (int frame = 0; frame < trace.Frames; frame++)
            {
                if (entered < 0 && state.TextAt(frame) == "On") entered = frame;
                if (driven < 0 && n.At(frame) == 5f) driven = frame;
            }
            Assert.GreaterOrEqual(entered, 0);
            Assert.AreEqual(entered, driven);
        }

        [Test]
        public void Client_RunsALocalOnlyDriver_OnlyOnTheWearer()
        {
            var controller = NewController(withDriver: true, localOnly: true);
            foreach (bool local in new[] { true, false })
                using (var client = new SimClient(controller, "C", local, 1))
                {
                    client.Write("Go", 1f);
                    client.Step(1f / 60f);
                    client.Step(1f / 60f);
                    Assert.AreEqual("On", client.StateLabels(0)[client.CurrentState(0)]);
                    Assert.AreEqual(local ? 5f : 0f, client.Read("N"),
                        local ? "the wearer runs it" : "a remote does not");
                }
        }

        [Test]
        public void Client_AnswersIsLocal_WhenTheControllerAsks()
        {
            var controller = NewController();
            controller.AddParameter(SimClient.IsLocalParameter,
                AnimatorControllerParameterType.Bool);
            using (var wearer = new SimClient(controller, "L", true, 1))
                Assert.AreEqual(1f, wearer.Read(SimClient.IsLocalParameter));
            using (var remote = new SimClient(controller, "R", false, 1))
                Assert.AreEqual(0f, remote.Read(SimClient.IsLocalParameter));
        }

        [Test]
        public void Client_CopiesWithTheDriversRangeConversion()
        {
            var controller = NewController();
            var on = FindState(controller, "On");
            var driver = VrcParameterDriver.AddTo(on, "Test");
            VrcParameterDriver.SetLocalOnly(driver, false);
            // -1..1 read as 0..1, which is the remap async sync would use on a Float target.
            VrcParameterDriver.AddCopyEntry(driver, "X", "X", true, -1f, 1f, 0f, 1f);

            using (var client = new SimClient(controller, "C", true, 1))
            {
                client.Write("X", 0f);
                client.Write("Go", 1f);
                client.Step(1f / 60f);
                client.Step(1f / 60f);
                Assert.AreEqual(0.5f, client.Read("X"), 1e-5f);
            }
        }

        [Test]
        public void Trace_SaysWhenASignalMoved()
        {
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var trace = Simulation.Run(NewController(), Clock(0.2f), stimulus);

            var go = trace.Find("Local", "Go");
            int changes = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
                if (go.ChangedAt(frame)) changes++;
            Assert.AreEqual(1, changes, "it went up once and stayed there");
            // Frame 2 ENDS at 0.05, which is what a cursor dropped there is pointing at.
            // The stimulus landed on frame 3, whose START had reached 0.05 — the two answers
            // differ by one on purpose, and each is the right one for its question.
            Assert.AreEqual(2, trace.FrameAt(0.05f));
            Assert.AreEqual(0.05f, trace.TimeAt(2), 1e-5f);
        }

        static AnimatorState FindState(AnimatorController controller, string name)
        {
            foreach (var child in controller.layers[0].stateMachine.states)
                if (child.state != null && child.state.name == name)
                    return child.state;
            Assert.Fail("no state '" + name + "'");
            return null;
        }
    }
}
