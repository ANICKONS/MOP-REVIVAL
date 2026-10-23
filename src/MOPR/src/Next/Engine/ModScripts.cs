// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Other mods' scripts (5.1.1). The Engine Lab rules apply here too: only work that is proven to be a no-op is
// skipped, the verify mode runs the original call as well and compares, and removing the patch restores the
// mod's code completely.
//
// Better MSC, BulbScript.Update: every lamp bulb of the house calls GetComponent("ChangeMeshBulb") every frame
// (and destroys that component when it finds one). No loaded assembly defines a type of that name, so nothing of
// that name can be attached and the call can only return null; Unity still looks the name up every time. Measured
// with the TestBot (bmsc-tour, bmsc-attrib2): 80 us per bulb per frame, 0.8 ms for the 10 bulbs of the house;
// skipping the whole method made frames 1.39 ms shorter in the living room (+25% FPS).
//
// The patch (a Harmony transpiler) replaces only these calls: Component.GetComponent(string) right after a
// string from Names, in the methods of Targets. The replacement returns null without asking Unity while no loaded
// assembly has a type of that name (assemblies loaded later are checked when they load); otherwise it asks Unity
// as before. Everything else in the method runs unchanged. A version of the mod without that call has nothing to
// replace, and the patch then leaves it alone.
//
// Names must never contain a name of an engine component without a script class (like "Halo"): those are found
// only by Unity itself, there is no type to look for.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Harmony;
using UnityEngine;

namespace MOP.Next.Engine
{
    static class ModScripts
    {
        const string HarmonyId = "com.anickon.mopr.engine.modscripts";

        sealed class Target
        {
            public readonly string Assembly, Type, Method, Label;
            public Target(string assembly, string type, string method, string label)
            {
                Assembly = assembly; Type = type; Method = method; Label = label;
            }
        }

        static readonly Target[] Targets =
        {
            new Target("BetterMSC", "BetterMSC.BulbScript", "Update", "Better MSC lamp bulbs"),
        };

        /// <summary>Component names looked up by the targets that no game or engine component has.</summary>
        static readonly string[] Names = { "ChangeMeshBulb" };
        static readonly bool[] nameExists = new bool[Names.Length];

        static HarmonyInstance harmony;
        static readonly MethodInfo lookup = typeof(ModScripts).GetMethod(nameof(GetComponentByName), BindingFlags.Static | BindingFlags.NonPublic);
        static readonly List<string> patched = new List<string>();
        static int replacedInMethod;

        public static bool Installed => harmony != null;
        public static bool Shadow { get; private set; }
        public static string Status { get; private set; } = "not installed";

        /// <summary>A/B measurement only (TestBot): while true, Unity answers every lookup as without the patch.</summary>
        public static bool Bypass { get; set; }

        public static long Calls, Skipped, ShadowChecked, ShadowMismatch;
        static string lastMismatch;

        /// <summary>
        /// Patches the targets that are loaded. Returns null on success, also when there is nothing to patch
        /// (Installed stays false, Status says why), else the error.
        /// </summary>
        public static string Install(bool shadow)
        {
            Remove();
            try
            {
                if (lookup == null) return "lookup method not found";
                List<KeyValuePair<Target, MethodInfo>> methods = new List<KeyValuePair<Target, MethodInfo>>();
                foreach (Target t in Targets)
                {
                    MethodInfo m = Find(t);
                    if (m != null) methods.Add(new KeyValuePair<Target, MethodInfo>(t, m));
                }
                if (methods.Count == 0)
                {
                    Status = "no supported mod";
                    return null;
                }

                RefreshNames();
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                Shadow = shadow;
                harmony = HarmonyInstance.Create(HarmonyId);
                HarmonyMethod transpiler = new HarmonyMethod(typeof(ModScripts).GetMethod(nameof(Transpiler), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (KeyValuePair<Target, MethodInfo> kv in methods)
                {
                    replacedInMethod = 0;
                    harmony.Patch(kv.Value, null, null, transpiler);
                    if (replacedInMethod == 0)
                    {
                        // This version of the mod doesn't make the call: leave the method as it is.
                        harmony.Unpatch(kv.Value, HarmonyPatchType.All, HarmonyId);
                        continue;
                    }
                    patched.Add($"{kv.Key.Label} ({kv.Key.Type}.{kv.Key.Method}, {replacedInMethod} lookup{(replacedInMethod == 1 ? "" : "s")})");
                }

                if (patched.Count == 0)
                {
                    Remove();
                    Status = "nothing to replace in this mod version";
                    return null;
                }
                Status = string.Join(", ", patched.ToArray());
                return null;
            }
            catch (Exception ex)
            {
                Remove();
                Status = "failed";
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        public static void Remove()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;
            patched.Clear();
            Status = "not installed";
        }

        public static void ResetStats()
        {
            Calls = Skipped = ShadowChecked = ShadowMismatch = 0;
            lastMismatch = null;
        }

        static MethodInfo Find(Target t)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != t.Assembly) continue;
                Type type = a.GetType(t.Type, false);
                MethodInfo m = type?.GetMethod(t.Method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (m != null && m.GetMethodBody() != null) return m;
            }
            return null;
        }

        // ----- Which names exist -------------------------------------------------------------------------

        static void RefreshNames()
        {
            for (int i = 0; i < nameExists.Length; i++) nameExists[i] = false;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) Scan(a);
        }

