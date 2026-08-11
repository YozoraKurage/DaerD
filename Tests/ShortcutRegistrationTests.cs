using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor.ShortcutManagement;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// The registered half of the table has its defaults written twice: once in the table, and
    /// once in the [Shortcut] attribute, which needs constants. Unity is the one that reconciles
    /// them, so it is Unity that gets asked whether they agree.
    /// </summary>
    public class ShortcutRegistrationTests
    {
        static HashSet<string> RegisteredIds()
        {
            var ids = new HashSet<string>();
            foreach (var id in ShortcutManager.instance.GetAvailableShortcutIds())
                if (id.StartsWith("DaerD/")) ids.Add(id);
            return ids;
        }

        [Test]
        public void EveryRebindableCommandIsRegisteredWithUnity()
        {
            var registered = RegisteredIds();
            if (registered.Count == 0)
                Assert.Ignore("the shortcut manager has no DaerD ids in this run — nothing to compare against");

            var missing = new List<string>();
            foreach (var shortcut in DaerDShortcuts.All)
                if (shortcut.Rebindable && !registered.Contains(shortcut.Id))
                    missing.Add(shortcut.Id);

            Assert.IsEmpty(missing,
                "table id(s) with no [Shortcut] behind them: " + string.Join(", ", missing));
        }

        [Test]
        public void NothingIsRegisteredThatTheTableDoesNotKnowAbout()
        {
            var registered = RegisteredIds();
            if (registered.Count == 0)
                Assert.Ignore("the shortcut manager has no DaerD ids in this run — nothing to compare against");

            var known = new HashSet<string>();
            foreach (var shortcut in DaerDShortcuts.All)
                if (shortcut.Rebindable) known.Add(shortcut.Id);

            var strays = new List<string>();
            foreach (var id in registered)
                if (!known.Contains(id)) strays.Add(id);

            // A registered shortcut the table does not carry works, but is invisible: it never
            // appears in the settings page and its description is never translated.
            Assert.IsEmpty(strays,
                "registered shortcut(s) missing from the table: " + string.Join(", ", strays));
        }

        [Test]
        public void TheDefaultsInTheTableAreTheDefaultsUnityRegistered()
        {
            var registered = RegisteredIds();
            if (registered.Count == 0)
                Assert.Ignore("the shortcut manager has no DaerD ids in this run — nothing to compare against");

            var wrong = new List<string>();
            foreach (var shortcut in DaerDShortcuts.All)
            {
                if (!shortcut.Rebindable || !registered.Contains(shortcut.Id)) continue;
                string bound = ShortcutManager.instance.GetShortcutBinding(shortcut.Id).ToString();
                // Only meaningful on a stock install; a rebound key is the user's business, and
                // a test run is one.
                if (!string.IsNullOrEmpty(bound) && !Same(bound, shortcut.Keys))
                    wrong.Add(shortcut.Id + ": table says " + shortcut.Keys + ", Unity says " + bound);
            }

            Assert.IsEmpty(wrong, string.Join("\n", wrong));
        }

        /// <summary>Unity spells a binding its own way ("Ctrl+Shift+A", "Up"); compared without
        /// spacing or case so the two lists can be written naturally.</summary>
        static bool Same(string unity, string table) =>
            Flatten(unity) == Flatten(table);

        static string Flatten(string keys) =>
            keys.Replace(" ", string.Empty).Replace("Cmd", "Ctrl")
                .Replace("↑", "Up").Replace("↓", "Down").Replace("←", "Left").Replace("→", "Right")
                .Replace("Arrow", string.Empty)
                .ToLowerInvariant();
    }
}
