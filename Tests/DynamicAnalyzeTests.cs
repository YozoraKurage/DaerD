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

        // ---- what VRChat syncs whether or not the avatar asked ---------------

        /// <summary>The base controller, plus built-ins it happens to read.</summary>
        static AnimatorController Reading(params string[] builtIns)
        {
            var controller = NewController();
            foreach (var name in builtIns)
            {
                Assert.IsTrue(VrcParameters.TryFind(name, out var definition),
                    "'" + name + "' is meant to be a built-in");
                controller.AddParameter(name,
                    definition.type == VrcParameters.ParamType.Bool
                        ? AnimatorControllerParameterType.Bool
                        : definition.type == VrcParameters.ParamType.Int
                            ? AnimatorControllerParameterType.Int
                            : AnimatorControllerParameterType.Float);
            }
            return controller;
        }

        [Test]
        public void BuiltIns_ReachTheOtherPersonWithoutAnybodySyncingThem()
        {
            // GestureLeft is in no store and on no wire, and it still arrives: VRChat carries
            // its own parameters. Nearly every FX controller is built on these, so a run that
            // waited for them to be listed showed a remote whose hand never moved.
            // Rounding is another test's business; this one is about when things arrive.
            var wire = new SyncWire { intervalSeconds = 0.2f, quantize = false }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "GestureLeft", 1f).At(0.05f, "X", 0.5f);
            var trace = Simulation.Run(Reading("GestureLeft"), Wired(0.5f, wire, stimulus));

            var here = trace.Find(Simulation.LocalScope, "GestureLeft");
            var there = trace.Find(Simulation.RemoteScope, "GestureLeft");
            Assert.AreEqual(1f, there.At(trace.Frames - 1), "it has to get there at all");

            // And not on the sample's cadence: the expression parameter beside it waits for the
            // next sample, and this does not.
            int moved = FirstFrameAt(here, 1f), arrived = FirstFrameAt(there, 1f);
            Assert.LessOrEqual(arrived - moved, 1,
                "a built-in is a continuous stream, not a passenger on the sample");
            Assert.Greater(FirstFrameAt(trace.Find(Simulation.RemoteScope, "X"), 0.5f), arrived,
                "the synced parameter poked in the same breath still waits its turn");
        }

        [Test]
        public void BuiltIns_ThatAreNotTheWearersToSend_StayWhereTheyAre()
        {
            // Two that must not ride along. AvatarVersion never leaves a client; IsOnFriendsList
            // answers whether the wearer is on YOUR friends list, so the wearer's own copy of it
            // is not an answer anybody else wants.
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X");
            var stimulus = new Stimulus()
                .At(0.05f, "AvatarVersion", 3f)
                .At(0.05f, "IsOnFriendsList", 1f);
            var trace = Simulation.Run(
                Reading("AvatarVersion", "IsOnFriendsList", "IsLocal"),
                Wired(0.5f, wire, stimulus));
            int last = trace.Frames - 1;

            Assert.AreEqual(3f, trace.Find(Simulation.LocalScope, "AvatarVersion").At(last));
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "AvatarVersion").At(last));
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "IsOnFriendsList").At(last));

            // The one this would break loudest: IsLocal is each client's own answer, and a wire
            // that carried it would tell the other person they are wearing the avatar.
            Assert.AreEqual(1f, trace.Find(Simulation.LocalScope, "IsLocal").At(last));
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "IsLocal").At(last));
        }

        [Test]
        public void ABuiltInNamedInTheStoreIsStillThePlatformsToSync()
        {
            // Putting a built-in in the expression parameters is a mistake people make, and
            // honouring it would round VelocityZ into the -1..1 the expression channel allows —
            // inventing a bug no headset has, in the values most likely to be outside it.
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("VelocityZ");
            var stimulus = new Stimulus().At(0.05f, "VelocityZ", 3.5f);
            var trace = Simulation.Run(Reading("VelocityZ"), Wired(0.5f, wire, stimulus));

            Assert.AreEqual(3.5f, trace.Find(Simulation.RemoteScope, "VelocityZ")
                .At(trace.Frames - 1), 1e-5f, "carried by the platform, so not rounded like a sample");
        }

        [Test]
        public void ASessionCarriesTheBuiltInsTheSameWayARunDoes()
        {
            var settings = Wired(1f, new SyncWire { intervalSeconds = 0.2f }.Syncs("X"));
            using (var session = new SimSession(Reading("GestureLeft"), settings))
            {
                session.StepOnce();
                session.Write(Simulation.LocalScope, "GestureLeft", 2f);
                session.StepOnce();
                Assert.AreEqual(2f, session.Read(Simulation.RemoteScope, "GestureLeft"),
                    "live and batch are the same simulation or neither is worth reading");
            }
        }

        [Test]
        public void Notes_SayWhereTheLocomotionNumbersAreNotMeantToAgree()
        {
            var controller = Reading("VelocityX", "AngularY");
            Assert.IsTrue(Says(SimNotes.For(controller), "VelocityX"),
                "playspace movement counts on their copy and not on yours");
            Assert.IsFalse(Says(SimNotes.For(controller, withRemote: false), "VelocityX"),
                "a divergence about the other person is not worth saying without one");
            Assert.IsFalse(Says(SimNotes.For(Reading("GestureLeft")), "GestureLeft"),
                "a built-in this run does carry faithfully is not a divergence");
        }

        static bool Says(System.Collections.Generic.List<string> notes, string word)
        {
            foreach (var note in notes)
                if (note.Contains(word)) return true;
            return false;
        }

        /// <summary>The first frame this signal reads the given value, or -1.</summary>
        static int FirstFrameAt(SignalTrace.Signal signal, float value)
        {
            for (int frame = 0; frame < signal.Frames; frame++)
                if (Mathf.Approximately(signal.At(frame), value)) return frame;
            return -1;
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
            // The whole run across the plot: the width less the two name columns, and less the
            // gutter the row list's scrollbar is always kept out of.
            Assert.AreEqual((800f - 288f - 16f) / 60f, view.pixelsPerFrame, 0.01f);
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
            int localCount = 0, localShown = 0;
            foreach (var row in view.Visible())
                if (row.IsHeader) { localCount = row.count; localShown = row.shown; }
                else shown.Add(row.signal.name);
            CollectionAssert.Contains(shown, "Go");
            CollectionAssert.Contains(shown, "Base/state");
            CollectionAssert.DoesNotContain(shown, "N");
            CollectionAssert.DoesNotContain(shown, "X");
            // The header still counts the quiet ones, so a reader can tell "not shown" from
            // "not there" — and says how many of them are actually on screen.
            Assert.Greater(localCount, shown.Count);
            Assert.AreEqual(shown.Count, localShown);

            view.movedOnly = false;
            int all = 0;
            foreach (var row in view.Visible()) if (!row.IsHeader) all++;
            Assert.AreEqual(localCount, all);
        }

        [Test]
        public void View_NeverHidesARowItOffersToPoke()
        {
            // Nothing in this run ever moves — the exact state a fresh live session starts
            // in. The value cells are the only controls there are, so a moved-only rule that
            // hid quiet rows hid the way to make anything move along with them.
            var view = new WaveformView
            {
                trace = Simulation.Run(NewController(), Clock(0.3f)),
                editable = signal => signal.kind != SignalKind.State,
            };

            var shown = new System.Collections.Generic.List<string>();
            int count = 0, listed = 0;
            foreach (var row in view.Visible())
                if (row.IsHeader) { count = row.count; listed = row.shown; }
                else shown.Add(row.signal.name);
            CollectionAssert.Contains(shown, "Go");
            CollectionAssert.Contains(shown, "N");
            // The state band is not editable and never moved, so it alone stays hidden — and
            // the header's pair is what says it is hidden rather than missing.
            CollectionAssert.DoesNotContain(shown, "Base/state");
            Assert.AreEqual(shown.Count, listed);
            Assert.Greater(count, listed);
        }

        /// <summary>Where a named row sits in the list, or -1. The number IS what the reader
        /// clicks on, which is why these tests compare it rather than the contents.</summary>
        static int RowAt(System.Collections.Generic.List<WaveformView.Row> rows, string name)
        {
            for (int i = 0; i < rows.Count; i++)
                if (!rows[i].IsHeader && rows[i].signal.name == name) return i;
            return -1;
        }

        [Test]
        public void View_HoldsTheRowListStill_WhileTheReaderIsTouchingIt()
        {
            // A live session grows the same trace, and the moved-only rule lets a row that has
            // just started moving into the middle of the list. Every editable cell below it
            // then moves down a row — between the frame the reader saw and the frame their
            // click is processed, which is how a value ends up typed into another signal.
            using (var session = new SimSession(NewController(), new SimSettings { clock = Clock(1f) }))
            {
                var view = new WaveformView
                {
                    trace = session.Trace,
                    editable = signal => signal.kind != SignalKind.State,
                };
                session.StepOnce();
                view.Invalidate();
                int cell = RowAt(view.Visible(true), "Base/transition");
                Assert.GreaterOrEqual(cell, 0, "an editable row is listed even while quiet");
                Assert.AreEqual(-1, RowAt(view.Visible(true), "Base/state"),
                    "the state row has not moved yet, so it is not listed yet");

                session.Write(Simulation.LocalScope, "Go", 1f);
                for (int i = 0; i < 4; i++) session.StepOnce();
                view.Invalidate();

                // Touched: the list keeps the shape the reader is pointing at.
                Assert.AreEqual(cell, RowAt(view.Visible(false), "Base/transition"));
                Assert.AreEqual(-1, RowAt(view.Visible(false), "Base/state"));
                // Let go: the row that earned its place takes it, and the cell moves down one.
                Assert.AreEqual(cell, RowAt(view.Visible(true), "Base/state"));
                Assert.AreEqual(cell + 1, RowAt(view.Visible(true), "Base/transition"));

                // The hold is against the run's own doing, not against the reader's. Typing in
                // the filter is a text field being edited — the very state the hold watches
                // for — and it is still answered, or the search box would do nothing until the
                // pointer left the list.
                view.filter = "Go";
                var filtered = view.Visible(false);
                Assert.GreaterOrEqual(RowAt(filtered, "Go"), 0);
                Assert.AreEqual(-1, RowAt(filtered, "Base/transition"));
            }
        }

        [Test]
        public void View_ZoomsAboutThePointer_AndStaysInsideItsLimits()
        {
            // 120 frames, and a plot as wide as a real window's.
            var view = new WaveformView { trace = Simulation.Run(NewController(), Clock(2f)) };
            var plot = new Rect(288f, 0f, 512f, 200f);
            view.FitPlot(plot.width);
            Assert.AreEqual(0, view.firstFrame);
            Assert.AreEqual(plot.width / 120f, view.pixelsPerFrame, 0.01f);

            // Whatever the pointer was over stays under it — the whole of what makes a wheel
            // usable on a long run.
            float pointer = plot.x + 300f;
            int under = view.FrameAtX(plot, pointer);
            view.ZoomAt(plot, pointer, 4f);
            Assert.AreEqual(4f * plot.width / 120f, view.pixelsPerFrame, 0.01f);
            Assert.LessOrEqual(Mathf.Abs(under - view.FrameAtX(plot, pointer)), 1,
                "the moment under the pointer, give or take the pixel it rounds to");

            view.ZoomAt(plot, pointer, 10000f);
            Assert.AreEqual(WaveformView.MaxZoom, view.pixelsPerFrame, 0.001f);
            view.ZoomAt(plot, pointer, 0.00001f);
            Assert.AreEqual(WaveformView.MinZoom, view.pixelsPerFrame, 0.001f);
        }

        [Test]
        public void View_PansInWholeFrames_ButKeepsTheFractionItHasTravelled()
        {
            var view = new WaveformView
            {
                trace = Simulation.Run(NewController(), Clock(2f)),
                pixelsPerFrame = 4f,
            };
            // Four pixels to the frame: three one-pixel drags are not a frame, and the fourth
            // is. Dropping the remainder each time would leave the run stuck under a slow hand.
            view.PanBy(1f);
            view.PanBy(1f);
            view.PanBy(1f);
            Assert.AreEqual(0, view.firstFrame);
            view.PanBy(1f);
            Assert.AreEqual(1, view.firstFrame);

            // And it stops at both ends of the run rather than travelling off it.
            view.PanBy(-10000f);
            Assert.AreEqual(0, view.firstFrame);
            view.PanBy(10000f);
            Assert.AreEqual(view.Frames - 1, view.firstFrame);
        }

        /// <summary>
        /// The waveform draws its labels with GUI.Label, never EditorGUI.LabelField.
        ///
        /// The two look interchangeable and are not: EditorGUI's takes a control id, even for a
        /// label, and the viewer draws a row's bands and range numbers on the repaint and on no
        /// other pass. Every id allocated after those would then differ between the pass that
        /// drew the row and the pass that carries the click — and IMGUI decides which field is
        /// being typed into by id, not by position, so the caret lands in a row further up or
        /// in a label, where it reads as the click having been ignored.
        ///
        /// Source-scanned rather than reasoned about, in the same spirit as the colour rule:
        /// this is a mistake that compiles, runs, and looks right until somebody clicks.
        /// </summary>
        [Test]
        public void TheWaveformTakesNoControlIdForALabel()
        {
            string folder = System.IO.Path.Combine(SourceRoot(), "DynamicAnalyze");
            Assert.IsTrue(System.IO.Directory.Exists(folder), "could not find the module's sources");

            var offenders = new System.Collections.Generic.List<string>();
            int scanned = 0;
            foreach (var file in System.IO.Directory.GetFiles(folder, "*.cs"))
            {
                scanned++;
                var lines = System.IO.File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    // EditorGUILayout's is a different call and a different problem: it is laid
                    // out, so it runs on every pass.
                    if (lines[i].Contains("EditorGUI.LabelField("))
                        offenders.Add(System.IO.Path.GetFileName(file) + ":" + (i + 1)
                            + "  " + lines[i].Trim());
            }

            Assert.Greater(scanned, 5, "found almost no sources — the scan is broken, not the code");
            Assert.IsEmpty(offenders,
                "these are labels, and EditorGUI's takes a control id for one:\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>The module's own folder, found through an asset DaerD owns — the tests run
        /// from a package path that is not the project's. Same trick as DaerDColorsTests.</summary>
        static string SourceRoot()
        {
            var anchor = ScriptableObject.CreateInstance<LocalizationAnchor>();
            var script = MonoScript.FromScriptableObject(anchor);
            string path = AssetDatabase.GetAssetPath(script);
            Object.DestroyImmediate(anchor);
            Assert.IsNotEmpty(path, "could not locate DaerD's own sources");
            // <package>/Editor/Localization/LocalizationAnchor.cs -> <package>/Editor
            return System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), ".."));
        }

        [Test]
        public void MayReshape_SaysNoWhileTheListIsBeingTouched()
        {
            var rows = new Rect(0f, 0f, 200f, 100f);
            var over = new Vector2(10f, 50f);
            var away = new Vector2(400f, 50f);

            Assert.IsTrue(WaveformView.MayReshape(true, 0, false, rows, away));
            // The pointer is over the list: a row appearing under it moves what is being aimed at.
            Assert.IsFalse(WaveformView.MayReshape(true, 0, false, rows, over));
            // Something is being dragged, and a drag is a control id held across frames.
            Assert.IsFalse(WaveformView.MayReshape(true, 17, false, rows, away));
            // A value is being typed: the field it is going into must not become another one.
            Assert.IsFalse(WaveformView.MayReshape(true, 0, true, rows, away));
            // Outside a GUI there is nobody to disturb — a test, or a headless rebuild.
            Assert.IsTrue(WaveformView.MayReshape(false, 17, true, rows, over));
        }

        [Test]
        public void View_MeasuresBetweenItsTwoCursors_AndPutsTheMarkDownAndBackUp()
        {
            var view = new WaveformView { trace = Simulation.Run(NewController(), Clock(1f)) };
            Assert.IsFalse(view.HasMark);
            Assert.AreEqual(0f, view.Span(), 1e-6f, "one cursor has nothing to measure to");

            view.cursorFrame = 48;
            view.Mark(12);
            Assert.IsTrue(view.HasMark);
            // Thirty-six frames of a sixtieth of a second each — the answer a reader wants is
            // the duration, and the frame numbers are only how it was pointed at.
            Assert.AreEqual(36f / 60f, view.Span(), 1e-3f);

            // Marked after the cursor rather than before it, which is the same measurement.
            view.Mark(56);
            Assert.AreEqual(8f / 60f, view.Span(), 1e-3f);

            // The same frame again picks it up.
            view.Mark(56);
            Assert.IsFalse(view.HasMark);
            Assert.AreEqual(0f, view.Span(), 1e-6f);
        }

        [Test]
        public void View_KeepsTheMarkInsideTheRunItIsShowing()
        {
            var view = new WaveformView { trace = Simulation.Run(NewController(), Clock(1f)) };
            view.cursorFrame = 50;
            view.Mark(55);

            // A shorter run under the same reader: a mark left pointing past the end would
            // measure to a moment that does not exist.
            view.trace = Simulation.Run(NewController(), Clock(0.2f));
            view.ClampCursors();
            Assert.AreEqual(view.Frames - 1, view.markFrame);
            Assert.AreEqual(view.Frames - 1, view.cursorFrame);
            Assert.IsTrue(view.HasMark);

            // And no run at all is nothing to measure between.
            view.trace = null;
            view.ClampCursors();
            Assert.IsFalse(view.HasMark);
            Assert.AreEqual(-1, view.markFrame);
        }

        [Test]
        public void Ghost_LinesUpTwoRunsByTime_NotByFrameNumber()
        {
            // Two runs of the same length whose frames are NOT the same lengths. By frame
            // number the second one drifts against the first from the first second on; by
            // time they are the same moments, which is what a comparison means.
            var stimulus = new Stimulus().At(0.25f, "Go", true);
            var settings = Wired(1f, new SyncWire { intervalSeconds = 0.1f }.Syncs("X"), stimulus);
            settings.clock.jitter = 0.4f;
            var jittery = Simulation.Run(NewController(), settings);

            var even = Simulation.Run(NewController(), Clock(1f), stimulus);
            Assert.AreNotEqual(jittery.TimeAt(30), even.TimeAt(30),
                "the two runs have to disagree about when frame 30 was, or this proves nothing");

            var cursor = new GhostCursor(jittery);
            bool everDiffered = false;
            for (int frame = 0; frame < even.Frames; frame++)
            {
                int at = cursor.At(even.TimeAt(frame));
                // The incremental walk and the trace's own search are the same answer — the
                // point of the walk is only that it costs one pass along the ghost instead of
                // one search per column.
                Assert.AreEqual(jittery.FrameAt(even.TimeAt(frame)), at, "column " + frame);
                if (at != frame) everDiffered = true;
            }
            Assert.IsTrue(everDiffered,
                "aligned by time, so the column and the ghost frame under it are not the same "
                + "number — if they always were, this would be a frame-number overlay");

            // Backwards is not a walk back: a row is drawn left to right, and the cursor is
            // only ever asked about a later moment than the last one.
            Assert.AreEqual(jittery.Frames - 1, cursor.At(0f));
        }

        [Test]
        public void Ghost_AddsNoRowsOfItsOwn_AndSharesTheRowsScale()
        {
            var stimulus = new Stimulus().At(0.05f, "X", 1f);
            var view = new WaveformView
            {
                trace = Simulation.Run(NewController(), Clock(0.5f), stimulus),
            };
            int before = view.Visible().Count;
            var mine = view.trace.Find(Simulation.LocalScope, "X");
            view.Measure();
            Assert.AreEqual(1f, view.RangeOf(mine).y, 1e-4f);

            // A second run that took the same parameter much further, and that has a signal
            // the first one has not got at all. A ghost is another reading of the same things,
            // so it must not put a row of its own on the list.
            var other = new SignalTrace();
            var theirs = other.Declare(Simulation.LocalScope, "X", SignalKind.Float);
            var extra = other.Declare(Simulation.LocalScope, "OnlyOverThere", SignalKind.Float);
            for (int i = 0; i < 30; i++) { Record(other, theirs, i * 0.5f); extra.Push(0f); }
            view.ghost = other;
            view.Invalidate();
            view.Measure();

            var names = new System.Collections.Generic.List<string>();
            foreach (var row in view.Visible())
                if (!row.IsHeader) names.Add(row.signal.name);
            Assert.AreEqual(before, view.Visible().Count);
            CollectionAssert.DoesNotContain(names, "OnlyOverThere");

            // One row, one scale. Drawing the two runs against ranges of their own would put
            // 1 and 14.5 at the same height and call them the same.
            Assert.AreEqual(14.5f, view.RangeOf(mine).y, 1e-4f);
            Assert.AreEqual(0f, view.RangeOf(mine).x, 1e-4f);
        }

        [Test]
        public void Lag_SummarySaysTheWorstAndWhose_AndGoesOnSayingItAsARunGrows()
        {
            var trace = new SignalTrace();
            var quiet = trace.Declare(Simulation.LagScope, "X", SignalKind.Float);
            var late = trace.Declare(Simulation.LagScope, "Go", SignalKind.Float);
            var summary = new LagSummary();

            Assert.IsFalse(summary.Known, "nothing measured is not an answer of zero");
            for (int i = 0; i < 5; i++) { Record(trace, quiet, 0f); late.Push(i * 0.01f); }
            summary.Update(trace);
            Assert.IsTrue(summary.Known);
            Assert.AreEqual("Go", summary.Parameter);
            Assert.AreEqual(0.04f, summary.Worst, 1e-4f);

            // The same trace, longer — a live session hands the viewer the same one over and
            // over, and a summary that only measured it once would freeze at the first repaint.
            for (int i = 0; i < 20; i++) { Record(trace, quiet, 0f); late.Push(0.5f); }
            summary.Update(trace);
            Assert.AreEqual(0.5f, summary.Worst, 1e-4f);

            // And a trimmed session still grows, so "what is new" cannot be read off the length.
            trace.Trim(4);
            for (int i = 0; i < 6; i++) { Record(trace, quiet, 2f); late.Push(0.5f); }
            trace.Trim(4);
            summary.Update(trace);
            Assert.AreEqual("X", summary.Parameter, "the worst moved to another parameter");
            Assert.AreEqual(2f, summary.Worst, 1e-4f);

            // A second run is a second trace and must not inherit the first one's worst.
            summary.Update(new SignalTrace());
            Assert.IsFalse(summary.Known);
            Assert.AreEqual(0f, summary.Worst, 1e-4f);
        }

        [Test]
        public void Lag_SummaryReadsARealRun_AndHasNothingToSayWithoutARemote()
        {
            var wire = new SyncWire { intervalSeconds = 0.2f }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(1f, wire, stimulus));

            var summary = new LagSummary();
            summary.Update(trace);
            Assert.IsTrue(summary.Known);
            // "Go" is never on the wire and never poked, so the parameter that fell furthest
            // behind is the one that travelled and was waited for.
            Assert.AreEqual("X", summary.Parameter);
            float worst = 0f;
            var lag = trace.Find(Simulation.LagScope, "X");
            for (int frame = 0; frame < trace.Frames; frame++)
                worst = Mathf.Max(worst, lag.At(frame));
            Assert.AreEqual(worst, summary.Worst, 1e-4f);

            // A one-client run has no Lag rows at all, and nothing to say about them.
            var alone = new LagSummary();
            alone.Update(Simulation.Run(NewController(), Clock(0.2f)));
            Assert.IsFalse(alone.Known);
        }

        [Test]
        public void Bands_TakeTheirColourFromTheStatesName()
        {
            // The same name is the same colour wherever it turns up, so two spans of one state
            // read as a repeat rather than as two different things.
            Assert.AreEqual(WaveformColors.BandFor("Idle"), WaveformColors.BandFor("Idle"));
            Assert.AreNotEqual(WaveformColors.BandFor("Idle"), WaveformColors.BandFor("On"));
            Assert.AreNotEqual(WaveformColors.BandFor("Idle"), WaveformColors.BandFor("Idle "));
            // A band with no name to hash falls back to the one colour every band used to be.
            Assert.AreEqual(WaveformColors.StateBand, WaveformColors.BandFor(string.Empty));

            // Translucent, because a band is the background a label sits on — and the label has
            // to be readable on either editor skin.
            var band = WaveformColors.BandFor("Idle");
            Assert.AreEqual(WaveformColors.StateBand.a, band.a, 1e-4f);
            float high = Mathf.Max(band.r, Mathf.Max(band.g, band.b));
            float low = Mathf.Min(band.r, Mathf.Min(band.g, band.b));
            Assert.Greater(high, 0.6f, "dark enough to swallow dark text");
            Assert.Less(high - low, 0.6f, "saturated enough to fight the text for attention");
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

        // ---- more than one other person -------------------------------------

        /// <summary>Two runs of the same experiment: alone with one other person, and with a
        /// second who walks in halfway. Everything about the first person has to be untouched by
        /// that — which is the compatibility promise (a single-remote trace is exactly what it
        /// always was) and the modelling promise (one person's wire is not another's) in one
        /// assertion.</summary>
        [Test]
        public void Remotes_LeaveTheFirstPersonsRunExactlyAsItWasAlone()
        {
            SyncWire Wire() => new SyncWire
            {
                intervalSeconds = 0.1f,
                dropChance = 0.3f,
                seed = 5,
            }.Syncs("X", "N");
            Stimulus Inputs() => new Stimulus().At(0f, "X", 0.5f).At(0.4f, "N", 3f)
                .At(0.7f, "X", -0.25f);

            var alone = Simulation.Run(NewController(), Wired(1.2f, Wire(), Inputs()));
            // 0.55 s is deliberately off the wire's own beat, so their arrival is a delivery
            // nothing else on the wire was going to make that frame.
            var crowd = Simulation.Run(NewController(),
                Wired(1.2f, Wire().Joining(0.55f), Inputs()));

            foreach (var signal in alone.Signals)
            {
                var same = crowd.Find(signal.scope, signal.name);
                Assert.IsNotNull(same, signal.Path + " went missing when somebody else arrived");
                // Every row but the send: the wearer reads its values once for everybody, and
                // handing the new arrival the state IS a delivery, so that one row is allowed
                // to say so. It is checked below instead.
                if (signal.Path == "Wire/sample") continue;
                for (int frame = 0; frame < alone.Frames; frame++)
                    Assert.AreEqual(signal.At(frame), same.At(frame), 1e-6f,
                        signal.Path + " moved at " + alone.TimeAt(frame) + "s because somebody"
                        + " else turned up");
            }

            var sent = alone.Find(Simulation.WireScope, "sample");
            var alsoSent = crowd.Find(Simulation.WireScope, "sample");
            var arrival = crowd.Find(Simulation.WireScope, "remote here 2");
            int extra = 0;
            for (int frame = 0; frame < alone.Frames; frame++)
            {
                if (sent.At(frame) != 0f)
                    Assert.AreEqual(1f, alsoSent.At(frame), "a send went missing");
                if (sent.At(frame) == alsoSent.At(frame)) continue;
                extra++;
                Assert.IsTrue(arrival.ChangedAt(frame),
                    "the wire sent at " + crowd.TimeAt(frame) + "s for no reason");
            }
            Assert.AreEqual(1, extra, "exactly one extra delivery: the second person arriving");
        }

        [Test]
        public void Remotes_ArriveOnTheirOwnTimeAndLoseTheirOwnSamples()
        {
            var wire = new SyncWire
            {
                intervalSeconds = 0.1f,
                dropChance = 0.5f,
                seed = 3,
            }.Syncs("X").Joining(0.5f);
            var trace = Simulation.Run(NewController(),
                Wired(1.5f, wire, new Stimulus().At(0f, "X", 0.5f)));

            var second = Simulation.RemoteScopeAt(1);
            Assert.AreEqual("Remote 2", second, "the second person is named after the first");
            Assert.IsNotNull(trace.Find(second, "X"));
            Assert.IsNotNull(trace.Find(Simulation.LagScopeAt(1), "X"),
                "each person has lag rows of their own");

            var here = trace.Find(Simulation.WireScope, "remote here");
            var alsoHere = trace.Find(Simulation.WireScope, "remote here 2");
            var theirs = trace.Find(second, "X");
            for (int frame = 0; frame < trace.Frames; frame++)
            {
                Assert.AreEqual(1f, here.At(frame), "the first person was there all along");
                if (trace.TimeAt(frame) < 0.5f)
                {
                    Assert.AreEqual(0f, alsoHere.At(frame));
                    Assert.AreEqual(0f, theirs.At(frame),
                        "a value reached somebody who was not in the instance");
                }
            }
            Assert.AreEqual(1f, alsoHere.At(trace.Frames - 1));
            Assert.AreEqual(0.5f, theirs.At(trace.Frames - 1), 0.01f,
                "the late arrival never caught up");

            // Half the samples are lost, and the two of them lose different ones: a stream each,
            // so one person's bad connection is not everybody's.
            var lost = trace.Find(Simulation.WireScope, "lost");
            var alsoLost = trace.Find(Simulation.WireScope, "lost 2");
            bool apart = false;
            for (int frame = 0; frame < trace.Frames && !apart; frame++)
                if (alsoHere.At(frame) != 0f && lost.At(frame) != alsoLost.At(frame))
                    apart = true;
            Assert.IsTrue(apart, "both remotes lost exactly the same samples — one stream, not two");
        }

        [Test]
        public void Session_RunsEverybodyTheWayARunDoes()
        {
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 0.5f, seed = 5 },
                wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X").Joining(0.2f),
            };
            settings.stimulus.At(0f, "X", 0.5f);
            var batch = Simulation.Run(NewController(), settings);
            using (var session = new SimSession(NewController(), settings))
            {
                // A session has hands rather than a list, so the same input is made by hand
                // before the first frame — which is where the run's own timed one lands.
                session.Write(Simulation.LocalScope, "X", 0.5f);
                for (int i = 0; i < 30; i++) session.StepOnce();
                Assert.AreEqual(batch.Signals.Count, session.Trace.Signals.Count);
                foreach (var signal in batch.Signals)
                {
                    var live = session.Trace.Find(signal.scope, signal.name);
                    Assert.IsNotNull(live, signal.Path);
                    for (int frame = 0; frame < batch.Frames; frame++)
                        Assert.AreEqual(signal.At(frame), live.At(frame), 1e-6f,
                            signal.Path + " at frame " + frame);
                }
            }
        }

        // ---- triggers -------------------------------------------------------

        /// <summary>Idle and On, and one Trigger that swaps them — something to press and
        /// something that visibly answers.</summary>
        static AnimatorController Pushbutton()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Bang", AnimatorControllerParameterType.Trigger);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;
            foreach (var pair in new[] { (from: idle, to: on), (from: on, to: idle) })
            {
                var transition = pair.from.AddTransition(pair.to);
                transition.hasExitTime = false;
                transition.hasFixedDuration = true;
                transition.duration = 0f;
                transition.AddCondition(AnimatorConditionMode.If, 0f, "Bang");
            }
            return controller;
        }

        static int Pulses(SignalTrace.Signal signal)
        {
            int count = 0;
            for (int frame = 0; frame < signal.Frames; frame++)
                if (signal.At(frame) != 0f) count++;
            return count;
        }

        [Test]
        public void Trigger_IsAPulseTheRunCanSee_AndTakesItsTransition()
        {
            var stimulus = new Stimulus().At(0.05f, "Bang", 1f);
            var trace = Simulation.Run(Pushbutton(), Clock(0.3f), stimulus);

            var bang = trace.Find(Simulation.LocalScope, "Bang");
            // Its own kind, so a viewer knows to offer a button rather than a checkbox.
            Assert.AreEqual(SignalKind.Trigger, bang.kind);

            // Mecanim takes a trigger down in the same frame the transition consumes it, so the
            // press would leave no mark at all if a run recorded only what was left of it.
            Assert.AreEqual(1, Pulses(bang), "a press is one frame up and then down again");
            int fired = -1;
            for (int frame = 0; frame < trace.Frames && fired < 0; frame++)
                if (bang.At(frame) != 0f) fired = frame;
            Assert.AreEqual(3, fired, "0.05 s at 60 fps is the fourth frame");

            var state = trace.Find(Simulation.LocalScope, "Base/state");
            Assert.AreEqual("Idle", state.TextAt(fired - 1));
            Assert.AreEqual("On", state.TextAt(trace.Frames - 1), "the press did nothing");
        }

        [Test]
        public void Trigger_FiresOncePerPress_SoTwoPressesAreTwoTransitions()
        {
            var stimulus = new Stimulus().At(0.05f, "Bang", 1f).At(0.15f, "Bang", 1f);
            var trace = Simulation.Run(Pushbutton(), Clock(0.4f), stimulus);

            var bang = trace.Find(Simulation.LocalScope, "Bang");
            Assert.AreEqual(2, Pulses(bang));

            // There and back: one press each way, and no press left standing to take a third.
            var state = trace.Find(Simulation.LocalScope, "Base/state");
            int changes = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
                if (state.ChangedAt(frame)) changes++;
            Assert.AreEqual(2, changes, "two presses, two transitions");
            Assert.AreEqual("Idle", state.TextAt(trace.Frames - 1));
        }

        [Test]
        public void Trigger_PokedLive_IsUpForOneFrameAndDownTheNext()
        {
            var settings = new SimSettings { clock = new SimClock { fps = 60f, seconds = 1f } };
            using (var session = new SimSession(Pushbutton(), settings))
            {
                session.StepOnce();
                var bang = session.Trace.Find(Simulation.LocalScope, "Bang");
                Assert.AreEqual(0f, bang.At(bang.Frames - 1));

                // What the value cell's button does: set it, and let the next frame answer.
                session.Write(Simulation.LocalScope, "Bang", 1f);
                session.StepOnce();
                Assert.AreEqual(1f, bang.At(bang.Frames - 1), "the press was never visible");
                var state = session.Trace.Find(Simulation.LocalScope, "Base/state");
                Assert.AreEqual("On", state.TextAt(state.Frames - 1));

                session.StepOnce();
                Assert.AreEqual(0f, bang.At(bang.Frames - 1),
                    "it stayed down after being consumed — a trigger nobody can let go of");
                Assert.AreEqual("On", state.TextAt(state.Frames - 1), "it fired twice");
            }
        }

        /// <summary>A trigger nothing consumes stays standing, and a run says so for as long as
        /// it does. The pulse is what Mecanim did with it, not a shape imposed on it.</summary>
        [Test]
        public void Trigger_NothingConsumes_StaysUpUntilItIsCleared()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Bang", AnimatorControllerParameterType.Trigger);
            controller.layers[0].stateMachine.AddState("Idle");

            var stimulus = new Stimulus().At(0.05f, "Bang", 1f).At(0.15f, "Bang", 0f);
            var trace = Simulation.Run(controller, Clock(0.3f), stimulus);
            var bang = trace.Find(Simulation.LocalScope, "Bang");

            Assert.AreEqual(0f, bang.At(2));
            Assert.AreEqual(1f, bang.At(3), "nothing was there to take it");
            Assert.AreEqual(1f, bang.At(8));
            Assert.AreEqual(0f, bang.At(9), "a poke of zero is the way to put it back down");
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
