// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Frame generation (5.1.1): NVIDIA Smooth Motion for My Summer Car, switched from the settings page.
//
// The game is limited by the processor, not the graphics card (TestBot bmsc-attrib2: 720p -> 360p changes nothing,
// living room 149 -> 144 FPS), so upscaling (DLSS) cannot add frames; DLSS frame generation needs DX12 or Vulkan and
// motion vectors (Unity 5.0: DX11, no motion vectors). Smooth Motion is frame generation inside the NVIDIA driver
// (GeForce RTX 40/50): it presents an extra frame between every two frames of a DX11/DX12/Vulkan game, from the two
// images alone. The driver reads it from the game's driver profile when the game starts (setting 0xB0D384C0; the
// profile "My Summer Car" ships with the driver and does not restrict the APIs). Writing that one setting needs no
// administrator rights; the NVIDIA app does the same. When the driver hooks the game, its module NvPresent64.dll is
// in the process - that is how MOPR tells "working" from "switched on, but the driver did not start it".
//
// Measured (fg-measure, RTX 5070 Ti, driver 617.14, 1280x720 window on a 143 Hz monitor, images counted with
// tools/FrameCounter): living room capped at 60 FPS 60 -> 120 images per second, uncapped 157 -> 274 (the game
// itself 157 -> 138 FPS); driving 45 km/h capped at 60 doubles as well. Not measured: input latency (about one frame
// of the game more, by design), artefacts.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

using MOP.Common;
using MOP.Localization;

namespace MOP.Next
{
    static class FrameGeneration
    {
        public const string Executable = "mysummercar.exe";
        /// <summary>Name of the profile MOPR creates only when the driver has none for the game (old drivers).</summary>
        const string OwnProfileName = "My Summer Car (MOPR)";

        public enum State { NotNvidia, Error, Off, Active, OnAfterRestart, OffAfterRestart, OnNotStarted }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        /// <summary>The driver's Smooth Motion module is in this process: it generates frames now.</summary>
        public static bool ActiveInProcess
        {
            get { try { return GetModuleHandleW("NvPresent64.dll") != IntPtr.Zero; } catch { return false; } }
        }

        static bool read, enabledInDriver, changedThisSession;
        static string error, profileName, driver;

        public static State Current
        {
            get
            {
                if (!read) Read();
                if (error != null) return NvidiaProfile.Load() != null ? State.NotNvidia : State.Error;
                bool active = ActiveInProcess;
                if (enabledInDriver) return active ? State.Active : changedThisSession ? State.OnAfterRestart : State.OnNotStarted;
                return active ? State.OffAfterRestart : State.Off;
            }
        }

        public static string Error => error;
        public static string Driver => driver;
        public static string ProfileName => profileName;

        /// <summary>Reads the driver profile (about 0.1 s: the whole driver database is loaded).</summary>
        public static void Read()
        {
            read = true;
            error = null;
            try
            {
                driver = NvidiaProfile.DriverVersion();
                using (NvidiaProfile p = NvidiaProfile.Open(Executable, out error))
                {
                    if (p == null) return;
                    profileName = p.ProfileName;
                    enabledInDriver = false;
                    if (!p.ProfileFound) return;
                    error = p.Get(NvidiaProfile.SmoothMotionEnable, out NvidiaProfile.Value v);
                    enabledInDriver = error == null && v.Found && v.Current != 0;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>Switches Smooth Motion in the game's driver profile. Null on success, else the error.</summary>
        public static string SetEnabled(bool on)
        {
            string err;
            try
            {
                using (NvidiaProfile p = NvidiaProfile.Open(Executable, out err))
                {
                    if (p == null) return err;
                    if (on)
                    {
                        if (!p.ProfileFound && (err = p.CreateProfile(OwnProfileName)) != null) return err;
                        err = p.Set(NvidiaProfile.SmoothMotionEnable, 1);
                    }
                    else if (p.ProfileFound)
                    {
                        // Back to what the driver ships (no value = off); a profile MOPR made goes away with it.
                        if (!p.ProfilePredefined && p.ProfileName == OwnProfileName) err = p.DeleteProfile();
                        else
                        {
                            err = p.Reset(NvidiaProfile.SmoothMotionEnable);
                            if (err == null && p.Get(NvidiaProfile.SmoothMotionEnable, out NvidiaProfile.Value v) == null && v.Found && v.Current != 0)
                                err = p.Set(NvidiaProfile.SmoothMotionEnable, 0);
                        }
                    }
                    if (err == null) err = p.Save();
                }
            }
            catch (Exception ex)
            {
                err = ex.GetType().Name + ": " + ex.Message;
            }
            if (err == null) changedThisSession = true;
            Read();
            return err;
        }

        /// <summary>Half the monitor's refresh rate: the FPS limit that lets every generated frame be shown.</summary>
        public static int HalfRefresh(out int refresh)
        {
            refresh = 60;
            try { refresh = Screen.currentResolution.refreshRate; } catch { }
            if (refresh <= 0) refresh = 60;
            // 143 / 119 / 59 are 143.97 / 119.88 / 59.94 Hz rounded down: half of the real rate is 72 / 60 / 30.
            return Mathf.Max(20, (refresh + 1) / 2);
        }

        /// <summary>One line for the settings page and the console.</summary>
        public static string StatusText()
        {
            string gpu = "?";
            try { gpu = SystemInfo.graphicsDeviceName; } catch { }
            switch (Current)
            {
                case State.Active: return MoprColors.Colorize(MoprColors.SuccessHex, Loc.T("menu.fg.active"));
                case State.OnAfterRestart: return MoprColors.Colorize(MoprColors.WarningHex, Loc.T("menu.fg.restart_on"));
                case State.OffAfterRestart: return MoprColors.Colorize(MoprColors.WarningHex, Loc.T("menu.fg.restart_off"));
                case State.OnNotStarted: return MoprColors.Colorize(MoprColors.WarningHex, Loc.F("menu.fg.not_started", gpu, driver ?? "?"));
                case State.Off: return MoprColors.Colorize(MoprColors.Hint, Loc.T("menu.fg.off"));
                case State.NotNvidia: return MoprColors.Colorize(MoprColors.Hint, Loc.F("menu.fg.not_nvidia", gpu));
                default: return MoprColors.Colorize(MoprColors.Danger, Loc.F("menu.fg.error", error ?? "?"));
            }
        }

        /// <summary>Console: mopr fg [on|off].</summary>
        public static void Command(string[] args)
        {
            string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            if (sub == "on" || sub == "off")
            {
                string err = SetEnabled(sub == "on");
                if (err != null) ModConsole.Warn("con.fg.failed", err);
                MopMenu.RefreshFrameGeneration();
            }
            else Read();
            ModConsole.Log(StatusText());
        }
    }
}
