using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// How long the generated watchers hold a state, which is a question neither of the other
    /// two suites asks: the builder tests read the shape a route has, and the runtime tests
    /// read the values that come out the far end, and a route that takes a second longer than
    /// it was meant to is invisible to both — the shape is the one that was asked for, and the
    /// values still arrive, only later than anybody wrote down.
    ///
    /// That is exactly how a judgement and a commit each spent about a second of every lap
    /// sitting in a state whose whole job was the driver that ran on the way in. An exit time
    /// of 0 reads as "leave at once" and fires where an exit time of 1 does, at the loop
    /// boundary. So the dwell of the routes that are supposed to have nothing to wait for is
    /// measured here, in frames, on a running Animator.
    ///
    /// The two came out differently, which is the other reason this suite exists. The
    /// judgement's else really did have nothing to wait for and now waits a millisecond. The
    /// commit's way out kept its loop, because taking it away was measured and it makes the
    /// group tear more often rather than less — so the wait is pinned here as a fact about the
    /// build, next to the hole it is standing in front of.
    ///
    /// The sweeps below are the other half of that. A group's tear is a property of WHEN the
    /// wearer made the change, so a handful of moments cannot tell you whether it happens —
    /// the runtime suite's "never half of a change" held for years on a build that tore at
    /// four of every thirteen moments, because the moments it picks are whole ones. Every
    /// claim this repository makes about a group is quoting a sweep from here.
    /// </summary>
    public class AsyncSyncDwellTests
    {
        DaerDLanguage _savedLanguage;

        [OneTimeSetUp]
        public void ForceEnglish()
        {
            _savedLanguage = L.Language;
            L.Language = DaerDLanguage.English;
        }

        [OneTimeTearDown]
        public void RestoreLanguage() => L.Language = _savedLanguage;

        /// <summary>A route with nothing to wait for may cost a frame; anything past that is
        /// the bug this suite exists for, which cost about sixty.</summary>
        const int Prompt = 2;

        static AnimatorController NewController()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);
            return controller;
        }

        static AsyncSyncBuilder.Request NewRequest(AnimatorController controller,
            AnimationClip empty = null)
        {
            var request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
                encoding = AsyncSyncBuilder.IndexEncoding.Int,
                stepSeconds = 0.3f,
                assignEmptyClip = empty != null,
                emptyClip = empty,
                addToStore = false,
            };
            request.targets.AddRange(new[] { "F", "B", "I" });
            return request;
        }

        /// <summary>A clip of a stated length, which is what a production setup fills its
        /// generated states with — and what every exit time on them is then normalized to.</summary>
        static AnimationClip NewEmptyClip(float length)
        {
            var clip = new AnimationClip { name = "Empty" };
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(GameObject), "m_IsActive"),
                AnimationCurve.Constant(0f, length, 1f));
            return clip;
        }

        static int LayerIndex(AnimatorController controller, string name)
        {
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i].name == name) return i;
            return -1;
        }

        // ---- the judgement ----------------------------------------------------

        /// <summary>
        /// Runs the stale watcher on a real Animator until it has judged, and reports which
        /// verdict it reached and how many frames it sat in Judge to reach it. Drivers are left
        /// out: the arrival bits are set by hand here, and a verdict's driver would put them
        /// down again — the question is which route is taken, not what it writes.
        /// </summary>
        static string Verdict(AnimationClip empty, bool everythingArrived, out int dwell)
        {
            var controller = NewController();
            try
            {
                var request = NewRequest(controller, empty);
                request.stale = true;
                request.skipDrivers = true;
                Assert.IsNull(AsyncSyncBuilder.Validate(request));
                Assert.IsTrue(AsyncSyncBuilder.Apply(request));

                int layer = LayerIndex(controller, "Async Stale");
                Assert.GreaterOrEqual(layer, 0, "the stale watcher was not built");

                using (var rig = new AnimatorRig(controller))
                {
                    // IsLocal is false by default, which is what arms the watcher, and the
                    // index starts at the marker's own value of 0.
                    if (everythingArrived)
                        foreach (var name in new[] { "F", "B", "I" })
                            rig.Set("Async/Fresh/" + name, true);

                    int entered = -1;
                    // Four seconds: long enough to catch a route that waits for a loop
                    // boundary, so a run that has the bug reports how long it really took.
                    for (int frame = 1; frame <= 240; frame++)
                    {
                        rig.Step();
                        string at = rig.CurrentState(layer, "Idle", "Judge", "Dirty", "Clean");
                        if (at == "Judge" && entered < 0) entered = frame;
                        if (at != "Dirty" && at != "Clean") continue;
                        Assert.Greater(entered, 0, "the watcher reached a verdict without judging");
                        dwell = frame - entered;
                        return at;
                    }
                    dwell = -1;
                    return null;
                }
            }
            finally
            {
                Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// The else of the judgement, which is the route with nothing to wait for, and the
        /// conditioned routes beside it. Both have to be eligible on the same frame — that is
        /// what makes the list order the else — and both have to be taken on the frame after
        /// the watcher arrives, which is what an exit time of zero did not do.
        /// </summary>
        [Test]
        public void AJudgementLeavesTheFrameAfterItLands_ByEitherRoute()
        {
            // Nothing arrived: a conditioned route is eligible, and it is not the last one.
            Assert.AreEqual("Dirty", Verdict(null, false, out int missing),
                "a lap that brought nothing was judged clean");
            Assert.LessOrEqual(missing, Prompt,
                "the watcher sat in Judge for " + missing + " frames before saying so");

            // Everything arrived: only the else is eligible, and it goes just as fast.
            Assert.AreEqual("Clean", Verdict(null, true, out int arrived),
                "a lap that brought everything was judged stale");
            Assert.Greater(arrived, 0, "the else of the judgement was never taken");
            Assert.LessOrEqual(arrived, Prompt,
                "the else waited " + arrived + " frames — an exit time is a loop boundary, "
                + "not an instant");

            // The same frame either way, which is the tie the list order settles: on the frame
            // the else became eligible, so did the routes above it, and the run above took the
            // conditioned one. Nothing about the layer's shape says that; only its order does.
            Assert.AreEqual(missing, arrived,
                "the two verdicts are not reached on the same frame, so the else is not a tie "
                + "the list order breaks");
        }

        /// <summary>
        /// The same measurement on the setup a wizard actually generates, where every state
        /// carries the Empty clip. Exit times are normalized to the motion, so a route meant to
        /// be over at once has to be written as a fraction of whatever clip is on the state —
        /// and a run with a half-second clip is the one that would show a dwell scaled to it.
        /// </summary>
        [Test]
        public void TheEmptyClipDoesNotStretchTheRouteOutOfAJudgement()
        {
            var clip = NewEmptyClip(0.5f);
            try
            {
                Assert.AreEqual(0.5f, clip.length, 1e-4f, "the clip under test has to have length");
                Assert.AreEqual("Clean", Verdict(clip, true, out int dwell));
                Assert.Greater(dwell, 0, "the else of the judgement was never taken");
                Assert.LessOrEqual(dwell, Prompt,
                    "with a 0.5 s clip on the states the else waited " + dwell + " frames");
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        // ---- the commit -------------------------------------------------------

        /// <summary>The cycle the group runs on, with real drivers and a wire under it — the
        /// same one the runtime suite uses, because the dwell being measured is the one a
        /// commit really has.</summary>
        static AnimatorController Grouped(out AsyncSyncBuilder.Request request,
            bool ready = false, bool requestable = false)
        {
            var controller = NewController();
            request = NewRequest(controller);
            var group = new GraphFrameData.AsyncSyncConfig.SyncGroup { name = "Outfit" };
            group.members.AddRange(new[] { "F", "I" });
            request.groups.Add(group);
            request.ready = ready;
            // The member the ring sends LAST, so a detour for it is a member arriving out of
            // turn rather than one arriving early in its own lap.
            if (requestable) request.requestTargets.Add("I");
            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));
            return controller;
        }

        static SimSettings Settings(AsyncSyncBuilder.Request request, float seconds,
            float loss = 0f, int wireSeed = 4)
        {
            var wire = new SyncWire
            {
                intervalSeconds = 0.1f,
                dropChance = loss,
                seed = wireSeed,
            };
            foreach (var (name, _) in AsyncSyncBuilder.GeneratedParameters(request))
                wire.Syncs(name);
            return new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = seconds, seed = 1 },
                wire = wire,
                stimulus = new Stimulus(),
            };
        }

        /// <summary>The first second at which the remote held one member's new value beside
        /// the other's old one, or -1 for a run in which it never did. Read from two seconds
        /// in, the way AsyncSyncRuntimeTests reads it: without the remote initialized flag the
        /// very first commit is allowed to carry a value nobody sent.</summary>
        static float FirstTornAt(SignalTrace trace)
        {
            var f = trace.Find(Simulation.RemoteScope, "F");
            var i = trace.Find(Simulation.RemoteScope, "I");
            for (int frame = trace.FrameAt(2f); frame < trace.Frames; frame++)
            {
                bool first = Mathf.Abs(f.At(frame) - 0.5f) < 0.01f && i.At(frame) == 3f;
                bool second = Mathf.Abs(f.At(frame) + 0.5f) < 0.01f && i.At(frame) == 7f;
                bool untouched = f.At(frame) == 0f && i.At(frame) == 0f;
                if (!first && !second && !untouched) return trace.TimeAt(frame);
            }
            return -1f;
        }

        /// <summary>One run of the pair, changed from one whole set to another at
        /// <paramref name="changedAt"/>.</summary>
        static SignalTrace ChangedAt(float changedAt, float loss = 0f, int wireSeed = 4,
            bool ready = false, bool requesting = false)
        {
            var controller = Grouped(out var request, ready, requesting);
            try
            {
                var settings = Settings(request, 8f, loss, wireSeed);
                settings.stimulus.At(0f, "F", 0.5f).At(0f, "I", 3f)
                    .At(changedAt, "F", -0.5f).At(changedAt, "I", 7f);
                // Asked for again and again, so the ring takes a detour at almost every step
                // boundary it is allowed to and the member is revisited out of turn all run.
                if (requesting)
                    for (int i = 0; i < 50; i++)
                        settings.stimulus.At(i * 0.15f, "Async/Req/I", 1f);
                return Simulation.Run(controller, settings);
            }
            finally
            {
                Object.DestroyImmediate(controller);
            }
        }

        /// <summary>
        /// The thirteen moments a change can be made at, a tenth of a second apart over one and
        /// a bit of the 0.9 s pass — the sweep 4d7a618 measured the tear with, kept as the unit
        /// of measurement so before and after are the same experiment. Thirteen tenths covers
        /// every phase of the pass and then some, and the pass is not a whole number of tenths,
        /// so the moments do not land on the same step of it twice.
        /// </summary>
        const int Phases = 13;

        static float PhaseAt(int phase) => 3f + phase * 0.1f;

        /// <summary>How many of the thirteen were shown torn, and which ones — the detail goes
        /// into the assertion message, because a sweep that regresses is only useful if it says
        /// where.</summary>
        static int TornOverASweep(float loss, int wireSeed, out string detail,
            bool ready = false, bool requesting = false)
        {
            var torn = new System.Text.StringBuilder();
            int count = 0;
            for (int phase = 0; phase < Phases; phase++)
            {
                float when = FirstTornAt(
                    ChangedAt(PhaseAt(phase), loss, wireSeed, ready, requesting));
                if (when < 0f) continue;
                count++;
                if (torn.Length > 0) torn.Append(", ");
                torn.Append(PhaseAt(phase).ToString("0.0")).Append("s shown torn at ")
                    .Append(when.ToString("0.00")).Append('s');
            }
            detail = torn.ToString();
            return count;
        }

        /// <summary>
        /// A clean line carries a group whole whenever the change is made. This is the sweep
        /// 4d7a618 wrote down as four of thirteen, run again on the build that latches: the
        /// members are read into their latches in one driver at the group's own step and sent
        /// from there, so a change made between two of their sends is either wholly in the lap
        /// or wholly in the next one, and there is no third possibility left to land on.
        ///
        /// The moment of the change is what is swept, and not the loss, because that was the
        /// whole surprise of the old measurement: the tear was a property of WHEN, on a wire
        /// that dropped nothing at all. A sweep is how a promise of this shape is stated —
        /// "never half" cannot be shown by the handful of moments a runtime test picks.
        /// </summary>
        [Test]
        public void AGroupIsWholeAtEveryMomentAChangeCanBeMadeAt()
        {
            int torn = TornOverASweep(0f, 4, out string detail);
            Assert.AreEqual(0, torn, torn + " of the " + Phases
                + " change moments reached the far side torn: " + detail);
        }

        /// <summary>
        /// The same sweep with a request held down all run, so the ring is detouring to a
        /// group member at nearly every step boundary and that member is sent several times a
        /// lap, out of turn.
        ///
        /// A detour takes no reading of its own — it sends the current latch — and that is the
        /// half of the design a shape test cannot show is right. The alternative reading, that
        /// a request should carry the freshest value it can find, is exactly what tears: it
        /// would put one member of a new reading on the wire while the rest of the group was
        /// still travelling from the old one, and it would do it at a moment nobody scheduled.
        /// What the request buys instead is that the group's whole reading lands sooner.
        /// </summary>
        [Test]
        public void ARequestForAGroupMemberSendsTheLatchAndNotTheFreshValue()
        {
            int torn = TornOverASweep(0f, 4, out string detail, requesting: true);
            Assert.AreEqual(0, torn, torn + " of the " + Phases
                + " change moments tore while a member was being requested: " + detail);
        }

        /// <summary>The lossy sweep's seeds. Eight, because the tear that is left needs two
        /// particular samples to go missing in two particular laps, and one seed either finds
        /// that or does not.</summary>
        static readonly int[] LossSeeds = { 2, 3, 4, 5, 6, 7, 8, 9 };

        /// <summary>
        /// What is left of the tear when the wire drops a quarter of its samples, and the
        /// honest edge of the promise above.
        ///
        /// The hole is on the receiving side and it is exactly one shape: a lap loses the
        /// arrival that would have put the group's flags down, so the flag of a member that
        /// arrived in the PREVIOUS lap is still standing, and a member of this lap completes
        /// the guard against it. Both values are latched readings and neither is half of
        /// anything — they are just two different laps' readings.
        ///
        /// Closing it needs the lap's identity to travel WITH the values, which is a generation
        /// number on the wire. That is the one thing a group may not spend: a group costs no
        /// synced bits today, and bits are the whole reason a cycle exists. A number wide
        /// enough to be unambiguous under loss is several of them, on an avatar whose budget
        /// the multiplexing was bought to stretch.
        ///
        /// Measured over eight loss seeds and the same thirteen moments: 32 of the 104 runs
        /// tore before the latch, 7 after — and all seven are the shape above, in the one seed
        /// that produces it. Ready is on here, so the first-commit hole 0e91fc3 closed is not
        /// what is being counted.
        /// </summary>
        [Test]
        public void AGroupUnderLossOnlyTearsWhenALapLosesItsOwnOpening()
        {
            var totals = new System.Text.StringBuilder();
            int torn = 0;
            foreach (int seed in LossSeeds)
            {
                torn += TornOverASweep(0.25f, seed, out string detail, ready: true);
                if (detail.Length > 0)
                    totals.Append("seed ").Append(seed).Append(": ").Append(detail).Append("; ");
            }
            int runs = Phases * LossSeeds.Length;

            Assert.Less(torn, 32, "the latch has to be a strict improvement on the " + 32
                + " of " + runs + " the build without it tore");
            Assert.LessOrEqual(torn, 7, torn + " of " + runs
                + " runs tore, which is worse than the 7 measured for this build: " + totals);
            Assert.Greater(torn, 0,
                "no run tore, so the hole this test describes is closed — rewrite the guarantee "
                + "in AsyncSyncApplier.BuildGroupLayers before deleting this half");
        }

        /// <summary>
        /// The commit does NOT come straight back out, and this is the test that says so on
        /// purpose. The reading that it should is the obvious one — the driver has run and the
        /// flags are down, so there is nothing left to wait for — and it is wrong for a reason
        /// no shape shows: see the test below, and <c>AsyncSyncApplier.AfterALoop</c>.
        ///
        /// Measured here at 61 frames, which is one loop of a motion-less state at 60 fps. On a
        /// production setup the states carry the Empty clip and the loop is that clip's length
        /// instead — the number is a property of the motion, not a constant.
        /// </summary>
        [Test]
        public void AGroupsCommitStandsForOneLoopOfItsOwnMotion()
        {
            var controller = Grouped(out var request);
            var settings = Settings(request, 6f);
            settings.stimulus.At(0f, "F", 0.5f).At(0f, "I", 3f)
                .At(3f, "F", -0.5f).At(3f, "I", 7f);

            var trace = Simulation.Run(controller, settings);
            // The remote's copy: nothing on the wearer's side ever raises an arrival flag, so
            // their commit layer never leaves Idle at all.
            var state = trace.Find(Simulation.RemoteScope, "Async Outfit/state");
            Assert.IsNotNull(state, "the group layer left no state row");

            int longest = 0, held = 0, commits = 0;
            for (int frame = 0; frame < trace.Frames; frame++)
            {
                if (state.TextAt(frame) != "Commit") { held = 0; continue; }
                if (held == 0) commits++;
                held++;
                longest = Mathf.Max(longest, held);
            }

            Assert.Greater(commits, 1, "the group never committed twice, so nothing was measured");
            Assert.Greater(longest, Prompt,
                "the commit came straight back out, which is measured to tear more, not less");
            Assert.AreEqual(60, longest, 5,
                "Commit stood for " + longest + " frames, which is not the second a loop of a "
                + "motion-less state is");

            Object.DestroyImmediate(controller);
        }

        /// <summary>
        /// The two moments 4d7a618 named, kept as themselves now that the sweep above covers
        /// the range they were drawn from. A change at 3.0 s always reached the far side whole
        /// — it is the moment AsyncSyncRuntimeTests picks, and the reason that suite's promise
        /// held while the property behind it did not. One at 3.4 s fell between the group's two
        /// sends and was shown half-old for a lap, which is what the latch closes.
        ///
        /// Written out separately from the sweep because a sweep that goes from four to zero
        /// only says the number moved. This says which moment moved, and it is the one that
        /// used to be the counterexample.
        /// </summary>
        [Test]
        public void TheChangeThatFellBetweenTwoSendsIsWholeNow()
        {
            Assert.AreEqual(-1f, FirstTornAt(ChangedAt(3f)),
                "the moment AsyncSyncRuntimeTests picks stopped being one of the whole ones");
            Assert.AreEqual(-1f, FirstTornAt(ChangedAt(3.4f)),
                "a change made between the group's two sends was shown half-old, which is the "
                + "tear the send-side latch exists to close");
        }
    }
}
