// Full scene snapshot for the MOPR6 equivalence harness (MOPR6\Docs\harness.ru.md, design §10.2).
// Unlike StateSnapshot (active FSMs + rigidbody positions only) it covers every object of the scene, active or not:
//   <path>                    .go     activeSelf, layer, tag
//   <path>                    .cmp    enabled state of every renderer / collider / behaviour on the object
//                                     ("MeshRenderer:1s1,BoxCollider:0,AudioSource:1"; sN = shadow casting mode)
//   <path>                    .joint  every joint: type, connected body (path), break force
//   <path>                    .pose   world position + rotation (objects with a Rigidbody or a PlayMakerFSM)
//   <path>                    .rb     isKinematic, useGravity, detectCollisions, mass, constraints
//   <path>                    .vel    velocity + angular velocity
//   <path>#<fsm>[~k]          .fsm    enabled, RestartOnEnable, active state, Started, Finished
//   <path>#<fsm>[~k]          .<t>.<name>   every variable (t = f i b s v2 v3 r q c go obj mat tex)
//   @global                   .<t>.<name>   PlayMaker global variables
//   <path>@<proxy>[~k]        .list / .table  PlayMakerArrayListProxy / PlayMakerHashTableProxy contents
// Paths are stable across runs: "name" or "name~k" where k is the index among siblings of the same name (sibling
// order; root objects of the same name are ordered by instance id, i.e. creation order).
// Objects of MSCLoader, the TestBot and MOPR6 itself (MOPR_*) are left out (they differ between variants by design).
// Floats are written with "R" in the invariant culture, so the host compares the exact values.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class FullSnapshot
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public sealed class Result
        {
            public int Keys, Objects, Fsms, Errors;
            public float Seconds;
            public string FirstError;
        }

        static readonly Dictionary<int, string> pathCache = new Dictionary<int, string>();
        static readonly Dictionary<string, List<GameObject>> rootsByName = new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
        // Transform instance id -> index among the siblings (or roots) of the same name.
        static readonly Dictionary<int, int> dupIndex = new Dictionary<int, int>();
        static readonly Dictionary<string, int> nameCount = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Writes the snapshot to <paramref name="file"/> (sorted TSV) and returns a summary.</summary>
        public static Result Take(string file, int frame)
        {
            float t0 = Time.realtimeSinceStartup;
            Result res = new Result();
            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>(200000);
            GameObject[] all = BeginPaths();

            foreach (GameObject go in all)
            {
                if (go == null || go.hideFlags != HideFlags.None) continue;
                string path = PathOf(go.transform);
                if (path == null) continue;   // excluded root
                res.Objects++;
                try { Object(go, path, rows); }
                catch (Exception e) { Error(res, path, e); }
            }

            foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || fsm.hideFlags != HideFlags.None || fsm.gameObject.hideFlags != HideFlags.None) continue;
                string path = PathOf(fsm.transform);
                if (path == null) continue;
                res.Fsms++;
                try { Fsm(fsm, path, rows); }
                catch (Exception e) { Error(res, path + "#" + fsm.FsmName, e); }
            }

            try { Variables("@global", FsmVariables.GlobalVariables, rows); }
            catch (Exception e) { Error(res, "@global", e); }

            foreach (MonoBehaviour mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (mb == null || mb.hideFlags != HideFlags.None) continue;
                string type = mb.GetType().Name;
                if (type != "PlayMakerArrayListProxy" && type != "PlayMakerHashTableProxy") continue;
                string path = PathOf(mb.transform);
                if (path == null) continue;
                try { Proxy(mb, type, path, rows); }
                catch (Exception e) { Error(res, path + "@" + type, e); }
            }

            // Duplicate keys (two FSMs or proxies with the same name and reference): numbered in a stable order.
            rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            StringBuilder sb = new StringBuilder(rows.Count * 72);
            sb.Append("#frame\t").Append(frame.ToString(Inv)).Append('\n');
            string last = null;
            int dup = 0;
            foreach (KeyValuePair<string, string> kv in rows)
            {
                string key = kv.Key;
                if (key == last) { dup++; key += "'" + dup.ToString(Inv); }
                else { last = key; dup = 0; }
                sb.Append(key).Append('\t').Append(Clean(kv.Value)).Append('\n');
                res.Keys++;
            }
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            res.Seconds = Time.realtimeSinceStartup - t0;
            EndPaths();
            return res;
        }

        /// <summary>
        /// Prepares stable paths for the objects of the scene (also used by Inventory) and returns every GameObject,
        /// active or not. <see cref="PathOf"/> is valid until <see cref="EndPaths"/>.
        /// </summary>
        internal static GameObject[] BeginPaths()
        {
            pathCache.Clear();
            rootsByName.Clear();
            dupIndex.Clear();
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (GameObject go in all)
            {
                if (go == null || go.hideFlags != HideFlags.None || go.transform.parent != null) continue;
                if (!rootsByName.TryGetValue(go.name, out List<GameObject> list)) rootsByName[go.name] = list = new List<GameObject>();
                list.Add(go);
            }
            foreach (List<GameObject> list in rootsByName.Values)
            {
                if (list.Count > 1) list.Sort((a, b) => a.GetInstanceID().CompareTo(b.GetInstanceID()));
                for (int i = 0; i < list.Count; i++) dupIndex[list[i].transform.GetInstanceID()] = i;
            }
            return all;
        }

        internal static void EndPaths()
        {
            pathCache.Clear();
            rootsByName.Clear();
            dupIndex.Clear();
        }

        static void Error(Result res, string where, Exception e)
        {
            res.Errors++;
            if (res.FirstError == null) res.FirstError = where + ": " + e.GetType().Name + ": " + e.Message;
        }

        static bool Excluded(string rootName) =>
            rootName.StartsWith("MSCLoader", StringComparison.Ordinal) || rootName.StartsWith("MOPR6", StringComparison.Ordinal) || rootName.StartsWith("MOPR_", StringComparison.Ordinal) ||
            rootName.StartsWith("MOPR_TestBot", StringComparison.Ordinal) || rootName.StartsWith("TestBot", StringComparison.Ordinal);

        /// <summary>Stable path ("a/b~1/c"), or null for objects that are left out.</summary>
        internal static string PathOf(Transform t)
        {
            int id = t.GetInstanceID();
            if (pathCache.TryGetValue(id, out string cached)) return cached;
            string path;
            Transform p = t.parent;
            string name = t.name;
            if (p == null)
            {
                if (Excluded(name)) path = null;
                else
                {
                    dupIndex.TryGetValue(id, out int k);
                    path = k > 0 ? name + "~" + k.ToString(Inv) : name;
                }
            }
            else
            {
                string parent = PathOf(p);
                if (parent == null) path = null;
                else
                {
                    if (!dupIndex.TryGetValue(id, out int k))
                    {
                        IndexChildren(p);
                        dupIndex.TryGetValue(id, out k);
                    }
                    path = parent + "/" + (k > 0 ? name + "~" + k.ToString(Inv) : name);
                }
            }
            pathCache[id] = path;
            return path;
        }

        // Once per parent: the index of every child among the earlier siblings of the same name.
        static void IndexChildren(Transform p)
        {
            nameCount.Clear();
            int n = p.childCount;
            for (int i = 0; i < n; i++)
            {
                Transform c = p.GetChild(i);
                string cn = c.name;
                nameCount.TryGetValue(cn, out int k);
                nameCount[cn] = k + 1;
                dupIndex[c.GetInstanceID()] = k;
            }
        }

        // ----- Objects ----------------------------------------------------------------------------------

        static readonly List<Component> comps = new List<Component>();

        static void Object(GameObject go, string path, List<KeyValuePair<string, string>> rows)
        {
            rows.Add(Row(path + ".go", (go.activeSelf ? "1" : "0") + "," + go.layer.ToString(Inv) + "," + go.tag));

            Component[] cs = go.GetComponents<Component>();
            StringBuilder cmp = null, joints = null;
            bool pose = false;
            Rigidbody rb = null;
            foreach (Component c in cs)
            {
                if (c == null || c is Transform) continue;
                // MOPR6's own helper components (the sleep sentinel) are not part of the game's state, like MOPR_* objects.
                string ns = c.GetType().Namespace;
                if (ns != null && ns.StartsWith("MOPR6", StringComparison.Ordinal)) continue;
                string flag = null;
                if (c is Renderer r) flag = (r.enabled ? "1" : "0") + "s" + ((int)r.shadowCastingMode).ToString(Inv);
                else if (c is Collider col) flag = col.enabled ? "1" : "0";
                else if (c is Behaviour b) flag = b.enabled ? "1" : "0";
                if (c is PlayMakerFSM) pose = true;
                if (c is Rigidbody body) { rb = body; pose = true; }
                if (c is Joint j)
                {
                    if (joints == null) joints = new StringBuilder();
                    else joints.Append(';');
                    joints.Append(c.GetType().Name).Append('>')
                          .Append(j.connectedBody != null ? PathOf(j.connectedBody.transform) ?? "?" : "world")
                          .Append(',').Append(F(j.breakForce));
                }
                if (flag == null) continue;
                if (cmp == null) cmp = new StringBuilder();
                else cmp.Append(',');
                cmp.Append(c.GetType().Name).Append(':').Append(flag);
            }
            if (cmp != null) rows.Add(Row(path + ".cmp", cmp.ToString()));
            if (joints != null) rows.Add(Row(path + ".joint", joints.ToString()));
            if (pose)
            {
                Transform t = go.transform;
                rows.Add(Row(path + ".pose", V(t.position) + ";" + Q(t.rotation)));
            }
            if (rb != null)
            {
                // Not IsSleeping(): PhysX puts resting bodies to sleep at slightly different moments in every run.
                rows.Add(Row(path + ".rb", (rb.isKinematic ? "k1" : "k0") + (rb.useGravity ? ",g1" : ",g0") + (rb.detectCollisions ? ",d1" : ",d0") +
                                           ",m" + F(rb.mass) + ",c" + ((int)rb.constraints).ToString(Inv)));
                rows.Add(Row(path + ".vel", V(rb.velocity) + ";" + V(rb.angularVelocity)));
            }
        }

        // ----- FSMs -------------------------------------------------------------------------------------

        static void Fsm(PlayMakerFSM pm, string path, List<KeyValuePair<string, string>> rows)
        {
            string key = path + "#" + pm.FsmName;
            Fsm f = pm.Fsm;
            if (f == null)
            {
                rows.Add(Row(key + ".fsm", (pm.enabled ? "1" : "0") + ",nofsm"));
                return;
            }
            rows.Add(Row(key + ".fsm", (pm.enabled ? "1" : "0") + (f.RestartOnEnable ? ",r1" : ",r0") + "," + (f.ActiveStateName ?? "<none>") +
                                       (f.Started ? ",st1" : ",st0") + (f.Finished ? ",fi1" : ",fi0")));
            Variables(key, pm.FsmVariables, rows);
        }

        static void Variables(string key, FsmVariables vars, List<KeyValuePair<string, string>> rows)
        {
            if (vars == null) return;
            foreach (FsmFloat v in vars.FloatVariables) rows.Add(Row(key + ".f." + v.Name, F(v.Value)));
            foreach (FsmInt v in vars.IntVariables) rows.Add(Row(key + ".i." + v.Name, v.Value.ToString(Inv)));
            foreach (FsmBool v in vars.BoolVariables) rows.Add(Row(key + ".b." + v.Name, v.Value ? "1" : "0"));
            foreach (FsmString v in vars.StringVariables) rows.Add(Row(key + ".s." + v.Name, v.Value ?? "<null>"));
            foreach (FsmVector2 v in vars.Vector2Variables) rows.Add(Row(key + ".v2." + v.Name, F(v.Value.x) + "," + F(v.Value.y)));
            foreach (FsmVector3 v in vars.Vector3Variables) rows.Add(Row(key + ".v3." + v.Name, V(v.Value)));
            foreach (FsmRect v in vars.RectVariables)
                rows.Add(Row(key + ".r." + v.Name, F(v.Value.x) + "," + F(v.Value.y) + "," + F(v.Value.width) + "," + F(v.Value.height)));
            foreach (FsmQuaternion v in vars.QuaternionVariables) rows.Add(Row(key + ".q." + v.Name, Q(v.Value)));
            foreach (FsmColor v in vars.ColorVariables)
                rows.Add(Row(key + ".c." + v.Name, F(v.Value.r) + "," + F(v.Value.g) + "," + F(v.Value.b) + "," + F(v.Value.a)));
            foreach (FsmGameObject v in vars.GameObjectVariables) rows.Add(Row(key + ".go." + v.Name, ObjectRef(v.Value)));
            foreach (FsmObject v in vars.ObjectVariables) rows.Add(Row(key + ".obj." + v.Name, ObjectRef(v.Value)));
            foreach (FsmMaterial v in vars.MaterialVariables) rows.Add(Row(key + ".mat." + v.Name, ObjectRef(v.Value)));
            foreach (FsmTexture v in vars.TextureVariables) rows.Add(Row(key + ".tex." + v.Name, ObjectRef(v.Value)));
        }

        static string ObjectRef(UnityEngine.Object o)
        {
            if (o == null) return "<null>";
            if (o is GameObject go) return go.hideFlags == HideFlags.None ? PathOf(go.transform) ?? "<excluded>" : "<asset>" + go.name;
            if (o is Component c) return (PathOf(c.transform) ?? "<excluded>") + ":" + c.GetType().Name;
            return o.GetType().Name + ":" + o.name;
        }

        // ----- Array list / hash table proxies (Assembly-CSharp types, read by reflection) ---------------

        static void Proxy(MonoBehaviour mb, string type, string path, List<KeyValuePair<string, string>> rows)
        {
            Type t = mb.GetType();
            string reference = t.GetField("referenceName", AnyInstance)?.GetValue(mb) as string ?? "";
            string key = path + "@" + reference;
            if (type == "PlayMakerArrayListProxy")
            {
                ArrayList list = t.GetField("arrayList", AnyInstance)?.GetValue(mb) as ArrayList;
                StringBuilder sb = new StringBuilder();
                if (list == null) sb.Append("<null>");
                else for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append('|'); sb.Append(Value(list[i])); }
                rows.Add(Row(key + ".list", sb.ToString()));
            }
            else
            {
                Hashtable table = t.GetField("hashTable", AnyInstance)?.GetValue(mb) as Hashtable;
                if (table == null) { rows.Add(Row(key + ".table", "<null>")); return; }
                List<string> items = new List<string>(table.Count);
                foreach (DictionaryEntry e in table) items.Add(Value(e.Key) + "=" + Value(e.Value));
                items.Sort(StringComparer.Ordinal);
                rows.Add(Row(key + ".table", string.Join("|", items.ToArray())));
            }
        }

        static string Value(object o)
        {
            if (o == null) return "<null>";
            if (o is float f) return F(f);
            if (o is Vector3 v) return V(v);
            if (o is Quaternion q) return Q(q);
            if (o is UnityEngine.Object uo) return ObjectRef(uo);
            return Convert.ToString(o, Inv);
        }

        // ----- Formatting ---------------------------------------------------------------------------------

        static KeyValuePair<string, string> Row(string key, string value) => new KeyValuePair<string, string>(key, value);
        static string F(float f) => f.ToString("R", Inv);
        static string V(Vector3 v) => F(v.x) + "," + F(v.y) + "," + F(v.z);
        static string Q(Quaternion q) => F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w);
        static string Clean(string s) => s.IndexOf('\n') < 0 && s.IndexOf('\t') < 0 && s.IndexOf('\r') < 0
            ? s : s.Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ');
    }
}
