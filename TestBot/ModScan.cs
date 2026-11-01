// Measurement for the "auto-protection" idea: which objects that MOPR switches off contain something of another
// mod? For every object MOPR manages (world objects, place children, vehicles, items, Satsuma parts) its whole
// subtree is scanned for
//   * components whose type comes from a mod's assembly,
//   * PlayMaker actions from a mod's assembly,
//   * MSCLoader FSM hooks (FsmInject) whose delegate points into a mod's assembly.
// Run it with the rules off ("mopr load-rules false" in the menu), so MOPR manages everything and the result can be
// compared with what the built-in rule files protect. Nothing is changed in the game.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MOPR.TestBot
{
    static class ModScan
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        sealed class Finding { public string Mod, Kind, What, Where; }

        /// <summary>Writes the full report and returns the summary line.</summary>
        public static string Run(out string report)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Dictionary<Assembly, string> mods = ModAssemblies();
            Assembly mopr = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MOPR");
            if (mopr == null) { report = ""; return "MOPR not loaded"; }

            // What MOPR switches, with the kind of switching.
            List<KeyValuePair<string, GameObject>> managed = Managed(mopr);

            StringBuilder sb = new StringBuilder();
            Dictionary<string, int> objectsPerMod = new Dictionary<string, int>();
            Dictionary<string, int> perKind = new Dictionary<string, int>();
            int flagged = 0, scanned = 0, components = 0;
            HashSet<GameObject> seen = new HashSet<GameObject>();

            foreach (KeyValuePair<string, GameObject> entry in managed)
            {
                GameObject root = entry.Value;
                if (root == null || !seen.Add(root)) continue;
                scanned++;
                List<Finding> found = new List<Finding>();
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform) continue;
                    components++;
                    string where = RelativePath(root.transform, c.transform);
                    string mod;
                    if (mods.TryGetValue(c.GetType().Assembly, out mod))
                        found.Add(new Finding { Mod = mod, Kind = "component", What = c.GetType().Name, Where = where });

                    PlayMakerFSM fsm = c as PlayMakerFSM;
                    if (fsm == null || fsm.FsmStates == null) continue;
                    foreach (FsmState state in fsm.FsmStates)
                    {
                        FsmStateAction[] actions;
                        try { actions = state.Actions; } catch { continue; }
                        if (actions == null) continue;
                        foreach (FsmStateAction a in actions)
                        {
                            if (a == null) continue;
                            if (mods.TryGetValue(a.GetType().Assembly, out mod))
                            {
                                found.Add(new Finding { Mod = mod, Kind = "fsm action", What = a.GetType().Name + " in " + fsm.FsmName + "/" + state.Name, Where = where });
                                continue;
                            }
                            foreach (string hookMod in DelegateMods(a, mods))
                                found.Add(new Finding { Mod = hookMod, Kind = "fsm hook", What = fsm.FsmName + "/" + state.Name, Where = where });
                        }
                    }
                }
                if (found.Count == 0) continue;

                flagged++;
                perKind.TryGetValue(entry.Key, out int k);
                perKind[entry.Key] = k + 1;
                sb.Append(entry.Key).Append('\t').Append(BotRunner.PathOf(root.transform)).Append('\t').Append(root.name).Append('\n');
                foreach (IGrouping<string, Finding> g in found.GroupBy(f => f.Mod))
                {
                    objectsPerMod.TryGetValue(g.Key, out int n);
                    objectsPerMod[g.Key] = n + 1;
                    sb.Append("    ").Append(g.Key).Append(": ").Append(g.Count()).Append(" (")
                      .Append(string.Join(", ", g.GroupBy(f => f.Kind).Select(x => x.Key + " " + x.Count()).ToArray())).Append(")\n");
                    foreach (Finding f in g.Take(6))
                        sb.Append("        ").Append(f.Kind).Append(' ').Append(f.What).Append(" @ ").Append(f.Where).Append('\n');
                    if (g.Count() > 6) sb.Append("        ...\n");
                }
            }
            sw.Stop();

            string summary = $"{scanned} managed objects scanned ({components} components) in {sw.ElapsedMilliseconds} ms; " +
                             $"{flagged} contain something of a mod (" +
                             string.Join(", ", perKind.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value).ToArray()) + "); by mod: " +
                             string.Join(", ", objectsPerMod.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value).ToArray()) +
                             "; mods scanned: " + string.Join(", ", mods.Values.Distinct().OrderBy(x => x).ToArray());
            report = summary + "\n\n" + sb.ToString();
            return summary;
        }

        /// <summary>Assembly -> mod ID, for every loaded mod except MSCLoader's own, MOPR and the TestBot.</summary>
        static Dictionary<Assembly, string> ModAssemblies()
        {
            Dictionary<Assembly, string> map = new Dictionary<Assembly, string>();
            foreach (Mod m in ModLoader.LoadedMods)
            {
                if (m == null || m.ID.StartsWith("MSCLoader_") || m.ID == "MOPR" || m.ID == "MOPR_TestBot") continue;
                Assembly a = m.GetType().Assembly;
                if (!map.ContainsKey(a)) map[a] = m.ID;
            }
            return map;
        }

        /// <summary>Mods whose code a delegate field of this action points to (MSCLoader's FsmInject hooks).</summary>
        static IEnumerable<string> DelegateMods(FsmStateAction action, Dictionary<Assembly, string> mods)
        {
            for (Type t = action.GetType(); t != null && t != typeof(FsmStateAction); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(Any | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    Delegate d = f.GetValue(action) as Delegate;
                    if (d == null) continue;
                    foreach (Delegate inv in d.GetInvocationList())
                    {
                        Type owner = inv.Method.DeclaringType;
                        if (owner != null && mods.TryGetValue(owner.Assembly, out string mod)) yield return mod;
                    }
                }
            }
        }

        static List<KeyValuePair<string, GameObject>> Managed(Assembly mopr)
        {
            List<KeyValuePair<string, GameObject>> list = new List<KeyValuePair<string, GameObject>>();
            void Add(string kind, GameObject g) { if (g != null) list.Add(new KeyValuePair<string, GameObject>(kind, g)); }

            foreach (object o in Items(Member(Static(mopr, "MOP.Managers.WorldObjectManager", "instance"), "worldObjects")))
                Add("world", Member(o, "GameObject") as GameObject);
            foreach (object p in Items(Member(Static(mopr, "MOP.Managers.PlaceManager", "instance"), "places")))
            {
                HashSet<string> black = new HashSet<string>(Items(Member(p, "GameObjectBlackList")).OfType<string>());
                foreach (object t in Items(Member(p, "DisableableChilds")))
                    if (t is Transform tr && tr != null && !black.Contains(tr.name)) Add("place", tr.gameObject);
            }
            foreach (object v in Items(Member(Static(mopr, "MOP.Managers.VehicleManager", "instance"), "vehicles")))
                Add("vehicle", Member(v, "gameObject") as GameObject);
            foreach (object i in Items(Member(Static(mopr, "MOP.Managers.ItemsManager", "instance"), "itemHooks")))
                if (i is Component c && c != null) Add("item", c.gameObject);
            object satsuma = Static(mopr, "MOP.Vehicles.Cases.Satsuma", "instance");
            foreach (object t in Items(Member(satsuma, "disableableObjects")))
                if (t is Transform tr && tr != null) Add("satsuma", tr.gameObject);
            return list;
        }

        static string RelativePath(Transform root, Transform t)
        {
            if (t == root) return ".";
            List<string> parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        static object Static(Assembly asm, string type, string field) => asm.GetType(type, false)?.GetField(field, Any)?.GetValue(null);

        static object Member(object o, string name)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(o);
                PropertyInfo p = t.GetProperty(name, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o, null);
            }
            return null;
        }

        static IEnumerable<object> Items(object list)
        {
            if (list is System.Collections.IEnumerable e)
                foreach (object o in e) yield return o;
        }
    }
}
