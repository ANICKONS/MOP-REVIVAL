// Script CPU profiler built on Harmony 1.2 (shipped with MSCLoader).
// Patches the Update / FixedUpdate / LateUpdate entry points of every MonoBehaviour in the game and mod
// assemblies (+ coroutines of MOPR), measures wall time per call with Stopwatch timestamps and attributes
// PlayMakerFSM time to each FSM and its active state. Patches are removed when the profile step ends.
//
// Unity calls these entry points one after another on the main thread, so a single static start
// timestamp is enough. If an original method throws, Harmony 1.2 skips the postfix: the next prefix
// simply overwrites the start timestamp.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Harmony;
using UnityEngine;

namespace MOPR.TestBot
{
    static class ScriptProfiler
    {
        const string HarmonyId = "mopr.testbot.profiler";
        static readonly string[] EntryPoints = { "Update", "FixedUpdate", "LateUpdate" };
        // Bucket 4: OnGUI. Bucket 5: physics messages (they can run several times per frame).
        static readonly string[] PhysicsMessages = { "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit" };
        static readonly string[] CollisionMessages = { "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit" };

        sealed class Stat { public long Ticks; public long Calls; public long Alloc; }
        sealed class FsmStat { public PlayMakerFSM Fsm; public long Ticks; public long Calls; public long Alloc; public Dictionary<string, Stat> States = new Dictionary<string, Stat>(); }

        /// <summary>
        /// Also sample the managed heap around every call. Mono's Boehm GC reports heap growth in whole blocks,
        /// so this is a statistical attribution (block acquisitions land in the method that triggered them).
        /// </summary>
        public static bool TrackAlloc;
        static long memStart, totalAlloc;

        static HarmonyInstance harmony;
        static readonly Dictionary<string, Stat> byKey = new Dictionary<string, Stat>();
        static readonly Dictionary<Type, string>[] keyCache =
        {
            new Dictionary<Type, string>(), new Dictionary<Type, string>(), new Dictionary<Type, string>(),
            new Dictionary<Type, string>(), new Dictionary<Type, string>(), new Dictionary<Type, string>()
        };
        static readonly Dictionary<int, FsmStat> byFsm = new Dictionary<int, FsmStat>();
        static long start;
        static bool recording;
        static int patched, failures;
        static long totalTicks, playMakerTicks, totalCalls, playMakerCalls;
        static float overheadInnerUs, overheadTotalUs;

        public static int Patched => patched;
        public static int Failures => failures;

        /// <summary>Target for the overhead calibration (patched exactly like a game Update).</summary>
        sealed class CalibDummy
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Update() { }
        }

        public static void Begin(Action<string> log)
        {
            byKey.Clear();
            byFsm.Clear();
            foreach (var c in keyCache) c.Clear();
            totalTicks = playMakerTicks = totalCalls = playMakerCalls = 0;
            totalAlloc = 0;
            overheadInnerUs = overheadTotalUs = 0f;
            patched = failures = 0;

            harmony = HarmonyInstance.Create(HarmonyId);
            Type self = typeof(ScriptProfiler);
            BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            // PlayMakerFSM: attributed per FSM instance.
            TryPatch(typeof(PlayMakerFSM).GetMethod("Update", any), "PmPost", log);
            TryPatch(typeof(PlayMakerFSM).GetMethod("LateUpdate", any), "PmLatePost", log);

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!ShouldProfile(asm)) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

