// Game state snapshot used to detect state corruption ("progress reset") caused by an optimizer.
// Captures, for every PlayMaker FSM on an ACTIVE object: active state + all float/int/bool/string
// variables, and the position of every active rigidbody. Comparing two snapshots taken at the same
// place (before leaving / after coming back) shows what changed; comparing that against a vanilla run
// separates natural changes (time, NPCs) from changes introduced by the mod under test.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class StateSnapshot
    {
        public static Dictionary<string, string> Take()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, int> dup = new Dictionary<string, int>(StringComparer.Ordinal);
            CultureInfo inv = CultureInfo.InvariantCulture;

            foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || !fsm.gameObject.activeInHierarchy) continue;
                string baseKey = BotRunner.PathOf(fsm.transform) + "#" + fsm.FsmName;
                dup.TryGetValue(baseKey, out int n);
                dup[baseKey] = n + 1;
                string key = n == 0 ? baseKey : baseKey + "#" + n;

                try
                {
                    d[key + ".state"] = fsm.ActiveStateName ?? "";
                    FsmVariables vars = fsm.FsmVariables;
                    foreach (FsmFloat v in vars.FloatVariables) d[key + ".f." + v.Name] = v.Value.ToString("0.###", inv);
                    foreach (FsmInt v in vars.IntVariables) d[key + ".i." + v.Name] = v.Value.ToString(inv);
                    foreach (FsmBool v in vars.BoolVariables) d[key + ".b." + v.Name] = v.Value ? "1" : "0";
                    foreach (FsmString v in vars.StringVariables) d[key + ".s." + v.Name] = v.Value ?? "";
                }
                catch { }
            }

            foreach (Rigidbody rb in UnityEngine.Object.FindObjectsOfType<Rigidbody>())
            {
                if (rb == null) continue;
                Vector3 p = rb.transform.position;
                string key = BotRunner.PathOf(rb.transform) + ".pos";
                dup.TryGetValue(key, out int n);
                dup[key] = n + 1;
                if (n > 0) key += "#" + n;
                d[key] = p.x.ToString("0.0", inv) + "," + p.y.ToString("0.0", inv) + "," + p.z.ToString("0.0", inv);
            }

            return d;
        }

        public static void Write(Dictionary<string, string> snap, string path)
        {
            StringBuilder sb = new StringBuilder(snap.Count * 64);
            List<string> keys = new List<string>(snap.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
                sb.Append(k).Append('\t').Append(snap[k].Replace('\n', ' ').Replace('\t', ' ')).Append('\n');
            File.WriteAllText(path, sb.ToString());
        }

        public static CompareResult Compare(string labelA, Dictionary<string, string> a, string labelB, Dictionary<string, string> b)
        {
            CompareResult r = new CompareResult { A = labelA, B = labelB, KeysA = a.Count, KeysB = b.Count };
            foreach (KeyValuePair<string, string> kv in a)
            {
                if (!b.TryGetValue(kv.Key, out string vb)) { r.OnlyA++; continue; }
                r.Common++;
                if (Same(kv.Key, kv.Value, vb)) continue;
                r.Changed++;
                if (r.Changes.Count < 400) r.Changes.Add(kv.Key + " : " + kv.Value + " -> " + vb);
            }
            foreach (string k in b.Keys)
                if (!a.ContainsKey(k)) r.OnlyB++;
            return r;
        }

        static bool Same(string key, string x, string y)
        {
            if (x == y) return true;
            if (key.EndsWith(".pos"))
            {
                // Physics jitter: treat moves below 0.25 m as unchanged.
                string[] p = x.Split(','), q = y.Split(',');
                if (p.Length != 3 || q.Length != 3) return false;
                float dx = F(p[0]) - F(q[0]), dy = F(p[1]) - F(q[1]), dz = F(p[2]) - F(q[2]);
                return dx * dx + dy * dy + dz * dz < 0.0625f;
            }
            return false;
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    }
}
