// PlayMaker action profiler: patches OnEnter / OnUpdate / OnLateUpdate / OnFixedUpdate of every action type
// used by the active FSMs and measures SELF time per concrete action type (nested actions, e.g. RunFSM
// sub-FSMs, are subtracted from their parent). Used together with ScriptProfiler (profile step, "Detail": true).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;

namespace MOPR.TestBot
{
    static class ActionProfiler
    {
        const string HarmonyId = "mopr.testbot.actionprofiler";
        static readonly string[] Hooks = { "OnUpdate", "OnLateUpdate", "OnFixedUpdate", "OnEnter" };

        sealed class Stat { public long Self, Incl, Calls, AllocSelf; }

        static HarmonyInstance harmony;
        static readonly Dictionary<Type, Stat>[] stats = { new Dictionary<Type, Stat>(), new Dictionary<Type, Stat>(), new Dictionary<Type, Stat>(), new Dictionary<Type, Stat>() };
        static readonly long[] startStack = new long[64];
        static readonly long[] childStack = new long[64];
        static readonly long[] memStack = new long[64];
        static readonly long[] childMemStack = new long[64];
        static int depth;
        static bool recording;
        static int patched, failures, types;
        static long totalSelf, totalAllocSelf;

        public static void Begin(Action<string> log)
        {
            foreach (var d in stats) d.Clear();
            depth = 0;
            totalSelf = totalAllocSelf = 0;
            patched = failures = types = 0;
            harmony = HarmonyInstance.Create(HarmonyId);

            HashSet<Type> used = new HashSet<Type>();
            foreach (PlayMakerFSM pm in PlayMakerFSM.FsmList.ToArray())
            {
                if (pm == null) continue;
                FsmState[] states;
                try { states = pm.FsmStates; } catch { continue; }
                if (states == null) continue;
                foreach (FsmState s in states)
                {
                    if (s == null || !s.ActionsLoaded) continue;
                    foreach (FsmStateAction a in s.Actions)
                        if (a != null) used.Add(a.GetType());
                }
            }
            types = used.Count;

            HashSet<string> done = new HashSet<string>();
            BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (Type t in used)
            {
                for (int h = 0; h < Hooks.Length; h++)
                {
                    MethodInfo m;
                    try { m = t.GetMethod(Hooks[h], inst, null, Type.EmptyTypes, null); }
                    catch { continue; }
                    if (m == null || m.DeclaringType == typeof(FsmStateAction) || m.IsAbstract || m.GetMethodBody() == null) continue;
                    string key = m.DeclaringType.FullName + "::" + m.Name;
                    if (!done.Add(key)) continue;
                    try
                    {
                        HarmonyMethod pre = new HarmonyMethod(typeof(ActionProfiler).GetMethod("Pre", BindingFlags.Static | BindingFlags.NonPublic)) { prioritiy = 1000 };
                        HarmonyMethod post = new HarmonyMethod(typeof(ActionProfiler).GetMethod("Post" + h, BindingFlags.Static | BindingFlags.NonPublic)) { prioritiy = 0 };
                        harmony.Patch(m, pre, post, null);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        failures++;
                        if (failures <= 5) log($"action profiler: cannot patch {key}: {e.GetType().Name} {e.Message}");
                    }
                }
            }
            log($"action profiler: {types} action types in use, {patched} methods patched, {failures} failures");
            recording = true;
        }

        /// <summary>Called once per frame from the bot (no action is running then).</summary>
        public static void FrameBoundary() => depth = 0;

        public static void End(ProfileResult r, int frames)
        {
            recording = false;
            try { harmony?.UnpatchAll(HarmonyId); } catch { }
            harmony = null;

            double tickMs = 1000.0 / Stopwatch.Frequency;
            r.ActionTypesInUse = types;
            r.ActionMethodsPatched = patched;
            r.ActionSelfMsPerFrame = frames > 0 ? (float)(totalSelf * tickMs / frames) : 0f;
            r.ByAction = stats
                .SelectMany((d, h) => d.Select(kv => new { Name = kv.Key.Name + "." + Hooks[h], S = kv.Value }))
                .OrderByDescending(x => x.S.Self).Take(80)
                .Select(x => new ActionEntry
                {
                    Name = x.Name,
                    SelfMsPerFrame = frames > 0 ? (float)(x.S.Self * tickMs / frames) : 0f,
                    InclMsPerFrame = frames > 0 ? (float)(x.S.Incl * tickMs / frames) : 0f,
                    CallsPerFrame = frames > 0 ? (float)x.S.Calls / frames : 0f,
                    SelfUsPerCall = x.S.Calls > 0 ? (float)(x.S.Self * tickMs * 1000.0 / x.S.Calls) : 0f
                }).ToList();
            if (ScriptProfiler.TrackAlloc)
            {
                float sec = Math.Max(0.001f, r.Seconds);
                r.ActionAllocKBPerSec = totalAllocSelf / 1024f / sec;
                r.ByAllocAction = stats
                    .SelectMany((d, h) => d.Select(kv => new { Name = kv.Key.Name + "." + Hooks[h], S = kv.Value }))
                    .Where(x => x.S.AllocSelf > 0).OrderByDescending(x => x.S.AllocSelf).Take(40)
                    .Select(x => ScriptProfiler.AllocEntry(x.Name, x.S.AllocSelf, x.S.Calls, sec)).ToList();
            }
            foreach (var d in stats) d.Clear();
        }

        static void Pre()
        {
            int d = depth;
            if (d >= startStack.Length) { depth = d + 1; return; }
            childStack[d] = 0;
            childMemStack[d] = 0;
            if (ScriptProfiler.TrackAlloc) memStack[d] = GC.GetTotalMemory(false);
            startStack[d] = Stopwatch.GetTimestamp();
            depth = d + 1;
        }

        static void Post0(object __instance) => Account(__instance, 0);
        static void Post1(object __instance) => Account(__instance, 1);
        static void Post2(object __instance) => Account(__instance, 2);
        static void Post3(object __instance) => Account(__instance, 3);

        static void Account(object instance, int hook)
        {
            long now = Stopwatch.GetTimestamp();
            int d = --depth;
            if (d < 0) { depth = 0; return; }
            if (d >= startStack.Length) return;
            long incl = now - startStack[d];
            long self = incl - childStack[d];
            if (d > 0) childStack[d - 1] += incl;

            long memSelf = 0;
            if (ScriptProfiler.TrackAlloc)
            {
                long memIncl = GC.GetTotalMemory(false) - memStack[d];
                if (memIncl < 0) memIncl = 0;
                memSelf = memIncl - childMemStack[d];
                if (memSelf < 0) memSelf = 0;
                if (d > 0) childMemStack[d - 1] += memIncl;
            }
            if (!recording || instance == null) return;

            Dictionary<Type, Stat> map = stats[hook];
            Type t = instance.GetType();
            if (!map.TryGetValue(t, out Stat s)) map[t] = s = new Stat();
            s.Self += self;
            s.Incl += incl;
            s.Calls++;
            s.AllocSelf += memSelf;
            totalSelf += self;
            totalAllocSelf += memSelf;
        }
    }
}
