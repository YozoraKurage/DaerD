using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD.Tests
{
    /// <summary>
    /// Guards the Japanese catalog against the one way it goes stale: a feature lands, the
    /// English strings go in with it, and nobody comes back to translate them. An untranslated
    /// string falls through to English silently — <see cref="L.Tr"/> is built that way on purpose
    /// — so nothing looks broken until a Japanese user opens the window. This reads the call
    /// sites out of the source and fails instead.
    /// </summary>
    public class LocalizationCoverageTests
    {
        /// <summary>
        /// <c>L.Tr(</c> followed by one or more string literals joined with <c>+</c> — the shape
        /// every call site uses, including the wrapped ones that span several lines. A call whose
        /// argument is a variable matches nothing and is left alone: there is no msgid to check.
        /// </summary>
        static readonly Regex Call = new Regex(
            @"L\.Tr\(\s*""(?<first>(?:[^""\\]|\\.)*)""(?<rest>(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*)",
            RegexOptions.Compiled);

        static readonly Regex Literal = new Regex(@"""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

        [Test]
        public void EveryUiStringHasAJapaneseTranslation()
        {
            var catalog = PoCatalog.Load("ja");
            Assert.That(catalog, Is.Not.Empty, "ja.po did not load — the rest of this test proves nothing");

            var callSites = CallSites();
            var missing = new List<string>();
            foreach (var entry in callSites)
            {
                if (catalog.TryGetValue(entry.Key, out var translated) && !string.IsNullOrEmpty(translated))
                    continue;
                missing.Add("  " + entry.Value + "\n    " + Excerpt(entry.Key) + Hint(entry.Key, catalog, callSites));
            }

            Assert.IsEmpty(missing,
                missing.Count + " UI string(s) reach a Japanese user in English. Translate them in "
                + "Editor/Localization/ja.po:\n" + string.Join("\n", missing));
        }

        /// <summary>
        /// Rewording an English string orphans its translation rather than updating it, and from
        /// the catalog's side that is indistinguishable from a brand new string. The old entry is
        /// still there though, so when a missing msgid opens the same way one that no call site
        /// asks for any more does, say so — the existing Japanese is usually most of the answer.
        /// </summary>
        static string Hint(string msgid, Dictionary<string, string> catalog,
            Dictionary<string, string> callSites)
        {
            const int Shoulder = 24;
            if (msgid.Length < Shoulder) return string.Empty;
            string opening = msgid.Substring(0, Shoulder);

            foreach (var pair in catalog)
            {
                if (!pair.Key.StartsWith(opening) || callSites.ContainsKey(pair.Key)) continue;
                return "\n    ...looks like a reworded '" + Excerpt(pair.Key) + "'"
                    + "\n       whose translation is '" + Excerpt(pair.Value) + "'";
            }
            return string.Empty;
        }

        /// <summary>Every msgid the editor assembly asks for, mapped to where it asks for it.</summary>
        static Dictionary<string, string> CallSites()
        {
            var found = new Dictionary<string, string>();
            foreach (var path in Directory.GetFiles(EditorFolder(), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                foreach (Match match in Call.Matches(text))
                {
                    var msgid = new StringBuilder(Unescape(match.Groups["first"].Value));
                    foreach (Match part in Literal.Matches(match.Groups["rest"].Value))
                        msgid.Append(Unescape(part.Groups[1].Value));

                    string key = msgid.ToString();
                    if (key.Length == 0 || found.ContainsKey(key)) continue;
                    found[key] = Path.GetFileName(path) + ":" + LineOf(text, match.Index);
                }
            }
            Assert.Greater(found.Count, 100,
                "found almost no L.Tr call sites — the scan is broken, not the catalog");
            return found;
        }

        /// <summary>
        /// The Editor folder on disk. Asking the AssetDatabase where a known script lives is what
        /// keeps this working wherever the package is installed — under Packages/ it is not a real
        /// directory until GetFullPath resolves it, the same dance <see cref="PoCatalog"/> does.
        /// </summary>
        static string EditorFolder()
        {
            var anchor = ScriptableObject.CreateInstance<LocalizationAnchor>();
            var script = MonoScript.FromScriptableObject(anchor);
            string assetPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            Object.DestroyImmediate(anchor);

            Assert.IsNotEmpty(assetPath, "could not locate the localization folder");
            // <Editor>/Localization/LocalizationAnchor.cs — up twice.
            string editor = Path.GetDirectoryName(Path.GetDirectoryName(assetPath));
            string full = Path.GetFullPath(editor);
            Assert.IsTrue(Directory.Exists(full), "the editor sources are not on disk at '" + full + "'");
            return full;
        }

        static string Unescape(string literal)
        {
            var builder = new StringBuilder(literal.Length);
            for (int i = 0; i < literal.Length; i++)
            {
                if (literal[i] != '\\' || i + 1 >= literal.Length)
                {
                    builder.Append(literal[i]);
                    continue;
                }
                char escaped = literal[++i];
                switch (escaped)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    default: builder.Append(escaped); break;
                }
            }
            return builder.ToString();
        }

        static int LineOf(string text, int index) => text.Substring(0, index).Split('\n').Length;

        static string Excerpt(string msgid)
        {
            string flat = msgid.Replace("\n", "\\n");
            return flat.Length <= 90 ? flat : flat.Substring(0, 90) + "…";
        }
    }
}
