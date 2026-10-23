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
// Localization (new in 5.1).
//   * Strings live in plain dictionaries, per language one file for the interface (Strings.En/Ru/Pl.cs:
//     settings, message boxes, the loading screen) and one for the console (Console.En/Ru/Pl.cs). A missing
//     key falls back to English, then to the key itself, so a missing translation is visible but never crashes.
//   * The console follows the chosen language. The copy MOPR keeps for mod reports and bug reports (see
//     ModConsole) is always English, so a report is readable by everyone.
//   * Technical details inside a message (object names, paths, exception texts, rule parser messages) are not
//     translated.
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
        Polish = 2,
    }

    /// <summary>
    /// A word that is translated when the message is formatted, in the message's own language (ON/OFF, a mode
    /// name...). Lets one call produce the console text and the English report copy.
    /// </summary>
    internal sealed class LocWord
    {
        public readonly string Key;
        public readonly string Hex;      // color, or null
        public LocWord(string key, string hex) { Key = key; Hex = hex; }
    }

    internal static class Loc
    {
        /// <summary>Names shown in the language dropdown, in <see cref="Language"/> order. Always native.</summary>
        internal static readonly string[] LanguageNames = { "English", "Русский", "Polski" };

        static readonly Dictionary<string, string>[] tables =
        {
            Merge(StringsEn.Table, ConsoleEn.Table),
            Merge(StringsRu.Table, ConsoleRu.Table),
            Merge(StringsPl.Table, ConsolePl.Table),
        };

        static Dictionary<string, string> Merge(Dictionary<string, string> ui, Dictionary<string, string> console)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(ui);
            foreach (KeyValuePair<string, string> kv in console) d[kv.Key] = kv.Value;
            return d;
        }

        static Language current = Language.English;

        internal static Language Current => current;

        /// <summary>Raised after the language changed.</summary>
        internal static event Action LanguageChanged;

        /// <summary>Localized string for <paramref name="key"/> (English, then the key itself as fallback).</summary>
        internal static string T(string key) => TIn(current, key);

        /// <summary>The string in a given language (English, then the key itself as fallback).</summary>
        internal static string TIn(Language language, string key)
        {
            if (key == null) return string.Empty;
            string value;
            int i = (int)language;
            if (i > 0 && i < tables.Length && tables[i].TryGetValue(key, out value))
                return value;
            if (tables[0].TryGetValue(key, out value))
                return value;
            return key;
        }

        /// <summary>string.Format over a localized string. A broken translation never throws.</summary>
        internal static string F(string key, params object[] args) => FIn(current, key, args);

        /// <summary>The English text of a message (the copy MOPR keeps for reports).</summary>
        internal static string En(string key, params object[] args) => FIn(Language.English, key, args);

        internal static string FIn(Language language, string key, params object[] args)
        {
            string format = TIn(language, key);
            if (args == null || args.Length == 0) return format;
            object[] resolved = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                LocWord w = args[i] as LocWord;
                if (w == null) { resolved[i] = args[i]; continue; }
                string text = TIn(language, w.Key);
                resolved[i] = w.Hex != null ? "<color=" + w.Hex + ">" + text + "</color>" : text;
            }
            try
            {
                return string.Format(format, resolved);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        /// <summary>A word translated at format time (see <see cref="LocWord"/>).</summary>
        internal static LocWord Word(string key, string hex = null) => new LocWord(key, hex);

        /// <summary>ON in green or OFF in grey, translated at format time.</summary>
        internal static LocWord OnOff(bool on) =>
            on ? new LocWord("con.on", Common.MoprColors.SuccessHex) : new LocWord("con.off", Common.MoprColors.MutedHex);

        /// <summary>Picks the singular or the plural key ("{key}_one" / "{key}_many") and formats it with <paramref name="count"/>.</summary>
        internal static string Plural(string key, int count, params object[] extra) => PluralIn(current, key, count, extra);

        /// <summary><see cref="Plural"/> in a given language (the English copy of a console line, for reports).</summary>
        internal static string PluralIn(Language language, string key, int count, params object[] extra)
        {
            object[] args = new object[1 + (extra != null ? extra.Length : 0)];
            args[0] = count;
            if (extra != null) Array.Copy(extra, 0, args, 1, extra.Length);
            string suffix = PluralSuffix(language, count);
            if (suffix == "_few" && !tables[(int)language].ContainsKey(key + suffix)) suffix = "_many";
            return FIn(language, key + suffix, args);
        }

        /// <summary>"_one", "_few" or "_many". Russian and Polish have a "few" form (2-4, not 12-14).</summary>
        static string PluralSuffix(Language language, int count)
        {
            int n = Math.Abs(count);
            if (n == 1) return "_one";
            if (language == Language.English) return "_many";
            int mod10 = n % 10, mod100 = n % 100;
            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return "_few";
            return "_many";
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

        /// <summary>Language for a first start: Russian for Russian, Ukrainian and Belarusian systems, Polish for Polish, else English.</summary>
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
                    case SystemLanguage.Polish:
                        return Language.Polish;
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
            switch (language)
            {
                case Language.Russian: return "ru";
                case Language.Polish: return "pl";
                default: return "en";
            }
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
                case "pl":
                    language = Language.Polish;
                    return true;
                default:
                    language = Language.English;
                    return false;
            }
        }

        /// <summary>Keys present in English but missing in another language.</summary>
        internal static List<string> MissingKeys(Language language)
        {
            List<string> missing = new List<string>();
            int i = (int)language;
            if (i <= 0 || i >= tables.Length) return missing;
            foreach (string key in tables[0].Keys)
            {
                // "_few" plural forms exist only where a language needs them.
                if (key.EndsWith("_few")) continue;
                if (!tables[i].ContainsKey(key))
                    missing.Add(key);
            }
            return missing;
        }
    }
}
