// Attribution helper: counts calls of up to 8 methods per frame (and their time, including the Harmony
// overhead), for the "callcount" step. Methods with parameters named "distance" (float) and "layerMask" (int),
// like PlayMaker's ActionHelpers.DoMousePick, also get a histogram of those arguments.
// In memory only, removed after the step.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Harmony;

namespace MOPR.TestBot
{
    static class CallCounter
    {
        const string HarmonyId = "mopr.testbot.callcounter";
        const int MaxTargets = 8;

        static HarmonyInstance harmony;
        static readonly List<MethodBase> targets = new List<MethodBase>();
        static readonly long[] calls = new long[MaxTargets];
        static readonly long[] ticks = new long[MaxTargets];
        static readonly Dictionary<string, long> args = new Dictionary<string, long>();
        public static bool Counting;

        /// <summary>specs = "Type:Method,Type:Method". Returns an error or null.</summary>
        public static string Install(string specs)
        {
            Uninstall();
            harmony = HarmonyInstance.Create(HarmonyId);
            string[] list = (specs ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (list.Length == 0 || list.Length > MaxTargets) return "1.." + MaxTargets + " methods expected";
            for (int i = 0; i < list.Length; i++)
            {
                int colon = list[i].IndexOf(':');
                if (colon < 0) return "bad method spec " + list[i];
                string typeName = list[i].Substring(0, colon), methodName = list[i].Substring(colon + 1);
                Type type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType(typeName, false); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (type == null) return "type not found: " + typeName;
                MethodInfo m = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.Name == methodName);
                if (m == null) return "method not found: " + list[i];

                MethodInfo pre = typeof(CallCounter).GetMethod("Pre" + i, BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo post = typeof(CallCounter).GetMethod("Post" + i, BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(m, new HarmonyMethod(pre), new HarmonyMethod(post), null);

                ParameterInfo[] ps = m.GetParameters();
                if (ps.Any(p => p.Name == "distance" && p.ParameterType == typeof(float)) &&
                    ps.Any(p => p.Name == "layerMask" && p.ParameterType == typeof(int)))
                {
                    harmony.Patch(m, new HarmonyMethod(typeof(CallCounter).GetMethod("PickArgs", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
                }
                targets.Add(m);
            }
            Reset();
            return null;
        }

        public static void Reset()
        {
            Array.Clear(calls, 0, calls.Length);
            Array.Clear(ticks, 0, ticks.Length);
            args.Clear();
        }

        public static void Uninstall()
        {
            Counting = false;
            if (harmony != null)
                foreach (MethodBase m in targets)
                {
                    try { harmony.Unpatch(m, HarmonyPatchType.All, HarmonyId); } catch { }
                }
            targets.Clear();
            harmony = null;
        }

        public static string Summary(int frames)
        {
            StringBuilder sb = new StringBuilder();
            CultureInfo ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < targets.Count; i++)
            {
                double us = ticks[i] * 1e6 / Stopwatch.Frequency;
                sb.Append(string.Format(ci, "{0}.{1}: {2:0.0} calls/frame, {3:0.000} ms/frame, {4:0.00} us/call; ",
                    targets[i].DeclaringType.Name, targets[i].Name, calls[i] / (double)Math.Max(1, frames),
                    us / 1000.0 / Math.Max(1, frames), calls[i] > 0 ? us / calls[i] : 0));
            }
            if (args.Count > 0)
            {
                sb.Append("args (layerMask/distance -> calls/frame): ");
                foreach (KeyValuePair<string, long> kv in args.OrderByDescending(k => k.Value).Take(12))
                    sb.Append(kv.Key).Append(" -> ").Append((kv.Value / (double)Math.Max(1, frames)).ToString("0.0", ci)).Append("; ");
            }
            return sb.ToString().TrimEnd(' ', ';');
        }

        static void PickArgs(float distance, int layerMask)
        {
            if (!Counting) return;
            string key = "0x" + layerMask.ToString("X8") + "/" + distance.ToString("0.###", CultureInfo.InvariantCulture);
            long n;
            args.TryGetValue(key, out n);
            args[key] = n + 1;
        }

        static void Begin(int i, out long state) { state = Counting ? Stopwatch.GetTimestamp() : 0; }
        static void End(int i, long state)
        {
            if (state == 0 || !Counting) return;
            ticks[i] += Stopwatch.GetTimestamp() - state;
            calls[i]++;
        }

        static void Pre0(out long __state) => Begin(0, out __state);
        static void Post0(long __state) => End(0, __state);
        static void Pre1(out long __state) => Begin(1, out __state);
        static void Post1(long __state) => End(1, __state);
        static void Pre2(out long __state) => Begin(2, out __state);
        static void Post2(long __state) => End(2, __state);
        static void Pre3(out long __state) => Begin(3, out __state);
        static void Post3(long __state) => End(3, __state);
        static void Pre4(out long __state) => Begin(4, out __state);
        static void Post4(long __state) => End(4, __state);
        static void Pre5(out long __state) => Begin(5, out __state);
        static void Post5(long __state) => End(5, __state);
        static void Pre6(out long __state) => Begin(6, out __state);
        static void Post6(long __state) => End(6, __state);
        static void Pre7(out long __state) => Begin(7, out __state);
        static void Post7(long __state) => End(7, __state);
    }
}
