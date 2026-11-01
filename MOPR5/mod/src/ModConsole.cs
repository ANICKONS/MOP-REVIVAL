// Modern Optimization Plugin
// Copyright(C) 2019-2022 Athlon

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.
//
// MOP - REVIVAL 5.1: the [MOPR] tag and the console colors (MoprColors). Warnings and errors go through
// MSCLoader's own Warning/Error, which already start the line with "MOPR Warning:" / "MOPR Error:", so the
// tag is left out there. The stored copy (bug reports) stays plain text and English (Info/Warn/Err/Block
// print the chosen language and store English; Debug only stores).

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using MOP.Common;
using MOP.Localization;

namespace MOP
{
    sealed class ModConsole : MSCLoader.ModConsole
    {
        // This class overrides default MSCLoader console messages,
        // in order to catch what MOP "says" and stores it into a list.

        static readonly List<string> messages = new List<string>();

        public new static void Log(string message)
        {
            Add(message);
            MSCLoader.ModConsole.Log(MoprColors.ConsoleLine(message));
        }

        public new static void LogError(string message)
        {
            Add($"[ERROR] {message}");
            MSCLoader.ModConsole.LogError(MoprColors.StripTag(message));
        }

        public new static void LogWarning(string message)
        {
            Add($"[WARNING] {message}");
            MSCLoader.ModConsole.LogWarning(MoprColors.StripTag(message));
        }

        public new static void Error(string message) => LogError(message);
        public new static void Warning(string message) => LogWarning(message);

        // ===== Localized messages (5.1) =====================================================================
        // The console shows the message in the chosen language; the stored copy (mod reports, bug reports) is
        // English. Keys are in Localization/Console.*.cs; arguments may be LocWords (translated per language).

        /// <summary>"[MOPR] text" in the console language.</summary>
        public static void Info(string key, params object[] args)
        {
            Add(MoprColors.TagText + " " + Loc.En(key, args));
            MSCLoader.ModConsole.Log(MoprColors.ConsoleLine(MoprColors.TagText + " " + Loc.F(key, args)));
        }

        /// <summary>"[MOPR] text" for a line the caller built in both languages itself.</summary>
        public static void InfoText(string localized, string english)
        {
            Add(MoprColors.TagText + " " + english);
            MSCLoader.ModConsole.Log(MoprColors.ConsoleLine(MoprColors.TagText + " " + localized));
        }

        /// <summary>Like Info, the text in green.</summary>
        public static void Success(string key, params object[] args)
        {
            Add(MoprColors.TagText + " " + Loc.En(key, args));
            MSCLoader.ModConsole.Log(MoprColors.StyledTag + " " + MoprColors.Success(Loc.F(key, args)));
        }

        public static void Warn(string key, params object[] args)
        {
            Add("[WARNING] " + Loc.En(key, args));
            MSCLoader.ModConsole.LogWarning(Loc.F(key, args));
        }

        public static void Err(string key, params object[] args)
        {
            Add("[ERROR] " + Loc.En(key, args));
            MSCLoader.ModConsole.LogError(Loc.F(key, args));
        }

        /// <summary>
        /// Internal progress and details: kept for mod reports and bug reports (and "mopr log"), not shown in
        /// the console. English, not translated.
        /// </summary>
        public static void Debug(string message)
        {
            Add("[DEBUG] " + MoprColors.StripTag(message));
        }

        /// <summary>A "[MOPR] header" line followed by plain lines, printed as one console entry.</summary>
        public sealed class Block
        {
            readonly System.Text.StringBuilder local = new System.Text.StringBuilder(), english = new System.Text.StringBuilder();

            public Block(string key, params object[] args)
            {
                local.Append(MoprColors.TagText).Append(' ').Append(Loc.F(key, args));
                english.Append(MoprColors.TagText).Append(' ').Append(Loc.En(key, args));
            }

            public Block Line(string key, params object[] args)
            {
                local.Append('\n').Append(Loc.F(key, args));
                english.Append('\n').Append(Loc.En(key, args));
                return this;
            }

            public void Print()
            {
                Add(english.ToString());
                MSCLoader.ModConsole.Log(MoprColors.ConsoleLine(local.ToString()));
            }
        }

        /// <summary>Console "mopr log [n]": the last stored lines (debug lines included).</summary>
        public static string Tail(int count)
        {
            int start = Math.Max(0, messages.Count - Math.Max(1, count));
            return string.Join("\n", messages.GetRange(start, messages.Count - start).ToArray());
        }

        public static void LogSilentError(string message)
        {
            Add($"[SILENT_ERROR] {message}");
            MSCLoader.ModConsole.Log(MoprColors.StyledTag + " " + MoprColors.Error(MoprColors.StripTag(message)));
        }

        static void Add(string message)
        {
            message = message.Replace(MoprColors.TagText + " ", "");
            message = message.Replace("<color=yellow>", "[WARNING] ");
            message = message.Replace("<color=green>", "[SYSTEM] ");
            message = message.Replace("<color=red>", "[ERROR] ");
            message = message.Trim();
            message = $"{DateTime.Now:HH:mm:ss.fff}: {message}";

            // Remove all things inside of <>.
            string[] messageArray = message.Split('\n');
            for (int i = 0; i < messageArray.Length; ++i)
            {
                if (i >= 1)
                {
                    messageArray[i] = new string(' ', 10) + messageArray[i];
                }
                messageArray[i] = Regex.Replace(messageArray[i], "<(.*?)>", "");
            }

            messages.Add(string.Join("\n", messageArray));
        }

        public static List<string> GetMessages()
        {
            return messages;
        }
    }
}
