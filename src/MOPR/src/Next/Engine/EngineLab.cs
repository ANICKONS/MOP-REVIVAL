// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MOPR Engine Lab: engine-level optimizations of PlayMaker itself, independent from MOP's object toggling.
//
// Rules for everything in this namespace:
//   * an optimization may only skip work that is PROVEN to be a no-op for the current live state
//     (no heuristics, no "close enough"); otherwise the original game code runs unchanged;
//   * every optimization has a SHADOW mode that runs the original code anyway and verifies the claim;
//   * everything can be switched off at runtime and is removed completely (Harmony unpatch).
// The experimental tier (SharedMouseRay, MirrorRate; 5.1) is the exception to the first rule: measured, verified
// where possible, but not provably exact - so it is off by default and has its own switches.
//
// Public static API (also used by the TestBot through reflection): SetMode, GetMode, ResetStats, Report.

using System;
using System.Text;
using MSCLoader;

using MOP.Common;
using MOP.Next;

namespace MOP.Next.Engine
{
    public static class EngineLab
    {
        public const string ModeOff = "off";
        public const string ModeOn = "on";
        public const string ModeShadow = "shadow";

        static string mode = ModeOff;
        static string lastError;

        public static string GetMode() => mode;
        public static string LastError => lastError;

        /// <summary>Switches the PlayMaker fast path. Returns null on success, else the error message.</summary>
        public static string SetMode(string newMode)
        {
            newMode = (newMode ?? ModeOff).Trim().ToLowerInvariant();
            if (newMode != ModeOn && newMode != ModeShadow) newMode = ModeOff;

            string error = null;
            if (newMode == ModeOff)
            {
                FsmFastPath.Remove();
                RedundantWrites.Remove();
                FsmPropertyFastPath.Remove();
            }
            else
            {
                bool shadow = newMode == ModeShadow;
                error = FsmFastPath.Install(shadow);
                if (error != null)
                {
                    newMode = ModeOff;
                    RedundantWrites.Remove();
                    FsmPropertyFastPath.Remove();
                    ModConsole.LogError("[MOPR] Engine: PlayMaker fast path disabled: " + error);
                }
                else
                {
                    // Independent patch sets: a failure here only disables that part (reported, not fatal).
                    string writes = RedundantWrites.Install(shadow);
                    if (writes != null) ModConsole.LogWarning("[MOPR] Engine: redundant write elimination unavailable: " + writes);
                    string property = FsmPropertyFastPath.Install(shadow);
                    if (property != null) ModConsole.LogWarning("[MOPR] Engine: FsmProperty fast path unavailable: " + property);
                }
            }

            if (newMode != mode)
                ModConsole.Log("[MOPR] Engine: PlayMaker fast path " + ModeWord(newMode));
            mode = newMode;
            lastError = error;
            ResetStats();
            return error;
        }

        /// <summary>Applies the mod settings (called when settings load or change).</summary>
        public static void ApplySettings()
        {
            bool verify = false;
            try
            {
                bool on = MopMenu.EngineFastPath != null && MopMenu.EngineFastPath.GetValue();
                verify = MopMenu.EngineVerify != null && MopMenu.EngineVerify.GetValue();
                string wanted = !on ? ModeOff : verify ? ModeShadow : ModeOn;
                if (wanted != mode) SetMode(wanted);
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "ENGINE_SETTINGS");
            }
            ApplyExperimental(verify);
        }

        /// <summary>Experimental tier (off by default, not provably exact): its own switches, same verify mode.</summary>
        static void ApplyExperimental(bool verify)
        {
            try
            {
                bool ray = MopMenu.ExperimentalMouseRay != null && MopMenu.ExperimentalMouseRay.GetValue();
                if (!ray)
                {
                    if (SharedMouseRay.Installed)
                    {
                        SharedMouseRay.Remove();
                        ModConsole.Log("[MOPR] Engine: shared mouse ray " + ModeWord(ModeOff));
                    }
                }
                else if (!SharedMouseRay.Installed || SharedMouseRay.Shadow != verify)
                {
                    string err = SharedMouseRay.Install(verify);
                    if (err == null)
                        ModConsole.Log("[MOPR] Engine: shared mouse ray " + ModeWord(verify ? ModeShadow : ModeOn) + Experimental);
                    else
                        ModConsole.LogWarning("[MOPR] Engine: shared mouse ray unavailable: " + err);
                    SharedMouseRay.ResetStats();
                }

                bool engineBay = MopMenu.ExperimentalEngineBay != null && MopMenu.ExperimentalEngineBay.GetValue();
                if (engineBay != EngineBayCulling.Installed)
                {
                    if (engineBay) EngineBayCulling.Install(); else EngineBayCulling.Remove();
                    EngineBayCulling.ResetStats();
                    ModConsole.Log("[MOPR] Render: engine bay hidden from the driver's seat " + ModeWord(engineBay ? ModeOn : ModeOff) + (engineBay ? Experimental : ""));
                }

                bool mirrors = MopMenu.ExperimentalMirrorRate != null && MopMenu.ExperimentalMirrorRate.GetValue();
                if (mirrors != MirrorRate.Installed)
                {
                    if (mirrors) MirrorRate.Install(); else MirrorRate.Remove();
                    MirrorRate.ResetStats();
                    ModConsole.Log("[MOPR] Render: mirrors at most 60 FPS " + ModeWord(mirrors ? ModeOn : ModeOff) + (mirrors ? Experimental : ""));
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "ENGINE_EXPERIMENTAL_SETTINGS");
            }
        }

