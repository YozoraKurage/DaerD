using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The shortcut table is the only statement of what a key does — the handlers switch on the
    /// command it resolves to, and the settings page edits it. Everything here is asked of
    /// <see cref="DaerDShortcuts.Defaults"/> or of a hand-built list, never of the live table:
    /// that one reads the preferences of whoever is running the tests.
    /// </summary>
    public class ShortcutTableTests
    {
        [Test]
        public void NoKeystrokeMeansTwoThingsInOneScope()
        {
            var seen = new Dictionary<string, DaerDCommand>();
            foreach (var shortcut in DaerDShortcuts.Defaults)
            {
                string key = shortcut.Scope + " " + shortcut.Binding.Keys;
                Assert.IsFalse(seen.ContainsKey(key),
                    key + " is both " + (seen.ContainsKey(key) ? seen[key].ToString() : "?")
                        + " and " + shortcut.Command);
                seen[key] = shortcut.Command;
            }
        }

        [Test]
        public void EveryCommandIsReachable()
        {
            var bound = new HashSet<DaerDCommand>();
            foreach (var shortcut in DaerDShortcuts.Defaults) bound.Add(shortcut.Command);

            var unreachable = new List<string>();
            foreach (DaerDCommand command in System.Enum.GetValues(typeof(DaerDCommand)))
                if (command != DaerDCommand.None && !bound.Contains(command))
                    unreachable.Add(command.ToString());

            Assert.IsEmpty(unreachable,
                "command(s) no key can reach: " + string.Join(", ", unreachable));
        }

        [Test]
        public void EveryShortcutSaysWhatItDoes()
        {
            foreach (var shortcut in DaerDShortcuts.Defaults)
                Assert.IsNotEmpty(shortcut.Description, shortcut.Command + " has no description");
        }

        static DaerDCommand Resolve(KeyCode key, bool ctrl = false, bool shift = false,
            ShortcutScope scope = ShortcutScope.Graph, bool alt = false) =>
            DaerDShortcuts.Resolve(DaerDShortcuts.Defaults, scope, key, ctrl, shift, alt);

        [Test]
        public void ModifiersAreMatchedExactly()
        {
            Assert.AreEqual(DaerDCommand.FrameAll, Resolve(KeyCode.A));
            Assert.AreEqual(DaerDCommand.SelectAllNodes, Resolve(KeyCode.A, ctrl: true));
            Assert.AreEqual(DaerDCommand.SelectAllTransitions, Resolve(KeyCode.A, ctrl: true, shift: true));

            // Shift alone is nobody's binding, and must not fall back to the unmodified one.
            Assert.AreEqual(DaerDCommand.None, Resolve(KeyCode.A, shift: true));
        }

        [Test]
        public void AScopeOnlyAnswersForItself()
        {
            // T wires the graph; in the inspector it is free for a text field to use.
            Assert.AreEqual(DaerDCommand.Connect, Resolve(KeyCode.T));
            Assert.AreEqual(DaerDCommand.None, Resolve(KeyCode.T, scope: ShortcutScope.Inspector));
        }

        [Test]
        public void ANullEventResolvesToNothing()
        {
            // The IMGUI handlers ask before checking anything else, including whether there is
            // an event at all.
            Assert.AreEqual(DaerDCommand.None, DaerDShortcuts.Resolve(ShortcutScope.Inspector, (Event)null));
        }

        // ---- rebinding ----------------------------------------------------------

        static List<DaerDShortcut> Table(params DaerDShortcut[] shortcuts) =>
            new List<DaerDShortcut>(shortcuts);

        static DaerDShortcut Entry(DaerDCommand command, KeyCode key, bool ctrl = false,
            bool shift = false, bool enabled = true, bool alt = false) =>
            new DaerDShortcut(ShortcutScope.Graph, command,
                new ShortcutBinding(key, ctrl, shift, enabled, alt), command.ToString());

        [Test]
        public void ASwitchedOffCommandHoldsNoKey()
        {
            var table = Table(Entry(DaerDCommand.FrameAll, KeyCode.A, enabled: false));

            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(table, ShortcutScope.Graph, KeyCode.A, false, false));
        }

        [Test]
        public void ASwitchedOffCommandDoesNotStandInTheWayOfAnother()
        {
            var table = Table(Entry(DaerDCommand.FrameAll, KeyCode.A, enabled: false));

            Assert.AreEqual(DaerDCommand.None, DaerDShortcuts.Conflict(table, ShortcutScope.Graph,
                DaerDCommand.Connect, new ShortcutBinding(KeyCode.A)));
        }

        [Test]
        public void AKeyAnotherCommandHolds_IsAConflict()
        {
            var table = Table(Entry(DaerDCommand.FrameAll, KeyCode.A));

            Assert.AreEqual(DaerDCommand.FrameAll, DaerDShortcuts.Conflict(table, ShortcutScope.Graph,
                DaerDCommand.Connect, new ShortcutBinding(KeyCode.A)));
            // Rebinding a command to the key it already has is not a clash with itself.
            Assert.AreEqual(DaerDCommand.None, DaerDShortcuts.Conflict(table, ShortcutScope.Graph,
                DaerDCommand.FrameAll, new ShortcutBinding(KeyCode.A)));
            // Nor is the same key in the other scope, where it means something else on purpose.
            Assert.AreEqual(DaerDCommand.None, DaerDShortcuts.Conflict(table, ShortcutScope.Inspector,
                DaerDCommand.Connect, new ShortcutBinding(KeyCode.A)));
        }

        [Test]
        public void ABindingSurvivesBeingWrittenDownAndReadBack()
        {
            foreach (var original in new[]
            {
                new ShortcutBinding(KeyCode.A),
                new ShortcutBinding(KeyCode.V, ctrl: true, shift: true),
                new ShortcutBinding(KeyCode.UpArrow, enabled: false),
                new ShortcutBinding(KeyCode.F2, ctrl: true, shift: false, enabled: false),
            })
            {
                Assert.IsTrue(ShortcutBinding.TryParse(original.Serialize(), out var read),
                    "could not read back " + original.Serialize());
                Assert.IsTrue(original.SameKeys(read), original.Keys + " came back as " + read.Keys);
                Assert.AreEqual(original.Enabled, read.Enabled, original.Serialize());
            }
        }

        [Test]
        public void RubbishInThePreferenceIsIgnoredRatherThanCrashing()
        {
            // A hand-edited or older preference must fall back to the default, not throw.
            Assert.IsFalse(ShortcutBinding.TryParse(string.Empty, out _));
            Assert.IsFalse(ShortcutBinding.TryParse("A", out _), "no separator");
            Assert.IsFalse(ShortcutBinding.TryParse("NotAKey:C", out _));
        }

        // ---- Alt --------------------------------------------------------------

        /// <summary>
        /// A modifier nobody reads is a modifier nobody can require: Alt+A used to fire
        /// FrameAll, because the resolver compared Ctrl and Shift and let anything else
        /// through. A user who held Alt while rebinding got the bare key instead, and then
        /// pressing the bare key did the thing they thought they had moved out of the way.
        /// </summary>
        [Test]
        public void AltIsMatchedLikeEveryOtherModifier()
        {
            Assert.AreEqual(DaerDCommand.FrameAll, Resolve(KeyCode.A));
            Assert.AreEqual(DaerDCommand.None, Resolve(KeyCode.A, alt: true));
            Assert.AreEqual(DaerDCommand.None, Resolve(KeyCode.A, ctrl: true, alt: true),
                "Ctrl+Alt+A is not Ctrl+A either");

            // And a binding that does ask for Alt only answers to Alt.
            var table = Table(Entry(DaerDCommand.FrameSelection, KeyCode.A, alt: true));
            Assert.AreEqual(DaerDCommand.FrameSelection,
                DaerDShortcuts.Resolve(table, ShortcutScope.Graph, KeyCode.A, false, false, true));
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(table, ShortcutScope.Graph, KeyCode.A, false, false, false));
        }

        [Test]
        public void AnAltBindingIsADifferentKeystrokeFromThePlainOne()
        {
            var table = Table(Entry(DaerDCommand.FrameAll, KeyCode.A));

            Assert.AreEqual(DaerDCommand.None, DaerDShortcuts.Conflict(table, ShortcutScope.Graph,
                DaerDCommand.Connect, new ShortcutBinding(KeyCode.A, alt: true)),
                "Alt+A is free while A is taken");
            Assert.AreEqual(DaerDCommand.FrameAll, DaerDShortcuts.Conflict(table, ShortcutScope.Graph,
                DaerDCommand.Connect, new ShortcutBinding(KeyCode.A)));
            StringAssert.Contains("Alt+", new ShortcutBinding(KeyCode.A, alt: true).Keys);
        }

        [Test]
        public void AnAltBindingIsStored_AndAPreferenceWrittenBeforeAltStillReads()
        {
            var alt = new ShortcutBinding(KeyCode.A, ctrl: true, alt: true);
            Assert.IsTrue(ShortcutBinding.TryParse(alt.Serialize(), out var read),
                "could not read back " + alt.Serialize());
            Assert.IsTrue(alt.SameKeys(read), alt.Keys + " came back as " + read.Keys);
            Assert.IsTrue(read.Alt);

            // The contract 60a8660 set: a preference this build cannot make sense of falls back
            // to the default. One written before Alt existed IS readable — it simply has no "A"
            // among its modifiers — so it must come back as the binding it was, not be dropped.
            Assert.IsTrue(ShortcutBinding.TryParse("V:CS", out var older));
            Assert.IsFalse(older.Alt);
            Assert.IsTrue(new ShortcutBinding(KeyCode.V, ctrl: true, shift: true).SameKeys(older));
            Assert.IsTrue(older.Enabled);
        }

        // ---- what the hints say -----------------------------------------------

        /// <summary>
        /// Every "press T" in the UI is the table's answer, spelled out. They used to be
        /// literals, so rebinding Connect left the graph's own hint telling the user to press a
        /// key that now did something else — the table said one thing and the window another.
        /// </summary>
        [Test]
        public void AKeyHintIsReadOffTheTable_AndFollowsARebinding()
        {
            var table = Table(
                Entry(DaerDCommand.Connect, KeyCode.T),
                Entry(DaerDCommand.FocusSearch, KeyCode.F, ctrl: true),
                Entry(DaerDCommand.MarkSources, KeyCode.M, enabled: false));

            Assert.AreEqual("T", DaerDShortcuts.KeysOf(table, ShortcutScope.Graph, DaerDCommand.Connect));
            Assert.AreEqual(new ShortcutBinding(KeyCode.F, ctrl: true).Keys,
                DaerDShortcuts.KeysOf(table, ShortcutScope.Graph, DaerDCommand.FocusSearch));
            Assert.AreEqual("  (T)", DaerDShortcuts.Hint(table, ShortcutScope.Graph, DaerDCommand.Connect));

            var rebound = Table(Entry(DaerDCommand.Connect, KeyCode.Y, shift: true));
            Assert.AreEqual("Shift+Y",
                DaerDShortcuts.KeysOf(rebound, ShortcutScope.Graph, DaerDCommand.Connect));
            Assert.AreEqual("  (Shift+Y)",
                DaerDShortcuts.Hint(rebound, ShortcutScope.Graph, DaerDCommand.Connect));
        }

        [Test]
        public void ACommandNoKeyReaches_HasNoHintRatherThanAnEmptyOne()
        {
            var table = Table(
                Entry(DaerDCommand.Connect, KeyCode.T),
                Entry(DaerDCommand.MarkSources, KeyCode.M, enabled: false));

            Assert.IsEmpty(DaerDShortcuts.KeysOf(table, ShortcutScope.Graph, DaerDCommand.MarkSources),
                "a switched-off command holds no key to name");
            Assert.IsEmpty(DaerDShortcuts.Hint(table, ShortcutScope.Graph, DaerDCommand.MarkSources));
            Assert.IsEmpty(DaerDShortcuts.Hint(table, ShortcutScope.Graph, DaerDCommand.Rename),
                "nor does one this scope does not have at all");

            // A sentence built around two keys goes altogether when either is missing: half of
            // it would name a shortcut that does not exist.
            Assert.AreEqual(" T then M copies", DaerDShortcuts.Sentence(
                Table(Entry(DaerDCommand.Connect, KeyCode.T), Entry(DaerDCommand.MarkSources, KeyCode.M)),
                ShortcutScope.Graph, DaerDCommand.Connect, DaerDCommand.MarkSources,
                "{0} then {1} copies"));
            Assert.IsEmpty(DaerDShortcuts.Sentence(table, ShortcutScope.Graph,
                DaerDCommand.Connect, DaerDCommand.MarkSources, "{0} then {1} copies"));
        }
    }
}
