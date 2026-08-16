using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What a finished run says about itself. Every test here runs a real controller through
    /// <see cref="Simulation"/> and then asks <see cref="RunFindings"/> what it made of the
    /// result, because the thing that can be wrong is the reading and not the arithmetic —
    /// a finding built from a hand-made trace would pass while the trace a run actually
    /// produces said something else.
    ///
    /// The findings are user-facing sentences, so the assertions are on their text and the
    /// language is pinned. TestLanguage pins it for the whole suite already; pinned again here
    /// so that this file means what it says when read on its own.
    /// </summary>
    public class RunFindingsTests
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

        // ---- the controllers -------------------------------------------------

        /// <summary>A transition slow enough to be caught in flight. One that finishes inside
        /// the frame it starts on is never on the via row at all, which is a case of its
        /// own below.</summary>
        static AnimatorStateTransition Blend(AnimatorStateTransition transition, string condition)
        {
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0.2f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, condition);
            return transition;
        }

        /// <summary>Idle and two places to go, on a condition each. With
        /// <paramref name="anyState"/> the same two are reachable from anywhere, which is what
        /// lets one run visit both of them.</summary>
        static AnimatorController Branching(bool anyState = false)
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Other", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Radial", AnimatorControllerParameterType.Float);
            controller.AddParameter("Solo", AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var wave = machine.AddState("Wave");
            var point = machine.AddState("Point");
            machine.defaultState = idle;

            if (!anyState)
            {
                Blend(idle.AddTransition(wave), "Go");
                Blend(idle.AddTransition(point), "Other");
                return controller;
            }
            Blend(machine.AddAnyStateTransition(wave), "Go").canTransitionToSelf = false;
            Blend(machine.AddAnyStateTransition(point), "Other").canTransitionToSelf = false;
            return controller;
        }

        /// <summary>Idle → On the way most VRChat toggles are written: no blend at all.</summary>
        static AnimatorController Instant()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Go", AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;
            var idle = machine.AddState("Idle");
            var on = machine.AddState("On");
            machine.defaultState = idle;

            var transition = idle.AddTransition(on);
            transition.hasExitTime = false;
            transition.hasFixedDuration = true;
            transition.duration = 0f;
            transition.AddCondition(AnimatorConditionMode.If, 0f, "Go");
            return controller;
        }

        /// <summary>One state and nothing that moves it, so every finding a run of it raises is
        /// about the wire rather than about the Animator.</summary>
        static AnimatorController Quiet()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Radial", AnimatorControllerParameterType.Float);
            controller.AddParameter("Solo", AnimatorControllerParameterType.Bool);
            controller.AddParameter("GestureLeft", AnimatorControllerParameterType.Int);

            var machine = controller.layers[0].stateMachine;
            machine.defaultState = machine.AddState("Idle");
            return controller;
        }

        // ---- running them ----------------------------------------------------

        static SyncWire Wire(params string[] synced) =>
            new SyncWire { intervalSeconds = 0.2f, quantize = true, seed = 1 }.Syncs(synced);

        static SimSettings Settings(float seconds, Stimulus stimulus, SyncWire wire = null) =>
            new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = seconds, jitter = 0f, seed = 7 },
                stimulus = stimulus ?? new Stimulus(),
                wire = wire,
                lagRows = false,
            };

        static List<string> Found(AnimatorController controller, SimSettings settings) =>
            RunFindings.For(Simulation.Run(controller, settings), settings);

        /// <summary>The one finding that opens the given way, or a failure naming what was
        /// found instead — an assertion against a list that turned out to be empty otherwise
        /// passes for the wrong reason.</summary>
        static string Finding(List<string> findings, string opening)
        {
            var matched = All(findings, opening);
            Assert.AreEqual(1, matched.Count,
                "expected one finding about \"" + opening + "\"; the run said:\n  "
                + string.Join("\n  ", findings.ToArray()));
            return matched[0];
        }

        static List<string> All(List<string> findings, string opening)
        {
            var matched = new List<string>();
            foreach (var finding in findings)
                if (finding.Contains(opening)) matched.Add(finding);
            return matched;
        }

        static void NoFinding(List<string> findings, string opening) =>
            Assert.IsEmpty(All(findings, opening),
                "nothing here is worth saying about \"" + opening + "\", and it said:\n  "
                + string.Join("\n  ", findings.ToArray()));

        // ---- states never entered --------------------------------------------

        [Test]
        public void NeverEntered_NamesTheStateNothingAskedFor()
        {
            var settings = Settings(1f, new Stimulus().At(0.05f, "Go", true));
            var finding = Finding(Found(Branching(anyState: true), settings), "never enters");

            StringAssert.Contains("Point", finding, "nothing in this run asked for it");
            StringAssert.DoesNotContain("Wave", finding, "this one it did enter");
            StringAssert.Contains("Base", finding, "a layer is the unit a reader thinks in");
            StringAssert.Contains(Simulation.LocalScope, finding, "and a client is the other");
        }

        [Test]
        public void NeverEntered_SaysNothingWhenTheRunVisitedEveryState()
        {
            // The same layer, asked for both of its states in turn. A finding that survived
            // this would be a finding about the controller rather than about the run.
            var settings = Settings(1.5f,
                new Stimulus().At(0.05f, "Go", true).At(0.5f, "Other", true));
            NoFinding(Found(Branching(anyState: true), settings), "never enters");
        }

        // ---- transitions never seen ------------------------------------------

        [Test]
        public void NeverFired_NamesTheTransitionThatDidNotRun()
        {
            var settings = Settings(1f, new Stimulus().At(0.05f, "Go", true));
            var finding = Finding(Found(Branching(), settings), "never seen in");

            StringAssert.Contains("Idle → Point", finding, "the route nothing opened");
            StringAssert.DoesNotContain("Idle → Wave", finding, "this one ran");
        }

        [Test]
        public void NeverFired_SaysNothingAboutALayerWhoseMovesTheRunCannotName()
        {
            // The whole reason this finding is bounded. A transition of no duration is over
            // inside the frame it starts on, so the via row never catches it — and "Idle → On
            // never fired" would then be said about a transition that had just fired. A layer
            // that moved without the row naming what moved it is left alone entirely.
            var settings = Settings(0.5f, new Stimulus().At(0.05f, "Go", true));
            var trace = Simulation.Run(Instant(), settings);

            Assert.AreEqual("On", trace.Find(Simulation.LocalScope, "Base/state")
                .TextAt(trace.Frames - 1), "the transition did run");
            NoFinding(RunFindings.For(trace, settings), "never seen in");
        }

        // ---- the wire --------------------------------------------------------

        [Test]
        public void LostChange_CountsWhatCameAndWentInsideOneSyncPeriod()
        {
            // Up at 0.25 s and down at 0.30 s, between the samples at 0.2 and 0.4: the wire
            // reads the whole set once a period and read the same value both times, so nothing
            // of this ever left the wearer.
            var settings = Settings(1f,
                new Stimulus().At(0.25f, "Toggle", true).At(0.30f, "Toggle", false),
                Wire("Toggle", "Radial"));
            var finding = Finding(Found(Quiet(), settings), "came and went");

            StringAssert.Contains("Toggle ×1", finding);
            StringAssert.DoesNotContain("Radial", finding, "that one never moved at all");
        }

        [Test]
        public void LostChange_SaysNothingAboutAValueHeldPastTheNextSample()
        {
            // The same press, held across a sample. It arrives late rather than never, which is
            // the wire working and not a finding.
            var settings = Settings(1f,
                new Stimulus().At(0.25f, "Toggle", true).At(0.6f, "Toggle", false),
                Wire("Toggle", "Radial"));
            NoFinding(Found(Quiet(), settings), "came and went");
        }

        [Test]
        public void Quantized_NamesTheValueThatArrivesAsADifferentNumber()
        {
            var settings = Settings(1f, new Stimulus().At(0.25f, "Radial", 2f), Wire("Radial"));
            var finding = Finding(Found(Quiet(), settings), "arrive changed");

            StringAssert.Contains("Radial 2 → 1", finding,
                "a Float crosses as 8 bits over -1..1, so 2 lands as 1");
        }

        [Test]
        public void Quantized_SaysNothingAboutARunThatWasToldNotToRound()
        {
            var wire = Wire("Radial");
            wire.quantize = false;
            var settings = Settings(1f, new Stimulus().At(0.25f, "Radial", 2f), wire);

            // Reporting rounding a run was told to skip would be reporting the settings back.
            NoFinding(Found(Quiet(), settings), "arrive changed");
        }

        [Test]
        public void Stranded_NamesAnInputThatIsNotOnTheWire()
        {
            var settings = Settings(1f,
                new Stimulus().At(0.25f, "Solo", true).At(0.3f, "GestureLeft", 3f),
                Wire("Toggle", "Radial"));
            var finding = Finding(Found(Quiet(), settings), "never leave");

            StringAssert.Contains("Solo", finding, "pressed here and carried nowhere");
            StringAssert.DoesNotContain("GestureLeft", finding,
                "VRChat carries the built-ins on its own channels");
        }

        // ---- all of it, and none of it ---------------------------------------

        /// <summary>One run arranged to raise every kind of finding there is, because the five
        /// are read out of one trace and a test per finding cannot see them tread on each
        /// other.</summary>
        static SimSettings Everything() =>
            Settings(1f, new Stimulus()
                    .At(0.05f, "Go", true)
                    .At(0.25f, "Radial", 0.5f)
                    .At(0.30f, "Radial", 0f)
                    .At(0.50f, "Radial", 2f)
                    .At(0.70f, "Solo", true),
                Wire("Radial"));

        [Test]
        public void Finds_EveryKindOfThingATraceCanSayAboutItself()
        {
            var settings = Everything();
            var findings = Found(Branching(), settings);

            // One per client for the two that are read per client. The other person's copy
            // never hears about Go, so it sits in Idle for the whole run — which is the
            // difference these two rows exist to show.
            var states = All(findings, "never enters");
            Assert.AreEqual(2, states.Count, "the wearer's copy and the other person's");
            StringAssert.Contains("Point", states[0]);
            StringAssert.Contains("Wave", states[1], "the remote reached neither");
            Assert.AreEqual(2, All(findings, "never seen in").Count);

            StringAssert.Contains("Radial ×1", Finding(findings, "came and went"));
            StringAssert.Contains("Radial 2 → 1", Finding(findings, "arrive changed"));
            StringAssert.Contains("Solo", Finding(findings, "never leave"));
        }

        [Test]
        public void WithoutSettings_SaysOnlyWhatTheTraceAloneKnows()
        {
            // A clip opened from disk: the same trace, and no record of what was synced or
            // what was pressed. The findings that would need those are skipped rather than
            // guessed at, and the two the trace answers by itself are unaffected.
            var settings = Everything();
            var findings = RunFindings.For(Simulation.Run(Branching(), settings), null);

            Assert.AreEqual(2, All(findings, "never enters").Count);
            Assert.AreEqual(2, All(findings, "never seen in").Count);
            NoFinding(findings, "came and went");
            NoFinding(findings, "arrive changed");
            NoFinding(findings, "never leave");
        }

        [Test]
        public void AQuietRunFindsNothingAndSaysSo()
        {
            // Every state entered, no transitions to miss, a synced value that stays where the
            // wire can carry it. An empty list rather than a reassuring sentence: the window
            // draws no frame at all for one, and a finding nobody needs is how a list of them
            // stops being read.
            var settings = Settings(1f, new Stimulus().At(0.25f, "Radial", 0.5f), Wire("Radial"));
            Assert.IsEmpty(Found(Quiet(), settings));
        }

        [Test]
        public void AnEmptyTraceIsNotAnErrorAndFindsNothing()
        {
            Assert.IsEmpty(RunFindings.For(null, null));
            Assert.IsEmpty(RunFindings.For(new SignalTrace(), Settings(1f, null)));
        }
    }
}
