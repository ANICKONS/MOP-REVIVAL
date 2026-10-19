// Satsuma parts snapshot (B1: "parts fall off, bolts reset"). Unlike StateSnapshot it also reads INACTIVE objects
// and only looks at the car, its parts database and loose parts:
//   - every PlayMaker FSM whose root is the Satsuma, "Database" or a loose part (tag PART): enabled, state and
//     all float/int/bool/string variables (bolt tightness, Installed, Bolted, Damaged...);
//   - every PlayMakerArrayListProxy there (the per-bolt stages that BoltCheck saves with ArrayListEasySave);
//   - every part (an object with a "BoltCheck" or "Removal" FSM): its parent ("where" - attached or fallen off),
//     its position in the car's frame while attached, and its joint.
// Keys use the object's hierarchy path when it was first seen, so a part that falls off keeps its key (its
// "where" value changes) and two runs of the same save (vanilla / MOPR) produce comparable keys.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class PartSnapshot
    {
        const string CarName = "SATSUMA(557kg, 248)";
        const float PositionTolerance = 0.03f;   // m, in the car's frame (springs and the body settle a little)

        // instance ID -> stable name (hierarchy path when first seen). Valid for one scene.
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Reset() { names.Clear(); used.Clear(); }

        static string Name(Transform t)
        {
            int id = t.GetInstanceID();
            if (names.TryGetValue(id, out string n)) return n;
            n = UniquePath(t);
            while (used.Contains(n)) n += "'";
            names[id] = n;
            used.Add(n);
            return n;
        }

        /// <summary>
        /// Hierarchy path; a level whose parent has several children of that name gets its sibling index
        /// ("Bolts/BoltPM[3]"), so the name does not depend on the order objects are enumerated in.
        /// </summary>
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

        static bool Included(Transform t, Transform car)
        {
            Transform root = t.root;
            if (car != null && root == car) return true;
            if (root.name == "Database") return true;
            return root.gameObject.tag == "PART";
        }

        static bool IsPart(GameObject go)
        {
            foreach (PlayMakerFSM f in go.GetComponents<PlayMakerFSM>())
                if (f != null && (f.FsmName == "BoltCheck" || f.FsmName == "Removal")) return true;
            return false;
        }

        public static Dictionary<string, string> Take(out string summary)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            GameObject carGo = GameObject.Find(CarName);
            Transform car = carGo != null ? carGo.transform : null;
            int fsms = 0, lists = 0, parts = 0, attached = 0, loose = 0;

            // FSMs, in a fixed order so duplicate FSM names on one object get the same index every time.
            List<PlayMakerFSM> all = Resources.FindObjectsOfTypeAll<PlayMakerFSM>()
                .Where(f => f != null && f.hideFlags == HideFlags.None && f.gameObject.hideFlags == HideFlags.None && Included(f.transform, car))
                .ToList();
            Dictionary<string, int> dup = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (PlayMakerFSM fsm in all.OrderBy(f => f.GetInstanceID()))
            {
                string baseKey = Name(fsm.transform) + "#" + fsm.FsmName;
                dup.TryGetValue(baseKey, out int k);
                dup[baseKey] = k + 1;
                string key = k == 0 ? baseKey : baseKey + "#" + k;
                fsms++;
                try
                {
                    d[key + "|en"] = fsm.enabled ? "1" : "0";
                    d[key + "|state"] = fsm.ActiveStateName ?? "";
                    FsmVariables v = fsm.FsmVariables;
                    foreach (FsmFloat x in v.FloatVariables) d[key + "|f." + x.Name] = x.Value.ToString("0.###", Inv);
                    foreach (FsmInt x in v.IntVariables) d[key + "|i." + x.Name] = x.Value.ToString(Inv);
                    foreach (FsmBool x in v.BoolVariables) d[key + "|b." + x.Name] = x.Value ? "1" : "0";
                    foreach (FsmString x in v.StringVariables) d[key + "|s." + x.Name] = x.Value ?? "";
                }
                catch { }
            }

            // Array list proxies (Assembly-CSharp type, read by reflection: the bot does not reference the game code).
            foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || mb.hideFlags != HideFlags.None || mb.GetType().Name != "PlayMakerArrayListProxy") continue;
                if (!Included(mb.transform, car)) continue;
                try
                {
                    Type t = mb.GetType();
                    string reference = t.GetField("referenceName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mb) as string ?? "";
                    ArrayList list = t.GetField("arrayList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(mb) as ArrayList;
                    string key = Name(mb.transform) + "@" + reference;
                    while (d.ContainsKey(key)) key += "'";
                    d[key] = list == null ? "null" : string.Join(",", list.Cast<object>().Select(o => o == null ? "null" : Convert.ToString(o, Inv)).ToArray());
                    lists++;
                }
                catch { }
            }

            // Parts: where they are, where in the car, their joint.
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.hideFlags != HideFlags.None || !IsPart(go)) continue;
                Transform t = go.transform;
                bool inCar = car != null && t.root == car;
                if (!inCar && go.tag != "PART" && t.root.name != "Database") continue;
                parts++;
                string n = Name(t);
                d["where|" + n] = (t.parent != null ? BotRunner.PathOf(t.parent) : "(root)") + (go.activeSelf ? "" : " [off]");
                if (inCar)
                {
                    attached++;
                    Vector3 p = car.InverseTransformPoint(t.position);
                    d["pos|" + n] = p.x.ToString("0.00", Inv) + "," + p.y.ToString("0.00", Inv) + "," + p.z.ToString("0.00", Inv);
                }
                else loose++;

                Joint j = go.GetComponent<Joint>();
                d["joint|" + n] = j == null ? "none" : j.GetType().Name + "->" + (j.connectedBody != null ? j.connectedBody.name : "world");
            }

            summary = $"{fsms} FSMs, {lists} array lists, {parts} parts ({attached} in the car, {loose} loose)";
            return d;
        }

        public static void Write(Dictionary<string, string> snap, string path)
        {
            StringBuilder sb = new StringBuilder(snap.Count * 72);
            foreach (string k in snap.Keys.OrderBy(k => k, StringComparer.Ordinal))
                sb.Append(k).Append('\t').Append(snap[k].Replace('\n', ' ').Replace('\t', ' ')).Append('\n');
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>Category of a key, for the summary: where, pos, joint, list, state, enabled, var.</summary>
        static string Category(string key)
        {
            if (key.StartsWith("where|")) return "where";
            if (key.StartsWith("pos|")) return "pos";
            if (key.StartsWith("joint|")) return "joint";
            if (key.Contains("@")) return "list";
            if (key.EndsWith("|state")) return "state";
            if (key.EndsWith("|en")) return "enabled";
            return "var";
        }

        static bool Same(string key, string a, string b)
        {
            if (a == b) return true;
            if (!key.StartsWith("pos|")) return false;
            string[] p = a.Split(','), q = b.Split(',');
            if (p.Length != 3 || q.Length != 3) return false;
            for (int i = 0; i < 3; i++)
                if (Math.Abs(float.Parse(p[i], Inv) - float.Parse(q[i], Inv)) > PositionTolerance) return false;
            return true;
        }

        /// <summary>
        /// Differences between two snapshots, one line each ("category  key  a -> b"), plus counts per category.
        /// </summary>
        public static string Compare(Dictionary<string, string> a, Dictionary<string, string> b, out string summary, out int changed)
        {
            SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> diffs = new List<KeyValuePair<string, string>>();
            void Add(string k, string va, string vb)
            {
                string c = Category(k);
                counts.TryGetValue(c, out int n);
                counts[c] = n + 1;
                diffs.Add(new KeyValuePair<string, string>(k, c.PadRight(8) + k + "\t" + (va ?? "(missing)") + " -> " + (vb ?? "(missing)")));
            }
            // Only the differences are sorted (a full sorted union of ~13k keys took 1.5 s in the game's Mono).
            foreach (KeyValuePair<string, string> kv in a)
            {
                if (b.TryGetValue(kv.Key, out string vb)) { if (!Same(kv.Key, kv.Value, vb)) Add(kv.Key, kv.Value, vb); }
                else Add(kv.Key, kv.Value, null);
            }
            foreach (KeyValuePair<string, string> kv in b)
                if (!a.ContainsKey(kv.Key)) Add(kv.Key, null, kv.Value);
            diffs.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
            List<string> lines = diffs.Select(d => d.Value).ToList();
            changed = lines.Count;
            summary = changed == 0 ? "identical" : changed + " differences (" + string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value).ToArray()) + ")";
            return summary + "\n" + string.Join("\n", lines.ToArray()) + "\n";
        }
    }
}
