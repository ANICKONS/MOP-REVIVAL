// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The bug report carries no personal data (requirement 12.3, design §12): the user's profile folder becomes
// %USERPROFILE% and the user name, where it is a whole path segment elsewhere (D:\Games\<name>\...), %USERNAME%.
// Shared code: compiled for net8.0 (tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MOPR6.Core.Diagnostics
{
    public static class Redact
    {
        public const string ProfileMark = "%USERPROFILE%";
        public const string NameMark = "%USERNAME%";

        /// <summary>
        /// <paramref name="text"/> without the profile folder and the user name as a path segment. Case-insensitive,
        /// both slash directions. A user name shorter than 2 characters is left alone (it would hit ordinary words).
        /// </summary>
        public static string Paths(string text, string userProfile, string userName)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            string profile = (userProfile ?? "").TrimEnd('\\', '/');
            if (profile.Length >= 3)
            {
                text = ReplaceIgnoreCase(text, profile, ProfileMark);
                text = ReplaceIgnoreCase(text, profile.Replace('\\', '/'), ProfileMark);
            }
            string name = (userName ?? "").Trim();
            if (name.Length >= 2)
            {
                // A whole segment: after a slash, before a slash, a quote, white space or the end.
                string pattern = @"(?<=[\\/])" + Regex.Escape(name) + @"(?=[\\/""'\s]|$)";
                text = Regex.Replace(text, pattern, NameMark, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            return text;
        }

        static string ReplaceIgnoreCase(string text, string find, string replacement)
        {
            int i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return text;
            StringBuilder sb = new StringBuilder(text.Length);
            int start = 0;
            while (i >= 0)
            {
                sb.Append(text, start, i - start).Append(replacement);
                start = i + find.Length;
                i = text.IndexOf(find, start, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(text, start, text.Length - start);
            return sb.ToString();
        }
    }
}
