// Reflection bridge to MOP - REVIVAL's Engine Lab (MOP.Next.Engine.EngineLab), so the bot does not depend on
// MOPR at compile time and keeps working in vanilla runs (all calls become no-ops).

using System;
using System.Reflection;

namespace MOPR.TestBot
{
    static class EngineBridge
    {
        static bool resolved;
        static Type lab;

        static Type Lab
        {
            get
            {
                if (resolved) return lab;
                resolved = true;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "MOPR") continue;
                    lab = asm.GetType("MOP.Next.Engine.EngineLab", false);
                    break;
                }
                return lab;
            }
        }

        public static bool Available => Lab != null;

        static object Call(string name, params object[] args)
        {
            Type t = Lab;
            if (t == null) return null;
            MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            return m?.Invoke(null, args);
        }

        public static string Mode => Available ? Call("GetMode") as string : "n/a";

        /// <summary>Returns null on success, else an error.</summary>
        public static string SetMode(string mode) => Available ? Call("SetMode", mode) as string : "MOPR engine not available";

        public static void SetDetail(bool on) => Call("SetDetailedStats", on);
        public static void ResetStats() => Call("ResetStats");
        public static string Counters() => Call("Counters") as string;
        public static string Report() => Call("Report") as string;

        public static long ShadowMismatches
        {
            get
            {
                PropertyInfo p = Lab?.GetProperty("ShadowMismatches", BindingFlags.Public | BindingFlags.Static);
                return p != null ? (long)p.GetValue(null, null) : 0L;
            }
        }
    }
}