        static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args) => Scan(args.LoadedAssembly);

        /// <summary>Marks the names that a type of the assembly has (simple or full name). Unreadable: all of them.</summary>
        static void Scan(Assembly a)
        {
            Type[] types;
            try { types = a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }   // a type that failed to load can't be a component
            catch
            {
                for (int i = 0; i < nameExists.Length; i++) nameExists[i] = true;
                return;
            }
            if (types == null) return;
            foreach (Type t in types)
            {
                if (t == null) continue;
                for (int i = 0; i < Names.Length; i++)
                    if (t.Name == Names[i] || t.FullName == Names[i]) nameExists[i] = true;
            }
        }

        // ----- The patch ---------------------------------------------------------------------------------

        static bool IsGetComponentByName(object operand)
        {
            MethodInfo m = operand as MethodInfo;
            if (m == null || m.Name != "GetComponent" || m.DeclaringType != typeof(Component) || m.IsGenericMethod) return false;
            ParameterInfo[] p = m.GetParameters();
            return p.Length == 1 && p[0].ParameterType == typeof(string);
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            for (int i = 1; i < list.Count; i++)
            {
                CodeInstruction call = list[i], name = list[i - 1];
                if (call.opcode != OpCodes.Call && call.opcode != OpCodes.Callvirt) continue;
                if (!IsGetComponentByName(call.operand)) continue;
                if (name.opcode != OpCodes.Ldstr || Array.IndexOf(Names, name.operand as string) < 0) continue;
                // Same stack in, same result out: (Component this, string name) -> Component. Labels stay on the
                // instruction, so branches into it still land here.
                call.opcode = OpCodes.Call;
                call.operand = lookup;
                replacedInMethod++;
            }
            return list;
        }

        /// <summary>Component.GetComponent(type) for a name of Names.</summary>
        static Component GetComponentByName(Component self, string type)
        {
            if (Bypass) return self.GetComponent(type);
            Calls++;
            int i = Array.IndexOf(Names, type);
            if (i < 0 || nameExists[i]) return self.GetComponent(type);
            if (Shadow)
            {
                ShadowChecked++;
                Component real = self.GetComponent(type);
                if (real != null)
                {
                    ShadowMismatch++;
                    lastMismatch = $"GetComponent(\"{type}\") on {self.name} found {real.GetType().FullName}";
                }
                return real;
            }
            Skipped++;
            return null;
        }

        public static void AppendReport(StringBuilder sb)
        {
            if (!Installed) { sb.AppendLine("Other mods' scripts: off (" + Status + ")"); return; }
            sb.AppendLine($"Other mods' scripts: {(Shadow ? "VERIFY" : "ON")}{(Bypass ? " (bypassed)" : "")} - {Status}");
            sb.AppendLine($"  lookups {Calls}, answered without Unity {Skipped}" +
                          (Shadow ? $", verified {ShadowChecked}, different {ShadowMismatch}" + (lastMismatch != null ? " (last: " + lastMismatch + ")" : "") : ""));
        }
    }
}
