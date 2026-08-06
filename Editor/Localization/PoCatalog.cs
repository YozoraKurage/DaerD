using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    /// <summary>
    /// Gettext .po reader — the format Poedit and the other VRChat editor extensions use, so a
    /// translator can open <c>Editor/Localization/&lt;locale&gt;.po</c>, translate, and drop it
    /// back in without touching any C#.
    ///
    /// Only what a UI catalog needs is supported: <c>msgid</c> / <c>msgstr</c>, the
    /// <c>msgid ""</c> + continuation-line form, and standard escapes. Plural forms and
    /// <c>msgctxt</c> are skipped rather than half-supported — an entry with a context is simply
    /// not indexed, so it falls back to its English source string.
    /// </summary>
    static class PoCatalog
    {
        /// <summary>Empty catalog handed out when a locale has no .po file (English, or a
        /// language nobody has translated yet). Callers then fall back to the msgid.</summary>
        static readonly Dictionary<string, string> Empty = new Dictionary<string, string>();

        /// <summary>
        /// Loads <paramref name="locale"/>.po from the folder this script lives in. Missing
        /// files are not an error: the source strings are English already.
        /// </summary>
        public static Dictionary<string, string> Load(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return Empty;
            string folder = FolderPath();
            if (string.IsNullOrEmpty(folder)) return Empty;

            // Asset paths use forward slashes and may sit under Packages/, which is not a real
            // directory for a VPM-installed package — GetFullPath is what resolves it to the
            // package cache, so go through it explicitly rather than hoping File.Exists does.
            string path = folder.Replace('\\', '/') + "/" + locale + ".po";
            try
            {
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) return Empty;
                return Parse(File.ReadAllText(full, Encoding.UTF8));
            }
            catch (IOException e)
            {
                Debug.LogWarning("DaerD: could not read '" + path + "' — falling back to English. " + e.Message);
                return Empty;
            }
        }

        /// <summary>Asset-database path of the folder holding this script (and the .po files).</summary>
        static string FolderPath()
        {
            var anchor = ScriptableObject.CreateInstance<LocalizationAnchor>();
            var script = MonoScript.FromScriptableObject(anchor);
            Object.DestroyImmediate(anchor);
            string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
            return string.IsNullOrEmpty(scriptPath) ? null : Path.GetDirectoryName(scriptPath);
        }

        public static Dictionary<string, string> Parse(string text)
        {
            var catalog = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return catalog;

            string id = null, str = null;
            bool inId = false, inStr = false, skipEntry = false;

            void Flush()
            {
                // The header entry has an empty msgid; an empty msgstr means "not translated".
                if (!skipEntry && !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(str))
                    catalog[id] = str;
                id = str = null;
                inId = inStr = skipEntry = false;
            }

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim('\r', ' ', '\t');
                if (line.Length == 0)
                {
                    Flush();
                    continue;
                }
                if (line[0] == '#') continue;   // comments and flags

                if (line.StartsWith("msgctxt"))
                {
                    // Contexts would need a composite key; nothing in this project uses them.
                    Flush();
                    skipEntry = true;
                    continue;
                }
                if (line.StartsWith("msgid_plural"))
                {
                    skipEntry = true;
                    continue;
                }
                if (line.StartsWith("msgid"))
                {
                    // A new msgid without a blank line before it still starts a new entry.
                    if (inStr) Flush();
                    id = Unquote(line);
                    inId = true;
                    inStr = false;
                    continue;
                }
                if (line.StartsWith("msgstr"))
                {
                    // "msgstr[0]" and friends belong to plural entries, which are skipped.
                    if (line.StartsWith("msgstr[")) skipEntry = true;
                    str = Unquote(line);
                    inId = false;
                    inStr = true;
                    continue;
                }
                if (line[0] == '"')
                {
                    // Continuation line of whichever field is open.
                    if (inId) id += Unquote(line);
                    else if (inStr) str += Unquote(line);
                }
            }
            Flush();
            return catalog;
        }

        /// <summary>Takes the text between the first and last quote and resolves .po escapes.</summary>
        static string Unquote(string value)
        {
            if (value == null) return string.Empty;
            int start = value.IndexOf('"');
            int end = value.LastIndexOf('"');
            if (start < 0 || end <= start) return string.Empty;

            var builder = new StringBuilder(end - start);
            for (int i = start + 1; i < end; i++)
            {
                char c = value[i];
                if (c != '\\' || i + 1 >= end)
                {
                    builder.Append(c);
                    continue;
                }
                char escaped = value[++i];
                switch (escaped)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    default: builder.Append(escaped); break;
                }
            }
            return builder.ToString();
        }

    }

    /// <summary>Editing a .po and saving it re-translates the open windows immediately.</summary>
    class PoReimportWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] imported, string[] deleted,
            string[] moved, string[] movedFrom)
        {
            if (Touches(imported) || Touches(deleted) || Touches(moved))
                L.ReloadCatalog();
        }

        static bool Touches(string[] paths)
        {
            foreach (var path in paths)
                if (path != null && path.EndsWith(".po")) return true;
            return false;
        }
    }
}
