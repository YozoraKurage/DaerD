using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.Animations;
using UnityEngine;
using Yozolab.DaerD.DynamicAnalyze;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// What DD DynamicAnalyze says before it runs anything: the settings that are already set
    /// up to answer nothing, and how much a run of them is about to cost.
    ///
    /// Both are read off settings rather than off a result, which is what lets them be asked
    /// without a window — and is the whole reason they are worth having, since the point of
    /// each is to be said while the field that causes it can still be edited.
    ///
    /// The warnings are user-facing sentences, so the assertions are on their text and the
    /// language is pinned. TestLanguage pins it for the whole suite already; pinned again here
    /// so this file means what it says when read on its own.
    /// </summary>
    public class RunWarningsTests
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

        /// <summary>What the controller declares, in the shape the panel hands over: names and
        /// nothing else, because that is all any of these questions is about.</summary>
        static List<string> Declared(params string[] names) => new List<string>(names);

        static List<string> Synced(params string[] names) => new List<string>(names);

        static List<string> Warnings(bool wire, List<string> synced, List<string> declared,
            Stimulus stimulus = null) =>
            RunWarnings.For(wire, synced, declared, stimulus);

        static string One(List<string> warnings, string opening)
        {
            var matched = new List<string>();
            foreach (var warning in warnings)
                if (warning.Contains(opening)) matched.Add(warning);
            Assert.AreEqual(1, matched.Count,
                "expected one warning about \"" + opening + "\"; the panel said:\n  "
                + string.Join("\n  ", warnings.ToArray()));
            return matched[0];
        }

        static void None(List<string> warnings, string opening)
        {
            foreach (var warning in warnings)
                Assert.IsFalse(warning.Contains(opening),
                    "nothing here is worth saying about \"" + opening + "\", and it said:\n  "
                    + string.Join("\n  ", warnings.ToArray()));
        }

        // ---- a wire that carries nothing --------------------------------------

        [Test]
        public void NothingTravels_IsSaidWhileThereIsStillTimeToSyncSomething()
        {
            var warnings = Warnings(true, Synced(), Declared("Toggle"));
            StringAssert.Contains("learns nothing", One(warnings, "Nothing is on the wire"));

            None(Warnings(true, Synced("Toggle"), Declared("Toggle")), "Nothing is on the wire");
        }

        [Test]
        public void NothingIsSaidToARunWithNobodyToSendTo()
        {
            // Every one of these is about a value reaching somebody else. With one client
            // there is nobody, and an empty sync list is then not a mistake but the setting.
            var stimulus = new Stimulus().At(0.1f, "Toggle", true);
            Assert.IsEmpty(Warnings(false, Synced(), Declared("Toggle"), stimulus));
        }

        // ---- names with nothing behind them -----------------------------------

        [Test]
        public void StaleName_NamesTheSyncedNameTheControllerHasNotGot()
        {
            var warning = One(
                Warnings(true, Synced("Toggle", "Renamed"), Declared("Toggle")),
                "not parameters of this controller");
            StringAssert.Contains("Renamed", warning);
            StringAssert.DoesNotContain("Toggle", warning, "this one is there");
            StringAssert.Contains("1 synced name", warning);
        }

        [Test]
        public void StaleName_LeavesTheBuiltInsAlone_HoweverTheControllerIsWritten()
        {
            // VRChat feeds a built-in whether or not the controller declares one, so a store
            // that names it is describing the platform rather than making a mistake.
            None(Warnings(true, Synced("GestureLeft"), Declared("Toggle")),
                "not parameters of this controller");
            None(Warnings(true, Synced("Toggle"), Declared("Toggle")),
                "not parameters of this controller");
        }

        [Test]
        public void TheListsMarkAndTheWarningAreTheSameAnswer()
        {
            // The panel marks a row with Missing and the sentence under it counts the same
            // call, so what a reader is looking at and what they are being told cannot differ.
            var declared = Declared("Toggle");
            Assert.IsTrue(RunWarnings.Missing("Renamed", declared));
            Assert.IsFalse(RunWarnings.Missing("Toggle", declared));
            Assert.IsFalse(RunWarnings.Missing("GestureLeft", declared));
            Assert.IsFalse(RunWarnings.Missing(string.Empty, declared));
        }

        // ---- inputs that will not leave ---------------------------------------

        [Test]
        public void StrandedInput_IsSaidBeforeTheRunAsWellAsAfterIt()
        {
            var stimulus = new Stimulus()
                .At(0.1f, "Toggle", true)
                .At(0.2f, "Solo", true)
                .At(0.3f, "Solo", false);
            var warning = One(
                Warnings(true, Synced("Toggle"), Declared("Toggle", "Solo"), stimulus),
                "not on the wire");

            StringAssert.Contains("Solo", warning);
            StringAssert.DoesNotContain("Toggle", warning, "that one travels");
            StringAssert.Contains("1 timed input", warning, "one name, however often it is poked");
        }

        [Test]
        public void StrandedInput_IsNotSaidOfAPressAimedAtSomebodyElsesCopy()
        {
            // An input aimed at a remote is asking what THAT copy does with it. Whether the
            // wire would have carried it is not the question being asked.
            var stimulus = new Stimulus().At(0.1f, "Solo", true, Simulation.RemoteScope);
            None(Warnings(true, Synced("Toggle"), Declared("Toggle", "Solo"), stimulus),
                "not on the wire");
        }

        [Test]
        public void StrandedInput_LeavesBuiltInsAndNamesTheControllerHasNotGotAlone()
        {
            // A built-in reaches the other person on VRChat's own channels, and a name the
            // controller does not declare is the other warning's business rather than counted
            // twice under this one.
            var stimulus = new Stimulus()
                .At(0.1f, "GestureLeft", 3f)
                .At(0.2f, "Ghost", true);
            None(Warnings(true, Synced("Toggle"), Declared("Toggle"), stimulus),
                "not on the wire");
        }

        [Test]
        public void AllThreeAreSaidTogetherWhenAllThreeAreTrue()
        {
            var stimulus = new Stimulus().At(0.1f, "Solo", true);
            var warnings = Warnings(true, Synced(), Declared("Solo"), stimulus);
            // The stale-name warning needs a name to be stale, so an empty list raises the
            // other two — which is the pairing a fresh window actually produces.
            Assert.AreEqual(2, warnings.Count);
            One(warnings, "Nothing is on the wire");
            One(warnings, "not on the wire");

            warnings = Warnings(true, Synced("Renamed"), Declared("Solo"), stimulus);
            Assert.AreEqual(2, warnings.Count);
            One(warnings, "not parameters of this controller");
            One(warnings, "not on the wire");
        }

        // ---- the store, beside the button that reads it -----------------------

        [Test]
        public void DiffersFromStore_IsAboutMembershipAndNotAboutOrder()
        {
            var stored = Declared("A", "B");
            Assert.IsFalse(RunWarnings.DiffersFromStore(Synced("B", "A"), stored),
                "a sample carries a set; the order it is listed in is not part of it");
            Assert.IsTrue(RunWarnings.DiffersFromStore(Synced("A"), stored));
            Assert.IsTrue(RunWarnings.DiffersFromStore(Synced("A", "B", "C"), stored));
            Assert.IsTrue(RunWarnings.DiffersFromStore(Synced("A", "C"), stored));
        }

        [Test]
        public void DiffersFromStore_SaysNothingWhenThereIsNoStoreToDifferFrom()
        {
            // Null and empty are different answers: nothing to compare against is not the same
            // as a store that syncs nothing, and a notice for the first would appear on every
            // controller that has no avatar behind it.
            Assert.IsFalse(RunWarnings.DiffersFromStore(Synced("A"), null));
            Assert.IsTrue(RunWarnings.DiffersFromStore(Synced("A"), Declared()));
        }

        // ---- what a run will cost ---------------------------------------------

        /// <summary>Three parameters and two layers, which is enough for the row count to be
        /// wrong in every way it could be: per client, per layer, per remote.</summary>
        static AnimatorController Small()
        {
            var controller = new AnimatorController();
            controller.AddLayer("Base");
            controller.AddLayer("Extra");
            controller.AddParameter("Toggle", AnimatorControllerParameterType.Bool);
            controller.AddParameter("Radial", AnimatorControllerParameterType.Float);
            controller.AddParameter("N", AnimatorControllerParameterType.Int);
            foreach (var layer in controller.layers)
                layer.stateMachine.defaultState = layer.stateMachine.AddState("Idle");
            return controller;
        }

        static SimSettings Settings(SyncWire wire, bool lagRows) =>
            new SimSettings
            {
                clock = new SimClock { fps = 60f, seconds = 0.5f, jitter = 0f, seed = 7 },
                stimulus = new Stimulus(),
                wire = wire,
                lagRows = lagRows,
            };

        static void AssertCosts(AnimatorController controller, SimSettings settings)
        {
            var trace = Simulation.Run(controller, settings);
            int rows = RunCost.Rows(settings, controller.parameters.Length,
                controller.layers.Length);
            Assert.AreEqual(trace.Signals.Count, rows,
                "the estimate counts the rows the recorder actually declares");
            Assert.AreEqual((long)trace.Frames * trace.Signals.Count,
                RunCost.Samples(settings, controller.parameters.Length,
                    controller.layers.Length),
                "and a sample is one row written once");
        }

        [Test]
        public void Cost_CountsExactlyTheRowsARunOfTheseSettingsDeclares()
        {
            var controller = Small();
            // An Animator question: one client, no wire rows, nobody to be behind.
            AssertCosts(controller, Settings(null, true));
            // One other person, with and without the row per parameter saying how far behind
            // they are — which is the setting that changes the cost most on a real avatar.
            AssertCosts(controller, Settings(new SyncWire { intervalSeconds = 0.1f }, true));
            AssertCosts(controller, Settings(new SyncWire { intervalSeconds = 0.1f }, false));
            // Three of them, arriving at their own times.
            AssertCosts(controller,
                Settings(new SyncWire { intervalSeconds = 0.1f }.Joining(0.1f, 0.2f), true));
        }

        [Test]
        public void Cost_IsFramesTimesRows_AndAnEmptySettingIsFree()
        {
            var settings = Settings(new SyncWire { intervalSeconds = 0.1f }, true);
            settings.clock.seconds = 10f;
            // 600 frames × (2 clients × (100 + 20 × 4) + 3 wire rows + 100 lag rows).
            Assert.AreEqual(600, settings.clock.Frames);
            Assert.AreEqual(2 * 180 + 3 + 100, RunCost.Rows(settings, 100, 20));
            Assert.AreEqual(600L * (2 * 180 + 3 + 100), RunCost.Samples(settings, 100, 20));

            Assert.AreEqual(0, RunCost.Rows(null, 100, 20));
            Assert.AreEqual(0L, RunCost.Samples(null, 100, 20));
        }

        [Test]
        public void Cost_ThresholdSitsJustPastARunSomebodyMeantToAskFor()
        {
            // The run the doc on RunCost.Uncomfortable describes: a hundred parameters and
            // twenty layers, four other people, a minute at 60 fps. Big, and asked for on
            // purpose — a window that confirmed this one would be confirming everything.
            var wire = new SyncWire { intervalSeconds = 0.1f }.Joining(0f, 0f, 0f);
            var settings = Settings(wire, true);
            settings.clock.seconds = 60f;
            long samples = RunCost.Samples(settings, 100, 20);
            Assert.AreEqual(4712400L, samples);
            Assert.Less(samples, RunCost.Uncomfortable);

            // Ten times as long is what a typed extra digit looks like, and it is asked about.
            settings.clock.seconds = 600f;
            Assert.Greater(RunCost.Samples(settings, 100, 20), RunCost.Uncomfortable);
        }
    }
}
