// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Redundant write elimination. Found with the TestBot action profiler: some of the game's "Systems" FSMs
// re-apply settings EVERY FRAME, although the settings almost never change:
//
//   * CInputSetAxisInverted / CInputAxisInverted (4x per frame) -> cInput._AxisInverted -> _SaveAxInverted():
//       builds a string with ~2 concatenations per axis (several KB of garbage per call) and writes it to the
//       registry with PlayerPrefs.SetString("cInput_axInv", ...). 30-70 us per call, plus GC pressure.
//   * SetGameVolume.OnUpdate: AudioListener.volume = x, every frame (~30 us in the audio engine).
//
// Both are skipped ONLY if the write cannot change anything:
//   _SaveAxInverted: the inversion flags, the axis count, the PlayerPrefs switch and the stored string are all
//     exactly what the previous save produced (verified element by element), and the registry key was not
//     deleted since. Only cInput writes that key, so its content is the last saved string. Deletion paths in
//     the game: cInput.Clear() and the PlayMaker actions PlayerPrefsDeleteAll / PlayerPrefsDeleteKey - all three
//     are hooked and invalidate the snapshot. PlayerPrefs.HasKey costs a registry query (~20-40 us, measured),
//     so it is only used as a safety net for unknown callers (other mods) every 256th skip. cInput reads the key
//     only at startup, and the game re-applies the inversion every frame, so even that case heals itself.
//   SetGameVolume: AudioListener.volume already returns the value that would be written.

