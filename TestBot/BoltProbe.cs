// Bolt probe (sleeping bolts, MOPR 5.1.1): every per-bolt "Screw" FSM (objects named BoltPM, active or not) with
// what the game keeps of it - stage, state, its collider (tool mode), the bolt's position and rotation that the
// stage sets, the restart flag. Whether the FSM component is enabled (asleep or not) and whether its object is
// active are counted in the summary only, so a snapshot of sleeping bolts compares equal to the same bolts awake.
// Snapshots survive "savegame" / "reloadgame" (static), so a save can be compared with the load.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class BoltProbe
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Dictionary<string, Dictionary<string, string>> snaps = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>Takes snapshot <paramref name="label"/> of the bolts under <paramref name="root"/> (all if empty).</summary>
        public static string Take(string label, string root)
        {
            GameObject rootGo = string.IsNullOrEmpty(root) ? null : GameOps.Resolve(root);
            Dictionary<string, string> snap = new Dictionary<string, string>();
            int total = 0, enabled = 0, active = 0, colliders = 0, stageSum = 0, noRestart = 0;
            Dictionary<string, int> states = new Dictionary<string, int>();
            foreach (PlayMakerFSM pm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (pm == null || pm.hideFlags != HideFlags.None || pm.FsmName != "Screw" || pm.gameObject.name != "BoltPM") continue;
                if (rootGo != null && !pm.transform.IsChildOf(rootGo.transform)) continue;
                total++;
                if (pm.enabled) enabled++;
                if (pm.gameObject.activeInHierarchy) active++;
                Fsm f = pm.Fsm;
                string state = pm.ActiveStateName ?? "";
                string key = (pm.enabled ? "" : "(off)") + state;
                states[key] = states.TryGetValue(key, out int n) ? n + 1 : 1;
                FsmInt stage = pm.FsmVariables.FindFsmInt("Stage");
                if (stage != null) stageSum += stage.Value;
                SphereCollider col = pm.GetComponent<SphereCollider>();
                if (col != null && col.enabled) colliders++;
                if (f != null && !f.RestartOnEnable) noRestart++;
                FsmGameObject bolt = pm.FsmVariables.FindFsmGameObject("ThisBolt");
                Transform bt = bolt != null && bolt.Value != null ? bolt.Value.transform : null;
                string value = "stage=" + (stage != null ? stage.Value.ToString(Inv) : "?") +
                               " state=" + state +
                               " col=" + (col == null ? "-" : col.enabled ? "1" : "0") +
                               " restart=" + (f == null ? "?" : f.RestartOnEnable ? "1" : "0") +
                               " rot=" + pm.transform.localEulerAngles.z.ToString("0.#", Inv) +
                               " bolt=" + (bt != null ? (bt.localPosition.z * 1000f).ToString("0.##", Inv) : "-");
                string path = UniquePath(pm.transform);
                while (snap.ContainsKey(path)) path += "'";
                snap[path] = value;
            }
            snaps[label] = snap;
            File.WriteAllText(System.IO.Path.Combine(TestBotMod.Folder, "boltsnap_" + label + ".tsv"),
                string.Join("\n", snap.OrderBy(k => k.Key).Select(k => k.Key + "\t" + k.Value).ToArray()) + "\n");
            return $"{total} bolts, FSM enabled {enabled}, object active {active}, stage sum {stageSum}, colliders on {colliders}, " +
                   $"restart off {noRestart}; states " + string.Join(", ", states.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value).ToArray()) +
                   $"; PlayMakerFSM.FsmList {PlayMakerFSM.FsmList.Count}";
        }

        /// <summary>Differences between two snapshots, per field; the full list goes to boltdiff_A_B.txt.</summary>
        public static string Compare(string a, string b)
        {
            if (a == null || b == null || !snaps.ContainsKey(a) || !snaps.ContainsKey(b)) return "missing snapshot " + a + " / " + b;
            Dictionary<string, string> sa = snaps[a], sb = snaps[b];
            Dictionary<string, int> byField = new Dictionary<string, int>();
            StringBuilder list = new StringBuilder();
            int missing = 0, added = 0, changed = 0;
            foreach (KeyValuePair<string, string> kv in sa.OrderBy(k => k.Key))
            {
                if (!sb.TryGetValue(kv.Key, out string other)) { missing++; list.Append("- ").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n'); continue; }
                if (other == kv.Value) continue;
                changed++;
                list.Append("~ ").Append(kv.Key).Append('\t').Append(kv.Value).Append("  ->  ").Append(other).Append('\n');
                string[] fa = kv.Value.Split(' '), fb = other.Split(' ');
                for (int i = 0; i < fa.Length && i < fb.Length; i++)
                {
                    if (fa[i] == fb[i]) continue;
                    string field = fa[i].Split('=')[0];
                    byField[field] = byField.TryGetValue(field, out int n) ? n + 1 : 1;
                }
            }
            foreach (KeyValuePair<string, string> kv in sb)
                if (!sa.ContainsKey(kv.Key)) { added++; list.Append("+ ").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n'); }
            File.WriteAllText(System.IO.Path.Combine(TestBotMod.Folder, "boltdiff_" + a + "_" + b + ".txt"), list.ToString());
            return $"{changed} changed" + (byField.Count > 0 ? " (" + string.Join(", ", byField.Select(k => k.Key + " " + k.Value).ToArray()) + ")" : "") +
                   $", {missing} missing, {added} new";
        }

        /// <summary>Hierarchy path with the sibling index where a parent has several children of that name.</summary>
        static string UniquePath(Transform t)
        {
            List<string> parts = new List<string>();
            for (Transform x = t; x != null; x = x.parent)
            {
                string s = x.name;
                Transform p = x.parent;
                if (p != null)
                {
                    int same = 0;
                    for (int i = 0; i < p.childCount && same < 2; i++)
                        if (p.GetChild(i).name == s) same++;
                    if (same > 1) s += "[" + x.GetSiblingIndex() + "]";
                }
                parts.Add(s);
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
