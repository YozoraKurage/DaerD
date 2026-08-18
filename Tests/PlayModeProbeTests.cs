using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using Yozolab.DaerD.Engine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What Mecanim actually does, asked twice — once with the Editor in Play mode and once
    /// without — because three of the things DD DynamicAnalyze said it could not promise were
    /// beliefs formed by stepping an Animator by hand in the Editor, and nobody had ever
    /// checked whether Play mode answers differently.
    ///
    /// Play mode answers the same to every question here, and two of the three beliefs were
    /// wrong regardless of the mode: a conditional Entry and a driver's write crossing a layer
    /// are both handled by this run exactly as Mecanim handles them. Those notes are gone from
    /// SimNotes and their evidence is here — which is the point of a measurement file. A
    /// sentence in a doc comment can be argued with; a number that fails when it stops being
    /// true cannot.
    ///
    /// Every measurement is written once, as a static method full of assertions, and run from
    /// both a <see cref="UnityTest"/> that enters Play mode and a plain <see cref="Test"/> that
    /// does not. That is the whole design: the pair passing is the finding. A number that only
    /// held in one mode would have to be written down twice, and the moment it is written twice
    /// the two copies can drift apart and nobody notices which one is the measurement.
    ///
    /// The numbers are pinned rather than remembered — the same reason
    /// <see cref="VrcSdkConformanceTests"/> exists. If a Unity upgrade changes any of them, the
    /// thing that fails is this file, and the answer is to re-read the result and re-argue the
    /// design, not to nudge the constant.
    ///
    /// Nothing here touches the VRChat SDK: the witness is <see cref="PlayModeProbeBehaviour"/>,
    /// a plain StateMachineBehaviour, so these run the same with the SDK installed and without.
    /// Controllers are built in code, on purpose — a probe that needed a fixture on disk would
    /// be a probe whose shape nobody could read.
    /// </summary>
    [Category("PlayModeProbe")]
    public class PlayModeProbeTests
    {
        const float Dt = 1f / 60f;

        // ---- the rig --------------------------------------------------------

        /// <summary>An Animator that only moves when asked. The player loop would step an
        /// enabled Animator too, and every measurement here counts steps, so the component is
        /// switched off and driven by hand — which is also exactly what
        /// <c>Tests/AnimatorRig.cs</c> and DD DynamicAnalyze's SimClient do, making the numbers
        /// comparable to theirs.</summary>
        sealed class Rig : IDisposable
        {
            public readonly Animator animator;
            readonly GameObject _host;

            public Rig(RuntimeAnimatorController controller)
            {
                _host = new GameObject("DaerD Play Mode Probe");
                _host.hideFlags = HideFlags.DontSave;
                animator = _host.AddComponent<Animator>();
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.runtimeAnimatorController = controller;
                animator.enabled = false;
                animator.Rebind();
            }

            public Rig Step(int steps = 1)
            {
                for (int i = 0; i < steps; i++)
                {
                    PlayModeProbeBehaviour.Step++;
                    animator.Update(Dt);
                }
                return this;
            }

            /// <summary>The current state's name, or "?" — a name rather than a hash because a
            /// failure message naming a state is worth more than one naming a number.</summary>
            public string State(int layer, params string[] candidates)
            {
                var info = animator.GetCurrentAnimatorStateInfo(layer);
                foreach (var name in candidates)
                    if (info.IsName(name)) return name;
                return "?";
            }

            public string Next(int layer, params string[] candidates)
            {
                var info = animator.GetNextAnimatorStateInfo(layer);
                foreach (var name in candidates)
                    if (info.IsName(name)) return name;
                return "?";
            }

            public void Dispose()
            {
                if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            }
        }

        static Rig Start(RuntimeAnimatorController controller)
        {
            PlayModeProbeBehaviour.Forget();
            return new Rig(controller);
        }

        static void Watch(AnimatorState state, string label, string writeBool = null)
        {
            var probe = state.AddStateMachineBehaviour<PlayModeProbeBehaviour>();
            Assert.IsNotNull(probe, "the probe behaviour has to be present");
            probe.label = label;
            probe.writeBool = writeBool;
        }

        /// <summary>The link every measurement here wants: taken the instant its condition is
        /// met, with no exit time and no blend unless one is asked for.</summary>
        static AnimatorStateTransition Wire(
            AnimatorState from, AnimatorState to, float duration = 0f)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = duration;
            return transition;
        }

        static void Weigh(AnimatorController controller, int layer, float weight)
        {
            var layers = controller.layers;
            layers[layer].defaultWeight = weight;
            controller.layers = layers;
        }

        static AnimatorControllerParameter Bool(string name, bool value = false) =>
            new AnimatorControllerParameter
            {
                name = name,
                type = AnimatorControllerParameterType.Bool,
                defaultBool = value,
            };

        // ---- M1: a condition on Entry ---------------------------------------

        /// <summary>Entry chooses A when P is up, and falls through to the default B.</summary>
        static AnimatorController EntryChoice(bool defaultTrue)
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddParameter(Bool("P", defaultTrue));
            var machine = controller.layers[0].stateMachine;
            var a = machine.AddState("A");
            var b = machine.AddState("B");
            machine.defaultState = b;
            machine.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            Watch(a, "A");
            Watch(b, "B");
            return controller;
        }

        /// <summary>Start, then a sub state machine whose own Entry chooses A when P is up —
        /// with a route back out, so the sub machine can be entered more than once.</summary>
        static AnimatorController EntryChoiceInsideSub()
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddParameter(Bool("P"));
            controller.AddParameter("Go", AnimatorControllerParameterType.Trigger);
            controller.AddParameter("Back", AnimatorControllerParameterType.Trigger);
            var machine = controller.layers[0].stateMachine;
            var start = machine.AddState("Start");
            machine.defaultState = start;
            Watch(start, "Start");

            var sub = machine.AddStateMachine("Sub");
            var a = sub.AddState("A");
            var b = sub.AddState("B");
            sub.defaultState = b;
            sub.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            Watch(a, "A");
            Watch(b, "B");
            Wire(a, start).AddCondition(AnimatorConditionMode.If, 0f, "Back");
            Wire(b, start).AddCondition(AnimatorConditionMode.If, 0f, "Back");

            var into = start.AddTransition(sub);
            into.hasExitTime = false;
            into.hasFixedDuration = true;
            into.duration = 0f;
            into.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            return controller;
        }

        /// <summary>
        /// M1 — a conditional Entry transition at the top of a layer is not taken when the
        /// layer starts, and the same shape one level down is taken on every visit.
        ///
        /// Four ways of arranging for the condition to be true when the layer starts were tried
        /// and all four land in the default state: the parameter declaring true as its default,
        /// the parameter written after Rebind but before the first step, no Rebind at all, and
        /// ten steps of waiting. The route exists on the asset — the assertion on
        /// <c>entryTransitions</c> says so — it simply gets no look on the way in, because a
        /// layer's root state machine is entered before there is anything to ask.
        ///
        /// It is not a dead route, though, which is what
        /// <see cref="MeasureEntryAfterExit"/> is for: pass through Exit and the same condition
        /// decides where the layer resumes. Only the first entry into a layer skips it.
        /// </summary>
        static void MeasureEntryConditions()
        {
            foreach (bool defaultTrue in new[] { true, false })
            {
                var controller = EntryChoice(defaultTrue);
                var machine = controller.layers[0].stateMachine;
                Assert.AreEqual(1, machine.entryTransitions.Length, "the route was built");
                Assert.AreEqual(1, machine.entryTransitions[0].conditions.Length);
                using (var rig = Start(controller))
                {
                    rig.Step(10);
                    Assert.AreEqual("B", rig.State(0, "A", "B"),
                        "a conditional Entry at the top of a layer is not taken, and the "
                        + "parameter's declared default (" + defaultTrue + ") makes no difference");
                    Assert.AreEqual(0, PlayModeProbeBehaviour.Enters("A"));
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("B"));
                    Assert.AreEqual(1, PlayModeProbeBehaviour.EnteredOn("B"),
                        "the default state is entered on the first step, not at Rebind");
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }

            // Written after Rebind and before the first step — the last moment anything could
            // matter, and it does not.
            {
                var controller = EntryChoice(false);
                using (var rig = Start(controller))
                {
                    rig.animator.SetBool("P", true);
                    rig.Step();
                    Assert.AreEqual("B", rig.State(0, "A", "B"),
                        "raising the condition before the first step does not open Entry either");
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }

            // No Rebind at all, in case the reset is what walks past Entry. It is not.
            {
                var controller = EntryChoice(true);
                PlayModeProbeBehaviour.Forget();
                var host = new GameObject("DaerD Play Mode Probe")
                {
                    hideFlags = HideFlags.DontSave,
                };
                var animator = host.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = false;
                animator.runtimeAnimatorController = controller;
                PlayModeProbeBehaviour.Step = 1;
                animator.Update(Dt);
                Assert.IsTrue(animator.GetBool("P"), "the declared default is live");
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("B"),
                    "without a Rebind the answer is the same");
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(controller);
            }

            // One level down, the same condition decides — twice, differently.
            {
                var controller = EntryChoiceInsideSub();
                using (var rig = Start(controller))
                {
                    rig.Step();
                    Assert.AreEqual("Start", rig.State(0, "Start", "A", "B"));

                    rig.animator.SetTrigger("Go");
                    rig.Step(2);
                    Assert.AreEqual("B", rig.State(0, "Start", "A", "B"),
                        "the sub machine's Entry took the fall-through with P down");

                    rig.animator.SetTrigger("Back");
                    rig.Step(2);
                    Assert.AreEqual("Start", rig.State(0, "Start", "A", "B"));

                    rig.animator.SetBool("P", true);
                    rig.animator.SetTrigger("Go");
                    rig.Step(2);
                    Assert.AreEqual("A", rig.State(0, "Start", "A", "B"),
                        "and re-read the condition on the second visit");
                    Assert.AreEqual(2, PlayModeProbeBehaviour.Enters("Start"));
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"));
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("B"));
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// M1c — the same question of a controller that lives on disk. Worth asking separately
        /// because the three earlier attempts recorded in ADR 0008 included "make it a saved
        /// asset", and a difference here would mean the in-memory controllers every other test
        /// in this repository is built from are not the thing being measured.
        /// </summary>
        static void MeasureEntryConditionsOnASavedAsset()
        {
            const string folder = "Assets/DaerDProbe";
            const string path = folder + "/EntryChoice.controller";
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            AssetDatabase.CreateFolder("Assets", "DaerDProbe");
            try
            {
                var saved = AnimatorController.CreateAnimatorControllerAtPath(path);
                saved.AddParameter(Bool("P", true));
                var machine = saved.layers[0].stateMachine;
                var a = machine.AddState("A");
                var b = machine.AddState("B");
                machine.defaultState = b;
                machine.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
                Watch(a, "A");
                Watch(b, "B");
                AssetDatabase.SaveAssets();

                var reloaded = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                Assert.IsNotNull(reloaded, "the controller came back off disk");
                using (var rig = Start(reloaded))
                {
                    rig.Step();
                    Assert.AreEqual("B", rig.State(0, "A", "B"),
                        "a saved controller answers exactly as the built one does");
                    Assert.AreEqual(0, PlayModeProbeBehaviour.Enters("A"));
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("B"));
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        /// <summary>The same choice at the top of a layer, with a way out of both states
        /// through Exit — which is how a root state machine gets entered a second time without
        /// the animator being rebuilt.</summary>
        static AnimatorController EntryChoiceWithAWayOut()
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddParameter(Bool("P", true));
            controller.AddParameter("Out", AnimatorControllerParameterType.Trigger);
            var machine = controller.layers[0].stateMachine;
            var a = machine.AddState("A");
            var b = machine.AddState("B");
            machine.defaultState = b;
            machine.AddEntryTransition(a).AddCondition(AnimatorConditionMode.If, 0f, "P");
            Watch(a, "A");
            Watch(b, "B");
            foreach (var state in new[] { a, b })
            {
                var leave = state.AddExitTransition();
                leave.hasExitTime = false;
                leave.hasFixedDuration = true;
                leave.duration = 0f;
                leave.AddCondition(AnimatorConditionMode.If, 0f, "Out");
            }
            return controller;
        }

        /// <summary>
        /// M1d — the condition on a layer's Entry is read every time the layer arrives at Entry.
        /// It is only the first arrival, the one that happens before the first step, that skips
        /// it.
        ///
        /// This is the half of M1 that turns "a dead route" into "a route with one blind spot",
        /// and it decides how a run should talk about the shape. A controller that splits the
        /// wearer from a remote at Entry does run down the default side for the whole session —
        /// but that is Mecanim's answer, not a simulation's approximation of one, so nothing
        /// here is a divergence to warn about. The same reading is what a headset gets.
        ///
        /// Read twice with different answers, because a route that happened to agree with the
        /// default once would prove nothing.
        /// </summary>
        static void MeasureEntryAfterExit()
        {
            var controller = EntryChoiceWithAWayOut();
            using (var rig = Start(controller))
            {
                rig.Step();
                Assert.AreEqual("B", rig.State(0, "A", "B"),
                    "the first entry into the layer walks past Entry even with P declared true");

                rig.animator.SetTrigger("Out");
                rig.Step(2);
                Assert.AreEqual("A", rig.State(0, "A", "B"),
                    "and the second one reads the condition it ignored");
                Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"));

                rig.animator.SetBool("P", false);
                rig.animator.SetTrigger("Out");
                rig.Step(2);
                Assert.AreEqual("B", rig.State(0, "A", "B"),
                    "the same route, the other answer — so it is read each time and not once");
                Assert.AreEqual(2, PlayModeProbeBehaviour.Enters("B"));
                Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"),
                    "with the condition down it falls through to the default again");
            }
            UnityEngine.Object.DestroyImmediate(controller);
        }

        // ---- M2: a state entered from itself --------------------------------

        static AnimatorController SelfRoute(float duration, bool fromAny, bool canSelf = true)
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddParameter("T", AnimatorControllerParameterType.Trigger);
            var machine = controller.layers[0].stateMachine;
            var a = machine.AddState("A");
            machine.defaultState = a;
            Watch(a, "A");

            AnimatorStateTransition transition;
            if (fromAny)
            {
                transition = machine.AddAnyStateTransition(a);
                transition.canTransitionToSelf = canSelf;
            }
            else
            {
                transition = a.AddTransition(a);
            }
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = duration;
            transition.AddCondition(AnimatorConditionMode.If, 0f, "T");
            return controller;
        }

        /// <summary>
        /// M2 — a state entered from itself is entered again, and the callbacks say so. Both
        /// routes back to the same state count (the state's own transition and Any with
        /// canTransitionToSelf), and the blend length does not matter: with a quarter-second
        /// blend the entry is already counted on the step the transition STARTS, which is the
        /// same timing DD DynamicAnalyze's SimClient applies a driver on.
        ///
        /// The control is the one that matters: with canTransitionToSelf off, nothing happens
        /// at all, so the count is measuring re-entry and not the trigger being consumed.
        /// </summary>
        static void MeasureSelfReEntry()
        {
            foreach (float duration in new[] { 0f, 0.25f })
            foreach (bool fromAny in new[] { false, true })
            {
                var controller = SelfRoute(duration, fromAny);
                using (var rig = Start(controller))
                {
                    rig.Step();
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"), "settled");

                    rig.animator.SetTrigger("T");
                    rig.Step();
                    string what = "duration " + duration + (fromAny ? " from Any" : " from itself");
                    Assert.AreEqual(2, PlayModeProbeBehaviour.Enters("A"),
                        "a state re-entered from itself is entered again — " + what);
                    Assert.AreEqual(duration > 0f, rig.animator.IsInTransition(0),
                        "and the entry is counted at the start of the blend, not its end — " + what);
                    Assert.AreEqual("A", rig.State(0, "A"));

                    rig.Step(30);
                    Assert.AreEqual(2, PlayModeProbeBehaviour.Enters("A"),
                        "one trigger, one re-entry — " + what);
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Exits("A"),
                        "the copy being left exits once — " + what);
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }

            {
                var controller = SelfRoute(0f, fromAny: true, canSelf: false);
                using (var rig = Start(controller))
                {
                    rig.Step();
                    rig.animator.SetTrigger("T");
                    rig.Step(5);
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"),
                        "with canTransitionToSelf off there is no re-entry to count");
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        // ---- M3: a behaviour's write, read elsewhere ------------------------

        /// <summary>A state that writes X on the way in, and a state on the other layer whose
        /// route waits for X. Which layer writes is the argument, because layer order is the
        /// reason a same-frame answer would be expected in one direction and not the other.</summary>
        static AnimatorController CrossLayer(int writerLayer)
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddLayer("L1");
            controller.AddParameter(Bool("Go"));
            controller.AddParameter(Bool("X"));
            Weigh(controller, 1, 1f);

            var writer = controller.layers[writerLayer].stateMachine;
            var idleW = writer.AddState("IdleW");
            var write = writer.AddState("Write");
            writer.defaultState = idleW;
            Watch(idleW, "IdleW");
            Watch(write, "Write", "X");
            Wire(idleW, write).AddCondition(AnimatorConditionMode.If, 0f, "Go");

            var reader = controller.layers[1 - writerLayer].stateMachine;
            var idleR = reader.AddState("IdleR");
            var read = reader.AddState("Read");
            reader.defaultState = idleR;
            Watch(idleR, "IdleR");
            Watch(read, "Read");
            Wire(idleR, read).AddCondition(AnimatorConditionMode.If, 0f, "X");
            return controller;
        }

        /// <summary>The writer and the reader of its write, on one layer.</summary>
        static AnimatorController SameLayerWrite()
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddParameter(Bool("Go"));
            controller.AddParameter(Bool("X"));
            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("IdleW");
            var write = machine.AddState("Write");
            var read = machine.AddState("Read");
            machine.defaultState = idle;
            Watch(idle, "IdleW");
            Watch(write, "Write", "X");
            Watch(read, "Read");
            Wire(idle, write).AddCondition(AnimatorConditionMode.If, 0f, "Go");
            Wire(write, read).AddCondition(AnimatorConditionMode.If, 0f, "X");
            return controller;
        }

        /// <summary>
        /// M3 — a write made from OnStateEnter is never read inside the step that made it. The
        /// value is there to anyone asking the Animator from outside at the end of that step,
        /// and every transition that reads it — on either layer, and on the writing layer
        /// itself — moves one step later.
        ///
        /// The direction being irrelevant is the finding. If layers were served in index order
        /// with their callbacks inline, a write from layer 0 would reach layer 1 the same step
        /// and not the other way round. Both take a step, which puts the callbacks after the
        /// whole frame's transition evaluation rather than inside it, and makes "a driver's
        /// write reaches the next layer inside the same frame" a claim about the VRChat client
        /// rather than about Mecanim.
        /// </summary>
        static void MeasureCrossLayerWrites()
        {
            foreach (int writerLayer in new[] { 0, 1 })
            {
                var controller = CrossLayer(writerLayer);
                using (var rig = Start(controller))
                {
                    rig.Step();
                    rig.animator.SetBool("Go", true);
                    rig.Step();
                    string what = "writer on layer " + writerLayer;
                    Assert.AreEqual(2, PlayModeProbeBehaviour.EnteredOn("Write"), what);
                    Assert.IsTrue(rig.animator.GetBool("X"),
                        "the write is visible from outside at the end of the step — " + what);
                    Assert.AreEqual(0, PlayModeProbeBehaviour.Enters("Read"),
                        "but no transition read it within that step — " + what);

                    rig.Step();
                    Assert.AreEqual(3, PlayModeProbeBehaviour.EnteredOn("Read"),
                        "the other layer moves exactly one step later — " + what);
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }

            {
                var controller = SameLayerWrite();
                using (var rig = Start(controller))
                {
                    rig.Step();
                    rig.animator.SetBool("Go", true);
                    rig.Step(3);
                    Assert.AreEqual(2, PlayModeProbeBehaviour.EnteredOn("Write"));
                    Assert.AreEqual(3, PlayModeProbeBehaviour.EnteredOn("Read"),
                        "the layer that wrote it waits a step too — so the delay is the "
                        + "callback's place in the frame, not the layer boundary");
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        // ---- M4: what an exit time waits for --------------------------------

        /// <summary>One second of nothing, or however many seconds are asked for. A state with
        /// no motion has a length anyway — Mecanim calls it a second — but exit time is read as
        /// a fraction of a lap, and a measurement of laps deserves laps somebody chose.</summary>
        static AnimationClip Seconds(float length)
        {
            var clip = new AnimationClip { name = length + "s" };
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.x",
                AnimationCurve.Constant(0f, length, 0f));
            return clip;
        }

        /// <summary>A single fall-through link: A drops into B on its exit time, with no
        /// condition and no blend. A clip length of 0 leaves A without a motion.</summary>
        static AnimatorController FallThroughLink(float exitTime, float clipSeconds)
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            var machine = controller.layers[0].stateMachine;
            var a = machine.AddState("A");
            var b = machine.AddState("B");
            machine.defaultState = a;
            Watch(a, "A");
            Watch(b, "B");
            if (clipSeconds > 0f) a.motion = Seconds(clipSeconds);
            var transition = a.AddTransition(b);
            transition.hasExitTime = true;
            transition.exitTime = exitTime;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            return controller;
        }

        /// <summary>The step A was left on, or 0 if it was still there after
        /// <paramref name="limit"/> of them. The state and the callback have to agree about
        /// which step it was, or the number is measuring one of them rather than the link.</summary>
        static int LeavesOn(float exitTime, float clipSeconds, int limit = 80)
        {
            var controller = FallThroughLink(exitTime, clipSeconds);
            try
            {
                using (var rig = Start(controller))
                {
                    for (int step = 1; step <= limit; step++)
                    {
                        rig.Step();
                        if (rig.State(0, "A", "B") != "B") continue;
                        Assert.AreEqual(step, PlayModeProbeBehaviour.EnteredOn("B"),
                            "the state row and OnStateEnter disagree about when the link fired");
                        return step;
                    }
                    return 0;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// M4 — an exit time of 0 does not mean "leave at once". It means the same as an exit
        /// time of 1: leave at the end of a lap of the state's own clip, or after the one
        /// second Mecanim gives a state with no motion of its own.
        ///
        /// This one is here because it was measured wrong first, and the way it was measured
        /// wrong is the lesson: four steps of watching a link that fires after sixty of them
        /// looks exactly like a link that never fires, and "exitTime 0 never fires" is what got
        /// written down. The window is eighty steps now, and the clip lengths are varied so the
        /// answer has to follow the lap rather than sit on a constant — half a second fires in
        /// half the steps, a quarter in a quarter of them.
        ///
        /// What it cost: the cyclic sync's Immediate route was written with exitTime 0 meaning
        /// "nothing to wait for" and paid a whole lap at every hop until this was measured —
        /// AsyncSyncApplier writes a millisecond now, and says why at length. The ways to leave
        /// at once are a small positive exit time (the last case here) and a route with no exit
        /// time whose condition is already true.
        /// </summary>
        static void MeasureExitTimeIsALap()
        {
            foreach (float clipSeconds in new[] { 0f, 1f, 0.5f, 0.25f })
            {
                // A state with no motion still runs for a second, so that is the lap to expect.
                float lap = clipSeconds > 0f ? clipSeconds : 1f;
                string what = clipSeconds > 0f ? clipSeconds + "s of clip" : "no motion";

                int zero = LeavesOn(0f, clipSeconds);
                Assert.AreNotEqual(0, zero, "exitTime 0 does fire, given a lap to wait for — " + what);
                Assert.AreEqual(lap, zero * Dt, Dt + 1e-4f,
                    "exitTime 0 fires at the end of the lap, within a step of it — " + what);

                Assert.AreEqual(zero, LeavesOn(1f, clipSeconds),
                    "exitTime 0 and exitTime 1 are the same instruction — " + what);
            }

            Assert.AreEqual(1, LeavesOn(0.01f, 0f),
                "a hundredth of a lap is over inside the first sixtieth of a second: THAT is "
                + "what 'leave at once' has to be written as");
        }

        // ---- M4b: a chain of fall-through links -----------------------------

        /// <summary>A → B → C, every link an exit-time fall-through with no condition.</summary>
        static AnimatorController FallThroughChain(float exitTime, bool withClip)
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            var machine = controller.layers[0].stateMachine;
            var a = machine.AddState("A");
            var b = machine.AddState("B");
            var c = machine.AddState("C");
            machine.defaultState = a;
            Watch(a, "A");
            Watch(b, "B");
            Watch(c, "C");
            if (withClip)
            {
                var clip = Seconds(1f);
                a.motion = clip;
                b.motion = clip;
                c.motion = clip;
            }
            foreach (var pair in new[] { (from: a, to: b), (from: b, to: c) })
            {
                var transition = pair.from.AddTransition(pair.to);
                transition.hasExitTime = true;
                transition.exitTime = exitTime;
                transition.hasFixedDuration = true;
                transition.duration = 0f;
            }
            return controller;
        }

        /// <summary>
        /// M4b — a chain of fall-through links walks one link per step, however little each
        /// link waits for.
        ///
        /// This is the half of the exit-time measurement the cyclic sync's latency is made of:
        /// N states of plumbing is N frames, and no amount of zero-length blending buys any of
        /// it back. Both variants are worth keeping — the one with clips because that is the
        /// shape a generated controller has, and the one without because a state with no motion
        /// is the shape a hand-built relay usually has, and they behave the same.
        /// </summary>
        static void MeasureFallThroughChain()
        {
            foreach (bool withClip in new[] { false, true })
            {
                // A hundredth of a lap: the first step of a sixtieth is already past it, so
                // every link is ready the moment its state is entered and only the frame
                // boundary is in the way.
                var controller = FallThroughChain(0.01f, withClip);
                using (var rig = Start(controller))
                {
                    string what = "clip: " + withClip;
                    rig.Step();
                    Assert.AreEqual("B", rig.State(0, "A", "B", "C"),
                        "one link on the first step — " + what);
                    rig.Step();
                    Assert.AreEqual("C", rig.State(0, "A", "B", "C"),
                        "and one on the second — " + what);
                    Assert.AreEqual(1, PlayModeProbeBehaviour.EnteredOn("B"), what);
                    Assert.AreEqual(2, PlayModeProbeBehaviour.EnteredOn("C"),
                        "a step never walks two links, however short the wait — " + what);
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        // ---- M5: what a zero-length transition looks like --------------------

        static AnimatorController Blend(float duration)
        {
            var controller = new AnimatorController();
            controller.AddLayer("L0");
            controller.AddParameter(Bool("Go"));
            var machine = controller.layers[0].stateMachine;
            var a = machine.AddState("A");
            var b = machine.AddState("B");
            machine.defaultState = a;
            Watch(a, "A");
            Watch(b, "B");
            Wire(a, b, duration).AddCondition(AnimatorConditionMode.If, 0f, "Go");
            return controller;
        }

        /// <summary>
        /// M5 — a zero-length transition cannot be observed from outside. By the end of the step
        /// that takes it the layer is simply in the destination: IsInTransition is false, there
        /// is no next state, and the transition info is empty. A quarter-second blend is visible
        /// in all three, and reports the destination as "next" while the current state is still
        /// the source.
        ///
        /// This is why the via row of DD DynamicAnalyze's waveform cannot name the transition a
        /// snap-cut went through: there is no moment at which anything is in it. The state
        /// bands, which change, are the only witness.
        /// </summary>
        static void MeasureZeroLengthTransition()
        {
            {
                var controller = Blend(0f);
                using (var rig = Start(controller))
                {
                    rig.Step();
                    rig.animator.SetBool("Go", true);
                    rig.Step();
                    var info = rig.animator.GetAnimatorTransitionInfo(0);
                    Assert.IsFalse(rig.animator.IsInTransition(0));
                    Assert.AreEqual("B", rig.State(0, "A", "B"), "already arrived");
                    Assert.AreEqual("?", rig.Next(0, "A", "B"), "and nothing is coming");
                    Assert.AreEqual(0f, info.duration, 1e-6f);
                    Assert.AreEqual(0f, info.normalizedTime, 1e-6f);
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("B"));
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }

            {
                var controller = Blend(0.25f);
                using (var rig = Start(controller))
                {
                    rig.Step();
                    rig.animator.SetBool("Go", true);
                    rig.Step();
                    var info = rig.animator.GetAnimatorTransitionInfo(0);
                    Assert.IsTrue(rig.animator.IsInTransition(0));
                    Assert.AreEqual("A", rig.State(0, "A", "B"), "still leaving");
                    Assert.AreEqual("B", rig.Next(0, "A", "B"), "and visibly on the way");
                    Assert.AreEqual(0.25f, info.duration, 1e-6f);
                    Assert.IsFalse(info.anyState);
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("B"),
                        "entered at the start of the blend, six frames before the bands agree");
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        // ---- M0: whether the callbacks happen at all -------------------------

        /// <summary>
        /// The premise under all of the above, and the one this repository had backwards.
        /// StateMachineBehaviour callbacks fire on an Animator stepped by hand, in Play mode and
        /// out of it, whether the component is enabled or not. A Parameter Driver run in the
        /// Editor therefore does get its OnStateEnter — what it does not get is the subscriber
        /// the SDK's driver raises its event to, which only the VRChat client and the emulator
        /// supply. "The rig runs drivers inert" is true; "because Mecanim never calls them" is
        /// not the reason.
        /// </summary>
        static void MeasureCallbacksFire()
        {
            foreach (bool enabled in new[] { false, true })
            {
                var controller = Blend(0f);
                PlayModeProbeBehaviour.Forget();
                var host = new GameObject("DaerD Play Mode Probe")
                {
                    hideFlags = HideFlags.DontSave,
                };
                var animator = host.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.runtimeAnimatorController = controller;
                animator.enabled = enabled;
                animator.Rebind();
                Assert.AreEqual(0, PlayModeProbeBehaviour.Enters("A"),
                    "Rebind alone enters nothing");

                PlayModeProbeBehaviour.Step = 1;
                animator.Update(Dt);
                Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"),
                    "a hand-stepped Animator raises OnStateEnter (enabled: " + enabled + ")");
                Assert.AreEqual(1, PlayModeProbeBehaviour.EnteredOn("A"));

                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// M0b — a layer's default state is entered on the first step, not at Rebind, and the
        /// one shape where it is not entered at all.
        ///
        /// The first half is what DD DynamicAnalyze's SimClient assumes when it serves the
        /// initial state's Parameter Drivers on the first frame of a run: Mecanim announces that
        /// entry, so the run is agreeing with a headset rather than inventing a frame. Rebind on
        /// its own puts the layer where it belongs without saying so, which is why nothing is
        /// served before the first step.
        ///
        /// The second half is the exception, and it is narrow: a state left inside the same step
        /// it is entered on is never announced at all. That is where an earlier reading of
        /// "the first state gets no OnStateEnter" came from — it was measured on a chain whose
        /// first link fires immediately, and it generalises to nothing but that. A driver on a
        /// state nothing rests in does not fire, here or on a headset.
        /// </summary>
        static void MeasureTheDefaultStatesEntry()
        {
            {
                var controller = Blend(0f);
                using (var rig = Start(controller))
                {
                    Assert.AreEqual(0, PlayModeProbeBehaviour.Enters("A"),
                        "Rebind puts the layer in its default state without announcing it");
                    rig.Step();
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"),
                        "the first step is where the default state is entered");
                    Assert.AreEqual(1, PlayModeProbeBehaviour.EnteredOn("A"));
                    rig.Step(10);
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("A"),
                        "and it is entered once, not once a step");
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }

            {
                var controller = FallThroughLink(0.01f, 0f);
                using (var rig = Start(controller))
                {
                    rig.Step();
                    Assert.AreEqual("B", rig.State(0, "A", "B"),
                        "the default state is gone inside the first step");
                    Assert.AreEqual(0, PlayModeProbeBehaviour.Enters("A"),
                        "and a state entered and left inside one step is never announced");
                    Assert.AreEqual(1, PlayModeProbeBehaviour.Enters("B"));
                    Assert.AreEqual(1, PlayModeProbeBehaviour.EnteredOn("B"));
                }
                UnityEngine.Object.DestroyImmediate(controller);
            }
        }

        // ---- in Play mode ----------------------------------------------------

        /// <summary>Leaves Play mode behind whatever happened, so one failed measurement does
        /// not take the rest of the suite with it.</summary>
        [TearDown]
        public void LeavePlayMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.isPlaying = false;
        }

        [UnityTest]
        public IEnumerator Play_TheCallbacksFireOnAHandSteppedAnimator()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying, "this half is the Play mode one");
            MeasureCallbacksFire();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_TheDefaultStateIsEnteredOnTheFirstStep()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureTheDefaultStatesEntry();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_EntryConditionsDecideOneLevelDownButNotWhenALayerStarts()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureEntryConditions();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_ARootEntryIsReadAgainEveryTimeExitIsPassed()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureEntryAfterExit();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_ASavedControllerAnswersEntryTheSameWay()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureEntryConditionsOnASavedAsset();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_AStateEnteredFromItselfIsEnteredAgain()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureSelfReEntry();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_AWriteFromOnStateEnterIsReadOneStepLater()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureCrossLayerWrites();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_AnExitTimeOfZeroWaitsForTheEndOfALap()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureExitTimeIsALap();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_AFallThroughChainWalksOneLinkPerStep()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureFallThroughChain();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator Play_AZeroLengthTransitionIsOverBeforeItCanBeSeen()
        {
            yield return new EnterPlayMode();
            Assert.IsTrue(Application.isPlaying);
            MeasureZeroLengthTransition();
            yield return new ExitPlayMode();
        }

        // ---- and out of it ---------------------------------------------------

        [Test]
        public void Edit_TheCallbacksFireOnAHandSteppedAnimator()
        {
            Assert.IsFalse(Application.isPlaying, "this half is the Edit mode one");
            MeasureCallbacksFire();
        }

        [Test]
        public void Edit_TheDefaultStateIsEnteredOnTheFirstStep()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureTheDefaultStatesEntry();
        }

        [Test]
        public void Edit_EntryConditionsDecideOneLevelDownButNotWhenALayerStarts()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureEntryConditions();
        }

        [Test]
        public void Edit_ARootEntryIsReadAgainEveryTimeExitIsPassed()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureEntryAfterExit();
        }

        [Test]
        public void Edit_ASavedControllerAnswersEntryTheSameWay()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureEntryConditionsOnASavedAsset();
        }

        [Test]
        public void Edit_AStateEnteredFromItselfIsEnteredAgain()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureSelfReEntry();
        }

        [Test]
        public void Edit_AWriteFromOnStateEnterIsReadOneStepLater()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureCrossLayerWrites();
        }

        [Test]
        public void Edit_AnExitTimeOfZeroWaitsForTheEndOfALap()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureExitTimeIsALap();
        }

        [Test]
        public void Edit_AFallThroughChainWalksOneLinkPerStep()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureFallThroughChain();
        }

        [Test]
        public void Edit_AZeroLengthTransitionIsOverBeforeItCanBeSeen()
        {
            Assert.IsFalse(Application.isPlaying);
            MeasureZeroLengthTransition();
        }
    }
}
