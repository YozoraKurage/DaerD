using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The shortcut table is the only statement of what a key does — the handlers switch on the
    /// command it resolves to, and the settings page reads it to list them. These are the two
    /// ways such a table goes wrong: two commands on one keystroke, and a command in the enum
    /// that nothing can reach.
    /// </summary>
    public class ShortcutTableTests
    {
        [Test]
        public void NoKeystrokeMeansTwoThingsInOneScope()
        {
            var seen = new Dictionary<string, DaerDCommand>();
            foreach (var shortcut in DaerDShortcuts.All)
            {
                string key = shortcut.Scope + " " + shortcut.Keys;
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
            foreach (var shortcut in DaerDShortcuts.All) bound.Add(shortcut.Command);

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
            foreach (var shortcut in DaerDShortcuts.All)
                Assert.IsNotEmpty(shortcut.Description, shortcut.Command + " has no description");
        }

        [Test]
        public void ModifiersAreMatchedExactly()
        {
            Assert.AreEqual(DaerDCommand.Paste,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.V, ctrl: true, shift: false));
            Assert.AreEqual(DaerDCommand.PasteAsNew,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.V, ctrl: true, shift: true));

            // Neither must fall back to the other, or to the unmodified key.
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.V, ctrl: false, shift: false));
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.V, ctrl: false, shift: true));
        }

        [Test]
        public void ARegisteredCommandIsNotAnsweredHereAsWell()
        {
            // These arrive through Unity's shortcut manager, at whatever key the user bound
            // them to. Answering for their defaults too would run them twice on a stock
            // install, and on the old key after a rebind.
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.T, ctrl: false, shift: false));
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.A, ctrl: true, shift: false));
        }

        [Test]
        public void OnlyTheKeysThatDependOnFocusAreHandledLocally()
        {
            // The copy / paste family is the whole of it: those three mean one thing in the
            // graph and another in the inspector, so which pane has focus has to decide.
            var local = new List<DaerDCommand>();
            foreach (var shortcut in DaerDShortcuts.All)
                if (!shortcut.Rebindable && !local.Contains(shortcut.Command)) local.Add(shortcut.Command);
            local.Sort();

            CollectionAssert.AreEquivalent(
                new[] { DaerDCommand.Copy, DaerDCommand.Paste, DaerDCommand.PasteAsNew }, local);
        }

        [Test]
        public void ANullEventResolvesToNothing()
        {
            // The IMGUI handlers ask before checking anything else, including whether there is
            // an event at all.
            Assert.AreEqual(DaerDCommand.None, DaerDShortcuts.Resolve(ShortcutScope.Inspector, (Event)null));
        }
    }
}
