// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.
//
// User interface localization (new in 5.1).
//   * Strings live in plain dictionaries (Strings.En.cs, Strings.Ru.cs). A missing key falls back to
//     English, then to the key itself, so a missing translation is visible but never crashes.
//   * Only UI text is localized: settings, message boxes, the loading screen. Console and log output
//     stays English, so bug reports are readable by everyone.
//   * No invisible marker characters inside the strings (MOPR 4.x used U+200B markers, which leaked
//     into saved names and comparisons).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MOP.Localization
{
    internal enum Language
    {
        English = 0,
        Russian = 1,
    }

    internal static class Loc
    {
        /// <summary>Names shown in the language dropdown, in <see cref="Language"/> order. Always native.</summary>
        internal static readonly string[] LanguageNames = { "English", "Русский" };

        static Language current = Language.English;

        internal static Language Current => current;

        /// <summary>Raised after the language changed.</summary>
        internal static event Action LanguageChanged;

        /// <summary>Localized string for <paramref name="key"/> (English, then the key itself as fallback).</summary>
        internal static string T(string key)
        {
            if (key == null) return string.Empty;

            string value;
            if (current == Language.Russian && StringsRu.Table.TryGetValue(key, out value))
                return value;
            if (StringsEn.Table.TryGetValue(key, out value))
                return value;
            return key;
        }

        /// <summary>string.Format over a localized string. A broken translation never throws.</summary>
        internal static string F(string key, params object[] args)
        {
            string format = T(key);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        /// <summary>Picks the English or the plural key ("{key}_one" / "{key}_many") and formats it with <paramref name="count"/>.</summary>
        internal static string Plural(string key, int count, params object[] extra)
        {
            object[] args = new object[1 + (extra != null ? extra.Length : 0)];
            args[0] = count;
            if (extra != null) Array.Copy(extra, 0, args, 1, extra.Length);
            return F(key + (count == 1 ? "_one" : "_many"), args);
        }

        /// <summary>Switches the language. Returns true if it changed.</summary>
        internal static bool SetLanguage(Language language)
        {
            if (!Enum.IsDefined(typeof(Language), language))
                language = Language.English;
            if (language == current)
                return false;

            current = language;
            Action handler = LanguageChanged;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { global::MOP.ModConsole.LogError("[MOPR] Language change handler failed: " + ex); }
            }
            return true;
        }

        /// <summary>Language for a first start: Russian for Russian, Ukrainian and Belarusian systems, else English.</summary>
        internal static Language DetectSystemLanguage()
        {
            try
            {
                switch (Application.systemLanguage)
                {
                    case SystemLanguage.Russian:
                    case SystemLanguage.Ukrainian:
                    case SystemLanguage.Belarusian:
                        return Language.Russian;
                }
            }
            catch
            {
                // Application.systemLanguage may throw on exotic setups; English is a safe default.
            }
            return Language.English;
        }

        internal static string ToCode(Language language)
        {
            return language == Language.Russian ? "ru" : "en";
        }

        /// <summary>Parses a stored language code. Unknown or empty codes return false.</summary>
        internal static bool TryParseCode(string code, out Language language)
        {
            switch ((code ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "en":
                    language = Language.English;
                    return true;
                case "ru":
                    language = Language.Russian;
                    return true;
                default:
                    language = Language.English;
                    return false;
            }
        }

        /// <summary>Keys present in English but missing in another language (console: mop loc).</summary>
        internal static List<string> MissingKeys(Language language)
        {
            List<string> missing = new List<string>();
            if (language == Language.English) return missing;
            foreach (string key in StringsEn.Table.Keys)
                if (!StringsRu.Table.ContainsKey(key))
                    missing.Add(key);
            return missing;
        }
    }
}
