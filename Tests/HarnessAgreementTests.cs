using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The two things in this repository that run a generated controller, held against each
    /// other.
    ///
    /// <see cref="AnimatorRig"/> is what the gadget and toggle tests measure with: a hidden host,
    /// a real Animator, stepped by hand, and nothing else. <see cref="Simulation"/> is what DD
    /// DynamicAnalyze answers with, and it is the same idea with a clock, a stimulus list and a
    /// model of VRChat wrapped around it. Two engines for one question is two chances to be
    /// wrong, so the useful thing to know is exactly where they part company.
    ///
    /// They part in two places, and both are on purpose: the simulation applies Parameter
    /// Drivers, and it answers IsLocal — because a headset does both and Mecanim stepped in the
    /// editor does neither. There is a third difference that is not about running anything: the
    /// rig can be handed a hierarchy to drive and the simulation cannot, so an object toggle is
    /// a question only the rig can be asked. Everywhere
    /// else — arithmetic, feedback, transition timing, the frame an input lands on, an uneven
    /// clock — they agree frame for frame, which is what makes a number read off one harness
    /// worth quoting about the other.
    /// </summary>
    [Category("Runtime")]
    public class HarnessAgreementTests
    {
        const float Fps = 60f;

        static SimClock Clock(int frames, float jitter = 0f) =>
            new SimClock { fps = Fps, seconds = frames / Fps, jitter = jitter, seed = 7 };

        /// <summary>
        /// The time to write on a stimulus so it lands immediately before frame
        /// <paramref name="frame"/> — the same moment as a <c>rig.Set</c> made between that
        /// frame's step and the one before it. Half a frame early rather than exactly on the
        /// boundary, because the run compares a written-down time against a sum of steps and a
        /// sum of thirty sixtieths is not the constant thirty sixtieths.
        /// </summary>
        static float Before(int frame) => frame <= 0 ? 0f : (frame - 0.5f) / Fps;

        /// <summary>An input, and the frame it is set before — one line of a run, said once for
        /// both harnesses so the comparison cannot be of two different experiments.</summary>
        struct Poke
        {
            public int frame;
            public string parameter;
            public float value;
        }

        static Poke At(int frame, string parameter, float value) =>
            new Poke { frame = frame, parameter = parameter, value = value };

        static Stimulus AsStimulus(IEnumerable<Poke> pokes)
        {
            var stimulus = new Stimulus();
            foreach (var poke in pokes) stimulus.At(Before(poke.frame), poke.parameter, poke.value);
            return stimulus;
        }

        /// <summary>Steps the rig, applying each poke before its frame, and reads one signal per
        /// frame — the rig's answer in the shape a trace comes back in.</summary>
        static float[] RigRun(AnimatorController controller, int frames, IEnumerable<Poke> pokes,
            System.Func<AnimatorRig, float> read)
        {
            var values = new float[frames];
            using (var rig = new AnimatorRig(controller))
                for (int i = 0; i < frames; i++)
                {
                    foreach (var poke in pokes)
                        if (poke.frame == i) Write(controller, rig, poke);
                    rig.Step();
                    values[i] = read(rig);
                }
            return values;
        }

        /// <summary>
        /// Writes one number to the rig the way <c>SimClient.Write</c> writes it: by the type the
        /// controller declares, not by the type of the value in hand.
        ///
        /// Worth its own method, because the rig does not do this and the difference is silent.
        /// <c>Set(name, 1f)</c> is a bare <c>SetFloat</c>, and SetFloat against a Bool parameter
        /// neither throws nor sets anything — a run written that way looks like a toggle that
        /// never fired. A test comparing the harnesses has to say the same thing to both, so the
        /// dispatch happens here rather than being left to whichever overload the call resolved.
        /// </summary>
        static void Write(AnimatorController controller, AnimatorRig rig, Poke poke)
        {
            foreach (var parameter in controller.parameters)
            {
                if (parameter.name != poke.parameter) continue;
                if (parameter.type == AnimatorControllerParameterType.Bool
                    || parameter.type == AnimatorControllerParameterType.Trigger)
                    rig.Set(poke.parameter, poke.value != 0f);
                else
                    rig.Set(poke.parameter, poke.value);
                return;
            }
            Assert.Fail("the controller has no parameter called " + poke.parameter);
        }

        static float[] SimRun(AnimatorController controller, int frames, IEnumerable<Poke> pokes,
            string signal)
        {
            var trace = Simulation.Run(controller, Clock(frames), AsStimulus(pokes));
            var found = trace.Find(Simulation.LocalScope, signal);
            Assert.IsNotNull(found, "the run recorded no signal called " + signal);
            Assert.AreEqual(frames, trace.Frames, "the clock produced a different number of frames");
            var values = new float[frames];
            for (int i = 0; i < frames; i++) values[i] = found.At(i);
            return values;
        }

        static void AssertSame(float[] rig, float[] sim, float tolerance, string what)
        {
            Assert.AreEqual(rig.Length, sim.Length, what + ": different lengths");
            for (int i = 0; i < rig.Length; i++)
                Assert.AreEqual(rig[i], sim[i], tolerance,
                    what + ": frame " + i + "\n  rig " + Describe(rig) + "\n  sim " + Describe(sim));
        }

        static string Describe(float[] values)
        {
            var text = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
                text.Append(i == 0 ? "" : " ").Append(values[i].ToString("0.####"));
            return text.ToString();
        }

        // ---- building -----------------------------------------------------------

        static AnimatorController NewController(params string[] floatParams)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            foreach (var name in floatParams)
                controller.AddParameter(name, AnimatorControllerParameterType.Float);
            return controller;
        }

        static AnimatorController Gadget(AapGadgets.Kind kind,
            System.Action<AapGadgets.Request> configure = null)
        {
            var controller = NewController("A", "B");
            var request = new AapGadgets.Request
            {
                controller = controller,
                kind = kind,
                inputA = "A",
                inputB = "B",
                output = "Out",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            configure?.Invoke(request);
            Assert.IsNull(AapGadgets.Validate(request), "the gadget was refused");
            Assert.IsTrue(AapGadgets.Apply(request), "the gadget failed to apply");
            return controller;
        }

        // ---- where they agree ----------------------------------------------------

        /// <summary>
        /// Four scenarios in one test on purpose: the claim is not that a smoothing agrees or
        /// that a transition agrees, it is that the two harnesses are one engine everywhere the
        /// simulation is not deliberately modelling something else. Each part says which one it
        /// is when it fails, and any of them failing is the same piece of news.
        /// </summary>
        [Test]
        public void Simulation_AndTheRig_AgreeFrameForFrame()
        {
            Agree_OnASmoothingsWholeCurve();
            Agree_OnTheFrameAnInputLandsOn();
            Agree_OnAToggleSTransition();
            Agree_OnAnUnevenClock();
        }

        /// <summary>
        /// Feedback, which is the hardest thing either harness has to get right: an exponential
        /// smoothing has no settled value, only a value that is a function of how many frames
        /// have gone by, so two engines agreeing on it at frame 30 are agreeing about every
        /// frame before it as well.
        /// </summary>
        static void Agree_OnASmoothingsWholeCurve()
        {
            var controller = Gadget(AapGadgets.Kind.Smooth,
                r => { r.smoothing = "Smoothing"; r.smoothingDefault = 0.5f; });
            const int frames = 40;
            var pokes = new[] { At(0, "A", 1f), At(0, "Smoothing", 0.5f) };

            var rig = RigRun(controller, frames, pokes, r => r.Get("Out"));
            var sim = SimRun(controller, frames, pokes, "Out");

            // The curve has to actually be a curve, or "they agree" would be a claim about two
            // flat lines.
            Assert.AreEqual(0.5f, rig[0], 1e-3f, "the first frame is half way there");
            Assert.AreEqual(0.75f, rig[1], 1e-3f);
            Assert.AreEqual(1f, rig[frames - 1], 1e-3f, "and it has arrived by the end");
            AssertSame(rig, sim, 1e-6f, "Smooth");
        }

        /// <summary>
        /// Timing, on the one gadget whose latency is a number you chose: a buffer of three
        /// frames moves exactly three frames after the input does, and both harnesses have to
        /// place that step on the same frame. This is also what pins the stimulus down — a
        /// poke written for a time has to land on the frame a <c>Set</c> between two steps
        /// lands on, or every trace DynamicAnalyze draws is off by one.
        /// </summary>
        static void Agree_OnTheFrameAnInputLandsOn()
        {
            var controller = Gadget(AapGadgets.Kind.Buffer,
                r => { r.bufferFrames = 3; r.rangeMin = -1f; r.rangeMax = 1f; });
            const int frames = 20, moves = 8;
            var pokes = new[] { At(0, "A", 0f), At(moves, "A", 0.8f) };

            var rig = RigRun(controller, frames, pokes, r => r.Get("Out"));
            var sim = SimRun(controller, frames, pokes, "Out");

            Assert.AreEqual(0f, rig[moves + 1], 1e-4f, "the buffer is still holding the old value");
            Assert.AreEqual(0.8f, rig[moves + 3], 1e-4f, "and lets go of it three frames on");
            AssertSame(rig, sim, 1e-6f, "Buffer");
        }

        /// <summary>
        /// A toggle's Bool layer, which is a transition rather than arithmetic: the same frame
        /// off, the same frame on, in both harnesses. Only the layer's state is compared — the
        /// rig can be handed a hierarchy and read <c>activeSelf</c> back off it, and the
        /// simulation cannot, because its client builds its own empty host and the animated
        /// paths resolve against nothing.
        /// </summary>
        static void Agree_OnAToggleSTransition()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            var request = new ToggleBuilder.Request
            {
                controller = controller,
                mode = ToggleBuilder.Mode.Layer,
                toggleName = "Hat",
                parameter = "Hat",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            request.targets.Add(new ToggleBuilder.Target { path = "Body/Hat" });
            Assert.IsNull(ToggleBuilder.Validate(request), "the toggle was refused");
            Assert.IsTrue(ToggleBuilder.Apply(request), "the toggle failed to apply");

            string layer = controller.layers[1].name;
            const int frames = 16, turnsOn = 4;
            var pokes = new[] { At(turnsOn, "Hat", 1f) };

            // The rig names the state it is in and the trace holds an index into the layer's
            // labels, so both are reduced to "is it the ON one" and compared as numbers. The
            // labels are kept anyway, to say what the run actually did if it did not.
            var labels = new List<string>();
            var trace = Simulation.Run(controller, Clock(frames), AsStimulus(pokes));
            var state = trace.Find(Simulation.LocalScope, layer + "/state");
            Assert.IsNotNull(state, "the run recorded no state row for the toggle's layer");

            var rig = RigRun(controller, frames, pokes,
                r => r.CurrentState(1, "Hat OFF", "Hat ON") == "Hat ON" ? 1f : 0f);
            var sim = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                labels.Add(state.TextAt(i));
                sim[i] = state.TextAt(i) == "Hat ON" ? 1f : 0f;
            }

            Assert.AreEqual(0f, rig[turnsOn - 1], "still off the frame before");
            Assert.AreEqual(1f, rig[turnsOn], "on the frame the bool was set for");
            AssertSame(rig, sim, 0f, "toggle layer state (" + string.Join(",", labels) + ")");
        }

        /// <summary>
        /// The clock is the simulation's own, and jitter is a thing the rig has no notion of —
        /// but the rig takes a frame length per step, so an uneven run can be replayed into it
        /// exactly. Doing that and getting the same answer says the jitter is a schedule rather
        /// than a second engine: the noise is in the clock, not in what is stepped by it.
        /// </summary>
        static void Agree_OnAnUnevenClock()
        {
            var controller = NewController();
            var request = new AapGadgets.Request
            {
                controller = controller,
                kind = AapGadgets.Kind.FrameTime,
                output = "Dt",
                layerIndex = -1,
                newLayerName = "DBT",
            };
            Assert.IsNull(AapGadgets.Validate(request), "the gadget was refused");
            Assert.IsTrue(AapGadgets.Apply(request), "the gadget failed to apply");

            const int frames = 10;
            var clock = Clock(frames, jitter: 0.4f);
            var steps = clock.Steps();

            var rig = new float[frames];
            using (var harness = new AnimatorRig(controller))
                for (int i = 0; i < frames; i++)
                {
                    harness.Step(1, steps[i]);
                    rig[i] = harness.Get("Dt");
                }

            var trace = Simulation.Run(controller, clock);
            var signal = trace.Find(Simulation.LocalScope, "Dt");
            var sim = new float[frames];
            for (int i = 0; i < frames; i++) sim[i] = signal.At(i);

            // A clock reading the frame it is on has to see the uneven lengths, or this compares
            // two harnesses on a signal neither of them varied.
            Assert.AreNotEqual(rig[1], rig[2], "the jittered frames were not different lengths");
            AssertSame(rig, sim, 1e-6f, "FrameTime on a jittered clock");
        }

        // ---- where they part company ---------------------------------------------

        /// <summary>
        /// The whole of the difference, in one place so that it can be counted: two things, both
        /// of them the simulation modelling VRChat rather than modelling Mecanim. Anything else
        /// the two harnesses ever disagree about is a bug in one of them, and this test is where
        /// the third entry would have to be argued for.
        /// </summary>
        [Test]
        public void Simulation_DiffersFromTheRig_OnlyWhereItModelsVrChat()
        {
            Differ_TheSimulationRunsADriverTheRigLeavesInert();
            Differ_TheSimulationAnswersIsLocal();
        }

        /// <summary>
        /// The first of the two, and the one worth the whole exercise — stated as a measurement
        /// rather than as a comment.
        ///
        /// A Parameter Driver is a StateMachineBehaviour, and Mecanim stepped by hand outside
        /// play mode never calls one — so the rig runs the controller with every driver in it
        /// inert, which is the right answer to "what does this animator do" and the wrong answer
        /// to "what does this avatar do". The simulation reads each driver's spec off the state
        /// and applies it itself, which is what lets it work without the SDK installed, tell the
        /// wearer from a remote, and put a wire in between.
        ///
        /// It applies them at the moment the transition STARTS, which is where a headset's
        /// OnStateEnter fires too — so with a blend in the way the drive is visible several
        /// frames before the state row says the destination has been reached.
        /// </summary>
        static void Differ_TheSimulationRunsADriverTheRigLeavesInert()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("N", AnimatorControllerParameterType.Int);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;
            var transition = idle.AddTransition(on);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.1f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Go");

            var driver = VrcParameterDriver.AddTo(on, "Agreement");
            Assert.IsNotNull(driver, "the driver behaviour (or its stub) has to be present");
            VrcParameterDriver.AddSetEntry(driver, "N", 5f);

            const int frames = 16, pressed = 3;
            var pokes = new[] { At(pressed, "Go", 1f) };

            var rig = RigRun(controller, frames, pokes, r => r.Get("N"));
            var sim = SimRun(controller, frames, pokes, "N");

            foreach (float value in rig)
                Assert.AreEqual(0f, value, "the rig does not run behaviours: N should never move — "
                    + Describe(rig));
            Assert.AreEqual(0f, sim[pressed - 1], "nothing has been entered yet");
            Assert.AreEqual(5f, sim[pressed], "the drive lands on the frame the transition starts — "
                + Describe(sim));

            // And the two agree about everything else in the same run: the driver is the whole
            // of the difference, not the leading edge of a drift.
            var rigState = RigRun(controller, frames, pokes,
                r => r.CurrentState(0, "Idle", "On") == "On" ? 1f : 0f);
            var simState = SimRun(controller, frames, pokes, "Base/state");
            AssertSame(rigState, simState, 0f, "the state the layer is in");
            Assert.AreEqual(0f, simState[pressed], "the blend is still running here, which is why "
                + "the drive being visible already is worth saying out loud");
        }

        /// <summary>
        /// The other one, and the smaller of the two: the simulation answers IsLocal, because a
        /// controller that splits the wearer from a remote reads that parameter once, when the
        /// layer is first entered, and a run where it was false would take the remote's branch
        /// of everything. The rig has no opinion about who is wearing the avatar, so it takes
        /// the branch the parameter's declared default asks for.
        /// </summary>
        static void Differ_TheSimulationAnswersIsLocal()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("IsLocal", AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var remote = machine.AddState("Remote");
            var local = machine.AddState("Local");
            machine.defaultState = remote;
            var transition = machine.AddAnyStateTransition(local);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, "IsLocal");

            const int frames = 6;
            using (var rig = new AnimatorRig(controller))
            {
                rig.Step(frames);
                Assert.AreEqual("Remote", rig.CurrentState(0, "Remote", "Local"),
                    "IsLocal defaults to false on the asset, and that is all the rig knows");
            }

            var trace = Simulation.Run(controller, Clock(frames));
            Assert.AreEqual(1f, trace.Find(Simulation.LocalScope, "IsLocal").At(frames - 1));
            Assert.AreEqual("Local",
                trace.Find(Simulation.LocalScope, "Base/state").TextAt(frames - 1),
                "the wearer's copy is the wearer's, and the branch has to say so");
        }
    }
}
