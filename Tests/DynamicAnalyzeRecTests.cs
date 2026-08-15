using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// DD DynamicAnalyze's Rec mood: what it can find in somebody else's Play mode, and what it
    /// writes down once it has found it.
    ///
    /// The tools this exists for — GestureManager, Av3Emulator — are not installed here and are
    /// not named by the code either, which is the whole design: both drive an avatar through a
    /// PlayableGraph, so the graph is what gets read and any tool that builds one is served by
    /// the same lines. What the tests build instead is a graph by hand, of the shape those tools
    /// build: a layer mixer with an <see cref="AnimatorControllerPlayable"/> per VRC playable
    /// layer, written out to an Animator that holds no controller of its own.
    ///
    /// <para>Half of this file is measurement rather than assertion of our own code, and
    /// deliberately so.</para> The Playable API is documented thinly enough that every load
    /// bearing thing about it here was checked by running it before any of the recorder was
    /// written: whether Unity hands out a graph nobody registered, whether an
    /// AnimatorOverrideController changes the hashes underneath, whether an Animator with a
    /// plain controller has a graph at all. Those answers are the reason the recorder is shaped
    /// the way it is, so they are pinned here — if a Unity upgrade changes one, the thing that
    /// fails is this file and the answer is to re-read the design, not to nudge the test.
    ///
    /// Nothing here touches the VRChat SDK, so it runs the same with it installed and without.
    /// </summary>
    public class DynamicAnalyzeRecTests
    {
        const float Dt = 1f / 60f;

        // ---- the rig ---------------------------------------------------------

        /// <summary>Idle → On when Go goes up, on a layer of the given name.</summary>
        static AnimatorController Controller(string layer, string parameter = "Go")
        {
            var controller = new AnimatorController();
            controller.name = "Rec " + layer;
            controller.hideFlags = HideFlags.HideAndDontSave;
            controller.AddLayer(layer);
            controller.AddParameter(parameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter("X", AnimatorControllerParameterType.Float);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;
            var transition = idle.AddTransition(on);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, parameter);
            return controller;
        }

        /// <summary>
        /// An avatar as a tool hands one to Mecanim: one Animator, no controller on it, and a
        /// graph writing to it whose inputs are a controller playable each.
        ///
        /// Everything it makes is destroyed on Dispose, graph included — a graph left behind is
        /// not only a leak, it is a candidate the next test would find (see
        /// <see cref="Graphs_KeepAnsweringAfterTheyAreDestroyed"/>).
        /// </summary>
        sealed class Rig : IDisposable
        {
            public readonly Animator animator;
            public readonly PlayableGraph graph;
            readonly GameObject _host;
            readonly List<AnimatorControllerPlayable> _playables =
                new List<AnimatorControllerPlayable>();

            public Rig(params RuntimeAnimatorController[] controllers)
            {
                _host = new GameObject("DaerD Rec Rig");
                _host.hideFlags = HideFlags.HideAndDontSave;
                animator = _host.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = false;

                graph = PlayableGraph.Create("DaerD Rec Rig Graph");
                // Stepped by hand, like everything else DaerD measures: a graph the player loop
                // drives would advance between an assertion and the next one.
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var mixer = AnimationLayerMixerPlayable.Create(graph, controllers.Length);
                for (int i = 0; i < controllers.Length; i++)
                {
                    var playable = AnimatorControllerPlayable.Create(graph, controllers[i]);
                    _playables.Add(playable);
                    graph.Connect(playable, 0, mixer, i);
                    mixer.SetInputWeight(i, 1f);
                }
                var output = AnimationPlayableOutput.Create(graph, "DaerD Rec", animator);
                output.SetSourcePlayable(mixer);
                graph.Play();
            }

            public AnimatorControllerPlayable Playable(int index) => _playables[index];

            public Rig Step(int frames = 1)
            {
                for (int i = 0; i < frames; i++) graph.Evaluate(Dt);
                return this;
            }

            public void Dispose()
            {
                if (graph.IsValid()) graph.Destroy();
                if (_host != null) UnityEngine.Object.DestroyImmediate(_host);
            }
        }

        static string[] Labels(SignalTrace trace, string row)
        {
            var signal = trace.Find(Simulation.PlayScope, row);
            Assert.IsNotNull(signal, "no row called '" + row + "'");
            return signal.labels;
        }

        static string TextAt(SignalTrace trace, string row, int frame)
        {
            var signal = trace.Find(Simulation.PlayScope, row);
            Assert.IsNotNull(signal, "no row called '" + row + "'");
            return signal.TextAt(frame);
        }

        /// <summary>Samples one frame per call with a frame counter that always moves — what a
        /// well-behaved editor update would hand it.</summary>
        static void Look(PlayRecorder recorder, int frames = 1)
        {
            for (int i = 0; i < frames; i++)
                Assert.IsTrue(recorder.Sample(recorder.Frames + 1, recorder.Frames * Dt),
                    "the recorder refused a frame it had not seen");
        }

        // ---- what the Playable API actually does -----------------------------

        /// <summary>
        /// Unity hands out every PlayableGraph in the editor, including one built by hand a
        /// moment ago — which is the single fact the whole feature stands on. Neither
        /// GestureManager nor Av3Emulator registers its graph anywhere DaerD could look it up,
        /// and neither has an API for being asked; they simply build a graph, and this is how a
        /// graph is found without knowing who built it.
        ///
        /// The output's target is the Animator by reference, not by name or by scene path, so
        /// "which avatar is this" is answered exactly rather than heuristically.
        /// </summary>
        [Test]
        public void Graphs_AreHandedOutByUnity_WithTheAnimatorTheyWriteTo()
        {
            using (var controller = Owned(Controller("Base")))
            using (var rig = new Rig(controller.asset))
            {
                bool found = false;
                foreach (var graph in UnityEditor.Playables.Utility.GetAllGraphs())
                    if (graph.IsValid() && graph.GetEditorName() == "DaerD Rec Rig Graph")
                        found = true;
                Assert.IsTrue(found, "a hand-built graph is not in Unity's list — the recorder "
                    + "has no way to find GestureManager's either");

                CollectionAssert.Contains(PlayRecorder.Driven(), rig.animator,
                    "the animator a graph writes to is not offered as a candidate");
                Assert.AreEqual(1, PlayRecorder.PlayablesOn(rig.animator).Count);
            }
        }

        /// <summary>
        /// A controller playable is several playables inside — measured, an
        /// AnimatorControllerPlayable of one layer reports a graph playable count of eight — so
        /// the walk that looks for them stops at the first one it reaches rather than descending
        /// into it. Descending would find its parts and count them as more controllers.
        /// </summary>
        [Test]
        public void Graphs_AreWalkedDownToTheControllersAndNoFurther()
        {
            using (var one = Owned(Controller("Base")))
            using (var two = Owned(Controller("FX")))
            using (var rig = new Rig(one.asset, two.asset))
            {
                var playables = PlayRecorder.PlayablesOn(rig.animator);
                Assert.AreEqual(2, playables.Count,
                    "the mixer has two controller playables under it and the walk found "
                    + playables.Count);
                Assert.Greater(rig.graph.GetPlayableCount(), 2,
                    "a controller playable is more than one playable, which is why the walk stops");
            }
        }

        /// <summary>
        /// A destroyed graph goes on being handed out, and goes on answering every question put
        /// to it — measured, and the reason <see cref="PlayRecorder.Matching"/> takes the LAST
        /// match rather than the first. An avatar re-selected inside one Play session leaves its
        /// old graph in the list beside the new one, and the newest is the one really running.
        ///
        /// What does clear out is a graph whose Animator has gone: the output answers a real
        /// null for its target, so last session's graphs cannot be mistaken for this session's.
        /// </summary>
        [Test]
        public void Graphs_KeepAnsweringAfterTheyAreDestroyed()
        {
            using (var controller = Owned(Controller("Base")))
            {
                var host = new GameObject("DaerD Rec Ghost");
                host.hideFlags = HideFlags.HideAndDontSave;
                var animator = host.AddComponent<Animator>();
                var graph = PlayableGraph.Create("DaerD Rec Ghost Graph");
                var playable = AnimatorControllerPlayable.Create(graph, controller.asset);
                var output = AnimationPlayableOutput.Create(graph, "o", animator);
                output.SetSourcePlayable(playable);
                graph.Play();
                graph.Destroy();

                Assert.AreEqual(1, PlayRecorder.PlayablesOn(animator).Count,
                    "a destroyed graph has stopped being handed out, which would be an "
                    + "improvement — re-read PlayRecorder.Matching, which works around it");

                UnityEngine.Object.DestroyImmediate(host);
                Assert.IsEmpty(PlayRecorder.PlayablesOn(animator),
                    "a graph whose animator is gone still claims one");
            }
        }

        /// <summary>
        /// An AnimatorOverrideController changes nothing a recording reads. GestureManager hands
        /// the avatar's controller to Mecanim wrapped in one, so the object running is never the
        /// object in the window's field — and if the wrapper renamed the layers or re-hashed the
        /// states, matching by layer name and naming states by full path hash would both be
        /// built on sand.
        ///
        /// Measured: the layer names are the base controller's, and every state's fullPathHash is
        /// the one <c>Animator.StringToHash("Layer.State")</c> gives for the base.
        /// </summary>
        [Test]
        public void Graphs_ThroughAnOverrideController_ReportTheBasesNamesAndHashes()
        {
            using (var controller = Owned(Controller("Base")))
            {
                var over = new AnimatorOverrideController(controller.asset);
                over.name = "Rec Override";
                over.hideFlags = HideFlags.HideAndDontSave;
                using (var rig = new Rig(over))
                {
                    rig.Step();
                    var playable = rig.Playable(0);
                    Assert.AreEqual("Base", playable.GetLayerName(0));
                    Assert.AreEqual(Animator.StringToHash("Base.Idle"),
                        playable.GetCurrentAnimatorStateInfo(0).fullPathHash,
                        "the wrapper moved the hashes, so a recording could not name a state");
                    Assert.AreEqual(0, PlayRecorder.Matching(controller.asset,
                            PlayRecorder.PlayablesOn(rig.animator)),
                        "the wrapped controller is not recognised as the one in the field");
                }
                UnityEngine.Object.DestroyImmediate(over);
            }
        }

        /// <summary>
        /// An Animator with an ordinary controller on it has a PlayableGraph of its own — Unity
        /// makes one the moment the controller is assigned, and hands it out with everybody
        /// else's. Measured, and it is why the Animator-reading fallback is a safety net rather
        /// than the road plain playback takes: plain playback goes through the graph like
        /// everything else, and gets the state rows that come with a matched playable.
        /// </summary>
        [Test]
        public void Graphs_ExistForAPlainAnimatorToo_SoPlainPlaybackIsRecordedLikeTheRest()
        {
            using (var controller = Owned(Controller("Base")))
            {
                var host = new GameObject("DaerD Rec Plain");
                host.hideFlags = HideFlags.HideAndDontSave;
                var animator = host.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = false;
                Assert.IsEmpty(PlayRecorder.PlayablesOn(animator),
                    "an Animator with no controller has nothing to read");

                animator.runtimeAnimatorController = controller.asset;
                Assert.AreEqual(1, PlayRecorder.PlayablesOn(animator).Count,
                    "assigning a controller did not give the Animator a graph");

                var recorder = PlayRecorder.On(animator, controller.asset);
                Assert.IsTrue(recorder.FromGraph);
                Assert.IsTrue(recorder.Matched);
                animator.Rebind();
                animator.Update(Dt);
                Look(recorder);
                Assert.AreEqual("Idle", TextAt(recorder.Trace, "Base/state", 0));

                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ---- what it records -------------------------------------------------

        /// <summary>
        /// The whole of it, end to end: a graph of the shape a tool builds, a controller playable
        /// picked out of it by its layer names, and the parameters and states of the avatar
        /// written into a trace of exactly the shape a simulated run produces.
        /// </summary>
        [Test]
        public void Recorder_WritesDownTheParametersAndStatesOfTheGraphItMatched()
        {
            using (var other = Owned(Controller("Gesture", "Hand")))
            using (var controller = Owned(Controller("FX")))
            using (var rig = new Rig(other.asset, controller.asset))
            {
                Assert.AreEqual(1, PlayRecorder.Matching(controller.asset,
                        PlayRecorder.PlayablesOn(rig.animator)),
                    "the layer names picked the wrong playable out of the two");

                var recorder = PlayRecorder.On(rig.animator, controller.asset);
                Assert.IsTrue(recorder.Matched);
                Assert.IsTrue(recorder.FromGraph);

                rig.Step();
                Look(recorder);
                rig.Playable(1).SetBool("Go", true);
                rig.Step(2);
                Look(recorder);

                Assert.AreEqual(2, recorder.Frames);
                Assert.AreEqual(0, recorder.Missed);

                var go = recorder.Trace.Find(Simulation.PlayScope, "Go");
                Assert.IsNotNull(go, "the recording has no row for the parameter that moved");
                Assert.AreEqual(0f, go.At(0));
                Assert.AreEqual(1f, go.At(1));

                CollectionAssert.AreEqual(new[] { "Idle", "On" }, Labels(recorder.Trace, "FX/state"));
                Assert.AreEqual("Idle", TextAt(recorder.Trace, "FX/state", 0));
                Assert.AreEqual("On", TextAt(recorder.Trace, "FX/state", 1));

                // The other playable's controller is not this one, so none of its rows are here
                // — a recording says what the controller in the field did and nothing else.
                Assert.IsNull(recorder.Trace.Find(Simulation.PlayScope, "Gesture/state"));
                Assert.IsNull(recorder.Trace.Find(Simulation.PlayScope, "Hand"));
            }
        }

        /// <summary>
        /// Layers matched by name and not by position. The multiset test lets a playable agree
        /// with the controller on which layers exist while disagreeing on their order, and
        /// reading layer 0's state under layer 1's labels would give rows that are wrong rather
        /// than missing — the one failure mode a recording must not have.
        /// </summary>
        [Test]
        public void Recorder_LinesLayersUpByName_NotByPosition()
        {
            using (var running = Owned(TwoLayers("Second", "First")))
            using (var asked = Owned(TwoLayers("First", "Second")))
            using (var rig = new Rig(running.asset))
            {
                Assert.AreEqual(0, PlayRecorder.Matching(asked.asset,
                        PlayRecorder.PlayablesOn(rig.animator)),
                    "the same layers in another order are the same controller to the matcher");

                var recorder = PlayRecorder.On(rig.animator, asked.asset);
                rig.Step();
                Look(recorder);
                // Each layer's state row carries that layer's own state, whichever index it sits
                // at in the playable.
                Assert.AreEqual("In First", TextAt(recorder.Trace, "First/state", 0));
                Assert.AreEqual("In Second", TextAt(recorder.Trace, "Second/state", 0));
            }
        }

        static AnimatorController TwoLayers(string first, string second)
        {
            var controller = new AnimatorController();
            controller.name = "Rec " + first + "+" + second;
            controller.hideFlags = HideFlags.HideAndDontSave;
            foreach (string layer in new[] { first, second })
            {
                controller.AddLayer(layer);
                var machine = controller.layers[controller.layers.Length - 1].stateMachine;
                machine.defaultState = machine.AddState("In " + layer);
            }
            return controller;
        }

        /// <summary>
        /// A controller nothing in the graph is running: the parameters of whatever IS running
        /// are recorded and nothing else. Not an error and not an empty trace — a reader who
        /// picked the wrong controller still gets to see the avatar's parameters move, which is
        /// usually how they work out that they picked the wrong one.
        /// </summary>
        [Test]
        public void Recorder_WithNothingRunningItsController_RecordsParametersOnly()
        {
            using (var running = Owned(Controller("Gesture", "Hand")))
            using (var asked = Owned(Controller("FX")))
            using (var rig = new Rig(running.asset))
            {
                Assert.AreEqual(-1, PlayRecorder.Matching(asked.asset,
                    PlayRecorder.PlayablesOn(rig.animator)));

                var recorder = PlayRecorder.On(rig.animator, asked.asset);
                Assert.IsFalse(recorder.Matched);
                Assert.IsTrue(recorder.FromGraph);

                rig.Step();
                Look(recorder);
                Assert.IsNotNull(recorder.Trace.Find(Simulation.PlayScope, "Hand"),
                    "the parameters of what is actually running are still recorded");
                Assert.IsNull(recorder.Trace.Find(Simulation.PlayScope, "Gesture/state"),
                    "a layer row without a matched controller would be a row nothing named");
                Assert.IsNull(recorder.Trace.Find(Simulation.PlayScope, "FX/state"));
            }
        }

        /// <summary>
        /// The fallback: an Animator whose graph carries no controller at all — a clip played
        /// straight at it — is read through the Animator component instead. Reachable rather than
        /// theoretical, and the reason the reading face is an abstraction with two sides.
        /// </summary>
        [Test]
        public void Recorder_WithNoControllerInTheGraph_ReadsTheAnimatorDirectly()
        {
            using (var controller = Owned(Controller("Base")))
            {
                var host = new GameObject("DaerD Rec Clip");
                host.hideFlags = HideFlags.HideAndDontSave;
                var animator = host.AddComponent<Animator>();
                var clip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
                clip.SetCurve(string.Empty, typeof(Transform), "localPosition.x",
                    AnimationCurve.Linear(0f, 0f, 1f, 1f));
                var graph = PlayableGraph.Create("DaerD Rec Clip Graph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(graph, "o", animator);
                output.SetSourcePlayable(AnimationClipPlayable.Create(graph, clip));
                graph.Play();

                CollectionAssert.Contains(PlayRecorder.Driven(), animator);
                Assert.IsEmpty(PlayRecorder.PlayablesOn(animator));

                var recorder = PlayRecorder.On(animator, controller.asset);
                Assert.IsFalse(recorder.FromGraph, "there is no controller playable to read");
                Assert.IsFalse(recorder.Matched);

                graph.Destroy();
                UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        /// And that the fallback would record the same numbers if it were reached. The two sides
        /// of <see cref="PlaySource"/> are separate forwarders to methods Unity spelt identically
        /// and never gave a common interface — measured, an AnimatorControllerPlayable implements
        /// IPlayable and IEquatable and nothing else — so the only thing keeping them saying the
        /// same thing is a test that asks both.
        /// </summary>
        [Test]
        public void PlaySource_AnswersTheSameOffAnAnimatorAsOffAPlayable()
        {
            using (var controller = Owned(Controller("Base")))
            using (var rig = new Rig(controller.asset))
            {
                var host = new GameObject("DaerD Rec Twin");
                host.hideFlags = HideFlags.HideAndDontSave;
                var animator = host.AddComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = false;
                animator.runtimeAnimatorController = controller.asset;
                animator.Rebind();

                var fromPlayable = PlaySource.Of(rig.Playable(0));
                var fromAnimator = PlaySource.Of(animator);

                rig.Playable(0).SetBool("Go", true);
                animator.SetBool("Go", true);
                rig.Step(2);
                animator.Update(Dt);
                animator.Update(Dt);

                Assert.IsTrue(fromPlayable.Alive);
                Assert.IsTrue(fromAnimator.Alive);
                Assert.AreEqual(fromPlayable.ParameterCount, fromAnimator.ParameterCount);
                Assert.AreEqual(fromPlayable.ParameterAt(0).name, fromAnimator.ParameterAt(0).name);
                Assert.AreEqual(fromPlayable.LayerCount, fromAnimator.LayerCount);
                Assert.AreEqual(fromPlayable.LayerName(0), fromAnimator.LayerName(0));
                Assert.AreEqual(fromPlayable.LayerWeight(0), fromAnimator.LayerWeight(0));
                Assert.AreEqual(fromPlayable.StateHash(0), fromAnimator.StateHash(0));
                Assert.AreEqual(fromPlayable.InTransition(0), fromAnimator.InTransition(0));
                Assert.AreEqual(
                    fromPlayable.Read("Go", AnimatorControllerParameterType.Bool),
                    fromAnimator.Read("Go", AnimatorControllerParameterType.Bool));
                Assert.AreEqual(Animator.StringToHash("Base.On"), fromAnimator.StateHash(0),
                    "both sides moved to the same state, so the comparison above meant something");

                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        // ---- the sampling ----------------------------------------------------

        /// <summary>
        /// One sample per frame of the RUNNING avatar, not per call. The editor's update fires
        /// more than once a frame and less than once a frame in turn, and both have to be handled
        /// where they happen: a second call inside one frame is a duplicate row with a step of
        /// zero beside it, and a call that skipped four frames is four frames this has nothing to
        /// say about. The first is refused; the second is counted and admitted to.
        /// </summary>
        [Test]
        public void Sample_TakesOneLookPerFrame_AndCountsTheOnesItMissed()
        {
            using (var controller = Owned(Controller("Base")))
            using (var rig = new Rig(controller.asset))
            {
                var recorder = PlayRecorder.On(rig.animator, controller.asset);

                Assert.IsTrue(recorder.Sample(100, 4f));
                Assert.IsFalse(recorder.Sample(100, 4.5f), "the same frame twice is one row");
                Assert.AreEqual(1, recorder.Frames);
                Assert.AreEqual(0, recorder.Missed);

                Assert.IsTrue(recorder.Sample(101, 4.25f));
                Assert.AreEqual(0, recorder.Missed, "consecutive frames miss nothing");

                Assert.IsTrue(recorder.Sample(105, 4.5f));
                Assert.AreEqual(3, recorder.Missed, "101 to 105 is three frames nobody looked at");
                Assert.AreEqual(3, recorder.Frames);

                // Time counts from the first sample, so a recording starts at zero the way a run
                // does and the two lie over each other without anybody doing arithmetic.
                Assert.AreEqual(0f, recorder.Trace.TimeAt(0), 1e-5f);
                Assert.AreEqual(0f, recorder.Trace.StepAt(0), 1e-5f,
                    "nothing elapsed before the first look");
                Assert.AreEqual(0.25f, recorder.Trace.TimeAt(1), 1e-5f);
                Assert.AreEqual(0.25f, recorder.Trace.StepAt(1), 1e-5f);
                Assert.AreEqual(0.5f, recorder.Trace.TimeAt(2), 1e-5f);
                Assert.AreEqual(0.25f, recorder.Trace.StepAt(2), 1e-5f);
            }
        }

        /// <summary>
        /// Nothing is read once the avatar is gone — which is what leaving Play mode does to it,
        /// and the recorder is not told.
        ///
        /// The avatar and not the graph, because the graph cannot be asked: destroying one
        /// leaves every handle into it valid (see
        /// <see cref="Graphs_KeepAnsweringAfterTheyAreDestroyed"/>). Both halves are asserted
        /// here so the limit is written down rather than discovered — a graph dropped under a
        /// living avatar is a case this goes on recording stale values for.
        /// </summary>
        [Test]
        public void Sample_StopsWhenTheAvatarItWasWatchingIsGone()
        {
            using (var controller = Owned(Controller("Base")))
            {
                PlayRecorder recorder;
                using (var rig = new Rig(controller.asset))
                {
                    recorder = PlayRecorder.On(rig.animator, controller.asset);
                    rig.Step();
                    Assert.IsTrue(recorder.Sample(1, 0f));
                    Assert.IsTrue(recorder.Alive);
                    rig.graph.Destroy();
                    Assert.IsTrue(recorder.Alive,
                        "a destroyed graph has become detectable, which would let the recorder "
                        + "stop on its own — re-read PlayRecorder.Alive");
                }
                Assert.IsFalse(recorder.Alive);
                Assert.IsFalse(recorder.Sample(2, 0.1f));
                Assert.AreEqual(1, recorder.Frames, "the trace keeps what it already had");
            }
        }

        // ---- and what the rest of the window can do with it --------------------

        /// <summary>
        /// A recording is a trace like any other. Its rows are named the way a simulated run's
        /// are — parameter by name, then state, transition, via and weight per layer — which is
        /// what lets it be saved as a clip, laid under a run as a ghost, and read by the findings.
        ///
        /// Its scope is not a client's, on purpose: a client is a copy of the avatar this module
        /// made and a reader may type into its cells, and nothing here can reach into somebody
        /// else's Play mode to set a parameter.
        /// </summary>
        [Test]
        public void Recording_IsShapedLikeARun_ExceptThatNobodyCanPokeIt()
        {
            using (var controller = Owned(Controller("Base")))
            using (var rig = new Rig(controller.asset))
            {
                var recorder = PlayRecorder.On(rig.animator, controller.asset);
                rig.Step();
                Look(recorder, 3);

                foreach (string row in new[] { "Base/state", "Base/transition", "Base/via",
                             "Base/weight", "Go", "X" })
                    Assert.IsNotNull(recorder.Trace.Find(Simulation.PlayScope, row),
                        "a run records a row called '" + row + "' and a recording does not");

                var run = Simulation.Run(controller.asset, new SimSettings
                {
                    clock = new SimClock { fps = 60f, seconds = 0.1f, seed = 1 },
                });
                foreach (var signal in recorder.Trace.Signals)
                {
                    var same = run.Find(Simulation.LocalScope, signal.name);
                    Assert.IsNotNull(same, "a recording has a row a run does not: " + signal.name);
                    Assert.AreEqual(same.kind, signal.kind, "the two rows called '" + signal.name
                        + "' are drawn differently, so a ghost of one over the other is a lie");
                }

                Assert.IsFalse(Simulation.IsClient(Simulation.PlayScope),
                    "a recorded row would offer a field that writes nowhere");

                // The findings a trace answers on its own speak about it, which is what a
                // stopped recording is for.
                var findings = RunFindings.For(recorder.Trace, null);
                Assert.IsNotEmpty(findings, "nothing was said about a run that never left Idle");
            }
        }

        // ---- more than one avatar in one recording ---------------------------

        /// <summary>
        /// The wearer and one other person's copy of the same avatar, in ONE trace: same frames,
        /// same clock, a scope each. That is the whole point of recording them together — two
        /// recordings taken separately would have different frame numbers and different starting
        /// instants, and lining them up afterwards would be arithmetic nobody should trust.
        ///
        /// The copies are handed in rather than looked up, which is what lets this test exist at
        /// all without Av3Emulator installed: who the copies ARE is the tool's question (see
        /// DynamicAnalyzeToolsTests), and what to do with them once named is this one's.
        /// </summary>
        [Test]
        public void Recorder_WithCopiesOfTheAvatar_PutsThemInOneTraceUnderAScopeEach()
        {
            using (var controller = Owned(Controller("Base")))
            using (var wearer = new Rig(controller.asset))
            using (var copy = new Rig(controller.asset))
            {
                var recorder = PlayRecorder.On(wearer.animator, controller.asset,
                    new List<Animator> { copy.animator });
                Assert.AreEqual(2, recorder.Sources);
                Assert.IsTrue(recorder.Matched, "the wearer's own graph runs the controller");

                wearer.Step();
                copy.Step();
                Look(recorder);

                // Only the wearer is told to go, which is what makes the two scopes worth
                // reading side by side: what crossed to the copy is what a run would model.
                wearer.Playable(0).SetBool("Go", true);
                wearer.Step(2);
                copy.Step(2);
                Look(recorder);

                Assert.AreEqual(2, recorder.Frames);
                Assert.AreEqual("On", TextAt(recorder.Trace, "Base/state", 1));
                var theirs = recorder.Trace.Find(Simulation.PlayRemoteScopeAt(0), "Base/state");
                Assert.IsNotNull(theirs, "the copy has no state row of its own");
                Assert.AreEqual("Idle", theirs.TextAt(1),
                    "nothing was pressed on the copy, so it should not have moved");
                Assert.AreEqual(recorder.Frames, theirs.Frames,
                    "every row of a trace has as many samples as the trace has frames");
                Assert.IsNotNull(recorder.Trace.Find(Simulation.PlayRemoteScopeAt(0), "Go"),
                    "the copy's parameters are recorded too");
            }
        }

        /// <summary>Three copies are spelt the way three remotes are — the first bare, the rest
        /// numbered — so a reader who has learnt one naming has learnt the other. The scopes are
        /// avatars for everything that reads state rows and clients for nothing: a recorded copy
        /// belongs to somebody else's Play mode and cannot be typed into.</summary>
        [Test]
        public void TheCopiesScopesAreNamedAndClassedLikeTheRunsAre()
        {
            using (var controller = Owned(Controller("Base")))
            using (var wearer = new Rig(controller.asset))
            using (var first = new Rig(controller.asset))
            using (var second = new Rig(controller.asset))
            {
                var recorder = PlayRecorder.On(wearer.animator, controller.asset,
                    new List<Animator> { first.animator, second.animator });
                Look(recorder);

                Assert.AreEqual(3, recorder.Sources);
                Assert.AreEqual("Play Remote", Simulation.PlayRemoteScopeAt(0));
                Assert.AreEqual("Play Remote 2", Simulation.PlayRemoteScopeAt(1));
                foreach (string scope in new[] { Simulation.PlayScope,
                             Simulation.PlayRemoteScopeAt(0), Simulation.PlayRemoteScopeAt(1) })
                {
                    Assert.IsNotNull(recorder.Trace.Find(scope, "Base/state"),
                        "no state row under " + scope);
                    Assert.IsTrue(Simulation.IsAvatar(scope),
                        scope + " is not read as an avatar, so no finding will speak about it");
                    Assert.IsFalse(Simulation.IsClient(scope),
                        scope + " would offer a value cell that writes nowhere");
                    Assert.IsFalse(Simulation.IsRemote(scope),
                        scope + " reads as a simulated remote, which it is not");
                }
            }
        }

        /// <summary>
        /// A copy that goes away mid-recording holds its last value and the recording carries
        /// on; the WEARER going away ends it. Av3Emulator destroys a clone the moment somebody
        /// unticks it, so this is the ordinary case rather than an edge one, and the two halves
        /// have to differ: every row of a trace has exactly as many samples as the trace has
        /// frames, and every reader is written on that.
        /// </summary>
        [Test]
        public void ACopyLeavingHoldsItsLastValue_AndOnlyTheWearerLeavingStopsTheRecording()
        {
            using (var controller = Owned(Controller("Base")))
            using (var wearer = new Rig(controller.asset))
            {
                using (var copy = new Rig(controller.asset))
                {
                    var recorder = PlayRecorder.On(wearer.animator, controller.asset,
                        new List<Animator> { copy.animator });
                    copy.Playable(0).SetBool("Go", true);
                    copy.Step(2);
                    Look(recorder, 2);
                    var theirs = recorder.Trace.Find(Simulation.PlayRemoteScopeAt(0), "Go");
                    Assert.AreEqual(1f, theirs.At(1), "the copy's own value was not recorded");

                    copy.Dispose();
                    Assert.IsTrue(recorder.Alive, "the wearer is still there");
                    Look(recorder, 2);
                    Assert.AreEqual(4, recorder.Frames);
                    Assert.AreEqual(recorder.Frames, theirs.Frames);
                    Assert.AreEqual(1f, theirs.At(3),
                        "the held value is the last one that was read");

                    wearer.Dispose();
                    Assert.IsFalse(recorder.Alive);
                    Assert.IsFalse(recorder.Sample(recorder.Frames + 1, 1f));
                }
            }
        }

        // ---- the window's third mood -----------------------------------------

        /// <summary>
        /// A window layout saved before Rec existed opens in the mood it was closed in.
        ///
        /// An editor layout is serialized fields by NAME, so the compatibility question is
        /// entirely about which names survive: the mood is two booleans rather than one enum
        /// precisely because an enum would have had to give the old <c>false</c> a number, and
        /// every layout ever saved carries <c>_live</c> and nothing else. It goes on being read
        /// alone; <c>_rec</c> is absent from such a layout and reads as its default.
        /// </summary>
        [Test]
        public void TheWindowsMood_IsStoredSoThatAnOldLayoutOpensWhereItWas()
        {
            var window = ScriptableObject.CreateInstance<DynamicAnalyzeWindow>();
            try
            {
                var serialized = new UnityEditor.SerializedObject(window);
                var live = serialized.FindProperty("_live");
                var rec = serialized.FindProperty("_rec");
                Assert.IsNotNull(live,
                    "the flag every saved layout carries has been renamed, so every one of them "
                    + "now opens in the wrong mood");
                Assert.IsNotNull(rec, "the Rec flag is not serialized, so the mood is forgotten");
                Assert.IsFalse(rec.boolValue,
                    "a layout that predates Rec has no value for it and must read as not Rec");
                Assert.IsFalse(live.boolValue,
                    "a fresh window opens computing runs, the way it always has");

                // And the armed toggle, which is the one setting that has to cross a domain
                // reload to do its job at all.
                Assert.IsNotNull(serialized.FindProperty("_armed"),
                    "arming does not survive entering Play mode, which is the only moment it "
                    + "would ever be read");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // ---- in a real Play mode ---------------------------------------------

        /// <summary>
        /// The one end-to-end, in the mode the feature only ever runs in.
        ///
        /// What it is really here to pin is the asymmetry the whole design rests on: entering
        /// Play mode reloads the domain and takes every non-serialized object with it, and
        /// LEAVING it does not — so a recording made inside Play mode is still there afterwards,
        /// which is what makes it worth making. Everything is therefore built after
        /// <see cref="EnterPlayMode"/> and read after <see cref="ExitPlayMode"/>; a trace built
        /// before the enter would not survive to be recorded into.
        ///
        /// The graph is left on Unity's own clock here rather than stepped by hand — this is the
        /// one test where the point is that real frames happen.
        /// </summary>
        [UnityTest]
        [Category("PlayModeProbe")]
        public IEnumerator Recording_MadeInPlayMode_IsStillThereAfterwards()
        {
            yield return new EnterPlayMode();

            var controller = Controller("Base");
            var host = new GameObject("DaerD Rec Play");
            var animator = host.AddComponent<Animator>();
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var graph = PlayableGraph.Create("DaerD Rec Play Graph");
            var playable = AnimatorControllerPlayable.Create(graph, controller);
            var output = AnimationPlayableOutput.Create(graph, "o", animator);
            output.SetSourcePlayable(playable);
            graph.Play();

            var recorder = PlayRecorder.On(animator, controller);
            Assert.IsTrue(recorder.Matched, "the graph built in Play mode was not found");

            for (int i = 0; i < 6; i++)
            {
                yield return null;
                recorder.Sample(Time.frameCount, Time.time);
                if (i == 2) playable.SetBool("Go", true);
            }
            int recorded = recorder.Frames;
            Assert.Greater(recorded, 1, "real frames went by and none of them were recorded");

            graph.Destroy();
            UnityEngine.Object.DestroyImmediate(host);

            yield return new ExitPlayMode();

            // No domain reload happens on the way out, which is the whole point: the trace is
            // still the trace, and everything the window offers over one now applies to it.
            Assert.AreEqual(recorded, recorder.Frames,
                "the recording did not survive leaving Play mode");
            var state = recorder.Trace.Find(Simulation.PlayScope, "Base/state");
            Assert.IsNotNull(state);
            Assert.AreEqual("On", state.TextAt(state.Frames - 1),
                "the avatar moved during the recording and the trace does not say so");
            UnityEngine.Object.DestroyImmediate(controller);
        }

        // ---- housekeeping ----------------------------------------------------

        /// <summary>An asset destroyed however the test leaves. Controllers made here are never
        /// on disk, and one left behind is one the next test's <c>GetAllGraphs</c> walk can still
        /// reach.</summary>
        sealed class Held<T> : IDisposable where T : UnityEngine.Object
        {
            public readonly T asset;
            public Held(T asset) { this.asset = asset; }
            public void Dispose()
            {
                if (asset != null) UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        static Held<T> Owned<T>(T asset) where T : UnityEngine.Object => new Held<T>(asset);
    }
}
