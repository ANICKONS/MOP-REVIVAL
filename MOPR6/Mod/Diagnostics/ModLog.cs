// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The mod's log (Mods\Config\Mod Settings\MOPR6\mopr6.log, new every session) and its error sink
// (requirement 12.1): every exception is written once per place and type with the object and the stack, and shown
// in the MSCLoader console; repeats are counted for the report.

using System;
using System.IO;
using System.Text;
using MOPR6.Core.Errors;
using MSCLoader;
using UnityEngine;

namespace MOPR6.Diagnostics
{
    static class ModLog
    {
        sealed class Output : IErrorOutput
        {
            public void Write(ErrorRecord first)
            {
                Line("ERROR " + first);
                if (!string.IsNullOrEmpty(first.Stack)) Line(first.Stack);
                ModConsole.Error("MOPR6: " + first.Place + ": " + first.Type + ": " + first.Message +
                                 (string.IsNullOrEmpty(first.Context) ? "" : " [" + first.Context + "]"));
            }
        }

        static string path;
        static StreamWriter writer;
        public static readonly ErrorSink Errors = new ErrorSink(new Output());

        /// <summary>Opens a new log file in the mod's settings folder (called once in ModSetup).</summary>
        public static void Open(string folder)
        {
            // MSCLoader sets the mods up again in the main menu: the session's log stays open (opening it again
            // fails with a sharing violation and would stop the log).
            if (writer != null) return;
            try
            {
                Directory.CreateDirectory(folder);
                path = Path.Combine(folder, "mopr.log");
                writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
                Line("MOPR6 " + BuildInfo.Version + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception e) // catch-ok: no log file is not fatal; the console still shows it
            {
                writer = null;
                ModConsole.Warning("MOPR6: cannot write the log file " + path + ": " + e.Message);
            }
        }

        public static string FilePath => path;

        public static void Line(string text)
        {
            string line = "[" + Now().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "] " + text;
            if (writer != null)
            {
                try { writer.WriteLine(line); }
                catch (IOException) { writer = null; }   // catch-ok: disk full / file gone: logging stops, the game goes on
            }
        }

        /// <summary>A line in the log and in the console.</summary>
        public static void Info(string text)
        {
            Line(text);
            ModConsole.Print("MOPR6: " + text);
        }

        public static void Warn(string text)
        {
            Line("WARNING " + text);
            ModConsole.Warning("MOPR6: " + text);
        }

        /// <summary>Records an exception (once per place and type, then counted).</summary>
        public static void Report(string place, Exception ex, string context = null) => Errors.Report(place, ex, context, Now());

        /// <summary>
        /// The mod's clock: unscaled game time (seconds). Not affected by Time.timeScale (sleeping, pause), and
        /// frame-exact in the harness's deterministic runs (Time.captureFramerate), so ticks, the save barrier's
        /// timeouts and the failure window happen on the same frame in every run.
        /// </summary>
        public static double Now()
        {
            try { return Time.unscaledTime; }
            catch (Exception) { return 0; }   // catch-ok: outside the main thread Unity throws; the time is only a label
        }
    }
}
