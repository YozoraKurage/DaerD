using NUnit.Framework;
using UnityEditor;
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

        // ---- the wire -------------------------------------------------------

        static SimSettings Wired(float seconds, SyncWire wire, Stimulus stimulus = null) =>
            new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = seconds, seed = 7 },
                stimulus = stimulus ?? new Stimulus(),
                wire = wire,
            };

        [Test]
        public void Wire_CarriesASyncedValue_OnItsOwnCadenceAndNotBefore()
        {
            var wire = new SyncWire { intervalSeconds = 0.2f }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(0.5f, wire, stimulus));

            var local = trace.Find(Simulation.LocalScope, "X");
            var remote = trace.Find(Simulation.RemoteScope, "X");
            Assert.IsNotNull(remote, "a wired run has both copies in it");

            // The wearer has it from the first frame; nobody else does until a sample goes.
            Assert.AreEqual(0.5f, local.At(0), 1e-5f);
            Assert.AreEqual(0f, remote.At(0));
            int arrived = -1;
            for (int frame = 0; frame < trace.Frames && arrived < 0; frame++)
                if (remote.At(frame) != 0f) arrived = frame;
            Assert.GreaterOrEqual(trace.TimeAt(arrived), 0.2f, "not before the first sample");
            Assert.Less(trace.TimeAt(arrived), 0.24f, "and not long after it");

            // And the sample is visible as its own signal.
            Assert.AreEqual(1f, trace.Find(Simulation.WireScope, "sample").At(arrived));
        }

        [Test]
        public void Wire_LeavesUnsyncedParametersWhereTheyAre()
        {
            var wire = new SyncWire { intervalSeconds = 0.05f }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f).At(0f, "Go", true);
            var trace = Simulation.Run(NewController(), Wired(0.4f, wire, stimulus));

            int last = trace.Frames - 1;
            Assert.AreEqual(0.5f, trace.Find(Simulation.RemoteScope, "X").At(last), 1e-2f);
            // "Go" is not on the wire, so the remote never hears it and never leaves Idle —
            // which is the whole class of bug a two-client run is for.
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "Go").At(last));
            Assert.AreEqual("Idle",
                trace.Find(Simulation.RemoteScope, "Base/state").TextAt(last));
            Assert.AreEqual("On", trace.Find(Simulation.LocalScope, "Base/state").TextAt(last));
        }

        [Test]
        public void Wire_LosesAChangeThatCameAndWentInsideOneSample()
        {
            var wire = new SyncWire { intervalSeconds = 0.2f, quantize = false }.Syncs("X");
            var stimulus = new Stimulus()
                .At(0.02f, "X", 0.5f)     // both inside the first interval: the remote
                .At(0.10f, "X", 0.25f);   // only ever sees where it ended up
            var trace = Simulation.Run(NewController(), Wired(0.6f, wire, stimulus));

            var remote = trace.Find(Simulation.RemoteScope, "X");
            for (int frame = 0; frame < trace.Frames; frame++)
                Assert.AreNotEqual(0.5f, remote.At(frame),
                    "a value that came and went inside one sample never left the wearer");
            Assert.AreEqual(0.25f, remote.At(trace.Frames - 1), 1e-5f);
        }

        [Test]
        public void Wire_DropsWholeSamplesTogether()
        {
            var wire = new SyncWire { intervalSeconds = 0.1f, dropChance = 1f }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(0.5f, wire, stimulus));

            var remote = trace.Find(Simulation.RemoteScope, "X");
            for (int frame = 0; frame < trace.Frames; frame++)
                Assert.AreEqual(0f, remote.At(frame), "every sample was lost");

            var lost = trace.Find(Simulation.WireScope, "lost");
            int losses = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
                if (lost.At(frame) != 0f) losses++;
            Assert.GreaterOrEqual(losses, 4, "a loss is visible per sample, not per parameter");
        }

        [Test]
        public void Wire_RoundsValuesTheWayTheNetworkDoes()
        {
            var wire = new SyncWire { intervalSeconds = 0.05f }.Syncs("X", "N");
            var stimulus = new Stimulus().At(0f, "X", 0.3f).At(0f, "N", 300f);
            var trace = Simulation.Run(NewController(), Wired(0.3f, wire, stimulus));

            int last = trace.Frames - 1;
            // 8 bits over -1..1: 0.3 is not one of the 255 values the wire can hold.
            float remote = trace.Find(Simulation.RemoteScope, "X").At(last);
            Assert.AreNotEqual(0.3f, remote);
            Assert.AreEqual(0.3f, remote, 0.008f);
            // An Int is a byte, so 300 arrives as 255.
            Assert.AreEqual(255f, trace.Find(Simulation.RemoteScope, "N").At(last));

            wire.quantize = false;
            var exact = Simulation.Run(NewController(), Wired(0.3f, wire, stimulus));
            Assert.AreEqual(0.3f,
                exact.Find(Simulation.RemoteScope, "X").At(exact.Frames - 1), 1e-5f);
        }

        [Test]
        public void Wire_KeepsOneSampleTogether()
        {
            // An index and the channel it describes must never be read half-updated, so a
            // sample either carries both new values or neither.
            var wire = new SyncWire { intervalSeconds = 0.2f, quantize = false }.Syncs("X", "N");
            var stimulus = new Stimulus().At(0.02f, "X", 0.5f).At(0.02f, "N", 3f);
            var trace = Simulation.Run(NewController(), Wired(0.6f, wire, stimulus));

            var x = trace.Find(Simulation.RemoteScope, "X");
            var n = trace.Find(Simulation.RemoteScope, "N");
            for (int frame = 0; frame < trace.Frames; frame++)
                Assert.AreEqual(x.At(frame) != 0f, n.At(frame) != 0f,
                    "one arrived without the other at frame " + frame);
        }

        [Test]
        public void Wire_MakesTheTwoCopiesTakeDifferentBranches()
        {
            // The IsLocal split every VRChat controller is built around: the wearer runs the
            // sending side and everyone else runs the receiving one.
            var controller = NewController();
            controller.AddParameter(SimClient.IsLocalParameter,
                AnimatorControllerParameterType.Bool);
            var machine = controller.layers[0].stateMachine;
            var mine = machine.AddState("Mine");
            var theirs = machine.AddState("Theirs");
            foreach (var (state, mode) in new[]
                     {
                         (mine, AnimatorConditionMode.If),
                         (theirs, AnimatorConditionMode.IfNot),
                     })
            {
                var route = machine.AddAnyStateTransition(state);
                route.hasExitTime = false;
                route.hasFixedDuration = true;
                route.duration = 0f;
                route.canTransitionToSelf = false;
                route.AddCondition(mode, 0f, SimClient.IsLocalParameter);
            }

            var trace = Simulation.Run(controller, Wired(0.2f, new SyncWire()));
            int last = trace.Frames - 1;
            Assert.AreEqual("Mine", trace.Find(Simulation.LocalScope, "Base/state").TextAt(last));
            Assert.AreEqual("Theirs",
                trace.Find(Simulation.RemoteScope, "Base/state").TextAt(last));
        }

        [Test]
        public void Stimulus_ReachesTheWearerUnlessItNamesSomebody()
        {
            var wire = new SyncWire { intervalSeconds = 10f };   // never samples in this run
            var stimulus = new Stimulus()
                .At(0f, "X", 0.5f)
                .At(0f, "N", 4f, Simulation.RemoteScope);
            var trace = Simulation.Run(NewController(), Wired(0.2f, wire, stimulus));

            Assert.AreEqual(0.5f, trace.Find(Simulation.LocalScope, "X").At(0), 1e-5f);
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "X").At(0));
            Assert.AreEqual(4f, trace.Find(Simulation.RemoteScope, "N").At(0));
            Assert.AreEqual(0f, trace.Find(Simulation.LocalScope, "N").At(0));
        }

        // ---- the live session -----------------------------------------------

        [Test]
        public void Session_StepsOnTheTimeItIsGiven_AndKeepsTheRemainder()
        {
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 100f, seconds = 1f, seed = 3 },
            };
            using (var session = new SimSession(NewController(), settings))
            {
                Assert.AreEqual(0, session.Trace.Frames);
                // A tenth of a second at 100 fps is ten frames, and half a frame's worth of
                // time is kept rather than rounded away.
                Assert.AreEqual(10, session.Advance(0.105f));
                Assert.AreEqual(10, session.Trace.Frames);
                Assert.AreEqual(0.1f, session.Time, 1e-4f);
                Assert.AreEqual(1, session.Advance(0.006f), "the leftover paid for the next one");

                // However long the editor was away, it never tries to catch up forever.
                Assert.AreEqual(SimSession.MaxCatchUp, session.Advance(60f));
            }
        }

        [Test]
        public void Session_TakesAPokeOnTheNextFrameItSteps()
        {
            using (var session = new SimSession(NewController(),
                       new SimSettings { clock = new SimClock { fps = 60f, seconds = 1f } }))
            {
                session.StepOnce();
                Assert.AreEqual(0f, session.Read(Simulation.LocalScope, "Go"));

                session.Write(Simulation.LocalScope, "Go", 1f);
                session.StepOnce();
                session.StepOnce();
                Assert.AreEqual(1f, session.Read(Simulation.LocalScope, "Go"));
                var state = session.Trace.Find(Simulation.LocalScope, "Base/state");
                Assert.AreEqual("On", state.TextAt(state.Frames - 1));
            }
        }

        [Test]
        public void Session_KeepsAWindowAndGoesOnCountingPastIt()
        {
            var settings = new SimSettings { clock = new SimClock { fps = 60f, seconds = 0.1f } };
            using (var session = new SimSession(NewController(), settings) { Window = 5 })
            {
                for (int i = 0; i < 20; i++) session.StepOnce();
                Assert.AreEqual(5, session.Trace.Frames, "the oldest frames are forgotten");
                foreach (var signal in session.Trace.Signals)
                    Assert.AreEqual(5, signal.Frames, "every row forgets the same ones");
                // Times count from the session's start, not from the start of what is kept.
                Assert.AreEqual(20f / 60f, session.Trace.TimeAt(4), 1e-4f);
            }
        }

        [Test]
        public void Session_AndARunAgreeOnWhatTheyRecord()
        {
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 0.2f, seed = 5 },
                wire = new SyncWire().Syncs("X"),
            };
            var batch = Simulation.Run(NewController(), settings);
            using (var session = new SimSession(NewController(), settings))
            {
                for (int i = 0; i < 12; i++) session.StepOnce();
                Assert.AreEqual(batch.Signals.Count, session.Trace.Signals.Count);
                for (int i = 0; i < batch.Signals.Count; i++)
                    Assert.AreEqual(batch.Signals[i].Path, session.Trace.Signals[i].Path);
            }
        }

        // ---- the remote view ------------------------------------------------

        [Test]
        public void Lag_SaysHowLongTheOtherPersonHasBeenLookingAtSomethingElse()
        {
            var wire = new SyncWire { intervalSeconds = 0.2f }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(1f, wire, stimulus));

            var lag = trace.Find(Simulation.LagScope, "X");
            Assert.IsNotNull(lag);
            Assert.AreEqual(SignalKind.Float, lag.kind);

            // Agreed at the start, behind from the moment the wearer moved, and agreed again
            // once the sample landed — the age of their copy, which is the remote view.
            Assert.AreEqual(0f, lag.At(0), 1e-4f);
            int worst = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
                if (lag.At(frame) > lag.At(worst)) worst = frame;
            Assert.Greater(lag.At(worst), 0.1f, "it fell behind while the sample was pending");
            Assert.Less(lag.At(worst), 0.25f, "and never by more than the wire's own cadence");
            Assert.AreEqual(0f, lag.At(trace.Frames - 1), 1e-4f);
        }

        [Test]
        public void Lag_KeepsClimbingForSomethingThatNeverTravels()
        {
            // "Go" is not on the wire, so the remote never agrees again after the wearer
            // moves — which reads as a line that only goes up.
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var trace = Simulation.Run(NewController(), Wired(1f, wire, stimulus));

            var lag = trace.Find(Simulation.LagScope, "Go");
            Assert.Greater(lag.At(trace.Frames - 1), 0.9f);
            for (int frame = 1; frame < trace.Frames; frame++)
                Assert.GreaterOrEqual(lag.At(frame), lag.At(frame - 1) - 1e-4f);
        }

        [Test]
        public void Lag_IsNotRecordedWithoutSomebodyToBeBehind()
        {
            var trace = Simulation.Run(NewController(), Clock(0.2f));
            foreach (var signal in trace.Signals)
                Assert.AreNotEqual(Simulation.LagScope, signal.scope);
        }

        // ---- the viewer's model side ----------------------------------------

        [Test]
        public void View_GroupsByScope_FiltersByPath_AndFitsTheRunAcrossTheWidth()
        {
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X");
            var view = new WaveformView
            {
                trace = Simulation.Run(NewController(), Wired(1f, wire)),
                movedOnly = false,
            };
            Assert.AreEqual(60, view.Frames);

            // A header per scope, and its rows under it.
            var scopes = new System.Collections.Generic.List<string>();
            foreach (var row in view.Visible())
                if (row.IsHeader) scopes.Add(row.scope);
            CollectionAssert.AreEqual(
                new[]
                {
                    Simulation.LocalScope, Simulation.RemoteScope,
                    Simulation.WireScope, Simulation.LagScope,
                },
                scopes);

            // Everything is recorded, and the filter is how a reader narrows it — which is why
            // a run does not need to be told in advance what it will be asked.
            view.filter = "Base/state";
            int rows = 0, headers = 0;
            foreach (var row in view.Visible())
                if (row.IsHeader) headers++; else rows++;
            Assert.AreEqual(2, rows, "one per client");
            Assert.AreEqual(2, headers, "and a header each; the wire and lag drop out");

            view.filter = string.Empty;
            view.Fit(800f);
            Assert.AreEqual(0, view.firstFrame);
            // The whole run across the plot, which is the width less the two name columns.
            Assert.AreEqual((800f - 288f) / 60f, view.pixelsPerFrame, 0.01f);
        }

        [Test]
        public void View_HidesWhatNeverMoved_ButStillCountsIt()
        {
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var view = new WaveformView
            {
                trace = Simulation.Run(NewController(), Clock(0.3f), stimulus),
            };

            // "N" and "X" are never touched in this run; "Go" and the layer are.
            var shown = new System.Collections.Generic.List<string>();
            int localCount = 0;
            foreach (var row in view.Visible())
                if (row.IsHeader) localCount = row.count;
                else shown.Add(row.signal.name);
            CollectionAssert.Contains(shown, "Go");
            CollectionAssert.Contains(shown, "Base/state");
            CollectionAssert.DoesNotContain(shown, "N");
            CollectionAssert.DoesNotContain(shown, "X");
            // The header still counts the quiet ones, so a reader can tell "not shown" from
            // "not there".
            Assert.Greater(localCount, shown.Count);

            view.movedOnly = false;
            int all = 0;
            foreach (var row in view.Visible()) if (!row.IsHeader) all++;
            Assert.AreEqual(localCount, all);
        }

        [Test]
        public void View_HasNothingToSayBeforeARun()
        {
            var view = new WaveformView();
            Assert.AreEqual(0, view.Frames);
            view.Fit(800f);
            Assert.AreEqual(0, view.firstFrame, "fitting nothing is not an error");
        }

        /// <summary>Records one more frame on every signal of a hand-built trace.</summary>
        static void Record(SignalTrace trace, SignalTrace.Signal signal, float value)
        {
            trace.Frame(trace.Frames / 60f, 1f / 60f);
            signal.Push(value);
        }

        /// <summary>
        /// A batch run hands the viewer a new trace each time, so measuring once per trace was
        /// enough. A LIVE session hands it the same one over and over as it grows, and the
        /// ranges froze at whatever the first repaint saw — every later value was then drawn
        /// against a scale from before it existed. Lag climbs for a whole session, so this was
        /// hit by every live run there has ever been.
        /// </summary>
        [Test]
        public void Ranges_FollowATraceThatGoesOnGrowing()
        {
            var trace = new SignalTrace();
            var lag = trace.Declare(Simulation.LagScope, "Go", SignalKind.Float);
            var ranges = new SignalRanges();

            for (int i = 0; i < 4; i++) Record(trace, lag, i);
            ranges.Update(trace);
            Assert.AreEqual(0f, ranges.Of(lag).x, 1e-4f);
            Assert.AreEqual(3f, ranges.Of(lag).y, 1e-4f);

            // The same trace, longer. This is the whole bug: nothing about the trace's identity
            // changed, and the top of the range has to move anyway.
            for (int i = 4; i < 40; i++) Record(trace, lag, i);
            ranges.Update(trace);
            Assert.AreEqual(0f, ranges.Of(lag).x, 1e-4f);
            Assert.AreEqual(39f, ranges.Of(lag).y, 1e-4f);

            // And once the session is long enough to start dropping its oldest frames, the
            // length stops growing while the run does not — so "how much is new" cannot be
            // read off the length.
            trace.Trim(8);
            for (int i = 40; i < 60; i++) Record(trace, lag, i);
            trace.Trim(8);
            ranges.Update(trace);
            Assert.AreEqual(59f, ranges.Of(lag).y, 1e-4f,
                "a trimmed session still grows; its range has to grow with it");
        }

        [Test]
        public void Ranges_AreMeasuredAfreshForEachRun_AndPadTheSignalsThatNeverMoved()
        {
            var trace = new SignalTrace();
            var flat = trace.Declare(Simulation.LocalScope, "N", SignalKind.Float);
            var flag = trace.Declare(Simulation.LocalScope, "Go", SignalKind.Bool);
            var ranges = new SignalRanges();
            for (int i = 0; i < 4; i++) { Record(trace, flat, 2f); flag.Push(0f); }
            ranges.Update(trace);

            // A row of one value would be a zero-height band to draw in.
            Assert.AreEqual(1.5f, ranges.Of(flat).x, 1e-4f);
            Assert.AreEqual(2.5f, ranges.Of(flat).y, 1e-4f);
            // A Bool is 0..1 whatever it happened to do, so an off-all-run row reads as off
            // rather than as the middle of nothing.
            Assert.AreEqual(0f, ranges.Of(flag).x, 1e-4f);
            Assert.AreEqual(1f, ranges.Of(flag).y, 1e-4f);

            // A second run is a second trace, and must not inherit the first one's scale.
            var second = new SignalTrace();
            var fresh = second.Declare(Simulation.LocalScope, "N", SignalKind.Float);
            for (int i = 0; i < 4; i++) Record(second, fresh, 100f + i);
            ranges.Update(second);
            Assert.AreEqual(100f, ranges.Of(fresh).x, 1e-4f);
            Assert.AreEqual(103f, ranges.Of(fresh).y, 1e-4f);
            Assert.AreEqual(new Vector2(0f, 1f), ranges.Of(flat),
                "a signal from the run before is not in this one's scale at all");
        }

        [Test]
        public void Client_HonoursPreventRepeats_OnARandomDriver()
        {
            var controller = NewController();
            var on = FindState(controller, "On");
            var driver = VrcParameterDriver.AddTo(on, "Test");
            VrcParameterDriver.SetLocalOnly(driver, false);
            // Two outcomes and a coin: without the option a run of these repeats itself
            // constantly, and with it a value never follows itself.
            VrcParameterDriver.AddRandomEntry(driver, "N", 0f, 1f, 1f, preventRepeats: true);

            using (var client = new SimClient(controller, "C", true, 11))
            {
                client.Write("Go", 1f);
                float previous = -1f;
                for (int i = 0; i < 20; i++)
                {
                    // Leave and re-enter, so the driver runs again.
                    client.Write("Go", 1f);
                    client.Step(1f / 60f);
                    float value = client.Read("N");
                    if (previous >= 0f && !Mathf.Approximately(value, previous))
                        Assert.AreNotEqual(previous, value);
                    previous = value;
                }
            }
        }

        [Test]
        public void Wire_HoldsTheOtherPersonBackUntilTheyArrive()
        {
            var wire = new SyncWire { intervalSeconds = 0.1f, remoteJoinsAt = 0.5f }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f).At(0f, "Go", true);
            var trace = Simulation.Run(NewController(), Wired(1f, wire, stimulus));

            var here = trace.Find(Simulation.WireScope, "remote here");
            var remote = trace.Find(Simulation.RemoteScope, "X");
            var state = trace.Find(Simulation.RemoteScope, "Base/state");

            int arrived = -1;
            for (int frame = 0; frame < trace.Frames && arrived < 0; frame++)
                if (here.At(frame) != 0f) arrived = frame;
            Assert.Greater(arrived, 0);
            Assert.AreEqual(0.5f, trace.TimeAt(arrived), 0.03f);

            // Before that they are not there: nothing crosses, and their copy is not running.
            for (int frame = 0; frame < arrived; frame++)
            {
                Assert.AreEqual(0f, remote.At(frame), "a value reached somebody who is absent");
                Assert.AreEqual("Idle", state.TextAt(frame), "their copy was running early");
            }

            // Arriving IS a delivery — they are handed the state at once rather than waiting
            // out an interval, which is why a late arrival decodes whatever it lands on.
            Assert.AreEqual(0.5f, remote.At(arrived), 0.01f);
            // Everything the wire carries, and nothing it does not: arriving is a delivery of
            // the synced set, not a copy of the wearer's whole animator.
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "Go").At(arrived));
            Assert.AreEqual(0f,
                trace.Find(Simulation.RemoteScope, "Go").At(trace.Frames - 1));
        }

        [Test]
        public void Wire_HandsNothingOverWhenEverybodyLoadedTogether()
        {
            // Joining at zero is the case where there is nothing yet to hand over, so the
            // first thing that crosses is still the first sample.
            var wire = new SyncWire { intervalSeconds = 0.2f }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(0.5f, wire, stimulus));

            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "X").At(0));
            Assert.AreEqual(1f, trace.Find(Simulation.WireScope, "remote here").At(0));
        }

        [Test]
        public void Lag_IsNotHeldAgainstSomebodyWhoIsNotThereYet()
        {
            var wire = new SyncWire { intervalSeconds = 0.1f, remoteJoinsAt = 0.4f }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(1f, wire, stimulus));

            var lag = trace.Find(Simulation.LagScope, "X");
            var here = trace.Find(Simulation.WireScope, "remote here");
            for (int frame = 0; frame < trace.Frames; frame++)
                if (here.At(frame) == 0f)
                    Assert.AreEqual(0f, lag.At(frame),
                        "counted an absence as being behind, at frame " + frame);
        }

        // ---- what a run does not promise ------------------------------------

        static bool Mentions(System.Collections.Generic.List<string> notes, string fragment)
        {
            foreach (var note in notes)
                if (note.Contains(fragment)) return true;
            return false;
        }

        [Test]
        public void Notes_AreEmptyForAControllerNothingDivergesOn()
        {
            CollectionAssert.IsEmpty(SimNotes.For(NewController()));
        }

        [Test]
        public void Notes_SayWhenALayerChoosesWhereToBeginWithACondition()
        {
            var controller = NewController();
            var machine = controller.layers[0].stateMachine;
            var entry = machine.AddEntryTransition(FindState(controller, "On"));

            // No conditions yet: an unconditional entry route goes where the default would.
            CollectionAssert.IsEmpty(SimNotes.For(controller));

            entry.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            var notes = SimNotes.For(controller);
            Assert.AreEqual(1, notes.Count);
            Assert.IsTrue(Mentions(notes, "Entry"), notes[0]);
            Assert.IsTrue(Mentions(notes, "Base"), "it names the layer");
        }

        [Test]
        public void Notes_SayWhenADriversStateCanBeEnteredFromItself()
        {
            var controller = NewController();
            var on = FindState(controller, "On");
            var driver = VrcParameterDriver.AddTo(on, "Test");
            VrcParameterDriver.AddSetEntry(driver, "N", 1f);
            CollectionAssert.IsEmpty(SimNotes.For(controller), "not yet — nothing re-enters it");

            var self = on.AddTransition(on);
            self.hasExitTime = true;
            self.exitTime = 1f;
            Assert.IsTrue(Mentions(SimNotes.For(controller), "re-entered"));
        }

        [Test]
        public void Notes_SayWhenADriverInOneLayerIsReadByAnother()
        {
            var controller = NewController();
            var driver = VrcParameterDriver.AddTo(FindState(controller, "On"), "Test");
            VrcParameterDriver.AddSetEntry(driver, "N", 1f);
            // Written in layer 0 and read nowhere else yet.
            CollectionAssert.IsEmpty(SimNotes.For(controller));

            controller.AddLayer("Other");
            var other = controller.layers[1].stateMachine;
            var a = other.AddState("A");
            var b = other.AddState("B");
            a.AddTransition(b).AddCondition(AnimatorConditionMode.Greater, 0f, "N");

            var notes = SimNotes.For(controller);
            Assert.IsTrue(Mentions(notes, "next frame"), string.Join(" / ", notes.ToArray()));
            Assert.IsTrue(Mentions(notes, "N"));
        }

        [Test]
        public void Notes_TellALayerWeightApartFromTheBehavioursThatChangeNothing()
        {
            var controller = NewController();
            var on = FindState(controller, "On");
            if (VrcBehaviours.Find(VrcBehaviours.LayerControl) == null)
                Assert.Ignore("needs the SDK's own behaviour types");

            on.AddStateMachineBehaviour(VrcBehaviours.Find(VrcBehaviours.LayerControl));
            var notes = SimNotes.For(controller);
            Assert.IsTrue(Mentions(notes, "weight"),
                "the one unrun behaviour that changes what a run records");

            on.AddStateMachineBehaviour(VrcBehaviours.Find(VrcBehaviours.TrackingControl));
            notes = SimNotes.For(controller);
            Assert.AreEqual(2, notes.Count, "and the rest, said separately");
            Assert.IsTrue(Mentions(notes, "nothing recorded depends on them"));
        }

        // ---- a run as a clip ------------------------------------------------

        [Test]
        public void Clip_CarriesTheRunBackOutOfItself()
        {
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "Go", true).At(0.05f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(0.5f, wire, stimulus));

            const string path = "Assets/DDTraceClipTest.anim";
            try
            {
                var clip = TraceClip.Save(trace, path);
                Assert.IsNotNull(clip);
                Assert.IsNotNull(TraceClip.ManifestOf(clip), "the signal list rides along");

                var reloaded = TraceClip.Load(clip);
                Assert.AreEqual(trace.Frames, reloaded.Frames);
                Assert.AreEqual(trace.Signals.Count, reloaded.Signals.Count);
                for (int i = 0; i < trace.Signals.Count; i++)
                {
                    var before = trace.Signals[i];
                    var after = reloaded.Signals[i];
                    Assert.AreEqual(before.scope, after.scope);
                    Assert.AreEqual(before.name, after.name);
                    Assert.AreEqual(before.kind, after.kind);
                    for (int frame = 0; frame < trace.Frames; frame += 5)
                        Assert.AreEqual(before.At(frame), after.At(frame), 1e-3f,
                            before.Path + " at " + frame);
                }
                // A state keeps its names, which is the one thing a curve cannot say.
                var state = reloaded.Find(Simulation.LocalScope, "Base/state");
                CollectionAssert.AreEqual(new[] { "Idle", "On" }, state.labels);
                Assert.AreEqual("On", state.TextAt(reloaded.Frames - 1));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void Clip_ReadsBackAsInput_AndDrivesTheSameRunAgain()
        {
            var first = Simulation.Run(NewController(), Clock(0.5f),
                new Stimulus().At(0.1f, "Go", true).At(0.2f, "N", 4f));

            const string path = "Assets/DDTraceInputTest.anim";
            try
            {
                var clip = TraceClip.Save(first, path);
                var stimulus = TraceClip.ToStimulus(clip, string.Empty,
                    new[] { "Go", "N", "X" });

                // One poke where the value started, and one at each change — not one per
                // frame, which would be a recording rather than a stimulus.
                Assert.Less(stimulus.entries.Count, 12, "far too many pokes");
                var again = Simulation.Run(NewController(), Clock(0.5f), stimulus);
                for (int frame = 0; frame < again.Frames; frame++)
                {
                    Assert.AreEqual(first.Find(Simulation.LocalScope, "Go").At(frame),
                        again.Find(Simulation.LocalScope, "Go").At(frame), "Go at " + frame);
                    Assert.AreEqual(first.Find(Simulation.LocalScope, "N").At(frame),
                        again.Find(Simulation.LocalScope, "N").At(frame), "N at " + frame);
                }
                Assert.AreEqual("On",
                    again.Find(Simulation.LocalScope, "Base/state").TextAt(again.Frames - 1));
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
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
