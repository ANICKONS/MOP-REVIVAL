// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Redundant writes (exact optimization, design §6; ported from MOPR 5.x Next/Engine/RedundantWrites.cs). Some of the
// game's "Systems" FSMs re-apply settings every frame although they almost never change:
//   * CInputSetAxisInverted / CInputAxisInverted (4x per frame) -> cInput._AxisInverted -> _SaveAxInverted(): a
//     string of ~2 concatenations per axis (several KB of garbage per call) written to the registry with
//     PlayerPrefs.SetString("cInput_axInv", ...): 30-70 us per call plus GC pressure;
//   * SetGameVolume.OnUpdate: AudioListener.volume = x every frame (~30 us in the audio engine).
// Both are skipped only when the write cannot change anything:
//   _SaveAxInverted: the inversion flags, the axis count, the PlayerPrefs switch and the stored string are exactly
//     what the previous real save produced (element by element), and the registry key was not deleted since. Only
//     cInput writes that key, so its content is the last saved string. The deletion paths of the game - cInput.Clear()
//     and the PlayMaker actions PlayerPrefsDeleteAll / PlayerPrefsDeleteKey - are hooked and invalidate the snapshot;
//     PlayerPrefs.HasKey (a registry query, 20-40 us) checks for unknown deleters every 256th skip;
//   SetGameVolume: AudioListener.volume already has the value that would be written.
// Verify: the original always runs; the string it builds must equal the snapshot that allowed the skip.
// Changes from 5.x: errors go to Fail (the system stops, the host decides).

using System;
using System.Reflection;
using System.Text;
using Harmony;
using HutongGames.PlayMaker;
using MOPR6.Core.Host;
using MOPR6.Host;
using UnityEngine;

namespace MOPR6.Engine
{
    public sealed class RedundantWrites : Optimization
    {
        public static readonly RedundantWrites Instance = new RedundantWrites();
        public const string SystemId = "engine.writes";
        const string HarmonyId = "mopr.engine.writes";
        const string AxisKey = "cInput_axInv";

        RedundantWrites() : base(SystemId) { }

        public override string Title => "Redundant writes";
        public override Tier Tier => Tier.Exact;
        public override bool DefaultOn => true;

        static HarmonyInstance harmony;
        static bool verify;
        static string parts;
        public static long AxisSaves, AxisSkipped, AxisChecked, AxisMismatch, VolumeSets, VolumeSkipped;

        // The state written by the last real _SaveAxInverted().
        static bool snapValid;
        static bool[] snap = new bool[0];
        static int snapLength;
        static bool snapPrefs;
        static string snapText;
        static bool skipping, checkPending;
        static string checkExpected;
        static int safetyCounter;

        protected internal override string OnInstall(SystemMode mode)
        {
            verify = mode == SystemMode.Verify;
            AxisSaves = AxisSkipped = AxisChecked = AxisMismatch = VolumeSets = VolumeSkipped = 0;
            snapValid = skipping = checkPending = false;
            harmony = HarmonyInstance.Create(HarmonyId);
            string axis = PatchCInput();
            string volume = PatchVolume();
            parts = "cInput axis save: " + (axis ?? "patched") + "; SetGameVolume: " + (volume ?? "patched");
            if (axis != null && volume != null) return parts;   // nothing to do; a single missing part is reported only
            return null;
        }

        protected internal override void OnRemove()
        {
            if (harmony != null) harmony.UnpatchAll(HarmonyId);
            harmony = null;
            snapValid = skipping = checkPending = false;
        }

        protected internal override void OnScene() { snapValid = false; }

        static Type FindType(string assembly, string name)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                if (asm.GetName().Name == assembly) return asm.GetType(name, false);
            return null;
        }

