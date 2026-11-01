// Keeps MSCLoader's console open during a test run (the author watches the runs): MSCLoader closes it on start and
// after every scene load. MSCLoader 1.4 has no public API for it: ModConsole.console (internal static ConsoleView)
// and ConsoleView.SetVisibility(bool) / viewContainer are reached by reflection. Opening only shows the console
// window (viewContainer.SetActive(true)); the input field is not focused, so nothing is typed into it.
// The same in every variant of a comparison or a benchmark, so it changes nothing between them.

using System;
using System.Reflection;
using MSCLoader;
using UnityEngine;

namespace MOPR.TestBot
{
    static class ConsoleKeeper
    {
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static bool resolved;
        static FieldInfo consoleField, containerField;
        static MethodInfo setVisibility;
        public static string Error { get; private set; }
        public static int Opened { get; private set; }

        static bool Resolve()
        {
            if (resolved) return consoleField != null;
            resolved = true;
            Type mc = typeof(ModConsole);
            consoleField = mc.GetField("console", AnyStatic);
            Type view = consoleField?.FieldType;
            setVisibility = view?.GetMethod("SetVisibility", AnyInstance, null, new[] { typeof(bool) }, null);
            containerField = view?.GetField("viewContainer", AnyInstance);
            if (consoleField == null || setVisibility == null || containerField == null)
            {
                Error = "MSCLoader console not found (ModConsole.console / ConsoleView.SetVisibility / viewContainer)";
                consoleField = null;
                return false;
            }
            return true;
        }

        /// <summary>Opens the console if it exists and is closed. Cheap when it is open already.</summary>
        public static void Keep()
        {
            if (!Resolve()) return;
            object console = consoleField.GetValue(null);
            if (console == null || (console is UnityEngine.Object uo && uo == null)) return;   // not created yet / destroyed
            GameObject container = containerField.GetValue(console) as GameObject;
            if (container == null || container.activeSelf) return;
            setVisibility.Invoke(console, new object[] { true });
            Opened++;
        }
    }
}
