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
    /// Guards the Japanese catalog against the two ways it goes stale, which are not the same
    /// way. The first is a string that goes through <see cref="L.Tr"/> and has no entry: the
    /// call site asks and the catalog has no answer. The second is a string that never asks at
    /// all — written straight into a GUIContent or a label argument — and no amount of checking
    /// the catalog can see it, because from the catalog's side nothing is missing.
    ///
    /// This class used to check only the first, and reported full coverage while roughly fifty
    /// strings were permanently English, seventeen of them with a translation sitting in the
    /// catalog that nothing asked for. Both halves are here now.
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
        /// The places a string literal is user-visible text: the label or tooltip of a GUIContent,
        /// the label argument of an IMGUI field, a button's caption, a dialog's title, a
        /// VisualElement's tooltip. Written to match a literal sitting DIRECTLY after the opening
        /// bracket, so a call already wrapped in <c>L.Tr(</c> simply does not match.
        ///
        /// What it does not see: the later arguments of a dialog (its body, its button captions)
        /// and anything built by concatenation before it reaches the call. Those are real gaps —
        /// stated here rather than papered over, because a check that pretends to be exhaustive
        /// is how this file came to be wrong in the first place.
        /// </summary>
        static readonly Regex UiLiteral = new Regex(
            @"(?:new GUIContent\(|"
            + @"EditorGUILayout\.(?:LabelField|HelpBox|Toggle|ToggleLeft|Slider|IntSlider|EnumPopup"
            + @"|Popup|ObjectField|TextField|DelayedTextField|IntField|FloatField|Vector2Field"
            + @"|Vector3Field|ColorField|CurveField|Foldout)\(|"
            + @"EditorGUI\.(?:LabelField|Toggle|Slider|EnumPopup|Popup|ObjectField|TextField"
            + @"|IntField|FloatField)\(|"
            + @"GUILayout\.(?:Button|Label|Toggle)\(|"
            + @"EditorUtility\.DisplayDialog\(|"
            + @"\btooltip = )""(?<text>(?:[^""\\]|\\.)*)""",
            RegexOptions.Compiled);

        static readonly Regex Letters = new Regex("[A-Za-z]", RegexOptions.Compiled);

        /// <summary>
        /// Literals that are user-visible and still must not be translated: glyphs used as
        /// buttons, acronyms DaerD shows as badges, product and brand names, and the two language
        /// names on the settings page, which are each written in their own language on purpose.
        /// </summary>
        static readonly HashSet<string> NotForTranslation = new HashSet<string>
        {
            "DaerD", "VRChat/", "English", "日本語",
            "AAP", "DBT", "SYNC", "C#", "Req",
        };

        [Test]
        public void EveryUiStringGoesThroughTheCatalogue()
        {
            var stranded = new List<string>();
            int scanned = 0;
            foreach (var path in Directory.GetFiles(EditorFolder(), "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match match in UiLiteral.Matches(lines[i]))
                    {
                        string text = Unescape(match.Groups["text"].Value);
                        // One letter or none is a glyph, an arrow or a spacer, not a sentence.
                        if (Letters.Matches(text).Count < 2) continue;
                        if (NotForTranslation.Contains(text)) continue;
                        stranded.Add("  " + Path.GetFileName(path) + ":" + (i + 1)
                            + "\n    \"" + Excerpt(text) + "\"");
                    }
            }

            Assert.Greater(scanned, 50, "found almost no sources — the scan is broken, not the code");
            Assert.IsEmpty(stranded,
                stranded.Count + " UI string(s) never reach the catalogue at all — wrap them in "
                + "L.Tr(), or add them to NotForTranslation if they are a glyph or a name:\n"
                + string.Join("\n", stranded));
        }

        /// <summary>
        /// The shortcut table states its descriptions as plain English fields and translates them
        /// where they are drawn, so the scan for <c>L.Tr("literal")</c> cannot see them: from its
        /// side there is nothing to check, and a Japanese user would simply get English. Asked of
        /// the table directly instead.
        /// </summary>
        [Test]
        public void EveryShortcutDescriptionHasAJapaneseTranslation()
        {
            var catalog = PoCatalog.Load("ja");
            Assert.That(catalog, Is.Not.Empty, "ja.po did not load — the rest of this test proves nothing");

            var missing = new List<string>();
            foreach (var shortcut in DaerDShortcuts.All)
                if (!catalog.TryGetValue(shortcut.Description, out var translated)
                    || string.IsNullOrEmpty(translated))
                    missing.Add("  " + shortcut.Keys + "  \"" + shortcut.Description + "\"");

            Assert.IsEmpty(missing,
                missing.Count + " shortcut description(s) reach a Japanese user in English:\n"
                + string.Join("\n", missing));
        }

        static readonly Regex Placeholder = new Regex(@"\{(\d+)[^}]*\}", RegexOptions.Compiled);

        /// <summary>
        /// A translation that uses a placeholder the English string does not have throws when it
        /// is formatted — <c>string.Format</c> is handed fewer arguments than the text asks for.
        /// It throws in Japanese only, so nothing catches it until a Japanese user reaches that
        /// message. The test run itself is pinned to English (see <c>TestLanguage</c>), which
        /// makes every other assertion mean something and makes this one necessary: it is the
        /// half of the risk that pinning hides.
        /// </summary>
        [Test]
        public void NoTranslationAsksForAnArgumentTheEnglishDoesNot()
        {
            var catalog = PoCatalog.Load("ja");
            Assert.That(catalog, Is.Not.Empty, "ja.po did not load — the rest of this test proves nothing");

            var wrong = new List<string>();
            foreach (var pair in catalog)
            {
                if (string.IsNullOrEmpty(pair.Value)) continue;
                var english = Indices(pair.Key);
                var japanese = Indices(pair.Value);
                japanese.ExceptWith(english);
                if (japanese.Count == 0) continue;
                var extra = new List<string>();
                foreach (int index in japanese) extra.Add("{" + index + "}");
                extra.Sort();
                wrong.Add("  " + string.Join(", ", extra) + " is not in the English:\n    \""
                    + Excerpt(pair.Key) + "\"\n    \"" + Excerpt(pair.Value) + "\"");
            }

            Assert.IsEmpty(wrong,
                wrong.Count + " translation(s) would throw when formatted:\n" + string.Join("\n", wrong));
        }

        static HashSet<int> Indices(string text)
        {
            var found = new HashSet<int>();
            foreach (Match match in Placeholder.Matches(text))
                if (int.TryParse(match.Groups[1].Value, out int index)) found.Add(index);
            return found;
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
