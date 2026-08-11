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
            Assert.AreEqual(DaerDCommand.FrameAll,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.A, ctrl: false, shift: false));
            Assert.AreEqual(DaerDCommand.SelectAllNodes,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.A, ctrl: true, shift: false));
            Assert.AreEqual(DaerDCommand.SelectAllTransitions,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.A, ctrl: true, shift: true));

            // Shift alone is nobody's binding, and must not fall back to the unmodified one.
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.A, ctrl: false, shift: true));
        }

        [Test]
        public void AScopeOnlyAnswersForItself()
        {
            // T wires the graph; in the inspector it is free for a text field to use.
            Assert.AreEqual(DaerDCommand.Connect,
                DaerDShortcuts.Resolve(ShortcutScope.Graph, KeyCode.T, ctrl: false, shift: false));
            Assert.AreEqual(DaerDCommand.None,
                DaerDShortcuts.Resolve(ShortcutScope.Inspector, KeyCode.T, ctrl: false, shift: false));
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
