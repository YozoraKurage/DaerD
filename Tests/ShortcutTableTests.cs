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
            ShortcutScope scope = ShortcutScope.Graph) =>
            DaerDShortcuts.Resolve(DaerDShortcuts.Defaults, scope, key, ctrl, shift);

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
            bool shift = false, bool enabled = true) =>
            new DaerDShortcut(ShortcutScope.Graph, command,
                new ShortcutBinding(key, ctrl, shift, enabled), command.ToString());

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
    }
}
