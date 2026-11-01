// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Localization (requirement 13.1; infrastructure copied from MOPR 5.x Localization/Loc.cs, the keys are new).
//   * One dictionary per language for the interface (Strings.En/Ru/Pl.cs: settings page, message boxes) and one for
//     the console (Console.En/Ru/Pl.cs). A missing key falls back to English, then to the key itself: a missing
//     translation is visible but never crashes.
//   * The console follows the chosen language; the bug report and the TestBot's copy of a console answer are always
//     English (requirement 13.1: reports in English).
//   * Technical details inside a message (object names, paths, system counters, exception texts) are not translated.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MOPR6.Localization
{
    internal enum Language
    {
        English = 0,
        Russian = 1,
        Polish = 2,
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
            if (i > 0 && i < tables.Length && tables[i].TryGetValue(key, out value)) return value;
            if (tables[0].TryGetValue(key, out value)) return value;
            return key;
        }

        /// <summary>string.Format over a localized string. A broken translation never throws.</summary>
        internal static string F(string key, params object[] args) => FIn(current, key, args);

        /// <summary>The English text of a message (reports, the TestBot).</summary>
        internal static string En(string key, params object[] args) => FIn(Language.English, key, args);

        internal static string FIn(Language language, string key, params object[] args)
        {
            string format = TIn(language, key);
            if (args == null || args.Length == 0) return format;
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException) // catch-ok: a broken translation shows its raw text instead of crashing
            {
                return format;
            }
        }

        /// <summary>Switches the language. Returns true if it changed.</summary>
        internal static bool SetLanguage(Language language)
        {
            if (!Enum.IsDefined(typeof(Language), language)) language = Language.English;
            if (language == current) return false;
            current = language;
            Action handler = LanguageChanged;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Diagnostics.ModLog.Report("language/Changed", ex); }
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
            catch (Exception) // catch-ok: Application.systemLanguage may throw on exotic setups; English is a safe default
            {
            }
            return Language.English;
        }

        internal static Language FromIndex(int index) =>
            Enum.IsDefined(typeof(Language), index) ? (Language)index : Language.English;

        /// <summary>Keys present in English but missing in another language (console "mopr lang check").</summary>
        internal static List<string> MissingKeys(Language language)
        {
            List<string> missing = new List<string>();
            int i = (int)language;
            if (i <= 0 || i >= tables.Length) return missing;
            foreach (string key in tables[0].Keys)
                if (!tables[i].ContainsKey(key)) missing.Add(key);
            return missing;
        }
    }
}
