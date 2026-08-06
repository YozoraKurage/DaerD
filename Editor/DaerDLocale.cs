using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yozolab.DaerD
{
    enum DaerDLanguage
    {
        Auto = 0,
        English = 1,
        Japanese = 2,
    }

    /// <summary>
    /// String table for the editor UI. Keys are the English strings themselves, so call sites
    /// read naturally and an untranslated string just falls through to English. The
    /// translations live in <c>Editor/Localization/&lt;locale&gt;.po</c> (gettext format, the
    /// same one Poedit and the other VRChat editor extensions use), so translating means
    /// editing a .po — no C# involved. A new language additionally needs its code added to
    /// <see cref="DaerDLanguage"/> and to <see cref="LocaleCode"/>.
    /// </summary>
    static class L
    {
        const string PrefKey = "Yozolab.DaerD.Language";

        // Tr runs many times per IMGUI repaint; resolve the language once instead of hitting
        // EditorPrefs on every call. Invalidated by the setter, reset naturally on domain reload.
        static bool? s_isJapanese;

        /// <summary>Fired when the user changes the language preference; UI rebuilds itself.</summary>
        public static event Action LanguageChanged;

        public static DaerDLanguage Language
        {
            get => (DaerDLanguage)EditorPrefs.GetInt(PrefKey, (int)DaerDLanguage.Auto);
            set
            {
                if (value == Language) return;
                EditorPrefs.SetInt(PrefKey, (int)value);
                s_isJapanese = null;
                s_catalog = null;
                LanguageChanged?.Invoke();
            }
        }

        public static bool IsJapanese
        {
            get
            {
                if (!s_isJapanese.HasValue)
                {
                    var language = Language;
                    s_isJapanese = language == DaerDLanguage.Japanese ||
                        (language == DaerDLanguage.Auto && Application.systemLanguage == SystemLanguage.Japanese);
                }
                return s_isJapanese.Value;
            }
        }

        /// <summary>
        /// Looks <paramref name="english"/> up in the current locale's catalog. An untranslated
        /// (or unknown) string falls through to the English source text, so a missing entry
        /// degrades to "not translated yet" rather than to a blank label.
        /// </summary>
        public static string Tr(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            return Catalog.TryGetValue(english, out var translated) ? translated : english;
        }

        public static string Tr(string english, params object[] args) =>
            string.Format(Tr(english), args);

        // Parsed once per domain reload (and once more after a language switch or a .po edit).
        static Dictionary<string, string> s_catalog;

        static Dictionary<string, string> Catalog =>
            s_catalog ?? (s_catalog = PoCatalog.Load(LocaleCode));

        /// <summary>File name (without extension) of the .po to load for the current language.</summary>
        static string LocaleCode => IsJapanese ? "ja" : "en";

        /// <summary>Drops the parsed catalog so the next <see cref="Tr"/> re-reads the .po.
        /// Called when a .po is reimported and when the language preference changes.</summary>
        public static void ReloadCatalog()
        {
            s_catalog = null;
            LanguageChanged?.Invoke();
        }
    }
}
