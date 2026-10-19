// Attribution helper: a Harmony prefix that skips one method while Skip is true. Used by the "abtoggle"
// step with Var "skipmethod" to measure what a single method costs (in memory only, removed afterwards).

using System;
using System.Linq;
using System.Reflection;
using Harmony;

namespace MOPR.TestBot
{
    static class MethodSkipper
    {
        const string HarmonyId = "mopr.testbot.skipper";
        static HarmonyInstance harmony;
        static MethodInfo target;

        public static bool Skip;

        /// <summary>spec = "Namespace.Type:Method" (parameterless or the only overload). Returns an error or null.</summary>
        public static string Install(string spec)
        {
            Uninstall();
            int colon = spec?.IndexOf(':') ?? -1;
            if (colon < 0) return "bad method spec " + spec;
            string typeName = spec.Substring(0, colon), methodName = spec.Substring(colon + 1);
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType(typeName, false); } catch { return null; } })
                .FirstOrDefault(t => t != null);
            if (type == null) return "type not found: " + typeName;
            target = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == methodName);
            if (target == null) return "method not found: " + spec;
            if (target.ReturnType != typeof(void)) return "only void methods can be skipped: " + spec;

            harmony = HarmonyInstance.Create(HarmonyId);
            harmony.Patch(target, new HarmonyMethod(typeof(MethodSkipper).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            Skip = false;
            return null;
        }

        public static void Uninstall()
        {
            Skip = false;
            if (harmony != null && target != null)
            {
                try { harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId); } catch { }
            }
            harmony = null;
            target = null;
        }

        static bool Prefix() => !Skip;
    }
}
