// Discovery tools for the bot: find game objects by name (including inactive ones) and dump PlayMaker FSMs
// (variables, states, actions with their parameters, transitions). Used to understand game logic that lives
// in scene assets (septic job, save points, item FSMs) without decompiling the assets.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class FsmDumper
    {
        const int MaxLines = 40000;

        /// <summary>Scene objects (active or not) whose name contains one of the comma separated terms.</summary>
        public static List<GameObject> Find(string terms, int max)
        {
            string[] parts = (terms ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            List<GameObject> found = new List<GameObject>();
            if (parts.Length == 0) return found;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.hideFlags != HideFlags.None) continue;
                string name = go.name;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (name.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found.Add(go);
                        break;
                    }
                }
                if (max > 0 && found.Count >= max) break;
            }
            return found;
        }

        /// <summary>One line per object: path, active state, components and FSM names.</summary>
        public static string ListObjects(List<GameObject> objects)
        {
            StringBuilder sb = new StringBuilder();
            foreach (GameObject go in objects.OrderBy(g => BotRunner.PathOf(g.transform)))
            {
                sb.Append(BotRunner.PathOf(go.transform));
                sb.Append(go.activeInHierarchy ? "  [active]" : go.activeSelf ? "  [inactive parent]" : "  [inactive]");
                Vector3 p = go.transform.position;
                sb.Append($"  pos({p.x:0.0},{p.y:0.0},{p.z:0.0})");
                sb.Append("  {");
                sb.Append(string.Join(", ", go.GetComponents<Component>().Where(c => c != null && !(c is Transform))
                    .Select(c => c is PlayMakerFSM f ? "FSM:" + f.FsmName : c.GetType().Name).ToArray()));
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        /// <summary>Dumps every FSM on the objects and (if recursive) on all their children.</summary>
        public static string Dump(IEnumerable<GameObject> roots, bool recursive)
        {
            StringBuilder sb = new StringBuilder();
            HashSet<int> seen = new HashSet<int>();
            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                PlayMakerFSM[] fsms = recursive ? root.GetComponentsInChildren<PlayMakerFSM>(true) : root.GetComponents<PlayMakerFSM>();
                foreach (PlayMakerFSM fsm in fsms)
                {
                    if (fsm == null || !seen.Add(fsm.GetInstanceID())) continue;
                    DumpFsm(fsm, sb);
                    if (Lines(sb) > MaxLines) { sb.AppendLine("... (truncated)"); return sb.ToString(); }
                }
            }
            return sb.ToString();
        }

        static int Lines(StringBuilder sb) => sb.Length / 60; // cheap estimate

        static void DumpFsm(PlayMakerFSM pm, StringBuilder sb)
        {
            Fsm fsm = pm.Fsm;
            sb.AppendLine("==================================================================================");
            sb.AppendLine($"FSM \"{pm.FsmName}\" on {BotRunner.PathOf(pm.transform)}");
            sb.AppendLine($"  enabled={pm.enabled} activeInHierarchy={pm.gameObject.activeInHierarchy} state=\"{pm.ActiveStateName}\" " +
                          $"start=\"{fsm.StartState}\" restartOnEnable={fsm.RestartOnEnable}");

            FsmVariables v = fsm.Variables;
            AppendVars(sb, "float", v.FloatVariables, x => x.Value.ToString("0.###"));
            AppendVars(sb, "int", v.IntVariables, x => x.Value.ToString());
            AppendVars(sb, "bool", v.BoolVariables, x => x.Value.ToString());
            AppendVars(sb, "string", v.StringVariables, x => "\"" + x.Value + "\"");
            AppendVars(sb, "vector3", v.Vector3Variables, x => x.Value.ToString());
            AppendVars(sb, "gameObject", v.GameObjectVariables, x => x.Value != null ? BotRunner.PathOf(x.Value.transform) : "null");
            AppendVars(sb, "object", v.ObjectVariables, x => x.Value != null ? x.Value.name + " (" + x.Value.GetType().Name + ")" : "null");

            if (fsm.GlobalTransitions != null && fsm.GlobalTransitions.Length > 0)
                sb.AppendLine("  global: " + string.Join(", ", fsm.GlobalTransitions.Select(t => t.EventName + " => " + t.ToState).ToArray()));

            foreach (FsmState state in pm.FsmStates)
            {
                sb.AppendLine($"  [{state.Name}]" + (state.Name == pm.ActiveStateName ? "  <== active" : ""));
                if (!state.IsInitialized || !state.ActionsLoaded)
                {
                    sb.AppendLine("      (actions not loaded)");
                }
                else
                {
                    FsmStateAction[] actions = state.Actions;
                    for (int i = 0; i < actions.Length; i++)
                    {
                        FsmStateAction a = actions[i];
                        if (a == null) { sb.AppendLine("      - null"); continue; }
                        sb.Append("      - ").Append(a.GetType().Name);
                        if (!a.Enabled) sb.Append(" (disabled)");
                        sb.Append(' ').AppendLine(DescribeFields(a));
                    }
                }
                foreach (FsmTransition t in state.Transitions)
                    sb.AppendLine($"      -> {t.EventName} => {t.ToState}");
            }
        }

        static void AppendVars<T>(StringBuilder sb, string kind, T[] vars, Func<T, string> value) where T : NamedVariable
        {
            if (vars == null || vars.Length == 0) return;
            sb.Append("  ").Append(kind).Append(": ");
            sb.AppendLine(string.Join(", ", vars.Select(x => x.Name + "=" + SafeValue(() => value(x))).ToArray()));
        }

        static string SafeValue(Func<string> f)
        {
            try { return f(); } catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        static string DescribeFields(object action)
        {
            List<string> parts = new List<string>();
            foreach (FieldInfo f in action.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                object value;
                try { value = f.GetValue(action); } catch { continue; }
                string s = Describe(value, 0);
                if (s == null) continue;
                parts.Add(f.Name + "=" + s);
            }
            return string.Join(" ", parts.ToArray());
        }

        static string Describe(object value, int depth)
        {
            if (value == null) return null;
            switch (value)
            {
                case FsmEvent e: return "event:" + e.Name;
                case FsmOwnerDefault o:
                    return o.OwnerOption == OwnerDefaultOption.UseOwner ? "owner" : "go:" + Describe(o.GameObject, depth + 1);
                case FsmGameObject g:
                    if (g.UseVariable) return "{" + g.Name + "}";
                    return g.Value != null ? BotRunner.PathOf(g.Value.transform) : "none";
                case FsmObject ob:
                    if (ob.UseVariable) return "{" + ob.Name + "}";
                    return ob.Value != null ? ob.Value.name + "(" + ob.Value.GetType().Name + ")" : "none";
                case NamedVariable nv:
                    if (nv.UseVariable) return "{" + nv.Name + "}";
                    if (nv.IsNone) return "none";
                    PropertyInfo p = nv.GetType().GetProperty("Value");
                    object inner = p != null ? SafeGet(p, nv) : null;
                    return inner is string str ? "\"" + str + "\"" : inner != null ? Convert.ToString(inner, System.Globalization.CultureInfo.InvariantCulture) : "null";
                case FsmEventTarget t:
                    return "target:" + t.target + (t.target == FsmEventTarget.EventTarget.GameObjectFSM ? "(" + Describe(t.gameObject, depth + 1) + "/" + Describe(t.fsmName, depth + 1) + ")" : "");
                case FsmProperty prop:
                    return "prop:" + prop.TargetTypeName + "." + prop.PropertyName + (prop.TargetObject != null ? "@" + Describe(prop.TargetObject, depth + 1) : "");
                case string s: return "\"" + s + "\"";
                case UnityEngine.Object uo: return uo.name + "(" + uo.GetType().Name + ")";
                case Array arr:
                    if (depth > 0) return "[" + arr.Length + "]";
                    List<string> items = new List<string>();
                    foreach (object o in arr) { items.Add(Describe(o, depth + 1) ?? "null"); if (items.Count >= 8) break; }
                    return "[" + string.Join(",", items.ToArray()) + (arr.Length > 8 ? ",..." : "") + "]";
                default:
                    if (value is bool || value is int || value is float || value is Enum || value is Vector3)
                        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    return null;
            }
        }

        static object SafeGet(PropertyInfo p, object o)
        {
            try { return p.GetValue(o, null); } catch { return null; }
        }
    }
}
