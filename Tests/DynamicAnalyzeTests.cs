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

        // ---- which transition -----------------------------------------------

        /// <summary>A transition that takes a fifth of a second on one condition. Long enough
        /// to be caught in flight: one that finishes inside the frame it starts on leaves no
        /// frame for a row to name it on.</summary>
        static AnimatorStateTransition Blend(AnimatorStateTransition transition, string condition)
        {
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.2f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, condition);
            return transition;
        }

        /// <summary>Idle with two ways out of it, so a run has something to tell apart. With
        /// <paramref name="anyState"/> the same two destinations are reached from Any State
        /// instead, which Mecanim reports differently.</summary>
        static AnimatorController NewBranchingController(bool anyState = false)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Other", AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var a = machine.AddState("A");
            var b = machine.AddState("B");
            machine.defaultState = idle;

            if (!anyState)
            {
                Blend(idle.AddTransition(a), "Go");
                Blend(idle.AddTransition(b), "Other");
                return controller;
            }
            Blend(machine.AddAnyStateTransition(a), "Go").canTransitionToSelf = false;
            Blend(machine.AddAnyStateTransition(b), "Other").canTransitionToSelf = false;
            return controller;
        }

        /// <summary>Steps until the layer is mid-blend and hands back what Mecanim calls the
        /// transition it is in. Fails rather than returning nothing, because a settled layer
        /// read as if it were blending would satisfy every assertion for the wrong reason.</summary>
        static AnimatorTransitionInfo Blending(AnimatorRig rig, int layer = 0)
        {
            for (int frame = 0; frame < 30; frame++)
            {
                rig.Step();
                if (rig.InTransition(layer)) return rig.Transition(layer);
            }
            Assert.Fail("nothing was blending after 30 frames");
            return default(AnimatorTransitionInfo);
        }

        static void Settle(AnimatorRig rig, int layer = 0)
        {
            for (int frame = 0; frame < 60 && rig.InTransition(layer); frame++) rig.Step();
        }

        /// <summary>The one transition a run was seen in, by the name its row gave it.</summary>
        static string ViaName(SignalTrace trace)
        {
            var via = trace.Find(Simulation.LocalScope, "Base/via");
            Assert.IsNotNull(via);
            var names = new System.Collections.Generic.List<string>();
            for (int frame = 0; frame < trace.Frames; frame++)
            {
                string name = via.TextAt(frame);
                if (name != "—" && !names.Contains(name)) names.Add(name);
            }
            Assert.AreEqual(1, names.Count,
                "this run goes through exactly one transition; it named " + names.Count);
            return names[0];
        }

        [Test]
        public void Mecanim_SpellsATransitionAsTheFullPathsOfItsTwoEnds()
        {
            // Measured rather than assumed: SimClient turns a controller into a table of
            // hashes and has nothing but this spelling to look them up by. A Unity that
            // changed its mind would leave every via row saying "—", and this is the test that
            // would say why.
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Down", AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var a = machine.AddState("A");
            machine.defaultState = idle;
            var deep = machine.AddStateMachine("Sub").AddState("Deep");
            Blend(idle.AddTransition(a), "Go");
            Blend(a.AddTransition(deep), "Down");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("Go", true);
                var info = Blending(rig);
                Assert.AreEqual(Animator.StringToHash("Base.Idle -> Base.A"), info.fullPathHash,
                    "both ends by their full paths, with \" -> \" between them");
                // The short names travel too, in their own field, and are exactly what cannot
                // be used: two sub-machines with an "Idle" apiece would share one.
                Assert.AreEqual(Animator.StringToHash("Idle -> A"), info.nameHash);
                Assert.AreEqual(0, info.userNameHash, "nobody named this transition");

                Settle(rig);
                rig.Set("Down", true);
                Assert.AreEqual(Animator.StringToHash("Base.A -> Base.Sub.Deep"),
                    Blending(rig).fullPathHash, "a sub-machine is a part of the path like any other");
            }
        }

        [Test]
        public void Mecanim_CallsAnAnyStatesSourceEntry_AndBothWaysOutOfAMachineExit()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            foreach (var name in new[] { "Go", "Into", "Out" })
                controller.AddParameter(name, AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var a = machine.AddState("A");
            machine.defaultState = idle;
            var sub = machine.AddStateMachine("Sub");
            sub.defaultState = sub.AddState("Deep");
            Blend(machine.AddAnyStateTransition(a), "Go").canTransitionToSelf = false;
            Blend(a.AddTransition(sub), "Into");
            Blend(a.AddExitTransition(), "Out");

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("Go", true);
                var info = Blending(rig);
                Assert.IsTrue(info.anyState);
                Assert.AreEqual(Animator.StringToHash("Entry -> Base.A"), info.fullPathHash,
                    "an any-state transition's source is spelt \"Entry\", of all things");

                Settle(rig);
                rig.Set("Go", false).Set("Into", true);
                Assert.AreEqual(Animator.StringToHash("Base.A -> Exit"),
                    Blending(rig).fullPathHash,
                    "a destination that is a sub-machine is spelt \"Exit\"");
            }

            using (var rig = new AnimatorRig(controller))
            {
                rig.Set("Go", true);
                Blending(rig);
                Settle(rig);
                rig.Set("Go", false).Set("Out", true);
                // The same hash as the transition into the sub-machine above, which is why a
                // state that has both of them can be named for neither.
                Assert.AreEqual(Animator.StringToHash("Base.A -> Exit"),
                    Blending(rig).fullPathHash, "and so is an Exit transition");
            }
        }

        [Test]
        public void Via_NamesTheTransitionThatIsFiring()
        {
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var trace = Simulation.Run(NewBranchingController(), Clock(0.5f), stimulus);

            var via = trace.Find(Simulation.LocalScope, "Base/via");
            Assert.IsNotNull(via, "a layer gets one of these the way it gets a state row");
            Assert.AreEqual(SignalKind.State, via.kind);
            CollectionAssert.AreEqual(new[] { "Idle → A", "Idle → B" }, via.labels,
                "both ways out of Idle, in the order the layer authors them");

            // Never a name without a blend, and — in a controller with nothing ambiguous in it
            // — never a blend without a name.
            var moving = trace.Find(Simulation.LocalScope, "Base/transition");
            int named = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
            {
                if (moving.At(frame) == 0f)
                {
                    Assert.AreEqual("—", via.TextAt(frame), "settled at frame " + frame);
                    continue;
                }
                Assert.AreEqual("Idle → A", via.TextAt(frame), "blending at frame " + frame);
                named++;
            }
            Assert.Greater(named, 1, "a fifth of a second is more than one frame of it");
            Assert.AreEqual("—", via.TextAt(0), "nothing has been asked for yet");
            Assert.AreEqual("—", via.TextAt(trace.Frames - 1), "and it arrived long ago");
        }

        [Test]
        public void Via_NamesAnAnyStateTransitionByWhereItGoes()
        {
            var trace = Simulation.Run(NewBranchingController(anyState: true), Clock(0.5f),
                new Stimulus().At(0.05f, "Go", true));

            // Which state it left is not part of it: an any-state transition can be taken from
            // anywhere, and naming it after wherever the layer happened to be would make one
            // transition look like a different one each time it fired.
            Assert.AreEqual("Any State → A", ViaName(trace));
        }

        [Test]
        public void Via_TellsTwoRoutesOutOfOneStateApart()
        {
            // The point of the row: both runs end in a state the layer could have reached two
            // ways, and the trace says which one it took.
            Assert.AreEqual("Idle → A", ViaName(Simulation.Run(NewBranchingController(),
                Clock(0.5f), new Stimulus().At(0.05f, "Go", true))));
            Assert.AreEqual("Idle → B", ViaName(Simulation.Run(NewBranchingController(),
                Clock(0.5f), new Stimulus().At(0.05f, "Other", true))));
        }

        [Test]
        public void Via_NamesNothingWhereMecanimNamesTwoTransitionsAlike()
        {
            // A state with a way into a sub-machine and a way out of the machine has two
            // transitions under one hash — see the pinning test above. The row says "—" for
            // both rather than naming whichever was authored first: a run that named the wrong
            // transition would be worse than one that named none.
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            foreach (var name in new[] { "Go", "Into", "Out" })
                controller.AddParameter(name, AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;
            var sub = machine.AddStateMachine("Sub");
            sub.defaultState = sub.AddState("Deep");
            Blend(idle.AddTransition(on), "Go");
            Blend(on.AddTransition(sub), "Into");
            Blend(on.AddExitTransition(), "Out");

            var trace = Simulation.Run(controller, Clock(1f),
                new Stimulus().At(0.05f, "Go", true).At(0.4f, "Into", true));

            var via = trace.Find(Simulation.LocalScope, "Base/via");
            CollectionAssert.AreEqual(new[] { "Idle → On" }, via.labels,
                "the one transition of this layer that has a name to itself");

            var moving = trace.Find(Simulation.LocalScope, "Base/transition");
            int nameless = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
                if (moving.At(frame) != 0f && via.TextAt(frame) == "—") nameless++;
            Assert.Greater(nameless, 1, "the second transition ran and stayed unnamed");
            Assert.AreEqual("Sub.Deep", trace.Find(Simulation.LocalScope, "Base/state")
                .TextAt(trace.Frames - 1), "it did go into the sub-machine");
        }

        [Test]
        public void Via_IsAddedBesideTheRowsThatWereThereBefore()
        {
            var trace = Simulation.Run(NewController(), Clock(0.2f),
                new Stimulus().At(0.05f, "Go", true));

            // A layer's rows, in this order. The two that were there before this one keep
            // their names, their places and their values, because a saved run and a ghost
            // comparison find a row by its name. A claim about the first three and not about
            // how many there are: rows added since go on the end, and the newest wave's own
            // test is where the whole list is pinned.
            var names = new System.Collections.Generic.List<string>();
            foreach (var signal in trace.Signals)
                if (signal.scope == Simulation.LocalScope
                    && signal.name.StartsWith("Base/", System.StringComparison.Ordinal))
                    names.Add(signal.name);
            Assert.GreaterOrEqual(names.Count, 3, "the layer lost rows it had");
            CollectionAssert.AreEqual(new[] { "Base/state", "Base/transition", "Base/via" },
                names.GetRange(0, 3));

            var state = trace.Find(Simulation.LocalScope, "Base/state");
            CollectionAssert.AreEqual(new[] { "Idle", "On" }, state.labels);
            Assert.AreEqual("Idle", state.TextAt(0));
            Assert.AreEqual("On", state.TextAt(trace.Frames - 1));

            // This controller blends in no time at all, so its via row has little or nothing to
            // say — and says nothing on every frame the layer was settled on.
            var via = trace.Find(Simulation.LocalScope, "Base/via");
            var moving = trace.Find(Simulation.LocalScope, "Base/transition");
            for (int frame = 0; frame < trace.Frames; frame++)
                if (moving.At(frame) == 0f)
                    Assert.AreEqual("—", via.TextAt(frame), "settled at frame " + frame);
        }

        // ---- a layer's weight -----------------------------------------------

        /// <summary>A clip that writes a value onto an animator parameter — an AAP, which is
        /// the one kind of write a layer's weight can scale.</summary>
        static AnimationClip AapClip(string parameter, float value)
        {
            var clip = new AnimationClip { name = parameter + " = " + value };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), parameter),
                new AnimationCurve(new Keyframe(0f, value)));
            return clip;
        }

        /// <summary>A base layer that writes X (or nothing at all) and a layer over it whose
        /// only state writes X = 1 through an AAP. The upper layer's name is the caller's,
        /// because a layer may be called something with a '/' in it.</summary>
        static AnimatorController AapLayers(float? baseValue, string over = "Over")
        {
            var controller = new AnimatorController();
            controller.AddParameter("X", AnimatorControllerParameterType.Float);
            controller.AddLayer("Base");
            controller.AddLayer(over);
            var layers = controller.layers;
            layers[0].defaultWeight = 1f;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;

            var bottom = controller.layers[0].stateMachine.AddState("Bottom");
            bottom.writeDefaultValues = true;
            if (baseValue.HasValue) bottom.motion = AapClip("X", baseValue.Value);
            var top = controller.layers[1].stateMachine.AddState("Top");
            top.writeDefaultValues = true;
            top.motion = AapClip("X", 1f);
            return controller;
        }

        static SimSession LiveSession(AnimatorController controller) =>
            new SimSession(controller,
                new SimSettings { clock = new SimClock { fps = 60f, seconds = 1f } });

        static void StepSession(SimSession session, int frames)
        {
            for (int i = 0; i < frames; i++) session.StepOnce();
        }

        static float LastAt(SimSession session, string scope, string name)
        {
            var signal = session.Trace.Find(scope, name);
            Assert.IsNotNull(signal, "no row '" + name + "' under " + scope);
            return signal.At(signal.Frames - 1);
        }

        [Test]
        public void Weight_ScalesWhatAnAnimatedParameterWrites()
        {
            // Measured, and the reason the row is worth having: a weight is not decoration,
            // it is the scale on every AAP value in the trace.
            using (var session = LiveSession(AapLayers(null)))
            {
                StepSession(session, 4);
                Assert.AreEqual(1f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f,
                    "the whole clip at full weight");

                session.Write(Simulation.LocalScope, "Over/weight", 0.5f);
                StepSession(session, 4);
                Assert.AreEqual(0.5f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f,
                    "half the weight, half the value");

                session.Write(Simulation.LocalScope, "Over/weight", 0f);
                StepSession(session, 4);
                Assert.AreEqual(0f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f);
            }

            // Over a base that writes 0.2 the layer blends towards THAT rather than towards
            // zero: 0.2 + 0.5 × (1 − 0.2). A weight scales the layer's contribution, which is
            // not the same thing as scaling the number it writes.
            using (var session = LiveSession(AapLayers(0.2f)))
            {
                session.Write(Simulation.LocalScope, "Over/weight", 0.5f);
                StepSession(session, 4);
                Assert.AreEqual(0.6f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f);
            }
        }

        [Test]
        public void Weight_RowShowsTheNewValue_FromTheFrameAfterItWasSet()
        {
            using (var session = LiveSession(AapLayers(null)))
            {
                StepSession(session, 2);
                var weight = session.Trace.Find(Simulation.LocalScope, "Over/weight");
                Assert.IsNotNull(weight);
                Assert.AreEqual(SignalKind.Float, weight.kind);
                Assert.AreEqual(1f, weight.At(weight.Frames - 1), 1e-4f);

                int recorded = weight.Frames;
                session.Write(Simulation.LocalScope, "Over/weight", 0.25f);
                Assert.AreEqual(recorded, weight.Frames, "a poke records nothing by itself");
                Assert.AreEqual(1f, weight.At(recorded - 1), 1e-4f,
                    "and does not change the frame already written down");

                session.StepOnce();
                Assert.AreEqual(0.25f, weight.At(weight.Frames - 1), 1e-4f);
            }
        }

        [Test]
        public void Weight_OfTheBaseLayerIsPinnedAtOne_WhateverAnybodySets()
        {
            using (var session = LiveSession(AapLayers(0.2f)))
            {
                session.Write(Simulation.LocalScope, "Base/weight", 0f);
                session.Write(Simulation.LocalScope, "Over/weight", 0f);
                StepSession(session, 4);

                Assert.AreEqual(1f, LastAt(session, Simulation.LocalScope, "Base/weight"), 1e-4f,
                    "Mecanim answers 1 for layer 0 whatever anybody sets");
                Assert.AreEqual(0.2f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f,
                    "and runs it in full — the base layer's own AAP still writes");

                // Which is why the window offers no field for that one row.
                Assert.IsFalse(session.CanSetWeight(Simulation.LocalScope, "Base/weight"));
                Assert.IsTrue(session.CanSetWeight(Simulation.LocalScope, "Over/weight"));
                Assert.IsFalse(session.CanSetWeight(Simulation.LocalScope, "X"));
            }
        }

        [Test]
        public void Weight_IsClampedToTheRangeAnAvatarCanBeIn()
        {
            using (var session = LiveSession(AapLayers(null)))
            {
                // Mecanim would have kept the 1.5 and mixed the layer in past the value it was
                // blending towards; nothing on a headset can ask for that.
                session.Write(Simulation.LocalScope, "Over/weight", 1.5f);
                StepSession(session, 3);
                Assert.AreEqual(1f, LastAt(session, Simulation.LocalScope, "Over/weight"), 1e-4f);
                Assert.AreEqual(1f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f);

                session.Write(Simulation.LocalScope, "Over/weight", -0.5f);
                StepSession(session, 3);
                Assert.AreEqual(0f, LastAt(session, Simulation.LocalScope, "Over/weight"), 1e-4f);
                Assert.AreEqual(0f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f);
            }
        }

        [Test]
        public void Weight_FindsALayerWhoseOwnNameHasASlashInIt()
        {
            using (var session = LiveSession(AapLayers(null, "Face/Eyes")))
            {
                var weight = session.Trace.Find(Simulation.LocalScope, "Face/Eyes/weight");
                Assert.IsNotNull(weight, "the row is named after the whole layer");

                // The tail of a layer's name is not a layer, and taking the row apart at its
                // last '/' would have found one.
                session.Write(Simulation.LocalScope, "Eyes/weight", 0.5f);
                StepSession(session, 3);
                Assert.AreEqual(1f, weight.At(weight.Frames - 1), 1e-4f);
                Assert.IsFalse(session.CanSetWeight(Simulation.LocalScope, "Eyes/weight"));

                session.Write(Simulation.LocalScope, "Face/Eyes/weight", 0.5f);
                StepSession(session, 3);
                Assert.AreEqual(0.5f, weight.At(weight.Frames - 1), 1e-4f);
                Assert.AreEqual(0.5f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f);
            }
        }

        [Test]
        public void Weight_IsTurnedOnTheClientWhoseRowItIs()
        {
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 1f },
                wire = new SyncWire(),
            };
            using (var session = new SimSession(AapLayers(null), settings))
            {
                session.Write(Simulation.RemoteScope, "Over/weight", 0f);
                StepSession(session, 3);
                Assert.AreEqual(1f, LastAt(session, Simulation.LocalScope, "Over/weight"), 1e-4f,
                    "the wearer's copy was not the one asked");
                Assert.AreEqual(0f, LastAt(session, Simulation.RemoteScope, "Over/weight"), 1e-4f);
                Assert.AreEqual(1f, LastAt(session, Simulation.LocalScope, "X"), 1e-4f);
                Assert.AreEqual(0f, LastAt(session, Simulation.RemoteScope, "X"), 1e-4f,
                    "two copies of one avatar, showing different numbers for the same reason a "
                    + "headset would");
            }
        }

        [Test]
        public void Weight_IsAddedBesideTheRowsThatWereThereBefore()
        {
            var trace = Simulation.Run(NewController(), Clock(0.2f),
                new Stimulus().At(0.05f, "Go", true));

            var names = new System.Collections.Generic.List<string>();
            foreach (var signal in trace.Signals)
                if (signal.scope == Simulation.LocalScope
                    && signal.name.StartsWith("Base/", System.StringComparison.Ordinal))
                    names.Add(signal.name);
            CollectionAssert.AreEqual(
                new[] { "Base/state", "Base/transition", "Base/via", "Base/weight" }, names);

            // The three that were there before keep their names, their places and their values.
            var state = trace.Find(Simulation.LocalScope, "Base/state");
            CollectionAssert.AreEqual(new[] { "Idle", "On" }, state.labels);
            Assert.AreEqual("Idle", state.TextAt(0));
            Assert.AreEqual("On", state.TextAt(trace.Frames - 1));
            var via = trace.Find(Simulation.LocalScope, "Base/via");
            var moving = trace.Find(Simulation.LocalScope, "Base/transition");
            for (int frame = 0; frame < trace.Frames; frame++)
                if (moving.At(frame) == 0f)
                    Assert.AreEqual("—", via.TextAt(frame), "settled at frame " + frame);

            var weight = trace.Find(Simulation.LocalScope, "Base/weight");
            Assert.AreEqual(SignalKind.Float, weight.kind);
            Assert.IsNull(weight.labels, "a weight is a number, not a band of names");
            for (int frame = 0; frame < trace.Frames; frame++)
                Assert.AreEqual(1f, weight.At(frame), 1e-4f, "nothing in a batch run turns it");
            Assert.IsFalse(weight.Moved, "so the moved-only rule keeps it out of the way");
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

        /// <summary>The first frame this row held anything but zero, or -1.</summary>
        static int FirstMoved(SignalTrace.Signal signal)
        {
            for (int frame = 0; frame < signal.Frames; frame++)
                if (signal.At(frame) != 0f) return frame;
            return -1;
        }

        [Test]
        public void Wire_WithALatency_LandsASampleThatLongAfterItWentOut()
        {
            var wire = new SyncWire
            {
                intervalSeconds = 0.2f,
                latencySeconds = 0.3f,
                quantize = false,
            }.Syncs("X");
            var stimulus = new Stimulus().At(0f, "X", 0.5f);
            var trace = Simulation.Run(NewController(), Wired(1f, wire, stimulus));

            // The first sample goes at 0.2 s and lands at 0.5 s. Until then the other person
            // is looking at the value they started the run with, which is the whole point.
            int sent = FirstMoved(trace.Find(Simulation.WireScope, "sample"));
            int landed = FirstMoved(trace.Find(Simulation.RemoteScope, "X"));
            Assert.Greater(sent, 0, "no sample ever went");
            Assert.Greater(landed, 0, "the sample never landed");
            Assert.AreEqual(0.2f, trace.StartOfFrame(sent), 0.02f, "a sample went early");
            Assert.AreEqual(0.3f, trace.StartOfFrame(landed) - trace.StartOfFrame(sent), 0.02f,
                "the trip is the latency, to within the frame it lands on");
            Assert.AreEqual(0.5f, trace.Find(Simulation.RemoteScope, "X").At(landed), 1e-5f);

            // The wire row is still about the sending: it is one frame of "a sample went",
            // which is what every reading of it — the findings included — takes it for.
            Assert.AreEqual(0f, trace.Find(Simulation.WireScope, "sample").At(landed),
                "the landing was recorded as a send");
        }

        [Test]
        public void Wire_CarriesTheValueItRead_NotTheOneItLandsOn()
        {
            var wire = new SyncWire
            {
                intervalSeconds = 0.2f,
                latencySeconds = 0.25f,
                quantize = false,
            }.Syncs("X");
            var stimulus = new Stimulus()
                .At(0f, "X", 0.5f)        // what the sample at 0.2 s reads
                .At(0.25f, "X", -0.5f);   // moved while that sample is still travelling
            var trace = Simulation.Run(NewController(), Wired(0.8f, wire, stimulus));

            var remote = trace.Find(Simulation.RemoteScope, "X");
            int landed = FirstMoved(remote);
            Assert.Greater(landed, 0, "nothing landed");
            Assert.AreEqual(0.5f, remote.At(landed), 1e-5f,
                "a sample in flight went back for the newer value");
            // The wearer had moved on 0.2 s before that landed, and the other person goes on
            // holding what was read until the sample that read the new value lands in its own
            // turn — acting on a stale value is the failure a latency exists to show.
            Assert.AreEqual(-0.5f, trace.Find(Simulation.LocalScope, "X").At(landed), 1e-5f);
            int caught = -1;
            for (int frame = landed; frame < trace.Frames && caught < 0; frame++)
                if (remote.At(frame) < 0f) caught = frame;
            Assert.Greater(caught, landed, "the new value never landed");
            Assert.AreEqual(0.65f, trace.StartOfFrame(caught), 0.02f,
                "read at 0.4 s and landed 0.25 s later");
            for (int frame = landed; frame < caught; frame++)
                Assert.AreEqual(0.5f, remote.At(frame), 1e-5f,
                    "the other person stopped holding the value they were sent at frame " + frame);
        }

        /// <summary>Which of the wearer's samples a run lost, in order — a character per
        /// sample, so two runs whose frames do not line up can still be compared.</summary>
        static string Drops(SignalTrace trace)
        {
            var text = new System.Text.StringBuilder();
            var sample = trace.Find(Simulation.WireScope, "sample");
            var lost = trace.Find(Simulation.WireScope, "lost");
            for (int frame = 0; frame < trace.Frames; frame++)
                if (sample.At(frame) != 0f) text.Append(lost.At(frame) != 0f ? 'x' : '.');
            return text.ToString();
        }

        static string Losses(int clockSeed, int wireSeed)
        {
            var settings = new SimSettings
            {
                // Jittered, so the clock's seed genuinely changes the run: the frames are of
                // different lengths and the samples fall on different ones.
                clock = new SimClock { fps = 60f, seconds = 1.5f, jitter = 0.5f, seed = clockSeed },
                wire = new SyncWire
                {
                    intervalSeconds = 0.1f,
                    dropChance = 0.5f,
                    seed = wireSeed,
                }.Syncs("X"),
                stimulus = new Stimulus().At(0f, "X", 0.5f),
            };
            string drops = Drops(Simulation.Run(NewController(), settings));
            Assert.Greater(drops.Length, 8, "too few samples to say anything about the dice");
            return drops.Substring(0, 8);
        }

        [Test]
        public void Wire_KeepsItsOwnDice_WhateverTheClockIsSeededWith()
        {
            // The wire has always had a seed of its own; what it is for is this. Two runs whose
            // frames land in different places lose exactly the same samples, so the timing can
            // be asked a new question without the losses moving underneath the answer.
            string first = Losses(3, 8);
            Assert.AreEqual(first, Losses(99, 8),
                "the clock's seed reshuffled the wire's losses");
            Assert.IsTrue(first.Contains("x") && first.Contains("."),
                "a run that lost everything or nothing proves nothing about a seed");
            // And the other way round: it is the wire's own seed that moves them.
            Assert.AreNotEqual(first, Losses(3, 12), "the wire's seed changed nothing");
        }

        /// <summary>
        /// Everything a run put on the named rows, as the frames each one changed on and what
        /// it changed to. A string rather than a handful of asserts because what is being
        /// pinned down is the whole of a run: a wedge that only checks the parts somebody
        /// thought of is not a wedge.
        /// </summary>
        static string[] Fingerprint(SignalTrace trace, params string[] paths)
        {
            var lines = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                int slash = path.IndexOf('/');
                var signal = trace.Find(path.Substring(0, slash), path.Substring(slash + 1));
                Assert.IsNotNull(signal, path + " is not a row this run has");
                var text = new System.Text.StringBuilder(path).Append(':');
                for (int frame = 0; frame < signal.Frames; frame++)
                    if (signal.ChangedAt(frame))
                        text.Append(' ').Append(frame).Append('=').Append(
                            signal.At(frame).ToString("0.#####",
                                System.Globalization.CultureInfo.InvariantCulture));
                lines[i] = text.ToString();
            }
            return lines;
        }

        /// <summary>
        /// What the run in <see cref="Wire_WithNoLatency_RunsTheRunItRanBeforeThereWasAny"/>
        /// produced back when a wire could not be given a latency at all — read off the engine
        /// as it stood before the delivery queue existed, which is the only moment such a
        /// number can honestly be taken. A line per row rather than one block of text, so no
        /// argument about line endings can ever be mistaken for a change in the simulation.
        /// </summary>
        static readonly string[] WireWithoutLatency =
        {
            "Wire/sample: 8=1 9=0 14=1 15=0 20=1 21=0 22=1 23=0 25=1 26=0 32=1 33=0 38=1 39=0 45=1 46=0 51=1 52=0 57=1 58=0 63=1 64=0 69=1 70=0 75=1 76=0 81=1 82=0 86=1 87=0",
            "Wire/lost: 8=1 9=0 14=1 15=0 57=1 58=0",
            "Wire/lost 2: 25=1 26=0 32=1 33=0 57=1 58=0 81=1 82=0",
            "Wire/remote here 2: 22=1",
            "Remote/X: 20=-0.24706 63=0.74902 81=-1",
            "Remote/N: 45=7 69=200",
            "Remote 2/X: 22=-0.24706 63=0.74902 86=-1",
            "Remote 2/N: 45=7 69=200",
        };

        [Test]
        public void Wire_WithNoLatency_RunsTheRunItRanBeforeThereWasAny()
        {
            // Loss, jitter, rounding, two people and one of them late: every mechanism whose
            // order of drawing from the wire's dice a delivery queue could disturb.
            var wire = new SyncWire { intervalSeconds = 0.1f, dropChance = 0.25f, seed = 3 }
                .Syncs("X", "N").Joining(0.35f);
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 1.5f, jitter = 0.4f, seed = 11 },
                wire = wire,
                stimulus = new Stimulus()
                    .At(0.05f, "X", 0.5f).At(0.3f, "X", -0.25f)
                    .At(0.62f, "N", 7f).At(0.9f, "X", 0.75f)
                    .At(1.1f, "N", 200f).At(1.25f, "X", -1f),
            };
            Assert.AreEqual(0f, settings.wire.latencySeconds,
                "the wedge is about the default, so the default has to still be none");
            var trace = Simulation.Run(NewController(), settings);
            CollectionAssert.AreEqual(WireWithoutLatency, Fingerprint(trace,
                    "Wire/sample", "Wire/lost", "Wire/lost 2", "Wire/remote here 2",
                    "Remote/X", "Remote/N", "Remote 2/X", "Remote 2/N"),
                "a wire with no latency is not running the run it ran before latency existed");
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
        public void BuiltIns_OnTheIkChannel_ArriveOnItsOwnTenthOfASecond()
        {
            // GestureLeft is in no store and on no wire, and it still arrives: VRChat carries
            // its own parameters. Nearly every FX controller is built on these, so a run that
            // waited for them to be listed showed a remote whose hand never moved.
            //
            // But it is not instant either. The IK channel updates ten times a second, so the
            // other person's hand changes on a tick and not on the frame the wearer's did —
            // and a run that showed it following frame for frame was flattering the platform
            // in exactly the parameters a gesture layer is built on.
            var wire = new SyncWire { intervalSeconds = 0.2f, quantize = false }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "GestureLeft", 1f).At(0.05f, "X", 0.5f);
            var trace = Simulation.Run(Reading("GestureLeft"), Wired(0.5f, wire, stimulus));

            var here = trace.Find(Simulation.LocalScope, "GestureLeft");
            var there = trace.Find(Simulation.RemoteScope, "GestureLeft");
            Assert.AreEqual(1f, there.At(trace.Frames - 1), "it has to get there at all");

            int moved = FirstFrameAt(here, 1f), arrived = FirstFrameAt(there, 1f);
            Assert.Greater(arrived, moved, "the channel ticks; it does not follow");
            Assert.LessOrEqual(trace.TimeAt(arrived) - trace.TimeAt(moved), 0.1f + 1f / 60f,
                "and never waits longer than the next tick of a 10 Hz channel");

            // Still well ahead of the expression parameter poked in the same breath, which
            // waits for the wearer's own cadence.
            Assert.Greater(FirstFrameAt(trace.Find(Simulation.RemoteScope, "X"), 0.5f), arrived,
                "the synced parameter is on a slower channel and still waits its turn");
        }

        [Test]
        public void BuiltIns_OnTheIkChannel_AreInterpolatedIntoPlaceOnTheOtherCopy()
        {
            // A Float on the IK channel is interpolated by the receiving client, so ten updates
            // a second look like motion rather than like a staircase. The wearer jumps to 1 on
            // the first frame; the other person's copy climbs to it over the interval.
            var wire = new SyncWire { intervalSeconds = 0.5f }.Syncs("X");   // never samples here
            var stimulus = new Stimulus().At(0f, "VelocityX", 1f);
            var trace = Simulation.Run(Reading("VelocityX"), Wired(0.4f, wire, stimulus));
            var there = trace.Find(Simulation.RemoteScope, "VelocityX");

            int moving = FirstMove(there);
            Assert.Greater(moving, 0, "it has to start moving at all");
            Assert.Less(there.At(moving), 1f, "and not snap: it is on its way, not arrived");

            int settled = -1;
            for (int frame = moving; frame < trace.Frames && settled < 0; frame++)
                if (there.At(frame) >= 1f - 1e-4f) settled = frame;
            Assert.Greater(settled, moving + 2, "it climbed rather than arrived");
            Assert.Less(trace.TimeAt(settled) - trace.TimeAt(moving), 0.12f,
                "and got there inside the interval it was given");
            for (int frame = moving; frame <= settled; frame++)
                Assert.GreaterOrEqual(there.At(frame), there.At(frame - 1) - 1e-5f,
                    "a straight line does not wander on its way");
        }

        [Test]
        public void BuiltIns_OnThePlayableChannel_RideTheSample_AndAreNotRoundedLikeOne()
        {
            // VRChat puts these on the same channel as an expression parameter, so they arrive
            // in the same delivery — and they are outside the avatar's bit budget, so the eight
            // bits that budget pays for are not charged to them.
            var wire = new SyncWire { intervalSeconds = 0.2f }.Syncs("X");
            var stimulus = new Stimulus()
                .At(0.05f, "GestureLeftWeight", 0.3f).At(0.05f, "X", 0.3f);
            var trace = Simulation.Run(Reading("GestureLeftWeight"), Wired(0.5f, wire, stimulus));

            var weight = trace.Find(Simulation.RemoteScope, "GestureLeftWeight");
            var expression = trace.Find(Simulation.RemoteScope, "X");
            Assert.AreEqual(FirstMove(expression), FirstMove(weight),
                "one channel, one sample, one frame");
            Assert.AreEqual(0.3f, weight.At(trace.Frames - 1), 1e-6f,
                "an avatar was never charged a bit for this, so it is not rounded to one");
            Assert.AreNotEqual(0.3f, expression.At(trace.Frames - 1),
                "unlike the expression parameter beside it, which is");

            // And it misses when the sample misses. A whole sample is lost or none of it is,
            // and a built-in riding in one is not a special case of that.
            var lossy = new SyncWire { intervalSeconds = 0.1f, dropChance = 1f }.Syncs("X");
            var lost = Simulation.Run(Reading("GestureLeftWeight"), Wired(0.5f, lossy,
                new Stimulus().At(0.05f, "GestureLeftWeight", 0.3f)));
            Assert.AreEqual(0f,
                lost.Find(Simulation.RemoteScope, "GestureLeftWeight").At(lost.Frames - 1),
                "a lost sample takes its playable built-ins with it");
        }

        [Test]
        public void BuiltIns_OnTheSpeechChannel_FollowTheWearerFrameForFrame()
        {
            // Nothing sends a viseme. Both clients compute it from audio that is crossing
            // anyway, so the two copies move together and no cadence comes into it.
            var wire = new SyncWire { intervalSeconds = 0.5f }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "Viseme", 4f);
            var trace = Simulation.Run(Reading("Viseme"), Wired(0.4f, wire, stimulus));

            Assert.AreEqual(FirstFrameAt(trace.Find(Simulation.LocalScope, "Viseme"), 4f),
                FirstFrameAt(trace.Find(Simulation.RemoteScope, "Viseme"), 4f),
                "the viseme is the shadow of the voice, and the voice is not on this wire");
        }

        [Test]
        public void BuiltIns_AvatarVersion_TravelsWithThePose()
        {
            // It used to be filed as never leaving the client, which is what an older
            // third-party table says. VRChat's own list puts it on the IK channel.
            var wire = new SyncWire { intervalSeconds = 0.5f }.Syncs("X");
            var stimulus = new Stimulus().At(0.05f, "AvatarVersion", 3f);
            var trace = Simulation.Run(Reading("AvatarVersion"), Wired(0.5f, wire, stimulus));

            Assert.AreEqual(3f,
                trace.Find(Simulation.RemoteScope, "AvatarVersion").At(trace.Frames - 1),
                "a controller branching on the avatar version branched only on the wearer");
        }

        [Test]
        public void BuiltIns_ThatAreNotTheWearersToSend_StayWhereTheyAre()
        {
            // The two shapes that must never cross, whatever channel exists. PreviewMode never
            // leaves a client at all; IsOnFriendsList answers whether the wearer is on YOUR
            // friends list, so the wearer's own copy of it is not an answer anybody else wants.
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X");
            var stimulus = new Stimulus()
                .At(0.05f, "PreviewMode", 3f)
                .At(0.05f, "IsOnFriendsList", 1f);
            var trace = Simulation.Run(
                Reading("PreviewMode", "IsOnFriendsList", "IsLocal"),
                Wired(0.5f, wire, stimulus));
            int last = trace.Frames - 1;

            Assert.AreEqual(3f, trace.Find(Simulation.LocalScope, "PreviewMode").At(last));
            Assert.AreEqual(0f, trace.Find(Simulation.RemoteScope, "PreviewMode").At(last));
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
                .At(trace.Frames - 1), 1e-4f, "carried by the platform, so not rounded like a sample");
        }

        [Test]
        public void BuiltIns_ChangeNothingInARunWithNobodyToSendTo()
        {
            // One client is an Animator question, not a VRChat one: there are no channels
            // because there is nobody on the other end of them, and the wearer's own values
            // are its own whatever VRChat would have done with them.
            var settings = new SimSettings
            {
                clock = Clock(0.3f),
                stimulus = new Stimulus()
                    .At(0.05f, "GestureLeft", 1f).At(0.05f, "VelocityX", 0.5f),
            };
            var trace = Simulation.Run(Reading("GestureLeft", "VelocityX"), settings);

            foreach (var signal in trace.Signals)
                Assert.AreEqual(Simulation.LocalScope, signal.scope, signal.Path);
            Assert.AreEqual(1f,
                trace.Find(Simulation.LocalScope, "GestureLeft").At(trace.Frames - 1));
            Assert.AreEqual(0.5f,
                trace.Find(Simulation.LocalScope, "VelocityX").At(trace.Frames - 1), 1e-6f,
                "and not interpolated towards itself");
        }

        [Test]
        public void ASessionCarriesTheBuiltInsTheSameWayARunDoes()
        {
            // Not "does it get there" but "do the two engines agree frame for frame". Each
            // channel now has a schedule, a queue or an interpolation of its own, and a session
            // that kept a second copy of any of them would drift inside a tenth of a second —
            // which is exactly the interval every answer about a gesture is measured in.
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 0.5f, seed = 5 },
                wire = new SyncWire { intervalSeconds = 0.2f, latencySeconds = 0.05f }
                    .Syncs("X"),
            };
            settings.stimulus
                .At(0f, "GestureLeft", 2f).At(0f, "VelocityX", 0.75f)
                .At(0f, "GestureLeftWeight", 0.4f).At(0f, "Viseme", 5f);
            string[] builtIns = { "GestureLeft", "VelocityX", "GestureLeftWeight", "Viseme" };
            var batch = Simulation.Run(Reading(builtIns), settings);

            using (var session = new SimSession(Reading(builtIns), settings))
            {
                // A session has hands rather than a list — see Session_RunsEverybodyTheWayARunDoes.
                session.Write(Simulation.LocalScope, "GestureLeft", 2f);
                session.Write(Simulation.LocalScope, "VelocityX", 0.75f);
                session.Write(Simulation.LocalScope, "GestureLeftWeight", 0.4f);
                session.Write(Simulation.LocalScope, "Viseme", 5f);
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

        /// <summary>The first frame this signal is anything but where it started, or -1. What
        /// "something reached the other person" looks like when the value it is on its way to
        /// is not the point.</summary>
        static int FirstMove(SignalTrace.Signal signal)
        {
            for (int frame = 0; frame < signal.Frames; frame++)
                if (signal.At(frame) != signal.At(0)) return frame;
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

        // ---- the row the reader picked out -----------------------------------

        /// <summary>A run of one Float with two edges in it — flat, up at frame 10, half at
        /// frame 30 — so what a jump should land on is arithmetic rather than something the
        /// test has to go looking for.</summary>
        static SignalTrace Stepped(out SignalTrace.Signal signal)
        {
            var trace = new SignalTrace();
            signal = trace.Declare(Simulation.LocalScope, "X", SignalKind.Float);
            for (int frame = 0; frame < 60; frame++)
                Record(trace, signal, frame < 10 ? 0f : frame < 30 ? 1f : 0.5f);
            return trace;
        }

        [Test]
        public void View_PicksOutOneRow_AndStillHasItAfterTheRunIsRunAgain()
        {
            var stimulus = new Stimulus().At(0.05f, "Go", true);
            var view = new WaveformView { trace = Simulation.Run(NewController(), Clock(0.5f), stimulus) };
            Assert.IsNull(view.Selected, "nothing is picked out until something is");

            var go = view.trace.Find(Simulation.LocalScope, "Go");
            view.Select(go);
            Assert.AreSame(go, view.Selected);
            Assert.IsTrue(view.IsSelected(go));
            Assert.IsFalse(view.IsSelected(view.trace.Find(Simulation.RemoteScope, "Go")),
                "the other person's copy of a name is another row");

            // The same settings again is a NEW trace of new signals. The selection is held as
            // scope and name for exactly this: a reader who re-runs to see what one changed
            // setting did is the reader least willing to lose the row they were watching, and
            // a held Signal would either vanish here or go on answering out of the old run.
            view.trace = Simulation.Run(NewController(), Clock(0.5f), stimulus);
            Assert.IsNotNull(view.Selected);
            Assert.AreNotSame(go, view.Selected, "that one belongs to the run that is gone");
            Assert.AreEqual("Go", view.SelectedName);

            view.Select(null);
            Assert.IsNull(view.Selected);
            Assert.IsFalse(view.IsSelected(go));
        }

        [Test]
        public void View_JumpsToTheSelectedRowsChanges_AndStopsAtTheEnds()
        {
            var view = new WaveformView { trace = Stepped(out var signal) };
            view.Select(signal);

            view.cursorFrame = 0;
            Assert.IsTrue(view.StepToChange(1));
            Assert.AreEqual(10, view.cursorFrame);
            Assert.IsTrue(view.StepToChange(1));
            Assert.AreEqual(30, view.cursorFrame);
            // Nothing past the last edge: the cursor stays where it is rather than travelling
            // to a frame that is not a change at all.
            Assert.IsFalse(view.StepToChange(1));
            Assert.AreEqual(30, view.cursorFrame);

            Assert.IsTrue(view.StepToChange(-1));
            Assert.AreEqual(10, view.cursorFrame);
            // Frame 0 is not an edge — a change is a difference from the frame before it, and
            // the first frame has none.
            Assert.IsFalse(view.StepToChange(-1));
            Assert.AreEqual(10, view.cursorFrame);
        }

        [Test]
        public void View_JumpsNowhereWithoutARowToJumpAlong()
        {
            var view = new WaveformView { trace = Stepped(out var signal) };
            view.cursorFrame = 5;
            Assert.IsFalse(view.StepToChange(1), "no row is picked out");
            Assert.AreEqual(5, view.cursorFrame);

            // A row picked out in another run is not a row in this one either.
            var elsewhere = new SignalTrace();
            view.Select(elsewhere.Declare(Simulation.RemoteScope, "Nowhere", SignalKind.Float));
            Assert.IsFalse(view.StepToChange(1));
            Assert.AreEqual(5, view.cursorFrame);

            view.Select(signal);
            Assert.IsTrue(view.StepToChange(1));
            Assert.AreEqual(10, view.cursorFrame);
        }

        [Test]
        public void View_SubtractsTheSelectedRowsValueBetweenTheTwoCursors()
        {
            var view = new WaveformView { trace = Stepped(out var signal) };
            view.Select(signal);
            view.cursorFrame = 40;
            Assert.IsNull(view.ValueDeltaText(), "one cursor has nothing to subtract from");
            Assert.AreEqual(0f, view.ValueDelta(), 1e-6f);

            view.Mark(15);
            // Signed, and cursor minus mark: which way it went is half of the question, and
            // the mark is where the reader was measuring from.
            Assert.AreEqual(-0.5f, view.ValueDelta(), 1e-6f);
            Assert.AreEqual("-0.5", view.ValueDeltaText());

            view.cursorFrame = 15;
            view.Mark(40);
            Assert.AreEqual(0.5f, view.ValueDelta(), 1e-6f);
            Assert.AreEqual("+0.5", view.ValueDeltaText(), "a rise says so");
        }

        [Test]
        public void View_SubtractsNothingOffARowWhoseValuesAreNotQuantities()
        {
            var trace = new SignalTrace();
            var count = trace.Declare(Simulation.LocalScope, "N", SignalKind.Int);
            var toggle = trace.Declare(Simulation.LocalScope, "Go", SignalKind.Bool);
            var state = trace.Declare(Simulation.LocalScope, "Base/state", SignalKind.State,
                new[] { "Idle", "On" });
            for (int frame = 0; frame < 20; frame++)
            {
                trace.Frame(frame / 60f, 1f / 60f);
                count.Push(frame < 10 ? 3f : 7f);
                toggle.Push(frame < 10 ? 0f : 1f);
                state.Push(frame < 10 ? 0f : 1f);
            }
            var view = new WaveformView { trace = trace, cursorFrame = 19 };
            view.Mark(0);

            view.Select(count);
            Assert.AreEqual("+4", view.ValueDeltaText(), "an Int row counts in whole numbers");

            // A Bool or a Trigger differing by one is what "it changed" already says, and the
            // difference between two state indices is arithmetic on names.
            view.Select(toggle);
            Assert.IsNull(view.ValueDeltaText());
            view.Select(state);
            Assert.IsNull(view.ValueDeltaText());
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
        public void Ghost_TakenFromTheRunInHandIsASnapshotOfIt()
        {
            // What the Clip menu's "Compare With This Run" does, and the whole of why it can:
            // a batch run REPLACES the trace rather than growing it, so a reference kept
            // before the next Run goes on being the run it was taken from.
            var view = new WaveformView
            {
                trace = Simulation.Run(NewController(), Clock(0.5f),
                    new Stimulus().At(0.05f, "X", 1f)),
            };
            var before = view.trace;
            view.ghost = view.trace;

            view.trace = Simulation.Run(NewController(), Clock(0.5f),
                new Stimulus().At(0.05f, "X", 4f));
            Assert.AreSame(before, view.ghost, "the ghost is the run it was taken from");
            Assert.AreNotSame(view.trace, view.ghost);

            // And it still reads as that run: the two rows say different things at the same
            // moment, which is the only reason to lay one under the other.
            var mine = view.trace.Find(Simulation.LocalScope, "X");
            var theirs = view.ghost.Find(Simulation.LocalScope, "X");
            Assert.AreEqual(4f, mine.At(mine.Frames - 1), 1e-4f);
            Assert.AreEqual(1f, theirs.At(theirs.Frames - 1), 1e-4f);
            // One row, one scale — the ghost is another reading of the same thing.
            view.Measure();
            Assert.AreEqual(4f, view.RangeOf(mine).y, 1e-4f);
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

        [Test]
        public void Session_HoldsASampleInFlightTheWayARunDoes()
        {
            // The same agreement as above, on a wire that takes time. A session steps one
            // frame at a time and a run computes the lot, so a queue that was kept in either
            // of them rather than in the one piece of machinery they share would show up here
            // as two simulations that no longer match.
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 0.6f, seed = 5 },
                wire = new SyncWire { intervalSeconds = 0.1f, latencySeconds = 0.15f }
                    .Syncs("X"),
            };
            settings.stimulus.At(0f, "X", 0.5f);
            var batch = Simulation.Run(NewController(), settings);
            Assert.AreEqual(0.5f,
                batch.Find(Simulation.RemoteScope, "X").At(batch.Frames - 1), 0.01f,
                "nothing crossed at all, so there is nothing to agree about");

            using (var session = new SimSession(NewController(), settings))
            {
                session.Write(Simulation.LocalScope, "X", 0.5f);
                for (int i = 0; i < 36; i++) session.StepOnce();
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

        // ---- the experiment a clip carries -----------------------------------

        /// <summary>Every field of a run's settings set to something that is not its default, so
        /// a round trip that quietly writes defaults back cannot pass.</summary>
        static SimSettings Elaborate()
        {
            var wire = new SyncWire
            {
                intervalSeconds = 0.15f,
                latencySeconds = 0.08f,
                dropChance = 0.25f,
                quantize = false,
                seed = 99,
                remoteJoinsAt = 0.2f,
            }.Joining(0.3f, 0.4f).Syncs("X", "N");
            return new SimSettings
            {
                clock = new SimClock { fps = 45f, seconds = 0.4f, jitter = 0.2f, seed = 11 },
                stimulus = new Stimulus()
                    .At(0.05f, "Go", true)
                    .At(0.1f, "X", 0.25f)
                    .At(0.15f, "N", 3f, Simulation.RemoteScope),
                lagRows = false,
                wire = wire,
            };
        }

        [Test]
        public void Clip_CarriesTheExperimentThatMadeIt()
        {
            var settings = Elaborate();
            var trace = Simulation.Run(NewController(), settings);

            const string path = "Assets/DDTraceSettingsTest.anim";
            try
            {
                var clip = TraceClip.Save(trace, path, settings);
                var read = TraceClip.SettingsOf(clip);
                Assert.IsNotNull(read, "the settings ride along with the run");

                Assert.AreEqual(45f, read.clock.fps, 1e-6f);
                Assert.AreEqual(0.4f, read.clock.seconds, 1e-6f);
                Assert.AreEqual(0.2f, read.clock.jitter, 1e-6f);
                Assert.AreEqual(11, read.clock.seed);
                Assert.IsFalse(read.lagRows);

                Assert.IsNotNull(read.wire, "a two-client run says so");
                Assert.AreEqual(0.15f, read.wire.intervalSeconds, 1e-6f);
                Assert.AreEqual(0.08f, read.wire.latencySeconds, 1e-6f,
                    "the delay is part of the run");
                Assert.AreEqual(0.25f, read.wire.dropChance, 1e-6f);
                Assert.IsFalse(read.wire.quantize);
                // The wire's own seed, which is the one thing about the experiment that cannot
                // be worked out from anything else: it differs from the clock's here, and that
                // difference is what says the wire was given a seed of its own.
                Assert.AreEqual(99, read.wire.seed);
                Assert.AreNotEqual(read.clock.seed, read.wire.seed);
                Assert.AreEqual(0.2f, read.wire.remoteJoinsAt, 1e-6f);
                Assert.AreEqual(3, read.wire.Remotes, "and how many people were in the instance");
                Assert.AreEqual(2, read.wire.laterJoins.Count);
                Assert.AreEqual(0.3f, read.wire.laterJoins[0], 1e-6f);
                Assert.AreEqual(0.4f, read.wire.laterJoins[1], 1e-6f);
                CollectionAssert.AreEqual(new[] { "X", "N" }, read.wire.parameters);

                // The hand as well as the wire: a run nobody can re-poke is not a run anybody
                // can repeat.
                var entries = read.stimulus.InOrder();
                Assert.AreEqual(3, entries.Count);
                Assert.AreEqual(0.05f, entries[0].atSeconds, 1e-6f);
                Assert.AreEqual("Go", entries[0].parameter);
                Assert.AreEqual(1f, entries[0].value, 1e-6f);
                Assert.AreEqual(string.Empty, entries[0].scope);
                Assert.AreEqual("X", entries[1].parameter);
                Assert.AreEqual(0.25f, entries[1].value, 1e-6f);
                Assert.AreEqual(Simulation.RemoteScope, entries[2].scope,
                    "an input aimed at somebody else stays aimed at them");

                // And the whole point of keeping them: the same settings run again the same way.
                var again = Simulation.Run(NewController(), read);
                Assert.AreEqual(trace.Frames, again.Frames);
                foreach (var signal in trace.Signals)
                {
                    var twin = again.Find(signal.scope, signal.name);
                    Assert.IsNotNull(twin, "row " + signal.Path + " ran again");
                    for (int frame = 0; frame < trace.Frames; frame += 3)
                        Assert.AreEqual(signal.At(frame), twin.At(frame), 1e-4f,
                            signal.Path + " at " + frame);
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test]
        public void Clip_WithNoSettings_SaysNothingRatherThanSayingDefaults()
        {
            var trace = Simulation.Run(NewController(), Clock(0.2f), new Stimulus());

            const string saved = "Assets/DDTraceNoSettingsTest.anim";
            const string foreign = "Assets/DDTraceForeignTest.anim";
            try
            {
                // A run saved the way every run was saved before the settings travelled: the
                // signal list is there, the settings are not, and the difference has to be
                // readable. Null is what keeps a window from overwriting a form with 60 fps.
                var clip = TraceClip.Save(trace, saved);
                Assert.IsNotNull(TraceClip.ManifestOf(clip), "the signal list still rides along");
                Assert.IsNull(TraceClip.SettingsOf(clip));
                Assert.AreEqual(trace.Frames, TraceClip.Load(clip).Frames,
                    "and such a clip still opens as a run");

                // And a clip that was never a DD run at all.
                AssetDatabase.CreateAsset(new AnimationClip(), foreign);
                Assert.IsNull(TraceClip.SettingsOf(
                    AssetDatabase.LoadAssetAtPath<AnimationClip>(foreign)));
                Assert.IsNull(TraceClip.SettingsOf(null));
            }
            finally
            {
                AssetDatabase.DeleteAsset(saved);
                AssetDatabase.DeleteAsset(foreign);
            }
        }

        [Test]
        public void Clip_WithItsSettings_LetsTheFindingsSpeakAboutTheWireAgain()
        {
            // "Go" is pressed on the wearer and is not on the wire — a finding that cannot be
            // reached from the trace alone, because nothing in a trace says what was pressed
            // rather than what moved, or what the wire was carrying.
            var wire = new SyncWire { intervalSeconds = 0.1f }.Syncs("X");
            var settings = Wired(0.4f, wire, new Stimulus().At(0.05f, "Go", true));
            var trace = Simulation.Run(NewController(), settings);

            const string path = "Assets/DDTraceFindingsTest.anim";
            try
            {
                var clip = TraceClip.Save(trace, path, settings);
                var reloaded = TraceClip.Load(clip);

                var alone = RunFindings.For(reloaded, null);
                var told = RunFindings.For(reloaded, TraceClip.SettingsOf(clip));
                Assert.Greater(told.Count, alone.Count,
                    "the settings the clip carries are worth findings the trace cannot reach");
                Assert.IsTrue(told.Exists(finding => finding.Contains("never leave the wearer")),
                    "an input that goes nowhere is one of them:\n  "
                    + string.Join("\n  ", told.ToArray()));
                Assert.IsFalse(alone.Exists(finding => finding.Contains("never leave the wearer")),
                    "and it is not guessed at when the clip does not say");
            }
            finally
            {
                AssetDatabase.DeleteAsset(path);
            }
        }

        // ---- the hand, written down ------------------------------------------

        static System.Collections.Generic.List<DynamicAnalyzeWindow.Poke> Hand() =>
            new System.Collections.Generic.List<DynamicAnalyzeWindow.Poke>();

        [Test]
        public void Hand_ALiveWriteBecomesATimedInputAtTheSecondItHappened()
        {
            var pokes = Hand();
            using (var session = LiveSession(NewController()))
            {
                StepSession(session, 6);
                var go = session.Trace.Find(Simulation.LocalScope, "Go");
                Assert.IsNotNull(go);

                DynamicAnalyzeWindow.Record(pokes, session, go, 1f);
                Assert.AreEqual(1, pokes.Count);
                Assert.AreEqual("Go", pokes[0].parameter);
                Assert.AreEqual(1f, pokes[0].value, 1e-6f);
                Assert.AreEqual(6f / 60f, pokes[0].at, 1e-4f, "at the moment it was pressed");
                // The wearer is the empty scope in this list, the way the panel writes one.
                Assert.AreEqual(string.Empty, pokes[0].scope);

                // And the point of taking it down: the same press, replayed as an experiment.
                var replay = new Stimulus().At(pokes[0].at, pokes[0].parameter, pokes[0].value,
                    pokes[0].scope);
                var again = Simulation.Run(NewController(), Clock(0.5f), replay);
                Assert.AreEqual("On",
                    again.Find(Simulation.LocalScope, "Base/state").TextAt(again.Frames - 1));
            }
        }

        [Test]
        public void Hand_AWriteToSomebodyElsesCopyKeepsWhoseItWas()
        {
            var pokes = Hand();
            var settings = new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 1f },
                wire = new SyncWire().Syncs("X"),
            };
            using (var session = new SimSession(NewController(), settings))
            {
                StepSession(session, 2);
                var remote = session.Trace.Find(Simulation.RemoteScope, "X");
                Assert.IsNotNull(remote, "the other person's copy of X is a row");

                DynamicAnalyzeWindow.Record(pokes, session, remote, 0.5f);
                Assert.AreEqual(1, pokes.Count);
                Assert.AreEqual(Simulation.RemoteScope, pokes[0].scope);
                Assert.AreEqual("X", pokes[0].parameter);
            }
        }

        [Test]
        public void Hand_DoesNotWriteDownALayersWeight()
        {
            var pokes = Hand();
            using (var session = LiveSession(AapLayers(null)))
            {
                StepSession(session, 2);
                var weight = session.Trace.Find(Simulation.LocalScope, "Over/weight");
                Assert.IsNotNull(weight);
                Assert.IsTrue(session.CanSetWeight(Simulation.LocalScope, "Over/weight"),
                    "this row IS one a live session takes a value for");

                // Taken live, and still not written down: a timed input cannot carry a weight
                // (see Stimulus), so a list holding one would replay into a different run.
                DynamicAnalyzeWindow.Record(pokes, session, weight, 0.5f);
                CollectionAssert.IsEmpty(pokes);

                // A parameter of the same session still is.
                DynamicAnalyzeWindow.Record(pokes, session,
                    session.Trace.Find(Simulation.LocalScope, "X"), 0.25f);
                Assert.AreEqual(1, pokes.Count);
            }
        }

        [Test]
        public void Hand_TwoWritesAtOneMomentAreOneInput_AndNothingElseIsDropped()
        {
            var pokes = Hand();
            using (var session = LiveSession(NewController()))
            {
                StepSession(session, 3);
                var x = session.Trace.Find(Simulation.LocalScope, "X");

                // A float cell being dragged writes on every repaint; the session has not moved,
                // so all of it happened at one moment and only the last value can be seen there.
                for (int i = 1; i <= 20; i++)
                    DynamicAnalyzeWindow.Record(pokes, session, x, i * 0.01f);
                Assert.AreEqual(1, pokes.Count, "one drag is one input");
                Assert.AreEqual(0.2f, pokes[0].value, 1e-6f, "and it is where the drag ended");

                // Time moving on makes the next write its own input — the values genuinely
                // happened at different seconds, and a run replaying them has to do the same.
                StepSession(session, 1);
                DynamicAnalyzeWindow.Record(pokes, session, x, 0.5f);
                Assert.AreEqual(2, pokes.Count);
                Assert.Greater(pokes[1].at, pokes[0].at);

                // A different parameter at the same moment is never a replacement either.
                DynamicAnalyzeWindow.Record(pokes, session,
                    session.Trace.Find(Simulation.LocalScope, "Go"), 1f);
                Assert.AreEqual(3, pokes.Count);
                Assert.AreEqual("Go", pokes[2].parameter);
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
