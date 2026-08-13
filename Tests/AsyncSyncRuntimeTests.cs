using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Async sync, run rather than read. Everything else about the cycle is checked by looking
    /// at what the builder wrote; this checks that what it wrote does the thing — a wearer's
    /// values reach somebody else's copy, in about the time the wizard promises, through a wire
    /// that samples and rounds and loses things the way the real one does.
    ///
    /// It is the only kind of test that can fail for the reason the technique is hard: nothing
    /// here asserts on a state name or a driver entry, so a layer that is shaped correctly and
    /// still does not sync fails here and nowhere else.
    /// </summary>
    public class AsyncSyncRuntimeTests
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

        /// <summary>A three-target cycle on a real controller, with real drivers. Motion-less
        /// states: an exit time then reads directly as seconds, which is what the step is.</summary>
        static AnimatorController Multiplexed(out AsyncSyncBuilder.Request request,
            System.Action<AsyncSyncBuilder.Request> tweak = null)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("F", AnimatorControllerParameterType.Float);
            controller.AddParameter("B", AnimatorControllerParameterType.Bool);
            controller.AddParameter("I", AnimatorControllerParameterType.Int);

            request = new AsyncSyncBuilder.Request
            {
                controller = controller,
                baseName = "Async",
                encoding = AsyncSyncBuilder.IndexEncoding.Int,
                stepSeconds = 0.3f,
                assignEmptyClip = false,
                addToStore = false,
            };
            request.targets.AddRange(new[] { "F", "B", "I" });
            tweak?.Invoke(request);
            Assert.IsNull(AsyncSyncBuilder.Validate(request));
            Assert.IsTrue(AsyncSyncBuilder.Apply(request));
            return controller;
        }

        /// <summary>The run: two clients, and the wire carrying exactly what the setup syncs.</summary>
        static SimSettings Settings(AsyncSyncBuilder.Request request, float seconds,
            float loss = 0f, float joinsAt = 0f)
        {
            var wire = new SyncWire
            {
                // Under the 0.3 s step, so an index change is sampled before the next one
                // replaces it — the condition the wizard's own warning is about.
                intervalSeconds = 0.1f,
                dropChance = loss,
                remoteJoinsAt = joinsAt,
                seed = 4,
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

        static float Remote(SignalTrace trace, string parameter) =>
            trace.Find(Simulation.RemoteScope, parameter).At(trace.Frames - 1);

        /// <summary>The first second at which the remote agreed, or -1.</summary>
        static float Agreed(SignalTrace trace, string parameter, float expected,
            float tolerance = 0.01f)
        {
            var remote = trace.Find(Simulation.RemoteScope, parameter);
            for (int frame = 0; frame < trace.Frames; frame++)
                if (Mathf.Abs(remote.At(frame) - expected) <= tolerance)
                    return trace.TimeAt(frame);
            return -1f;
        }

        // ---- does it sync at all ---------------------------------------------

        [Test]
        public void ACycleCarriesEveryTargetToARemote_InsideOnePass()
        {
            var controller = Multiplexed(out var request);
            var settings = Settings(request, 4f);
            settings.stimulus.At(0f, "F", 0.5f).At(0f, "B", true).At(0f, "I", 3f);

            var trace = Simulation.Run(controller, settings);

            // Quantized on the way over: a Float is 8 bits across -1..1, so 0.5 comes back
            // near 0.5 and not exactly at it.
            Assert.AreEqual(0.5f, Remote(trace, "F"), 0.01f);
            Assert.AreEqual(1f, Remote(trace, "B"));
            Assert.AreEqual(3f, Remote(trace, "I"));

            // Three slots at 0.3 s is a 0.9 s pass. Everything should be there inside one,
            // plus the sample the wire was waiting for.
            foreach (var (name, expected) in new[] { ("F", 0.5f), ("B", 1f), ("I", 3f) })
            {
                float at = Agreed(trace, name, expected);
                Assert.Greater(at, 0f, name + " never arrived");
                Assert.Less(at, 1.2f, name + " took longer than a pass");
            }
        }

        [Test]
        public void ARemoteFollowsTheWearerWhenTheValueChangesAgain()
        {
            var controller = Multiplexed(out var request);
            var settings = Settings(request, 6f);
            settings.stimulus.At(0f, "I", 3f).At(2f, "I", 7f);

            var trace = Simulation.Run(controller, settings);
            Assert.AreEqual(7f, Remote(trace, "I"));

            var remote = trace.Find(Simulation.RemoteScope, "I");
            // It held the old value until the cycle came round again, and never showed a
            // value the wearer never had.
            for (int frame = 0; frame < trace.Frames; frame++)
                Assert.That(remote.At(frame), Is.EqualTo(0f).Or.EqualTo(3f).Or.EqualTo(7f),
                    "at frame " + frame);
        }

        [Test]
        public void TheLagRowMatchesTheWizardsRefreshInterval()
        {
            var controller = Multiplexed(out var request);
            var settings = Settings(request, 6f);
            // Slower than the pass, so the remote can actually catch each value. Changing
            // faster than the cycle sends is not the cycle being late — it is being asked for
            // something it never promised, and the lag row would then measure the asking.
            for (int i = 1; i < 4; i++) settings.stimulus.At(i * 1.5f, "I", 10 + i);

            var trace = Simulation.Run(controller, settings);
            var lag = trace.Find(Simulation.LagScope, "I");

            float worst = 0f;
            // Skip the first pass: nothing has arrived yet, so the lag is the age of the run.
            for (int frame = trace.FrameAt(1.5f); frame < trace.Frames; frame++)
                worst = Mathf.Max(worst, lag.At(frame));

            float promised = AsyncSyncBuilder.RefreshIntervals(request)["I"];
            Assert.AreEqual(0.9f, promised, 1e-4f, "three slots at 0.3 s");
            // The promise is how long between two sends; the wire adds up to one sample on
            // top, and the value has to be wrong for a moment before it can be seen to be.
            Assert.Less(worst, promised + 0.25f, "worse than the wizard promises");
            Assert.Greater(worst, 0.2f, "suspiciously good — is anything being measured?");
        }

        // ---- the reliability flags -------------------------------------------

        [Test]
        public void Ready_TurnsOnForARemoteOncePerPass_AndIsOnForTheWearerAtOnce()
        {
            var controller = Multiplexed(out var request, r => r.ready = true);
            var trace = Simulation.Run(controller, Settings(request, 4f));

            var wearer = trace.Find(Simulation.LocalScope, "Async/Ready");
            Assert.AreEqual(1f, wearer.At(0), "their own values were never anywhere else");

            var remote = trace.Find(Simulation.RemoteScope, "Async/Ready");
            Assert.AreEqual(0f, remote.At(0));
            int latched = -1;
            for (int frame = 0; frame < trace.Frames && latched < 0; frame++)
                if (remote.At(frame) != 0f) latched = frame;
            Assert.Greater(latched, 0, "Ready never latched");
            Assert.Less(trace.TimeAt(latched), 1.3f, "later than the pass it promises");
            // A latch: it never falls again.
            for (int frame = latched; frame < trace.Frames; frame++)
                Assert.AreEqual(1f, remote.At(frame), "Ready fell at frame " + frame);
        }

        [Test]
        public void Stale_StaysDownOnACleanWire_AndComesUpWhenSamplesAreLost()
        {
            var clean = Multiplexed(out var request, r => r.stale = true);
            var trace = Simulation.Run(clean, Settings(request, 6f));
            var stale = trace.Find(Simulation.RemoteScope, "Async/Stale");

            // The first lap is judged before anything has arrived, so it is allowed to be
            // suspicious; after that a clean wire has nothing to be suspicious about.
            for (int frame = trace.FrameAt(2f); frame < trace.Frames; frame++)
                Assert.AreEqual(0f, stale.At(frame),
                    "a clean wire read as drifting at " + trace.TimeAt(frame) + "s");

            var lossy = Multiplexed(out var lossyRequest, r => r.stale = true);
            var noisy = Simulation.Run(lossy, Settings(lossyRequest, 6f, loss: 0.6f));
            var flagged = noisy.Find(Simulation.RemoteScope, "Async/Stale");
            bool raised = false;
            for (int frame = 0; frame < noisy.Frames; frame++)
                if (flagged.At(frame) != 0f) raised = true;
            Assert.IsTrue(raised, "losing most of the wire went unnoticed");
        }

        /// <summary>
        /// The same flag on a pass with no lap marker to spare: every target is requestable,
        /// so no slot's arrival can stand for a lap, and one step is given an index value of
        /// its own instead. Run rather than read, because the whole question is whether a
        /// value the ring writes once a pass — and the decoder therefore decodes once a pass —
        /// really does arm the watcher exactly once.
        /// </summary>
        [Test]
        public void Stale_WorksOnAPassThatHadToBuyItsLapMarker()
        {
            var clean = Multiplexed(out var request, r =>
            {
                r.stale = true;
                r.requestTargets.AddRange(new[] { "F", "B", "I" });
            });
            var slots = AsyncSyncBuilder.BuildSlots(request);
            Assert.IsTrue(AsyncSyncBuilder.BuildClock(request, slots,
                AsyncSyncBuilder.EffectiveSchedule(request, slots)).markerDedicated,
                "the pass this test is about is one with no marker of its own");

            var trace = Simulation.Run(clean, Settings(request, 6f));
            var stale = trace.Find(Simulation.RemoteScope, "Async/Stale");
            // Judged at all: a marker nobody decodes would leave the flag at its default
            // forever, which is the failure this test exists to catch.
            bool judged = false;
            for (int frame = 0; frame < trace.Frames; frame++)
                if (stale.At(frame) != 0f) judged = true;
            Assert.IsTrue(judged, "the bought marker never armed the watcher");
            // ...and then settled, because the wire is clean.
            for (int frame = trace.FrameAt(2f); frame < trace.Frames; frame++)
                Assert.AreEqual(0f, stale.At(frame),
                    "a clean wire read as drifting at " + trace.TimeAt(frame) + "s");

            var lossy = Multiplexed(out var lossyRequest, r =>
            {
                r.stale = true;
                r.requestTargets.AddRange(new[] { "F", "B", "I" });
            });
            var noisy = Simulation.Run(lossy, Settings(lossyRequest, 6f, loss: 0.6f));
            var flagged = noisy.Find(Simulation.RemoteScope, "Async/Stale");
            bool raised = false;
            for (int frame = 0; frame < noisy.Frames; frame++)
                if (flagged.At(frame) != 0f) raised = true;
            Assert.IsTrue(raised, "losing most of the wire went unnoticed");
        }

        // ---- somebody who turned up late --------------------------------------

        [Test]
        public void ALateArrivalIsCaughtUpInsideOnePass()
        {
            // The case the whole technique is about: they were not there when the values were
            // set, and nothing will be set again.
            var controller = Multiplexed(out var request, r => r.ready = true);
            var settings = Settings(request, 8f, joinsAt: 3.4f);
            settings.stimulus.At(0f, "F", 0.5f).At(0f, "B", true).At(0f, "I", 3f);

            var trace = Simulation.Run(controller, settings);
            Assert.AreEqual(0.5f, Remote(trace, "F"), 0.01f);
            Assert.AreEqual(1f, Remote(trace, "B"));
            Assert.AreEqual(3f, Remote(trace, "I"));

            foreach (var (name, expected) in new[] { ("F", 0.5f), ("B", 1f), ("I", 3f) })
            {
                float at = Agreed(trace, name, expected);
                Assert.Greater(at, 3.4f, name + " reached somebody who was not there");
                Assert.Less(at - 3.4f, 1.2f, name + " took longer than a pass to catch up");
            }
        }

        [Test]
        public void ReadyDoesNotLieToSomebodyWhoArrivedMidPass()
        {
            var controller = Multiplexed(out var request, r => r.ready = true);
            // Deliberately awkward: they turn up in the middle of a step, so the index they
            // are handed is one they will not see change for a while.
            var settings = Settings(request, 8f, joinsAt: 3.55f);
            settings.stimulus.At(0f, "F", 0.5f).At(0f, "B", true).At(0f, "I", 3f);

            var trace = Simulation.Run(controller, settings);
            var ready = trace.Find(Simulation.RemoteScope, "Async/Ready");

            int latched = -1;
            for (int frame = 0; frame < trace.Frames && latched < 0; frame++)
                if (ready.At(frame) != 0f) latched = frame;
            Assert.Greater(latched, 0, "Ready never latched for a late arrival");

            // The promise: not before every value is actually theirs. Ready may be late; it
            // may never be early.
            foreach (var (name, expected) in new[] { ("F", 0.5f), ("B", 1f), ("I", 3f) })
            {
                float agreed = Agreed(trace, name, expected);
                Assert.Greater(agreed, 0f);
                Assert.LessOrEqual(agreed, trace.TimeAt(latched) + 1e-3f,
                    "Ready was on while " + name + " was still somebody else's value");
            }
            Assert.Less(trace.TimeAt(latched) - 3.55f, 1.3f, "later than the pass it promises");
        }

        // ---- groups -----------------------------------------------------------

        [Test]
        public void AGroupNeverShowsARemoteHalfOfAChange()
        {
            var group = new GraphFrameData.AsyncSyncConfig.SyncGroup { name = "Outfit" };
            group.members.AddRange(new[] { "F", "I" });
            var controller = Multiplexed(out var request, r => r.groups.Add(group));

            var settings = Settings(request, 8f);
            // Both halves of one change, twice, so the remote has to land on 3 and 0.5 and
            // then on 7 and -0.5 — and never on one of each.
            settings.stimulus.At(0f, "F", 0.5f).At(0f, "I", 3f)
                .At(3f, "F", -0.5f).At(3f, "I", 7f);

            var trace = Simulation.Run(controller, settings);
            var f = trace.Find(Simulation.RemoteScope, "F");
            var i = trace.Find(Simulation.RemoteScope, "I");

            Assert.AreEqual(-0.5f, f.At(trace.Frames - 1), 0.01f);
            Assert.AreEqual(7f, i.At(trace.Frames - 1));

            // Once every member has been sent for real. The first commit is not the group's
            // to get right: a remote that has just arrived decodes the index it finds — zero,
            // because nothing has been sent yet — as slot zero arriving, and then holds a
            // value nobody sent until that slot comes round for real. That is what the remote
            // initialized flag is for, and a group is worth exactly as much as it says.
            for (int frame = trace.FrameAt(2f); frame < trace.Frames; frame++)
            {
                bool firstSet = Mathf.Abs(f.At(frame) - 0.5f) < 0.01f && i.At(frame) == 3f;
                bool secondSet = Mathf.Abs(f.At(frame) + 0.5f) < 0.01f && i.At(frame) == 7f;
                bool untouched = f.At(frame) == 0f && i.At(frame) == 0f;
                Assert.IsTrue(firstSet || secondSet || untouched,
                    "a torn pair at " + trace.TimeAt(frame) + "s: F=" + f.At(frame)
                    + " I=" + i.At(frame));
            }
        }

        // ---- requests ---------------------------------------------------------

        [Test]
        public void ARequestGetsTheValueThereSoonerThanThePassWould()
        {
            var controller = Multiplexed(out var request, r => r.requestTargets.Add("I"));
            var settings = Settings(request, 6f);
            // Change it just after its own step has gone by, which is the worst moment: the
            // cycle would not carry it again for nearly a whole pass.
            settings.stimulus.At(1f, "I", 9f).At(1f, "Async/Req/I", 1f);

            var trace = Simulation.Run(controller, settings);
            float arrived = Agreed(trace, "I", 9f);
            Assert.Greater(arrived, 0f, "the request never arrived");
            Assert.Less(arrived - 1f, 0.75f,
                "a request that saved nothing — the detour should beat the pass");
        }

        [Test]
        public void RequestsHeldDownForeverStillLetTheCycleRound()
        {
            // The starvation guarantee, run: a flag that is never released must not pin the
            // cycle to one slot. Everything else still has to arrive, inside twice a pass.
            var controller = Multiplexed(out var request, r => r.requestTargets.Add("I"));
            var settings = Settings(request, 8f);
            settings.stimulus.At(0f, "F", 0.5f).At(0f, "B", true).At(0f, "I", 3f);
            for (int i = 0; i < 40; i++) settings.stimulus.At(i * 0.15f, "Async/Req/I", 1f);

            var trace = Simulation.Run(controller, settings);
            Assert.AreEqual(0.5f, Remote(trace, "F"), 0.01f);
            Assert.AreEqual(1f, Remote(trace, "B"));

            float worst = 0f;
            foreach (var name in new[] { "F", "B" })
                worst = Mathf.Max(worst, Agreed(trace, name, name == "F" ? 0.5f : 1f));
            Assert.Greater(worst, 0f);
            Assert.Less(worst, 2f * 0.9f + 0.4f, "starved: a pass took longer than twice");
        }
    }
}
