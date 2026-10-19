// Scripted game manipulation for repro scenarios: resolve objects by full hierarchy path (also inactive ones
// and names containing '/', e.g. "GIFU(750/450psi)"), set FSM variables, send FSM events, (de)activate and
// move objects, and read values for the "watch" step.
//
// Probe syntax (watch step), one value per probe, "label=" prefix optional:
//   path|Fsm|Var         FSM variable (float, int, bool, string, gameObject, vector3)
//   path|Fsm|@state      active state name of the FSM
//   path|#Component.member  public or private field / property of a component (by type name)
//   path|@active         activeInHierarchy
//   path|@self           activeSelf
//   path|@parent         hierarchy path of the parent ("(root)" at the scene root)
//   path|@pos            world position
//   path|@in:OTHER       position in the frame of the object OTHER (local x,y,z)
//   |@player             distance of the player to path (path given before the bar)
//   global|Var           global FSM variable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class GameOps
    {
        static readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>();
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void ClearCache() => cache.Clear();

        /// <summary>Exact hierarchy path, active or not. Null if not found.</summary>
        public static GameObject Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            GameObject go;
            if (cache.TryGetValue(path, out go) && go != null) return go;

            if (path.StartsWith("name:"))
            {
                // First scene object with exactly this name, active ones preferred.
                string name = path.Substring(5);
                GameObject inactive = null;
                go = null;
                foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (candidate == null || candidate.hideFlags != HideFlags.None || candidate.name != name) continue;
                    if (candidate.activeInHierarchy) { go = candidate; break; }
                    if (inactive == null) inactive = candidate;
                }
                go = go ?? inactive;
                if (go != null) cache[path] = go;
                return go;
            }

            go = null;
            // Fast path for simple names; Unity splits on '/', so names containing '/' need the search.
            try { go = GameObject.Find(path); } catch { }
            if (go == null || BotRunner.PathOf(go.transform) != path)
            {
                go = null;
                foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (candidate == null || candidate.hideFlags != HideFlags.None) continue;
                    if (!path.EndsWith(candidate.name)) continue; // cheap prefilter (the name may itself contain '/')
                    if (BotRunner.PathOf(candidate.transform) == path) { go = candidate; break; }
                }
            }
            if (go != null) cache[path] = go;
            return go;
        }

        public static PlayMakerFSM Fsm(GameObject go, string fsmName)
        {
            if (go == null) return null;
            return go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == fsmName);
        }

        static NamedVariable FindVariable(FsmVariables vars, string name)
        {
            if (vars == null) return null;
            return (NamedVariable)vars.FindFsmFloat(name) ?? (NamedVariable)vars.FindFsmBool(name) ?? (NamedVariable)vars.FindFsmInt(name)
                ?? (NamedVariable)vars.FindFsmString(name) ?? (NamedVariable)vars.FindFsmGameObject(name) ?? (NamedVariable)vars.FindFsmVector3(name);
        }

        /// <summary>Sets an FSM (or global, when path is "global") variable from text. Returns an error or null.</summary>
        public static string SetVariable(string path, string fsmName, string varName, string value)
        {
            FsmVariables vars;
            if (path == "global") vars = FsmVariables.GlobalVariables;
            else
            {
                GameObject go = Resolve(path);
                if (go == null) return "object not found: " + path;
                PlayMakerFSM fsm = Fsm(go, fsmName);
                if (fsm == null) return "FSM not found: " + fsmName + " on " + path;
                vars = fsm.FsmVariables;
            }

            NamedVariable v = FindVariable(vars, varName);
            if (v == null) return "variable not found: " + varName;
            CultureInfo ci = CultureInfo.InvariantCulture;
            try
            {
                if (v is FsmFloat f) f.Value = float.Parse(value, ci);
                else if (v is FsmBool b) b.Value = bool.Parse(value);
                else if (v is FsmInt i) i.Value = int.Parse(value, ci);
                else if (v is FsmString s) s.Value = value;
                else if (v is FsmGameObject g) g.Value = string.IsNullOrEmpty(value) || value == "null" ? null : Resolve(value);
                else if (v is FsmVector3 v3)
                {
                    string[] p = value.Split(',');
                    v3.Value = new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci));
                }
                else return "unsupported variable type " + v.GetType().Name;
            }
            catch (Exception e) { return "bad value '" + value + "': " + e.Message; }
            return null;
        }

        public static string SendEvent(string path, string fsmName, string eventName)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            PlayMakerFSM fsm = Fsm(go, fsmName);
            if (fsm == null) return "FSM not found: " + fsmName;
            fsm.SendEvent(eventName);
            return null;
        }

        public static string SetActive(string path, bool active)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            go.SetActive(active);
            return null;
        }

        /// <summary>
        /// Moves <paramref name="path"/> so that its descendant <paramref name="anchor"/> (or itself) lands on
        /// <paramref name="target"/> plus <paramref name="offset"/>. Rigidbodies of the moved object are woken up.
        /// </summary>
        public static string Align(string path, string anchor, string target, Vector3 offset)
        {
            GameObject go = Resolve(path);
            GameObject a = string.IsNullOrEmpty(anchor) ? go : Resolve(anchor);
            GameObject t = Resolve(target);
            if (go == null || a == null || t == null) return "align: object not found (" + path + " / " + anchor + " / " + target + ")";
            // Use the collider centre where there is one: a transform pivot often sits on a joint shared with a neighbour.
            Collider tc = t.GetComponent<Collider>();
            Vector3 targetPos = tc != null ? tc.bounds.center : t.transform.position;
            Collider ac = a.GetComponent<Collider>();
            Vector3 anchorPos = ac != null ? ac.bounds.center : a.transform.position;
            Vector3 delta = targetPos + offset - anchorPos;
            go.transform.position += delta;
            foreach (Rigidbody rb in t.GetComponentsInChildren<Rigidbody>()) rb.WakeUp();
            foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>()) rb.WakeUp();
            return null;
        }

        /// <summary>Sets a field or property of a component: spec "Component.member", value as text.</summary>
        public static string SetMember(string path, string spec, string value)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            int dot = spec.IndexOf('.');
            if (dot < 0) return "bad member spec " + spec;
            string typeName = spec.Substring(0, dot), member = spec.Substring(dot + 1);
            Component c = go.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().Name == typeName);
            if (c == null) return "no component " + typeName;
            for (Type t = c.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(member, Any | BindingFlags.DeclaredOnly);
                if (f != null) { f.SetValue(c, ParseAs(value, f.FieldType)); return null; }
                PropertyInfo p = t.GetProperty(member, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite) { p.SetValue(c, ParseAs(value, p.PropertyType), null); return null; }
            }
            return "no member " + member;
        }

        static object ParseAs(string value, Type type)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (type == typeof(bool)) return bool.Parse(value);
            if (type == typeof(float)) return float.Parse(value, ci);
            if (type == typeof(int)) return int.Parse(value, ci);
            if (type == typeof(string)) return value;
            if (type == typeof(Vector3))
            {
                string[] p = value.Split(',');
                return new Vector3(float.Parse(p[0], ci), float.Parse(p[1], ci), float.Parse(p[2], ci));
            }
            throw new NotSupportedException(type.Name);
        }

        /// <summary>Places an object at Target + offset (Target empty: its own position + offset). Zeroes rigidbody velocity.</summary>
        public static string Place(string path, string target, Vector3 offset, bool local = false)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            Vector3 basePos = go.transform.position;
            if (!string.IsNullOrEmpty(target))
            {
                GameObject t = Resolve(target);
                if (t == null) return "target not found: " + target;
                basePos = t.transform.position;
                if (local) offset = t.transform.rotation * offset; // offset in the target's own frame
            }
            go.transform.position = basePos + offset;
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            return null;
        }

        /// <summary>
        /// Sets an object down on the ground at world x/z: a ray from <paramref name="from"/> straight down (the object's
        /// own colliders ignored), the object upright (its yaw kept) <paramref name="clearance"/> m above the hit.
        /// </summary>
        public static string PlaceOnGround(string path, Vector3 from, float clearance, out string where)
        {
            where = null;
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            Transform root = go.transform.root;
            RaycastHit[] hits = Physics.RaycastAll(from, Vector3.down, 500f);
            float best = float.MaxValue;
            RaycastHit ground = default(RaycastHit);
            foreach (RaycastHit h in hits)
            {
                if (h.collider == null || h.collider.isTrigger || h.collider.transform.root == root) continue;
                if (h.distance < best) { best = h.distance; ground = h; }
            }
            if (best == float.MaxValue) return "no ground below " + Format(from);
            go.transform.rotation = Quaternion.Euler(0f, go.transform.eulerAngles.y, 0f);
            go.transform.position = ground.point + Vector3.up * clearance;
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            where = Format(go.transform.position) + " on " + BotRunner.PathOf(ground.collider.transform);
            return null;
        }

        /// <summary>Calls a parameterless method: "Namespace.Type:StaticMember.Method" or "Namespace.Type:Method" (static).</summary>
        public static string Call(string spec)
        {
            int colon = spec.IndexOf(':');
            if (colon < 0) return "bad call spec " + spec;
            string typeName = spec.Substring(0, colon);
            string[] chain = spec.Substring(colon + 1).Split('.');
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(typeName); } catch { return null; } })
                .FirstOrDefault(t => t != null);
            if (type == null) return "type not found: " + typeName;

            object target = null;
            Type current = type;
            for (int i = 0; i < chain.Length - 1; i++)
            {
                BindingFlags flags = (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo p = current.GetProperty(chain[i], flags);
                FieldInfo f = p == null ? current.GetField(chain[i], flags) : null;
                if (p == null && f == null) return "member not found: " + chain[i];
                target = p != null ? p.GetValue(target, null) : f.GetValue(target);
                if (target == null) return chain[i] + " is null";
                current = target.GetType();
            }
            MethodInfo m = current.GetMethod(chain[chain.Length - 1], (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (m == null) return "method not found: " + chain[chain.Length - 1];
            m.Invoke(target, null);
            return null;
        }

        /// <summary>
        /// Returns a setter for a static bool property or field, spec "Namespace.Type.Member" (public or not).
        /// Used by abtoggle Var "staticbool" for switches a mod exposes for A/B timing.
        /// </summary>
        public static string StaticBoolSetter(string spec, out Action<bool> setter)
        {
            setter = null;
            int dot = spec.LastIndexOf('.');
            if (dot < 0) return "bad static spec " + spec;
            string typeName = spec.Substring(0, dot), member = spec.Substring(dot + 1);
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(typeName, false); } catch { return null; } })
                .FirstOrDefault(t => t != null);
            if (type == null) return "type not found: " + typeName;
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo p = type.GetProperty(member, S);
            if (p != null && p.PropertyType == typeof(bool) && p.CanWrite) { setter = b => p.SetValue(null, b, null); return null; }
            FieldInfo f = type.GetField(member, S);
            if (f != null && f.FieldType == typeof(bool)) { setter = b => f.SetValue(null, b); return null; }
            return "no writable static bool " + member;
        }

        // ----- Rendering attribution (in memory only; restored by the matching "on" call) ---------------

        static readonly Dictionary<Renderer, bool> savedEnabled = new Dictionary<Renderer, bool>();
        static readonly Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode> savedShadows = new Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode>();

        /// <summary>Disables every renderer under path (remembering the state) or restores them. Returns the count.</summary>
        public static string SetRenderers(string path, bool enabled, out int count)
        {
            count = 0;
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!enabled)
                {
                    if (!savedEnabled.ContainsKey(r)) savedEnabled[r] = r.enabled;
                    r.enabled = false;
                    count++;
                }
                else if (savedEnabled.TryGetValue(r, out bool was))
                {
                    r.enabled = was;
                    savedEnabled.Remove(r);
                    count++;
                }
            }
            return null;
        }

        /// <summary>Turns shadow casting off for every renderer under path (remembering it) or restores it.</summary>
        public static string SetShadowCasting(string path, bool enabled, out int count)
        {
            count = 0;
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!enabled)
                {
                    if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off) continue;
                    if (!savedShadows.ContainsKey(r)) savedShadows[r] = r.shadowCastingMode;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    count++;
                }
                else if (savedShadows.TryGetValue(r, out UnityEngine.Rendering.ShadowCastingMode was))
                {
                    r.shadowCastingMode = was;
                    savedShadows.Remove(r);
                    count++;
                }
            }
            return null;
        }

        /// <summary>Sets a QualitySettings property (shadowDistance, shadowCascades, pixelLightCount, lodBias...). Returns the previous value.</summary>
        static readonly Dictionary<string, object> savedQuality = new Dictionary<string, object>();

        /// <summary>
        /// Sets a QualitySettings property (shadowDistance, shadowCascades, pixelLightCount, lodBias...).
        /// Value "restore" puts back the value it had before the first change.
        /// </summary>
        public static string SetQuality(string name, string value, out string previous)
        {
            previous = null;
            PropertyInfo p = typeof(QualitySettings).GetProperty(name, BindingFlags.Static | BindingFlags.Public);
            if (p == null) return "no QualitySettings." + name;
            object current = p.GetValue(null, null);
            previous = Format(current);
            if (value == "restore")
            {
                if (!savedQuality.TryGetValue(name, out object original)) return "nothing to restore";
                p.SetValue(null, original, null);
                savedQuality.Remove(name);
                return null;
            }
            if (!savedQuality.ContainsKey(name)) savedQuality[name] = current;
            p.SetValue(null, ParseAs(value, p.PropertyType), null);
            return null;
        }

        // ----- Unity message dispatch cost -----------------------------------------------------------------

        /// <summary>Empty Update + LateUpdate: measures what Unity charges per enabled MonoBehaviour per frame.</summary>
        public class DispatchDummy : MonoBehaviour
        {
            void Update() { }
            void LateUpdate() { }
        }

        static GameObject dummyRoot;

        /// <summary>Creates count dummies (count 0 destroys them).</summary>
        public static void SetDummies(int count)
        {
            if (dummyRoot != null) UnityEngine.Object.Destroy(dummyRoot);
            dummyRoot = null;
            if (count <= 0) return;
            dummyRoot = new GameObject("MOPR_TestBot_Dummies");
            for (int i = 0; i < count; i++)
            {
                GameObject g = new GameObject("d" + i);
                g.transform.parent = dummyRoot.transform;
                g.AddComponent<DispatchDummy>();
            }
        }

        // ----- Camera attribution: every enabled camera whose object name contains one of the terms --------

        static readonly List<Camera> switchedCameras = new List<Camera>();

        /// <summary>
        /// terms = "RearViewMirrorCam,LeftSideMirrorCam". enabled = false switches the matching enabled cameras off
        /// (remembered); true switches exactly those back on.
        /// </summary>
        public static string SetCameras(string terms, bool enabled, out int count)
        {
            count = 0;
            if (!enabled)
            {
                switchedCameras.Clear();
                string[] t = (terms ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                foreach (Camera c in Camera.allCameras)
                {
                    if (c == null || !c.enabled || !t.Any(x => c.name.Contains(x))) continue;
                    c.enabled = false;
                    switchedCameras.Add(c);
                }
                count = switchedCameras.Count;
                return count == 0 ? "no enabled camera matches " + terms : null;
            }
            foreach (Camera c in switchedCameras)
                if (c != null) { c.enabled = true; count++; }
            switchedCameras.Clear();
            return null;
        }

        // ----- Chassis attribution: colliders under a path -------------------------------------------------

        static readonly Dictionary<Collider, bool> savedColliders = new Dictionary<Collider, bool>();

        public static string SetColliders(string path, bool enabled, out int count)
        {
            count = 0;
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                if (c == null) continue;
                if (!enabled)
                {
                    if (!c.enabled) continue;
                    savedColliders[c] = true;
                    c.enabled = false;
                    count++;
                }
                else if (savedColliders.ContainsKey(c))
                {
                    c.enabled = true;
                    savedColliders.Remove(c);
                    count++;
                }
            }
            return null;
        }

        /// <summary>
        /// Histogram of the running FSMs under path: FSM name, active state and its per-frame actions,
        /// most common first. Shows what hundreds of FSMs are doing every frame.
        /// </summary>
        public static string FsmHistogram(string path, int max)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            int running = 0;
            foreach (PlayMakerFSM f in go.GetComponentsInChildren<PlayMakerFSM>(false))
            {
                if (f == null || !f.enabled || f.Fsm == null || f.Fsm.Finished) continue;
                running++;
                HutongGames.PlayMaker.FsmState st = f.Fsm.ActiveState;
                string actions = "";
                if (st != null && st.Actions != null)
                {
                    List<string> names = new List<string>();
                    foreach (HutongGames.PlayMaker.FsmStateAction a in st.Actions)
                    {
                        if (a == null || !a.Enabled) continue;
                        FieldInfo ef = a.GetType().GetField("everyFrame", Any);
                        bool every = ef != null && ef.FieldType == typeof(bool) && (bool)ef.GetValue(a);
                        names.Add(a.GetType().Name + (every ? "*" : ""));
                    }
                    actions = string.Join(",", names.ToArray());
                }
                string key = f.FsmName + " [" + (st != null ? st.Name : "-") + "] " + actions;
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(running).Append(" running FSMs, ").Append(counts.Count).Append(" distinct (FSM [state] actions, * = every frame)");
            foreach (var kv in counts.OrderByDescending(k => k.Value).Take(max > 0 ? max : 40))
                sb.Append("\n    ").Append(kv.Value.ToString().PadLeft(4)).Append("  ").Append(kv.Key);
            return sb.ToString();
        }

        /// <summary>Histogram of the action types in ALL states of every FSM under path (active or not).</summary>
        public static string ActionTypes(string path)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            int fsms = 0;
            foreach (PlayMakerFSM f in go.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f == null || f.Fsm == null) continue;
                fsms++;
                foreach (HutongGames.PlayMaker.FsmState st in f.FsmStates)
                {
                    // Never force-load actions: LoadActions throws on FSMs that were never initialized (inactive objects).
                    if (st == null || !st.ActionsLoaded || st.Actions == null)
                    {
                        counts["(actions not loaded)"] = counts.TryGetValue("(actions not loaded)", out int u) ? u + 1 : 1;
                        continue;
                    }
                    foreach (HutongGames.PlayMaker.FsmStateAction a in st.Actions)
                    {
                        if (a == null) continue;
                        string n = a.GetType().Name;
                        counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
                    }
                }
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(fsms).Append(" FSMs, ").Append(counts.Count).Append(" action types");
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                sb.Append("\n    ").Append(kv.Value.ToString().PadLeft(5)).Append("  ").Append(kv.Key);
            return sb.ToString();
        }

        /// <summary>Enabled lights within radius of the player: type, range, intensity, shadows, render mode.</summary>
        public static string Lights(Transform player, float radius)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                float d = player != null ? Vector3.Distance(player.position, l.transform.position) : 0f;
                if (l.type != LightType.Directional && d > radius) continue;
                n++;
                sb.Append($"\n    {BotRunner.PathOf(l.transform)}: {l.type}, d {d:0} m, range {l.range:0.#}, intensity {l.intensity:0.##}, " +
                          $"shadows {l.shadows}, mode {l.renderMode}, mask {l.cullingMask}");
            }
            return n + " lights" + sb;
        }

        // ----- Render baking prototype (measurement only) -----------------------------------------------

        static readonly List<Renderer> bakedOriginals = new List<Renderer>();
        static readonly List<GameObject> bakedObjects = new List<GameObject>();

        /// <summary>
        /// Combines the enabled MeshRenderers under path by (material, shadow mode, receive shadows, layer) into
        /// meshes parented to path, in path's local space. Originals stay as they are until ShowBaked(true).
        /// </summary>
        public static string Bake(string path, out string summary)
        {
            summary = "";
            Unbake();
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            Transform root = go.transform;
            Matrix4x4 toRoot = root.worldToLocalMatrix;

            Dictionary<string, List<CombineInstance>> groups = new Dictionary<string, List<CombineInstance>>();
            Dictionary<string, Renderer> templates = new Dictionary<string, Renderer>();
            Dictionary<string, Material> materials = new Dictionary<string, Material>();
            Dictionary<string, int> vertices = new Dictionary<string, int>();
            int skipped = 0;

            foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (r == null || !r.enabled) continue;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                Material[] mats = r.sharedMaterials;
                if (mesh == null || mats == null || mats.Length != mesh.subMeshCount) { skipped++; continue; }

                bool used = false;
                Matrix4x4 m = toRoot * r.transform.localToWorldMatrix;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    string key = mats[i].GetInstanceID() + "|" + (int)r.shadowCastingMode + "|" + r.receiveShadows + "|" + r.gameObject.layer;
                    int vc = mesh.vertexCount;
                    if (vertices.TryGetValue(key, out int have) && have + vc > 64000)
                    {
                        // Unity 5.0 meshes are limited to 65k vertices: start a new chunk for this key.
                        int chunk = 1;
                        while (vertices.TryGetValue(key + "#" + chunk, out int h2) && h2 + vc > 64000) chunk++;
                        key = key + "#" + chunk;
                    }
                    if (!groups.TryGetValue(key, out List<CombineInstance> list))
                    {
                        groups[key] = list = new List<CombineInstance>();
                        templates[key] = r;
                        materials[key] = mats[i];
                        vertices[key] = 0;
                    }
                    list.Add(new CombineInstance { mesh = mesh, subMeshIndex = i, transform = m });
                    vertices[key] += vc;
                    used = true;
                }
                if (used) bakedOriginals.Add(r);
            }

            int failed = 0;
            foreach (var kv in groups)
            {
                try
                {
                    Mesh combined = new Mesh { name = "MOPR_Baked" };
                    combined.CombineMeshes(kv.Value.ToArray(), true, true);
                    GameObject b = new GameObject("MOPR_Baked_" + bakedObjects.Count);
                    b.layer = templates[kv.Key].gameObject.layer;
                    b.transform.SetParent(root, false);
                    b.AddComponent<MeshFilter>().sharedMesh = combined;
                    MeshRenderer br = b.AddComponent<MeshRenderer>();
                    br.sharedMaterial = materials[kv.Key];
                    br.shadowCastingMode = templates[kv.Key].shadowCastingMode;
                    br.receiveShadows = templates[kv.Key].receiveShadows;
                    br.enabled = false;
                    bakedObjects.Add(b);
                }
                catch (Exception) { failed++; }
            }
            summary = $"{bakedOriginals.Count} renderers -> {bakedObjects.Count} combined meshes ({skipped} skipped, {failed} failed)";
            return null;
        }

        /// <summary>true: draw the combined meshes instead of the originals; false: the originals.</summary>
        public static void ShowBaked(bool baked)
        {
            foreach (GameObject b in bakedObjects) if (b != null) b.GetComponent<MeshRenderer>().enabled = baked;
            foreach (Renderer r in bakedOriginals) if (r != null) r.enabled = !baked;
        }

        public static void Unbake()
        {
            ShowBaked(false);
            foreach (GameObject b in bakedObjects)
            {
                if (b == null) continue;
                Mesh m = b.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.Destroy(b);
                if (m != null) UnityEngine.Object.Destroy(m);
            }
            bakedObjects.Clear();
            bakedOriginals.Clear();
        }

        /// <summary>Counts renderers, visible renderers, shadow casters, materials and FSMs under path.</summary>
        public static string Census(string path)
        {
            GameObject go = Resolve(path);
            if (go == null) return "object not found: " + path;
            int renderers = 0, enabled = 0, visible = 0, casters = 0, materials = 0, fsms = 0, activeFsms = 0, colliders = 0, rbs = 0;
            HashSet<Material> unique = new HashSet<Material>();
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                renderers++;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                enabled++;
                if (r.isVisible) visible++;
                if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) casters++;
                foreach (Material m in r.sharedMaterials) { materials++; if (m != null) unique.Add(m); }
            }
            foreach (PlayMakerFSM f in go.GetComponentsInChildren<PlayMakerFSM>(true)) { fsms++; if (f.enabled && f.gameObject.activeInHierarchy) activeFsms++; }
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) if (c.enabled && c.gameObject.activeInHierarchy) colliders++;
            foreach (Rigidbody b in go.GetComponentsInChildren<Rigidbody>(true)) if (b.gameObject.activeInHierarchy) rbs++;
            return $"renderers {renderers} (enabled {enabled}, visible {visible}, shadow casters {casters}), material slots {materials} ({unique.Count} unique), " +
                   $"FSMs {fsms} (running {activeFsms}), colliders {colliders}, rigidbodies {rbs}";
        }

        // ----- MSCLoader mod settings page (main menu) ------------------------------------------------

        static readonly List<GameObject> activatedForSettings = new List<GameObject>();

        /// <summary>
        /// The live mod menu UI (MSCLoader.ModMenu.instance.UI). Resources.FindObjectsOfTypeAll also returns the prefab
        /// copies of the menu, which never render; everything below is looked up under this object first.
        /// </summary>
        static GameObject ModMenuUi()
        {
            Type t = typeof(MSCLoader.ModLoader).Assembly.GetType("MSCLoader.ModMenu");
            object inst = t?.GetField("instance", AnyStatic)?.GetValue(null);
            return inst != null ? t.GetField("UI", Any)?.GetValue(inst) as GameObject : null;
        }

        static MonoBehaviour FindMenuBehaviour(string typeName)
        {
            GameObject ui = ModMenuUi();
            if (ui != null)
            {
                MonoBehaviour live = ui.GetComponentsInChildren<MonoBehaviour>(true).FirstOrDefault(b => b != null && b.GetType().Name == typeName);
                if (live != null) return live;
            }
            return Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(b => b != null && b.GetType().Name == typeName);
        }

        static MonoBehaviour FindUniversalView()
        {
            MonoBehaviour mmv = FindMenuBehaviour("ModMenuView");
            MonoBehaviour view = mmv != null ? mmv.GetType().GetField("universalView", Any)?.GetValue(mmv) as MonoBehaviour : null;
            return view ?? FindMenuBehaviour("UniversalView");
        }

        /// <summary>Slides the mod menu in or out like its arrow button does (ModMenuButton.ButtonClicked).</summary>
        public static void SetModMenuOpened(bool open)
        {
            MonoBehaviour btn = FindMenuBehaviour("ModMenuButton");
            if (btn == null) return;
            object opened = btn.GetType().GetField("opened", Any)?.GetValue(btn);
            if (opened is bool b && b == open) return;
            btn.GetType().GetMethod("ButtonClicked", Any)?.Invoke(btn, null);
        }

        // ----- MSCLoader console ----------------------------------------------------------------------

        /// <summary>Runs a console command line as if typed (MSCLoader's ConsoleController.RunCommandString).</summary>
        public static string RunConsoleCommand(string line)
        {
            if (string.IsNullOrEmpty(line)) return "no command";
            object cc = typeof(MSCLoader.ConsoleCommand).GetField("cc", AnyStatic)?.GetValue(null);
            if (cc == null) return "console not ready";
            MethodInfo run = cc.GetType().GetMethod("RunCommandString", Any);
            if (run == null) return "RunCommandString not found";
            run.Invoke(cc, new object[] { line });
            return null;
        }

        // ----- MOP's cached FSM variables ---------------------------------------------------------------

        /// <summary>
        /// Every static field of typeName (in the MOPR assembly) that holds an FSM variable or a GameObject:
        /// "live" if it belongs to an FSM of the current scene (or the globals), "STALE" if not (left over from an
        /// earlier load: reading it returns old values), "null" if not cached yet.
        /// </summary>
        public static string FsmCache(string typeName, out string summary)
        {
            Assembly mop = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MOPR");
            Type type = mop?.GetType(typeName, false);
            if (type == null) { summary = "type not found: " + typeName; return summary + "\n"; }

            // Reference set of every variable object of the current scene's FSMs (active or not) and the globals.
            HashSet<object> live = new HashSet<object>(new ReferenceComparer());
            void AddVars(FsmVariables v)
            {
                if (v == null) return;
                foreach (NamedVariable n in v.FloatVariables) live.Add(n);
                foreach (NamedVariable n in v.IntVariables) live.Add(n);
                foreach (NamedVariable n in v.BoolVariables) live.Add(n);
                foreach (NamedVariable n in v.StringVariables) live.Add(n);
                foreach (NamedVariable n in v.GameObjectVariables) live.Add(n);
                foreach (NamedVariable n in v.Vector3Variables) live.Add(n);
            }
            AddVars(FsmVariables.GlobalVariables);
            foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (fsm != null && fsm.hideFlags == HideFlags.None) AddVars(fsm.FsmVariables);

            int nLive = 0, nStale = 0, nNull = 0;
            List<string> lines = new List<string>();
            foreach (FieldInfo f in type.GetFields(AnyStatic).OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (f.IsLiteral) continue;
                bool isVar = typeof(NamedVariable).IsAssignableFrom(f.FieldType);
                bool isUnity = typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType);
                if (!isVar && !isUnity) continue;

                object value = f.GetValue(null);
                string state;
                if (value == null) { state = "null"; nNull++; }
                else if (isUnity)
                {
                    // Unity's == null is true for objects destroyed with their scene.
                    bool alive = (UnityEngine.Object)value != null;
                    state = alive ? "live" : "STALE (destroyed)";
                    if (alive) nLive++; else nStale++;
                }
                else if (live.Contains(value)) { state = "live = " + Format(ValueOf((NamedVariable)value)); nLive++; }
                else { state = "STALE = " + Format(ValueOf((NamedVariable)value)); nStale++; }
                lines.Add($"{f.Name}\t{f.FieldType.Name}\t{state}");
            }
            summary = $"{nLive} live, {nStale} stale, {nNull} not cached ({type.Name})";
            return summary + "\n" + string.Join("\n", lines.ToArray()) + "\n";
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // ----- What MOP manages -------------------------------------------------------------------------

        /// <summary>
        /// MOP's management decisions after a load, one sorted line each, through reflection only (the same code
        /// reads any MOPR build): world objects, items, vehicles, places, sectors, the Satsuma. Two builds that read
        /// rules differently show up as different lines.
        /// </summary>
        public static string MoprState(out string summary)
        {
            Assembly mop = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MOPR");
            List<string> lines = new List<string>();
            Dictionary<string, int> counts = new Dictionary<string, int>();
            void Add(string category, string text)
            {
                lines.Add(category + "\t" + text);
                counts.TryGetValue(category, out int n);
                counts[category] = n + 1;
            }
            if (mop == null) { summary = "MOPR not loaded"; return ""; }
            CultureInfo ci = CultureInfo.InvariantCulture;
            string Lod(object lod) => lod == null ? "none" : (Member(lod, "lodObject") as UnityEngine.Object != null ? "yes" : "empty");
            string Method(object d) => (d as Delegate)?.Method.Name ?? "null";
            string V(Vector3 v) => v.x.ToString("0.##", ci) + "," + v.y.ToString("0.##", ci) + "," + v.z.ToString("0.##", ci);

            object world = Static(mop, "MOP.Managers.WorldObjectManager", "instance");
            foreach (object o in Items(Member(world, "worldObjects")))
            {
                GameObject g = Member(o, "GameObject") as GameObject;
                Add("world", (g != null ? BotRunner.PathOf(g.transform) : "null") + "\t" + o.GetType().Name + " dist=" + Member(o, "Distance") + " on=" + Member(o, "DisableOn"));
            }

            object items = Static(mop, "MOP.Managers.ItemsManager", "instance");
            foreach (object o in Items(Member(items, "itemHooks")))
            {
                Component c = o as Component;
                if (c == null) continue;
                Add("item", BotRunner.PathOf(c.transform) + "\t" + Method(Member(o, "Toggle")) +
                            " renderer=" + (Member(o, "renderer") as UnityEngine.Object != null ? "yes" : "no") + " lod=" + Lod(Member(o, "dummy")));
            }

            object vehicles = Static(mop, "MOP.Managers.VehicleManager", "instance");
            foreach (object v in Items(Member(vehicles, "vehicles")))
            {
                GameObject g = Member(v, "gameObject") as GameObject;
                List<string> keep = new List<string>();
                foreach (object p in Items(Member(v, "preventToggleOnObjects")))
                {
                    Transform t = Member(p, "ObjectTransform") as Transform;
                    keep.Add(t != null ? t.name : "null");
                }
                keep.Sort(StringComparer.Ordinal);
                Add("vehicle", (g != null ? g.name : "null") + "\t" + v.GetType().Name + " toggle=" + Method(Member(v, "Toggle")) + " active=" + Member(v, "IsActive") +
                               " lod=" + Lod(Member(v, "dummyCar")) + " keep=[" + string.Join(",", keep.ToArray()) + "]");
            }

            object places = Static(mop, "MOP.Managers.PlaceManager", "instance");
            foreach (object p in Items(Member(places, "places")))
            {
                List<string> black = Items(Member(p, "GameObjectBlackList")).Select(x => x as string ?? "null").ToList();
                black.Sort(StringComparer.Ordinal);
                List<string> childs = Items(Member(p, "DisableableChilds")).Select(x => x is Transform t && t != null ? BotRunner.PathOf(t) : "null").ToList();
                childs.Sort(StringComparer.Ordinal);
                GameObject g = Member(p, "gameObject") as GameObject;
                string name = g != null ? g.name : "null";
                Add("place", name + "\tblacklist=[" + string.Join(",", black.ToArray()) + "] childs=" + childs.Count);
                foreach (string ch in childs) Add("placechild", name + "\t" + ch);
            }

            object sectors = Static(mop, "MOP.Managers.SectorManager", "instance");
            foreach (object s in Items(Member(sectors, "sectors")))
            {
                Component c = s as Component;
                if (c == null) continue;
                List<string> ignore = Items(Member(s, "ignoreList")).Select(x => x as string ?? "null").ToList();
                ignore.Sort(StringComparer.Ordinal);
                BoxCollider box = c.GetComponent<BoxCollider>();
                Add("sector", V(c.transform.position) + "\tsize=" + (box != null ? V(box.size) : "-") + " rot=" + V(c.transform.localEulerAngles) +
                              " dist=" + Member(s, "DrawDistance") + " keep=[" + string.Join(",", ignore.ToArray()) + "]");
            }
            foreach (object o in Items(Member(sectors, "objectsToDisable")))
            {
                GameObject g = o as GameObject;
                Add("sectorobj", g != null ? BotRunner.PathOf(g.transform) : "null");
            }

            object satsuma = Static(mop, "MOP.Vehicles.Cases.Satsuma", "instance");
            if (satsuma != null)
            {
                Add("satsuma", "passive\t" + Member(satsuma, "IsPassive"));
                Add("satsuma", "renderers\t" + Items(Member(satsuma, "renderers")).Count());
                List<string> dis = Items(Member(satsuma, "disableableObjects")).Select(x => x is Transform t && t != null ? t.name : "null").ToList();
                dis.Sort(StringComparer.Ordinal);
                Add("satsuma", "disableable\t" + dis.Count + " " + string.Join(",", dis.ToArray()));
            }

            lines.Sort(StringComparer.Ordinal);
            summary = string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => kv.Key + " " + kv.Value).ToArray());
            return string.Join("\n", lines.ToArray()) + "\n";
        }

        static object Static(Assembly asm, string type, string field)
        {
            Type t = asm.GetType(type, false);
            return t?.GetField(field, AnyStatic)?.GetValue(null);
        }

        /// <summary>A field or property by name, searched up the class hierarchy (private members of base classes too).</summary>
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

        /// <summary>One line for the bot log: is the menu UI found, is the page really shown, is the menu slid in.</summary>
        public static string ModSettingsState()
        {
            GameObject ui = ModMenuUi();
            MonoBehaviour view = FindUniversalView();
            MonoBehaviour btn = FindMenuBehaviour("ModMenuButton");
            object opened = btn?.GetType().GetField("opened", Any)?.GetValue(btn);
            return $"menu UI {(ui != null ? ui.name + (ui.activeInHierarchy ? " (active)" : " (inactive)") : "not found")}, " +
                   $"page {(view != null ? (view.gameObject.activeInHierarchy ? "shown" : "hidden") : "not found")}, opened {opened ?? "?"}";
        }

        /// <summary>Opens the settings page of a mod in MSCLoader's mod menu (internal UniversalView.FillSettings).</summary>
        public static string OpenModSettings(string modId)
        {
            MSCLoader.Mod mod = MSCLoader.ModLoader.LoadedMods.FirstOrDefault(m => m.ID == modId);
            if (mod == null) return "mod not loaded: " + modId;
            MonoBehaviour view = FindUniversalView();
            if (view == null) return "UniversalView not found";

            // The page only renders inside an active mod menu: activate the inactive ancestors (restored by CloseModSettings).
            activatedForSettings.Clear();
            for (Transform t = view.transform.parent; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    t.gameObject.SetActive(true);
                    activatedForSettings.Add(t.gameObject);
                }
            }
            MethodInfo fill = view.GetType().GetMethod("FillSettings", Any);
            if (fill == null) return "FillSettings not found";
            fill.Invoke(view, new object[] { mod });
            return null;
        }

        /// <summary>Closes the page (MSCLoader saves the mod settings on close) and restores what OpenModSettings activated.</summary>
        public static string CloseModSettings()
        {
            MonoBehaviour view = FindUniversalView();
            if (view != null)
            {
                MethodInfo close = view.GetType().GetMethod("CloseView", Any);
                if (close != null) close.Invoke(view, null);
            }
            for (int i = activatedForSettings.Count - 1; i >= 0; i--)
                if (activatedForSettings[i] != null) activatedForSettings[i].SetActive(false);
            activatedForSettings.Clear();
            SetModMenuOpened(false);
            return view == null ? "UniversalView not found" : null;
        }

        /// <summary>Changes a mod setting like the user would (value + its change callback). Returns the previous value.</summary>
        public static string SetModSetting(string modId, string settingId, string value, out string previous)
        {
            previous = null;
            MSCLoader.Mod mod = MSCLoader.ModLoader.LoadedMods.FirstOrDefault(m => m.ID == modId);
            if (mod == null) return "mod not loaded: " + modId;
            MethodInfo get = typeof(MSCLoader.Settings).GetMethod("GetModSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            System.Collections.IEnumerable list = get != null ? get.Invoke(null, new object[] { mod }) as System.Collections.IEnumerable : null;
            if (list == null) return "settings list not found";

            FieldInfo idField = typeof(MSCLoader.ModSetting).GetField("ID", Any);
            FieldInfo actionField = typeof(MSCLoader.ModSetting).GetField("DoAction", Any);
            foreach (object o in list)
            {
                MSCLoader.ModSetting s = o as MSCLoader.ModSetting;
                if (s == null || idField == null || (string)idField.GetValue(s) != settingId) continue;
                CultureInfo ci = CultureInfo.InvariantCulture;
                switch (s)
                {
                    case MSCLoader.SettingsDropDownList d:
                        previous = d.GetSelectedItemIndex().ToString(ci);
                        d.SetSelectedItemIndex(int.Parse(value, ci));
                        break;
                    case MSCLoader.SettingsCheckBox c:
                        {
                            previous = c.GetValue().ToString();
                            bool want = bool.Parse(value);
                            // On an open page, click the toggle like the user does: MSCLoader's own listener stores the
                            // value and runs the callback, and the checkbox picture follows. Otherwise set the value.
                            object element = typeof(MSCLoader.ModSetting).GetField("SettingsElement", Any)?.GetValue(s);
                            UnityEngine.UI.Toggle toggle = element?.GetType().GetField("checkBox", Any)?.GetValue(element) as UnityEngine.UI.Toggle;
                            if (toggle != null && toggle.gameObject.activeInHierarchy)
                            {
                                toggle.isOn = want;
                                if (c.GetValue() == want) return null;
                            }
                            c.SetValue(want);
                        }
                        break;
                    case MSCLoader.SettingsCheckBoxGroup g:
                        // Radio group member: set it; switch the other members off with their own steps.
                        previous = g.GetValue().ToString();
                        g.SetValue(bool.Parse(value));
                        break;
                    case MSCLoader.SettingsSliderInt si:
                        previous = si.GetValue().ToString(ci);
                        si.SetValue(int.Parse(value, ci));
                        break;
                    default:
                        return "unsupported setting type " + s.GetType().Name;
                }
                (actionField?.GetValue(s) as Action)?.Invoke();
                return null;
            }
            return "setting not found: " + settingId;
        }

        /// <summary>
        /// One line per setting of the mod: type, ID, Name, and the text currently shown by its live UI element
        /// (settingName / value / header title). "stale" counts live labels that differ from Name.
        /// </summary>
        public static string DumpModSettings(string modId, out int live, out int stale)
        {
            live = stale = 0;
            MSCLoader.Mod mod = MSCLoader.ModLoader.LoadedMods.FirstOrDefault(m => m.ID == modId);
            if (mod == null) return "mod not loaded: " + modId;
            MethodInfo get = typeof(MSCLoader.Settings).GetMethod("GetModSettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            System.Collections.IEnumerable list = get != null ? get.Invoke(null, new object[] { mod }) as System.Collections.IEnumerable : null;
            if (list == null) return "settings list not found";

            FieldInfo idF = typeof(MSCLoader.ModSetting).GetField("ID", Any);
            FieldInfo nameF = typeof(MSCLoader.ModSetting).GetField("Name", Any);
            FieldInfo elemF = typeof(MSCLoader.ModSetting).GetField("SettingsElement", Any);
            FieldInfo headF = typeof(MSCLoader.ModSetting).GetField("HeaderElement", Any);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (object o in list)
            {
                MSCLoader.ModSetting s = o as MSCLoader.ModSetting;
                if (s == null) continue;
                string name = nameF?.GetValue(s) as string ?? "";
                string shown = null, value = null;
                UnityEngine.Object el = elemF?.GetValue(s) as UnityEngine.Object;
                if (el != null)
                {
                    shown = (el.GetType().GetField("settingName", Any)?.GetValue(el) as UnityEngine.UI.Text)?.text;
                    value = (el.GetType().GetField("value", Any)?.GetValue(el) as UnityEngine.UI.Text)?.text;
                }
                UnityEngine.Object head = headF?.GetValue(s) as UnityEngine.Object;
                if (head != null)
                    shown = (head.GetType().GetField("HeaderTitle", Any)?.GetValue(head) as UnityEngine.UI.Text)?.text;

                if (shown != null)
                {
                    live++;
                    bool upper = s is MSCLoader.SettingsButton || s is MSCLoader.SettingsHeader;
                    if (shown != (upper ? name.ToUpper() : name)) stale++;
                }
                string oneLine(string x) => x == null ? "-" : x.Replace("\n", "\\n");
                sb.Append(s.GetType().Name).Append('\t').Append(idF?.GetValue(s) ?? "").Append('\t')
                  .Append(oneLine(name)).Append("\tshown=").Append(oneLine(shown)).Append("\tvalue=").AppendLine(oneLine(value));
            }
            return sb.ToString();
        }

        /// <summary>Scrolls the open settings page (0 = bottom, 1 = top).</summary>
        public static string ScrollModSettings(float normalized)
        {
            MonoBehaviour view = FindUniversalView();
            if (view == null) return "UniversalView not found";
            // Unity 5.0 has no GetComponentInChildren(bool includeInactive).
            UnityEngine.UI.ScrollRect scroll = view.GetComponentsInChildren<UnityEngine.UI.ScrollRect>(true).FirstOrDefault();
            if (scroll == null) return "ScrollRect not found";
            scroll.verticalNormalizedPosition = normalized;
            return null;
        }

        public static string Read(string probe, Transform player)
        {
            try
            {
                int eq = probe.IndexOf('=');
                if (eq >= 0 && probe.IndexOf('|') > eq) probe = probe.Substring(eq + 1);
                string[] parts = probe.Split('|');
                if (parts.Length < 2) return "?";

                if (parts[0] == "static")
                {
                    // static|Namespace.Type.Member (public or not, property or field)
                    string spec = parts[1];
                    int dot = spec.LastIndexOf('.');
                    string typeName = spec.Substring(0, dot), member = spec.Substring(dot + 1);
                    Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(typeName, false); } catch { return null; } })
                        .FirstOrDefault(t => t != null);
                    if (type == null) return "no type";
                    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    PropertyInfo sp = type.GetProperty(member, S);
                    if (sp != null) return Format(sp.GetValue(null, null));
                    FieldInfo sf = type.GetField(member, S);
                    if (sf != null) return Format(sf.GetValue(null));
                    MethodInfo sm = type.GetMethod(member, S, null, Type.EmptyTypes, null);
                    return sm != null ? Format(sm.Invoke(null, null)) : "no member";
                }

                if (parts[0] == "global")
                {
                    NamedVariable gv = FindVariable(FsmVariables.GlobalVariables, parts[1]);
                    return gv != null ? Format(ValueOf(gv)) : "no var";
                }

                GameObject go = Resolve(parts[0]);
                if (go == null) return "missing";

                if (parts.Length == 2)
                {
                    switch (parts[1])
                    {
                        case "@active": return go.activeInHierarchy ? "1" : "0";
                        case "@self": return go.activeSelf ? "1" : "0";
                        case "@parent": return go.transform.parent != null ? BotRunner.PathOf(go.transform.parent) : "(root)";
                        case "@pos": return Format(go.transform.position);
                        case "@player": return player != null ? Vector3.Distance(player.position, go.transform.position).ToString("0.0", CultureInfo.InvariantCulture) : "?";
                    }
                    if (parts[1].StartsWith("@in:"))
                    {
                        // Position in the frame of another object (e.g. cargo relative to the van).
                        GameObject frame = Resolve(parts[1].Substring(4));
                        if (frame == null) return "no frame";
                        Vector3 p = frame.transform.InverseTransformPoint(go.transform.position);
                        return p.x.ToString("0.00", CultureInfo.InvariantCulture) + "," + p.y.ToString("0.00", CultureInfo.InvariantCulture) + "," + p.z.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    if (parts[1].StartsWith("#")) return ReadMember(go, parts[1].Substring(1));
                    return "?";
                }

                PlayMakerFSM fsm = Fsm(go, parts[1]);
                if (fsm == null) return "no fsm";
                if (parts[2] == "@state") return (fsm.enabled ? "" : "(off)") + fsm.ActiveStateName;
                NamedVariable v = FindVariable(fsm.FsmVariables, parts[2]);
                return v != null ? Format(ValueOf(v)) : "no var";
            }
            catch (Exception e)
            {
                return "err:" + e.GetType().Name;
            }
        }

        static string ReadMember(GameObject go, string spec)
        {
            int dot = spec.IndexOf('.');
            if (dot < 0) return "?";
            string typeName = spec.Substring(0, dot), member = spec.Substring(dot + 1);
            Component c = go.GetComponents<Component>().FirstOrDefault(x => x != null && x.GetType().Name == typeName);
            if (c == null) return "no " + typeName;
            for (Type t = c.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(member, Any | BindingFlags.DeclaredOnly);
                if (f != null) return Format(f.GetValue(c));
                PropertyInfo p = t.GetProperty(member, Any | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) return Format(p.GetValue(c, null));
            }
            return "no " + member;
        }

        /// <summary>Typed value of an FSM variable (PlayMaker 1.6 has no RawValue).</summary>
        static object ValueOf(NamedVariable v)
        {
            switch (v)
            {
                case FsmFloat f: return f.Value;
                case FsmBool b: return b.Value;
                case FsmInt i: return i.Value;
                case FsmString s: return s.Value;
                case FsmGameObject g: return g.Value;
                case FsmVector3 v3: return v3.Value;
                default: return v != null ? v.ToString() : null;
            }
        }

        static string Format(object value)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            switch (value)
            {
                case null: return "null";
                case float f: return f.ToString("0.###", ci);
                case bool b: return b ? "1" : "0";
                case Vector3 v: return string.Format(ci, "({0:0.0},{1:0.0},{2:0.0})", v.x, v.y, v.z);
                case Quaternion q: return string.Format(ci, "({0:0.000000},{1:0.000000},{2:0.000000},{3:0.000000})", q.x, q.y, q.z, q.w);
                case GameObject g: return g != null ? g.name : "null";
                case UnityEngine.Object o: return o != null ? o.name : "null";
                default: return Convert.ToString(value, ci);
            }
        }
    }
}
