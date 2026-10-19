// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Every color of the mod in one place (5.1): the console and the settings page. Same palette as MOPR 4.x.
// Change values only here. Text colors are "#RRGGBB" for Unity rich text (<color=...>).

using UnityEngine;

namespace MOP.Common
{
    internal static class MoprColors
    {
        // ===== Console =====
        public const string TagText = "[MOPR]";
        public const string ConsoleTag = "#FF7A1A";      // [MOPR], bold orange
        public const string ConsoleBody = "#F0C562";     // normal MOPR line, soft beige
        public const string VersionHex = "#35E0E0";      // version numbers, cyan
        public const string SuccessHex = "#3FB950";      // done / on
        public const string WarningHex = "#F5C518";      // attention
        public const string ErrorHex = "#FF5151";        // failure
        public const string AccentHex = "#35E0E0";       // names and numbers, cyan
        public const string MutedHex = "#9AA6B2";        // off / unimportant, grey

        // ===== Settings page =====
        public const string Brand = "#FF7A1A";           // "MOP - REVIVAL"
        public const string Author = "#F5A623";
        public const string Subheader = "#35E0E0";       // "▍ GROUP" inside a section
        public const string SubheaderOther = "#F5C518";  // "▍ OTHER"
        public const string Danger = "#FF5151";          // Safe mode, emergency things
        public const string Hint = "#9AA6B2";            // the explanation under a setting
        public const string Note = "#F5C518";            // a notice the player should read

        public static readonly Color HeaderTitle = Rgb(0xF5, 0xC5, 0x18);
        public static readonly Color HeaderInfoBackground = Rgb(0x00, 0x00, 0x00);
        public static readonly Color HeaderLanguageBackground = Rgb(0x2E, 0x7D, 0x32);
        public static readonly Color HeaderExperimentalBackground = Rgb(0x8A, 0x45, 0x00);

        /// <summary>Accent bar in front of a subheader.</summary>
        public const string SubheaderMarker = "▍";

        static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        // ===== Helpers =====
        public static string Colorize(string hex, string text) => "<color=" + hex + ">" + text + "</color>";
        public static string Bold(string text) => "<b>" + text + "</b>";

        public static string Version(string s) => Bold(Colorize(VersionHex, s));
        public static string Success(string s) => Colorize(SuccessHex, s);
        public static string Warning(string s) => Colorize(WarningHex, s);
        public static string Error(string s) => Colorize(ErrorHex, s);
        public static string Accent(string s) => Colorize(AccentHex, s);
        public static string Muted(string s) => Colorize(MutedHex, s);

        /// <summary>"On" in green or "Off" in grey.</summary>
        public static string OnOff(bool on) => on ? Success("On") : Muted("Off");

        /// <summary>Subheader text: bold, colored, "▍ TEXT".</summary>
        public static string SubheaderText(string hex, string text) => Bold(Colorize(hex, SubheaderMarker + " " + text.ToUpper()));

        /// <summary>The styled [MOPR] tag.</summary>
        public static string StyledTag => Bold(Colorize(ConsoleTag, TagText));

        /// <summary>
        /// Console form of a line: when it starts with [MOPR], the tag is orange and the rest beige (colors inside
        /// the rest still win on their parts). Lines that format themselves before the tag keep their format.
        /// </summary>
        public static string ConsoleLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            int idx = raw.IndexOf(TagText);
            if (idx < 0) return raw;

            string before = raw.Substring(0, idx);
            string after = raw.Substring(idx + TagText.Length);
            if (before.Trim().Length == 0)
                return before + StyledTag + Colorize(ConsoleBody, after);
            return before + StyledTag + after;
        }

        /// <summary>The line without a leading [MOPR] tag (MSCLoader prefixes warnings and errors with the mod name).</summary>
        public static string StripTag(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string t = raw.TrimStart();
            return t.StartsWith(TagText) ? t.Substring(TagText.Length).TrimStart() : raw;
        }
    }
}