        /// <summary>Console word for a mode: ON green, VERIFY yellow, OFF grey.</summary>
        static string ModeWord(string m)
        {
            switch (m)
            {
                case ModeOn: return MoprColors.Success("ON");
                case ModeShadow: return MoprColors.Warning("VERIFY");
                default: return MoprColors.Muted("OFF");
            }
        }

        static string Experimental => " " + MoprColors.Muted("(experimental)");

        /// <summary>Machine-readable counters of the experimental shared mouse ray (TestBot probe).</summary>
        public static string MouseRayCounters() =>
            $"installed={SharedMouseRay.Installed};queries={SharedMouseRay.Queries};raycasts={SharedMouseRay.Raycasts};" +
            $"flushes={SharedMouseRay.CameraFlushes};stale={SharedMouseRay.StaleHits};checked={SharedMouseRay.ShadowChecked};" +
            $"mismatch={SharedMouseRay.ShadowMismatch}";

        public static void ResetStats()
        {
            FsmFastPath.ResetStats();
            RedundantWrites.ResetStats();
            FsmPropertyFastPath.ResetStats();
            SharedMouseRay.ResetStats();
        }

        /// <summary>Called when a scene was loaded: the caches must not keep objects of the previous scene alive.</summary>
        public static void OnSceneChanged()
        {
            FsmFastPath.ClearCaches();
            FsmPropertyFastPath.ClearCaches();
            SharedMouseRay.ClearCaches();
            MirrorRate.ClearCaches();
            EngineBayCulling.ClearCaches();
        }

        public static void SetDetailedStats(bool on)
        {
            FsmFastPath.DetailedStats = on;
            ResetStats();
        }

        public static long ShadowMismatches =>
            FsmFastPath.UpdateStats.ShadowMismatch + FsmFastPath.LateStats.ShadowMismatch + RedundantWrites.AxisShadowMismatch +
            FsmPropertyFastPath.ShadowMismatch;

        /// <summary>Machine-readable counters: "key=value;key=value".</summary>
        public static string Counters()
        {
            FsmFastPath.Counters u = FsmFastPath.UpdateStats, l = FsmFastPath.LateStats;
            StringBuilder sb = new StringBuilder();
            sb.Append("mode=").Append(mode);
            sb.Append(";update.calls=").Append(u.Calls).Append(";update.skipIdle=").Append(u.SkipIdle).Append(";update.skipTimer=").Append(u.SkipTimer);
            sb.Append(";late.calls=").Append(l.Calls).Append(";late.skipIdle=").Append(l.SkipIdle);
            sb.Append(";shadow.checked=").Append(u.ShadowChecked + l.ShadowChecked + RedundantWrites.AxisShadowChecked).Append(";shadow.mismatch=").Append(ShadowMismatches);
            sb.Append(";axis.saves=").Append(RedundantWrites.AxisSaves).Append(";axis.skipped=").Append(RedundantWrites.AxisSkipped);
            sb.Append(";volume.sets=").Append(RedundantWrites.VolumeSets).Append(";volume.skipped=").Append(RedundantWrites.VolumeSkipped);
            sb.Append(";property.get=").Append(FsmPropertyFastPath.GetCalls).Append(";property.getFast=").Append(FsmPropertyFastPath.GetFast);
            sb.Append(";property.set=").Append(FsmPropertyFastPath.SetCalls).Append(";property.setFast=").Append(FsmPropertyFastPath.SetFast);
            for (int i = 0; i < FsmFastPath.ReasonCount; i++)
            {
                if (u.Reasons[i] > 0) sb.Append(";update.run.").Append(FsmFastPath.ReasonNames[i]).Append('=').Append(u.Reasons[i]);
                if (l.Reasons[i] > 0) sb.Append(";late.run.").Append(FsmFastPath.ReasonNames[i]).Append('=').Append(l.Reasons[i]);
            }
            return sb.ToString();
        }

        public static string Report()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[MOPR] Engine Lab - PlayMaker fast path: " + ModeWord(mode) + (lastError != null ? " (" + lastError + ")" : ""));
            if (mode != ModeOff)
            {
                FsmFastPath.AppendReport(sb);
                RedundantWrites.AppendReport(sb);
                FsmPropertyFastPath.AppendReport(sb);
            }
            SharedMouseRay.AppendReport(sb);
            sb.AppendLine(MirrorRate.Report());
            sb.AppendLine(EngineBayCulling.Report());
            return sb.ToString().TrimEnd();
        }

        /// <summary>Console: mop engine [on|off|shadow|stats|reset|detail].</summary>
        public static void Command(string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "stats";
            switch (sub)
            {
                case ModeOn:
                case ModeOff:
                case ModeShadow:
                    SetMode(sub);
                    break;
                case "reset":
                    ResetStats();
                    break;
                case "detail":
                    SetDetailedStats(!FsmFastPath.DetailedStats);
                    ModConsole.Log("[MOPR] Engine: detailed statistics " + MoprColors.OnOff(FsmFastPath.DetailedStats));
                    break;
            }
            ModConsole.Log(Report());
        }
    }
}
