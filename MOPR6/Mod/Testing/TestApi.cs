// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The stable API the TestBot reaches by reflection (type "MOPR6.Testing.TestApi" in the assembly MOPR), so the bot
// does not reference the mod and keeps working in vanilla runs. Every method returns plain strings / numbers.

using System;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Core.Ledger;
using MOPR6.Host;
using MOPR6.Ledger;
using MOPR6.Diagnostics;

namespace MOPR6.Testing
{
    public static class TestApi
    {
        public static string Version() => BuildInfo.Version;

        public static string Systems() => SystemHost.Status();

        /// <summary>id, mode = off | on | verify. Null on success, else an error.</summary>
        public static string SetMode(string id, string mode)
        {
            if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(id, "render", StringComparison.OrdinalIgnoreCase))
            {
                // Every exact system (A/B of the whole engine section) or every render system.
                Tier tier = string.Equals(id, "all", StringComparison.OrdinalIgnoreCase) ? Tier.Exact : Tier.Render;
                string first = null;
                foreach (Optimization x in SystemHost.Systems)
                    if (x.Tier == tier) first = first ?? SetMode(x.Id, tier == Tier.Render && mode == "verify" ? "on" : mode);
                return first;
            }
            Optimization s = SystemHost.Find(id);
            if (s == null) return "no system " + id;
            switch ((mode ?? "").ToLowerInvariant())
            {
                case "off": SystemHost.Remove(s); return null;
                case "on":
                    return SystemHost.SetMode(s, SystemMode.On);
                case "verify":
                    return SystemHost.SetMode(s, SystemMode.Verify);
                default: return "unknown mode " + mode;
            }
        }

        /// <summary>A/B of a system that switches its effect without being removed (render.merge): on | off.</summary>
        public static string SetAb(string id, string state)
        {
            Optimization s = SystemHost.Find(id);
            if (s == null) return "no system " + id;
            Render.IAbSwitch ab = s as Render.IAbSwitch;
            if (ab == null) return id + " has no A/B switch";
            return ab.SetAb(string.Equals(state, "on", StringComparison.OrdinalIgnoreCase));
        }

        public static string GetMode(string id)
        {
            Optimization s = SystemHost.Find(id);
            return s == null ? "none" : s.State.ToString();
        }

        /// <summary>Everything back (requirement 5.3); returns "restored N, not restored M".</summary>
        public static string RestoreAll()
        {
            int n = Arbiters.Ledger != null ? Arbiters.Ledger.Count : 0;
            List<RestoreRecord> odd = Arbiters.RestoreAll("TestApi");
            return "restored " + (n - odd.Count) + ", not restored " + odd.Count;
        }

        public static string LedgerDump() => Arbiters.Dump();

        /// <summary>Resumes systems suspended by a save barrier (the harness tests the barrier by hand).</summary>
        public static string ResumeAll()
        {
            SystemHost.ResumeAll();
            return "resumed";
        }

        /// <summary>Verify mode: mismatches of every exact system so far (7.2: must stay 0).</summary>
        public static long Mismatches() => SystemHost.Mismatches();

        /// <summary>Everything for a test log: systems with their counters, inventory, ledger, barrier, errors.</summary>
        public static string Report() =>
            "MOPR6 " + BuildInfo.Version + "\n" + SystemHost.Status() + Catalog.Vanilla.Status() + "\nledger: " + LedgerCount() +
            " held (" + Arbiters.Summary() + "); barrier: " + Barrier() + "; errors: " + ErrorCount() + "; verify mismatches: " + Mismatches();

        public static int LedgerCount() => Arbiters.Ledger != null ? Arbiters.Ledger.Count : 0;

        public static string Barrier() =>
            SaveHooks.Barrier.State + ", raised " + SaveHooks.Barrier.Raises + ", cancels " + SaveHooks.Barrier.Cancels +
            ", last " + (SaveHooks.LastSource ?? "-") + ", not restored " + SaveHooks.LastNotRestored;

        public static string Errors()
        {
            StringBuilder sb = new StringBuilder();
            foreach (MOPR6.Core.Errors.ErrorRecord r in ModLog.Errors.Records) sb.Append(r).Append('\n');
            return sb.ToString();
        }

        public static int ErrorCount() => ModLog.Errors.Total;

        /// <summary>A console command line ("probe hide YARD/Building"), as typed after "mopr"; the answer in English.</summary>
        public static string Command(string line)
        {
            string[] args = (line ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return MoprCommand.Execute(args, Localization.Language.English);
        }

        /// <summary>The bug report text (English, redacted), without writing a file.</summary>
        public static string BugReportText() => BugReport.Build();
    }
}
