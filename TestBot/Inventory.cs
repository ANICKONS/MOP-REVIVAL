// Inventory dump for the MOPR6 catalogue (design §5, task 23): the structure of every object and FSM of the scene,
// active or not, from which "EquivCompare catalog build" makes MOPR6\Resources\catalog\inventory.json.
// Taken on vanilla (only MSCLoader and the TestBot) right after the load. One JSON object per line:
//   {"t":"game","asm":"<md5 of Assembly-CSharp.dll>","frame":N}
//   {"t":"o","p":"<path>","c":["BoxCollider","PlayMakerFSM",...],"n":<children>}           components sorted
//   {"t":"f","p":"<path>","name":"Use","i":0,"start":"State 1",
//    "s":[{"n":"State 1","a":["HutongGames.PlayMaker.Actions.SetBoolValue","-..."],"tr":["FINISHED>Next"]}],
//    "g":["GLOBALEVENT>State"]}
// Paths are the stable paths of FullSnapshot. Action types come from the loaded actions when the FSM has loaded them
// (an action inserted by a mod at run time is seen) and from the serialized ActionData otherwise - so nothing is
// initialized by the dump. A disabled action has a leading '-'. MOPR6 builds the same shape in the game and compares
// its hash (Core Fingerprint.Of(FsmShape)) with the inventory.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class Inventory
    {
        public sealed class Result
        {
            public int Objects, Fsms, Rows, Errors;
            public float Seconds;
            public string FirstError;
        }

        const BindingFlags I = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo actionNamesField = typeof(ActionData).GetField("actionNames", I);
        static readonly FieldInfo actionEnabledField = typeof(ActionData).GetField("actionEnabled", I);

        public static Result Take(string file)
        {
            float t0 = Time.realtimeSinceStartup;
            Result res = new Result();
            StringBuilder sb = new StringBuilder(1 << 22);
            sb.Append("{\"t\":\"game\",\"asm\":").Append(Json(GameAssemblyHash())).Append(",\"frame\":").Append(BotRunner.SyncedFrame).Append("}\n");
            res.Rows++;

            GameObject[] all = FullSnapshot.BeginPaths();
            try
            {
                List<string> names = new List<string>();
                foreach (GameObject go in all)
                {
                    if (go == null || go.hideFlags != HideFlags.None) continue;
                    string path = FullSnapshot.PathOf(go.transform);
                    if (path == null) continue;
                    try
                    {
                        names.Clear();
                        foreach (Component c in go.GetComponents<Component>()) names.Add(c == null ? "null" : c.GetType().Name);
                        names.Sort(StringComparer.Ordinal);
                        sb.Append("{\"t\":\"o\",\"p\":").Append(Json(path)).Append(",\"c\":");
                        JsonList(sb, names);
                        sb.Append(",\"n\":").Append(go.transform.childCount).Append("}\n");
                        res.Objects++;
                        res.Rows++;
                    }
                    catch (Exception e) { Error(res, path, e); }
                }

                Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>().OrderBy(f => f.GetInstanceID()))
                {
                    if (fsm == null || fsm.hideFlags != HideFlags.None || fsm.gameObject.hideFlags != HideFlags.None) continue;
                    string path = FullSnapshot.PathOf(fsm.transform);
                    if (path == null) continue;
                    try
                    {
                        // Index among the FSMs of the same name on the object, in component order.
                        int k = 0;
                        foreach (PlayMakerFSM other in fsm.GetComponents<PlayMakerFSM>())
                        {
                            if (ReferenceEquals(other, fsm)) break;
                            if (other.FsmName == fsm.FsmName) k++;
                        }
                        Fsm(sb, fsm, path, k);
                        res.Fsms++;
                        res.Rows++;
                    }
                    catch (Exception e) { Error(res, path + "#" + fsm.FsmName, e); }
                }
            }
            finally { FullSnapshot.EndPaths(); }

            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            res.Seconds = Time.realtimeSinceStartup - t0;
            return res;
        }

        static void Fsm(StringBuilder sb, PlayMakerFSM pm, string path, int k)
        {
            Fsm fsm = pm.Fsm;
            sb.Append("{\"t\":\"f\",\"p\":").Append(Json(path)).Append(",\"name\":").Append(Json(pm.FsmName))
              .Append(",\"i\":").Append(k).Append(",\"start\":").Append(Json(fsm.StartState)).Append(",\"s\":[");
            List<string> list = new List<string>();
            FsmState[] states = fsm.States ?? new FsmState[0];
            for (int si = 0; si < states.Length; si++)
            {
                FsmState s = states[si];
                if (si > 0) sb.Append(',');
                sb.Append("{\"n\":").Append(Json(s.Name)).Append(",\"a\":");
                list.Clear();
                Actions(s, list);
                JsonList(sb, list);
                sb.Append(",\"tr\":");
                list.Clear();
                foreach (FsmTransition t in s.Transitions ?? new FsmTransition[0]) list.Add(t.EventName + ">" + t.ToState);
                JsonList(sb, list);
                sb.Append('}');
            }
            sb.Append("],\"g\":");
            list.Clear();
            foreach (FsmTransition t in fsm.GlobalTransitions ?? new FsmTransition[0]) list.Add(t.EventName + ">" + t.ToState);
            JsonList(sb, list);
            sb.Append("}\n");
        }

        /// <summary>Action type names of a state without loading its actions (see the header).</summary>
        static void Actions(FsmState s, List<string> list)
        {
            if (s.ActionsLoaded)
            {
                foreach (FsmStateAction a in s.Actions) list.Add(a == null ? "null" : (a.Enabled ? "" : "-") + a.GetType().ToString());
                return;
            }
            ActionData data = s.ActionData;
            if (data == null) return;
            List<string> names = actionNamesField?.GetValue(data) as List<string>;
            List<bool> enabled = actionEnabledField?.GetValue(data) as List<bool>;
            if (names == null) return;
            for (int i = 0; i < names.Count; i++)
            {
                bool on = enabled == null || i >= enabled.Count || enabled[i];
                list.Add((on ? "" : "-") + names[i]);
            }
        }

        static string GameAssemblyHash()
        {
            try
            {
                Assembly asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (asm == null || string.IsNullOrEmpty(asm.Location)) return "unknown";
                using (MD5 md5 = MD5.Create())
                    return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(asm.Location))).Replace("-", "");
            }
            catch (Exception e) { return "error " + e.GetType().Name; }
        }

        static void Error(Result res, string where, Exception e)
        {
            res.Errors++;
            if (res.FirstError == null) res.FirstError = where + ": " + e.GetType().Name + ": " + e.Message;
        }

        static void JsonList(StringBuilder sb, List<string> items)
        {
            sb.Append('[');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json(items[i]));
            }
            sb.Append(']');
        }

        static string Json(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