                foreach (Type t in types)
                {
                    if (t == null || t.IsGenericTypeDefinition || t == typeof(PlayMakerFSM)) continue;

                    if (typeof(MonoBehaviour).IsAssignableFrom(t))
                    {
                        for (int i = 0; i < EntryPoints.Length; i++)
                        {
                            MethodInfo m = t.GetMethod(EntryPoints[i], any, null, Type.EmptyTypes, null);
                            if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
                            TryPatch(m, "Post" + i, log);
                        }
                        if (!Extended) continue;
                        MethodInfo gui = t.GetMethod("OnGUI", any, null, Type.EmptyTypes, null);
                        if (gui != null && !gui.IsAbstract && gui.GetMethodBody() != null) TryPatch(gui, "Post4", log);
                        foreach (string msg in PhysicsMessages) PatchMessage(t.GetMethod(msg, any, null, new[] { typeof(Collider) }, null), log);
                        foreach (string msg in CollisionMessages) PatchMessage(t.GetMethod(msg, any, null, new[] { typeof(Collision) }, null), log);
                    }
                    else if (IsCoroutine(asm, t))
                    {
                        MethodInfo m = t.GetMethod("MoveNext", any, null, Type.EmptyTypes, null);
                        if (m != null && m.GetMethodBody() != null) TryPatch(m, "Post3", log);
                    }
                }
            }

            Calibrate(log);
            log($"profiler: {patched} methods patched, {failures} failures, overhead {overheadInnerUs:0.000} us measured / {overheadTotalUs:0.000} us total per call");
            recording = true;
        }

        /// <summary>
        /// Measures the profiler's own cost on an empty method: "inner" is what the profiler reports for a method
        /// that does nothing (subtracted from the results), "total" is the real extra wall time per call.
        /// </summary>
        static void Calibrate(Action<string> log)
        {
            MethodInfo m = typeof(CalibDummy).GetMethod("Update");
            try
            {
                CalibDummy d = new CalibDummy();
                const int N = 20000;
                double tickUs = 1e6 / Stopwatch.Frequency;

                for (int i = 0; i < 1000; i++) d.Update();
                long b0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < N; i++) d.Update();
                long b1 = Stopwatch.GetTimestamp();

                int before = patched;
                TryPatch(m, "Post0", log);
                if (patched == before) return; // patch failed (already logged)
                patched = before;
                string key = typeof(CalibDummy).FullName + ".Update";
                recording = true;
                for (int i = 0; i < 1000; i++) d.Update();
                byKey.Remove(key);
                long p0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < N; i++) d.Update();
                long p1 = Stopwatch.GetTimestamp();
                recording = false;

                if (byKey.TryGetValue(key, out Stat s) && s.Calls > 0)
                    overheadInnerUs = (float)(s.Ticks * tickUs / s.Calls);
                overheadTotalUs = (float)Math.Max(0.0, ((p1 - p0) - (b1 - b0)) * tickUs / N);
            }
            catch (Exception e)
            {
                log("profiler: calibration failed: " + e.Message);
            }
            finally
            {
                recording = false;
                byKey.Remove(typeof(CalibDummy).FullName + ".Update");
                totalTicks = playMakerTicks = totalCalls = playMakerCalls = 0;
                totalAlloc = 0;
                try { harmony.Unpatch(m, HarmonyPatchType.All, HarmonyId); } catch { }
            }
        }

        public static ProfileResult End(string label, float seconds, int frames, float frameMsAvg)
        {
            recording = false;
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;

            double tickMs = 1000.0 / Stopwatch.Frequency;
            float perFrame(long ticks) => frames > 0 ? (float)(ticks * tickMs / frames) : 0f;

            float corrected(long ticks, long calls) => Math.Max(0f, perFrame(ticks) - (frames > 0 ? calls * overheadInnerUs / 1000f / frames : 0f));

            ProfileResult r = new ProfileResult
            {
                Label = label, Seconds = seconds, Frames = frames, FrameMsAvg = frameMsAvg,
                ScriptMsPerFrame = perFrame(totalTicks), PlayMakerMsPerFrame = perFrame(playMakerTicks),
                ScriptMsPerFrameCorrected = corrected(totalTicks, totalCalls),
                PlayMakerMsPerFrameCorrected = corrected(playMakerTicks, playMakerCalls),
                CallsPerFrame = frames > 0 ? (float)totalCalls / frames : 0f,
                OverheadInnerUs = overheadInnerUs, OverheadTotalUs = overheadTotalUs,
                PatchedMethods = patched, PatchFailures = failures
            };

            r.ByType = byKey.OrderByDescending(kv => kv.Value.Ticks).Take(80)
                .Select(kv => Entry(kv.Key, kv.Value.Ticks, kv.Value.Calls, frames, tickMs)).ToList();

            r.ByFsm = byFsm.Values.OrderByDescending(s => s.Ticks).Take(80)
                .Select(s => Entry(FsmName(s.Fsm), s.Ticks, s.Calls, frames, tickMs)).ToList();

            // PlayMaker time summed per root object (e.g. everything inside the Satsuma).
            r.ByFsmRoot = byFsm.Values
                .GroupBy(s => { try { return s.Fsm != null ? s.Fsm.transform.root.name : "(destroyed)"; } catch { return "(unknown)"; } })
                .Select(g => new { Name = g.Key + " (" + g.Count() + " FSMs)", Ticks = g.Sum(x => x.Ticks), Calls = g.Sum(x => x.Calls) })
                .OrderByDescending(x => x.Ticks).Take(30)
                .Select(x => Entry(x.Name, x.Ticks, x.Calls, frames, tickMs)).ToList();

            r.ByFsmState = byFsm.Values
                .SelectMany(s => s.States.Select(st => new { Name = FsmName(s.Fsm) + " :: " + st.Key, st.Value }))
                .OrderByDescending(x => x.Value.Ticks).Take(80)
                .Select(x => Entry(x.Name, x.Value.Ticks, x.Value.Calls, frames, tickMs)).ToList();

            if (TrackAlloc)
            {
                float sec = Math.Max(0.001f, seconds);
                r.AllocKBPerSec = totalAlloc / 1024f / sec;
                r.ByAllocType = byKey.Where(kv => kv.Value.Alloc > 0).OrderByDescending(kv => kv.Value.Alloc).Take(40)
                    .Select(kv => AllocEntry(kv.Key, kv.Value.Alloc, kv.Value.Calls, sec)).ToList();
                r.ByAllocFsmState = byFsm.Values
                    .SelectMany(s => s.States.Select(st => new { Name = FsmName(s.Fsm) + " :: " + st.Key, st.Value }))
                    .Where(x => x.Value.Alloc > 0).OrderByDescending(x => x.Value.Alloc).Take(60)
                    .Select(x => AllocEntry(x.Name, x.Value.Alloc, x.Value.Calls, sec)).ToList();
            }

            byKey.Clear();
            byFsm.Clear();
            return r;
        }

        static ProfileEntry Entry(string name, long ticks, long calls, int frames, double tickMs)
        {
            float us = calls > 0 ? (float)(ticks * tickMs * 1000.0 / calls) : 0f;
            float ms = frames > 0 ? (float)(ticks * tickMs / frames) : 0f;
            float cpf = frames > 0 ? (float)calls / frames : 0f;
            return new ProfileEntry
            {
                Name = name,
                MsPerFrame = ms,
                CallsPerFrame = cpf,
                UsPerCall = us,
                CorrectedMsPerFrame = Math.Max(0f, ms - cpf * overheadInnerUs / 1000f),
                CorrectedUsPerCall = Math.Max(0f, us - overheadInnerUs)
            };
        }

        public static AllocEntry AllocEntry(string name, long bytes, long calls, float seconds) => new AllocEntry
        {
            Name = name,
            KBPerSec = bytes / 1024f / seconds,
            BytesPerCall = calls > 0 ? (float)bytes / calls : 0f
        };

        static string FsmName(PlayMakerFSM fsm)
        {
            if (fsm == null) return "(destroyed FSM)";
            try { return BotRunner.PathOf(fsm.transform) + " [" + fsm.FsmName + "]"; }
            catch { return "(unknown FSM)"; }
        }

        static bool ShouldProfile(Assembly asm)
        {
            if (asm == typeof(ScriptProfiler).Assembly) return false;
            string name = asm.GetName().Name;
            if (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass" || name == "Assembly-UnityScript" ||
                name == "Assembly-UnityScript-firstpass" || name == "PlayMaker" || name == "MSCLoader")
                return true;
            try
            {
                string location = asm.Location ?? "";
                return location.Replace('/', '\\').IndexOf("\\Mods\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        /// <summary>Extended mode: also OnGUI, trigger/collision messages and the coroutines of every profiled assembly.</summary>
        public static bool Extended;

        static bool IsCoroutine(Assembly asm, Type t)
        {
            // MOP's main loop is a coroutine, not an Update. Extended mode: coroutines of the game and other mods too.
            if (!Extended && !asm.GetName().Name.StartsWith("MOPR")) return false;
            if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t) || t.IsAbstract || t.IsInterface) return false;
            return t.IsDefined(typeof(CompilerGeneratedAttribute), false) || t.IsNested;
        }

        static void PatchMessage(MethodInfo m, Action<string> log)
        {
            if (m == null || m.IsAbstract || m.GetMethodBody() == null) return;
            TryPatch(m, "Post5", log);
        }

        static void TryPatch(MethodInfo original, string postfixName, Action<string> log)
        {
            if (original == null) return;
            try
            {
                Type self = typeof(ScriptProfiler);
                // Highest priority: our prefix runs before any other prefix (e.g. the MOPR engine fast path, which
                // may skip the original - Harmony 1.2 then skips the remaining prefixes, but postfixes always run).
                HarmonyMethod pre = new HarmonyMethod(self.GetMethod("Pre", BindingFlags.Static | BindingFlags.NonPublic)) { prioritiy = 1000 };
                HarmonyMethod post = new HarmonyMethod(self.GetMethod(postfixName, BindingFlags.Static | BindingFlags.NonPublic)) { prioritiy = 0 };
                harmony.Patch(original, pre, post, null);
                patched++;
            }
            catch (Exception e)
            {
                failures++;
                if (failures <= 5) log($"profiler: cannot patch {original.DeclaringType?.FullName}.{original.Name}: {e.GetType().Name} {e.Message}");
            }
        }

        // ----- Patches -----------------------------------------------------------------------------------

        static void Pre()
        {
            if (TrackAlloc) memStart = GC.GetTotalMemory(false);
            start = Stopwatch.GetTimestamp();
        }

        static long AllocSince()
        {
            if (!TrackAlloc) return 0;
            long d = GC.GetTotalMemory(false) - memStart;
            return d > 0 ? d : 0;   // negative: a collection ran during the call
        }

        static void Post0(object __instance) => Account(__instance, 0, ".Update");
        static void Post1(object __instance) => Account(__instance, 1, ".FixedUpdate");
        static void Post2(object __instance) => Account(__instance, 2, ".LateUpdate");
        static void Post3(object __instance) => Account(__instance, 3, " (coroutine)");
        static void Post4(object __instance) => Account(__instance, 4, ".OnGUI");
        static void Post5(object __instance) => Account(__instance, 5, " (physics message)");

        static void Account(object instance, int bucket, string suffix)
        {
            long dt = Stopwatch.GetTimestamp() - start;
            long dm = AllocSince();
            if (!recording || instance == null) return;

            Type t = instance.GetType();
            if (!keyCache[bucket].TryGetValue(t, out string key))
            {
                key = t.FullName + suffix;
                keyCache[bucket][t] = key;
            }
            if (!byKey.TryGetValue(key, out Stat s)) byKey[key] = s = new Stat();
            s.Ticks += dt;
            s.Calls++;
            s.Alloc += dm;
            totalTicks += dt;
            totalCalls++;
            totalAlloc += dm;
        }

        static void PmPost(PlayMakerFSM __instance) => AccountFsm(__instance, "PlayMakerFSM.Update");
        static void PmLatePost(PlayMakerFSM __instance) => AccountFsm(__instance, "PlayMakerFSM.LateUpdate");

        static void AccountFsm(PlayMakerFSM fsm, string key)
        {
            long dt = Stopwatch.GetTimestamp() - start;
            long dm = AllocSince();
            if (!recording || fsm == null) return;

            if (!byKey.TryGetValue(key, out Stat s)) byKey[key] = s = new Stat();
            s.Ticks += dt;
            s.Calls++;
            s.Alloc += dm;
            totalTicks += dt;
            playMakerTicks += dt;
            totalCalls++;
            playMakerCalls++;
            totalAlloc += dm;

            int id = fsm.GetInstanceID();
            if (!byFsm.TryGetValue(id, out FsmStat f)) byFsm[id] = f = new FsmStat { Fsm = fsm };
            f.Ticks += dt;
            f.Calls++;
            f.Alloc += dm;

            string state = fsm.ActiveStateName ?? "(none)";
            if (!f.States.TryGetValue(state, out Stat st)) f.States[state] = st = new Stat();
            st.Ticks += dt;
            st.Calls++;
            st.Alloc += dm;
        }
    }
}