        static bool StaticField(Type t, string name, Type type)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null && f.FieldType == type;
        }

        static HarmonyMethod Hook(string name)
        {
            MethodInfo m = typeof(RedundantWrites).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null) throw new MissingMethodException(nameof(RedundantWrites), name);
            return new HarmonyMethod(m);
        }

        static string PatchCInput()
        {
            Type t = FindType("cInput", "cInput");
            if (t == null) return "cInput not loaded";
            const BindingFlags S = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo save = t.GetMethod("_SaveAxInverted", S, null, Type.EmptyTypes, null);
            if (save == null || save.ReturnType != typeof(void)) return "_SaveAxInverted not found";
            if (!StaticField(t, "_invertAxis", typeof(bool[])) || !StaticField(t, "_axisLength", typeof(int)) ||
                !StaticField(t, "_usePlayerPrefs", typeof(bool)) || !StaticField(t, "_exAxisInverted", typeof(string)))
                return "cInput fields changed";
            MethodInfo clear = t.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (clear == null) return "cInput.Clear not found";
            MethodInfo[] deleters = new MethodInfo[2];
            string[] names = { "PlayerPrefsDeleteAll", "PlayerPrefsDeleteKey" };
            for (int i = 0; i < names.Length; i++)
            {
                Type a = FindType("Assembly-CSharp", "HutongGames.PlayMaker.Actions." + names[i]);
                deleters[i] = a != null ? a.GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null) : null;
                if (deleters[i] == null) return names[i] + ".OnEnter not found";
            }
            // Every known way to delete the registry key invalidates the snapshot - patched before the save itself.
            harmony.Patch(clear, Hook(nameof(Invalidate)), null, null);
            foreach (MethodInfo d in deleters) harmony.Patch(d, Hook(nameof(Invalidate)), null, null);
            harmony.Patch(save, Hook(nameof(SaveAxisPrefix)), Hook(nameof(SaveAxisPostfix)), null);
            return null;
        }

        static string PatchVolume()
        {
            Type t = FindType("Assembly-CSharp", "HutongGames.PlayMaker.Actions.SetGameVolume");
            if (t == null) return "SetGameVolume not found";
            MethodInfo update = t.GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            FieldInfo volume = t.GetField("volume", BindingFlags.Instance | BindingFlags.Public);
            if (update == null || volume == null || volume.FieldType != typeof(FsmFloat)) return "SetGameVolume changed";
            harmony.Patch(update, Hook(nameof(VolumePrefix)), null, null);
            return null;
        }

        // ----- cInput._SaveAxInverted (Harmony injects cInput's private static fields: "___" + "_invertAxis") -----

        static void Invalidate() { snapValid = false; }

        static bool Unchanged(bool[] invert, int length, bool usePrefs, string text)
        {
            if (!snapValid || length != snapLength || usePrefs != snapPrefs || !ReferenceEquals(text, snapText)) return false;
            int n = length + 1;
            if (invert == null || n < 0 || n > invert.Length || n > snap.Length) return false;
            for (int i = 0; i < n; i++) if (invert[i] != snap[i]) return false;
            if (usePrefs && (++safetyCounter & 255) == 0 && !PlayerPrefs.HasKey(AxisKey)) return false;
            return true;
        }

        static bool SaveAxisPrefix(bool[] ____invertAxis, int ____axisLength, bool ____usePlayerPrefs, string ____exAxisInverted)
        {
            skipping = checkPending = false;
            if (!Instance.Live) return true;
            try
            {
                AxisSaves++;
                if (!Unchanged(____invertAxis, ____axisLength, ____usePlayerPrefs, ____exAxisInverted)) return true;
                if (verify)
                {
                    checkPending = true;
                    checkExpected = snapText;
                    return true;
                }
                skipping = true;
                AxisSkipped++;
                return false;
            }
            catch (Exception ex)
            {
                skipping = checkPending = false;
                Instance.Fail("SaveAxis", ex);
                return true;
            }
        }

        static void SaveAxisPostfix(bool[] ____invertAxis, int ____axisLength, bool ____usePlayerPrefs, string ____exAxisInverted)
        {
            // Harmony 1.2 runs postfixes even when the prefix skipped the original.
            if (skipping) { skipping = false; return; }
            try
            {
                if (checkPending)
                {
                    checkPending = false;
                    AxisChecked++;
                    if (!string.Equals(____exAxisInverted, checkExpected, StringComparison.Ordinal)) AxisMismatch++;
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
            catch (Exception ex)
            {
                snapValid = false;
                Instance.Fail("SaveAxis/post", ex);
            }
        }

        // ----- SetGameVolume.OnUpdate ----------------------------------------------------------------------------

        static bool VolumePrefix(FsmFloat ___volume)
        {
            if (!Instance.Live || verify || ___volume == null) return true;
            VolumeSets++;
            if (AudioListener.volume != ___volume.Value) return true;
            VolumeSkipped++;
            return false;
        }

        public override long Mismatches => AxisMismatch;

        public override void Report(StringBuilder sb)
        {
            sb.Append("  ").Append(parts ?? "-").Append('\n');
            sb.Append("  cInput axis saves ").Append(AxisSaves).Append(", skipped ").Append(AxisSkipped);
            if (verify) sb.Append(", checked ").Append(AxisChecked).Append(", mismatches ").Append(AxisMismatch);
            sb.Append("; volume sets ").Append(VolumeSets).Append(", skipped ").Append(VolumeSkipped).Append('\n');
        }
    }
}
