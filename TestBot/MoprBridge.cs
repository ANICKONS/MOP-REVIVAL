// Reflection bridge to MOPR6's test API (MOPR6.Testing.TestApi), so the bot does not reference MOPR6 at compile time
// and keeps working in vanilla and MOPR 5.x runs (every call then reports "MOPR6 not loaded").

using System;
using System.Reflection;

namespace MOPR.TestBot
{
    static class MoprBridge
    {
        static bool resolved;
        static Type api;

        static Type Api
        {
            get
            {
                if (resolved && api != null) return api;
                resolved = true;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t;
                    try { t = asm.GetType("MOPR6.Testing.TestApi", false); }
                    catch (Exception) { continue; }
                    if (t != null) { api = t; break; }
                }
                return api;
            }
        }

        public static bool Available => Api != null;

        /// <summary>Calls a public static method of TestApi; returns its result as text, or an error text.</summary>
        public static string Call(string method, params object[] args)
        {
            Type t = Api;
            if (t == null) return "MOPR6 not loaded";
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i]?.GetType() ?? typeof(string);
            MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, types, null);
            if (m == null) return "TestApi." + method + " not found";
            try
            {
                object r = m.Invoke(null, args);
                return r == null ? "null" : Convert.ToString(r, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (TargetInvocationException e)
            {
                return "TestApi." + method + " threw " + (e.InnerException?.GetType().Name ?? "?") + ": " + e.InnerException?.Message;
            }
        }
    }
}