using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOP.Next.Engine
{
    static class RedundantWrites
    {
        const string HarmonyId = "com.anickon.mopr.engine.writes";
        const string AxisKey = "cInput_axInv";

        static HarmonyInstance harmony;
        public static bool Shadow { get; private set; }
        public static string Status { get; private set; } = "not installed";

        public static long AxisSaves, AxisSkipped, AxisShadowChecked, AxisShadowMismatch;
        public static long VolumeSets, VolumeSkipped;

        // Snapshot of the state written by the last real _SaveAxInverted().
        static bool snapValid;
        static bool[] snap = new bool[0];
        static int snapLength;
        static bool snapPrefs;
        static string snapText;
        static bool skipping, shadowPending;
        static string shadowExpected;

        public static string Install(bool shadow)
        {
            Remove();
            Shadow = shadow;
            harmony = HarmonyInstance.Create(HarmonyId);
            string axis = PatchCInput();
            string volume = PatchVolume();
            Status = "cInput axis save: " + (axis ?? "patched") + "; SetGameVolume: " + (volume ?? "patched");
            return axis != null && volume != null ? Status : null;
        }

        public static void Remove()
        {
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
            snapValid = false;
            skipping = shadowPending = false;
            Status = "not installed";
        }

        public static void ResetStats()
        {
            AxisSaves = AxisSkipped = AxisShadowChecked = AxisShadowMismatch = 0;
            VolumeSets = VolumeSkipped = 0;
        }

        static Type FindType(string assembly, string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == assembly)
                    return asm.GetType(name, false);
            return null;
        }

        static string PatchCInput()
        {
            try
            {
                Type t = FindType("cInput", "cInput");
                if (t == null) return "cInput not loaded";
                const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                MethodInfo save = t.GetMethod("_SaveAxInverted", S, null, Type.EmptyTypes, null);
                if (save == null || save.ReturnType != typeof(void)) return "_SaveAxInverted not found";
                if (!StaticField(t, "_invertAxis", typeof(bool[])) || !StaticField(t, "_axisLength", typeof(int)) ||
                    !StaticField(t, "_usePlayerPrefs", typeof(bool)) || !StaticField(t, "_exAxisInverted", typeof(string)))
                    return "cInput fields changed";

                // Every known way to delete the registry key must invalidate the snapshot - patch those first.
                MethodInfo clear = t.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (clear == null) return "cInput.Clear not found";
                harmony.Patch(clear, Hook("Invalidate"), null, null);
                foreach (string action in new[] { "PlayerPrefsDeleteAll", "PlayerPrefsDeleteKey" })
                {
                    Type a = FindType("Assembly-CSharp", "HutongGames.PlayMaker.Actions." + action);
                    MethodInfo enter = a?.GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                    if (enter == null) return action + ".OnEnter not found";
                    harmony.Patch(enter, Hook("Invalidate"), null, null);
                }

                harmony.Patch(save, Hook("SaveAxisPrefix"), Hook("SaveAxisPostfix"), null);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        static string PatchVolume()
        {
            try
            {
                Type t = FindType("Assembly-CSharp", "HutongGames.PlayMaker.Actions.SetGameVolume");
                if (t == null) return "SetGameVolume not found";
                MethodInfo update = t.GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                FieldInfo volume = t.GetField("volume", BindingFlags.Instance | BindingFlags.Public);
                if (update == null || volume == null || volume.FieldType != typeof(FsmFloat)) return "SetGameVolume changed";
                harmony.Patch(update, Hook("VolumePrefix"), null, null);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        static bool StaticField(Type t, string name, Type type)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null && f.FieldType == type;
        }

        static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(RedundantWrites).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

        // ----- cInput._SaveAxInverted -------------------------------------------------------------------
        // Harmony injects private static fields of cInput: "___" + "_invertAxis" etc.

        static int safetyCounter;

        static void Invalidate() => snapValid = false;

        static bool Unchanged(bool[] invert, int length, bool usePrefs, string text)
        {
            if (!snapValid || length != snapLength || usePrefs != snapPrefs || !ReferenceEquals(text, snapText)) return false;
            int n = length + 1;
            if (invert == null || n < 0 || n > invert.Length || n > snap.Length) return false;
            for (int i = 0; i < n; i++)
                if (invert[i] != snap[i]) return false;
            // Safety net for deletions outside the game code (see the header).
            if (usePrefs && (++safetyCounter & 255) == 0 && !PlayerPrefs.HasKey(AxisKey)) return false;
            return true;
        }

        static bool SaveAxisPrefix(bool[] ____invertAxis, int ____axisLength, bool ____usePlayerPrefs, string ____exAxisInverted)
        {
            skipping = shadowPending = false;
            AxisSaves++;
            if (!Unchanged(____invertAxis, ____axisLength, ____usePlayerPrefs, ____exAxisInverted)) return true;
            if (Shadow)
            {
                shadowPending = true;
                shadowExpected = snapText;
                return true;
            }
            skipping = true;
            AxisSkipped++;
            return false;
        }

        static void SaveAxisPostfix(bool[] ____invertAxis, int ____axisLength, bool ____usePlayerPrefs, string ____exAxisInverted)
        {
            // Harmony 1.2 runs postfixes even when the prefix skipped the original.
            if (skipping) { skipping = false; return; }
            if (shadowPending)
            {
                shadowPending = false;
                AxisShadowChecked++;
                if (!string.Equals(____exAxisInverted, shadowExpected, StringComparison.Ordinal)) AxisShadowMismatch++;
            }

            int n = ____axisLength + 1;
            if (____invertAxis == null || n < 0 || n > ____invertAxis.Length) { snapValid = false; return; }
            if (snap.Length < n) snap = new bool[Math.Max(n, 64)];
            Array.Copy(____invertAxis, snap, n);
            snapLength = ____axisLength;
            snapPrefs = ____usePlayerPrefs;
            snapText = ____exAxisInverted;
            snapValid = true;
        }

        // ----- SetGameVolume.OnUpdate ---------------------------------------------------------------------

        static bool VolumePrefix(FsmFloat ___volume)
        {
            VolumeSets++;
            if (Shadow || ___volume == null) return true;
            if (AudioListener.volume != ___volume.Value) return true;
            VolumeSkipped++;
            return false;
        }

        public static void AppendReport(System.Text.StringBuilder sb)
        {
            sb.AppendLine("Redundant writes: " + Status);
            sb.AppendLine($"  cInput axis saves: {AxisSaves}, skipped {AxisSkipped}" +
                          (Shadow ? $", shadow checked {AxisShadowChecked}, MISMATCHES {AxisShadowMismatch}" : ""));
            sb.AppendLine($"  game volume sets: {VolumeSets}, skipped {VolumeSkipped}");
        }
    }
}
