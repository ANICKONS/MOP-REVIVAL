// YAIM (Yet Another Inventory Mod) through reflection, to test MOPR with an inventory mod without keybinds:
// "pickup" puts an object into the inventory the way YAIM's pickup key does (InventoryHandler.AttemptPickUp,
// limits bypassed), "drop" takes that object out in front of the player (DropCurrent), "state" prints where
// an object is and what its physics and renderer are doing. "state" works without YAIM.

using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MOPR.TestBot
{
    static class YaimProbe
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        static Type Find(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(name, false); } catch { return null; } })
                .FirstOrDefault(t => t != null);

        /// <summary>Returns a one-line result (state or error).</summary>
        public static string Run(string action, string path)
        {
            GameObject go = GameOps.Resolve(path);
            if (go == null) return "not found: " + path;
            switch ((action ?? "").ToLowerInvariant())
            {
                case "state": return State(go);
                case "pickup": return Pickup(go) ?? State(go);
                case "drop": return Drop(go) ?? State(go);
                default: return "unknown action " + action + " (pickup, drop, state)";
            }
        }

        static object Inventory(out Type type, out string error)
        {
            error = null;
            type = Find("Ceres.YAIM.InventoryHandler");
            if (type == null) { error = "YAIM is not loaded"; return null; }
            object inv = type.GetField("Singleton", Any)?.GetValue(null);
            if (inv == null) error = "YAIM inventory not created yet";
            return inv;
        }

        static string Pickup(GameObject go)
        {
            object inv = Inventory(out Type type, out string error);
            if (inv == null) return error;
            MethodInfo m = type.GetMethod("AttemptPickUp", Any);
            object ok = m.Invoke(inv, new object[] { go, true });
            return (bool)ok ? null : "YAIM refused the pickup";
        }

        static string Drop(GameObject go)
        {
            object inv = Inventory(out Type type, out string error);
            if (inv == null) return error;
            IList items = type.GetField("Items", Any).GetValue(inv) as IList;
            int index = items != null ? items.IndexOf(go) : -1;
            if (index < 0) return "not in the inventory";

            // DropCurrent drops Items[UIHandler.CurIndex].
            Type ui = Find("Ceres.YAIM.UI.UIHandler");
            object handler = ui?.GetField("Singleton", Any)?.GetValue(null);
            if (handler == null) return "YAIM UI not created yet";
            ui.GetField("CurIndex", Any).SetValue(handler, index);

            Transform player = GameObject.Find("PLAYER")?.transform;
            if (player == null) return "no player";
            Vector3 pos = player.position + player.forward * 1.5f + Vector3.up * 0.6f;
            type.GetMethod("DropCurrent", Any).Invoke(inv, new object[] { pos });
            return null;
        }

        public static string State(GameObject go)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            Vector3 p = go.transform.position;
            Rigidbody rb = go.GetComponent<Rigidbody>();
            Renderer r = go.GetComponent<Renderer>();
            Transform player = GameObject.Find("PLAYER")?.transform;
            string s = string.Format(ci, "pos=({0:0.00},{1:0.00},{2:0.00}) active={3}", p.x, p.y, p.z, go.activeSelf);
            if (player != null) s += string.Format(ci, " fromPlayer={0:0.0}m", Vector3.Distance(p, player.position));
            if (rb != null) s += string.Format(ci, " kinematic={0} gravity={1} collisions={2} speed={3:0.00}",
                                               rb.isKinematic, rb.useGravity, rb.detectCollisions, rb.velocity.magnitude);
            if (r != null) s += " renderer=" + r.enabled;
            s += " parent=" + (go.transform.parent != null ? go.transform.parent.name : "-");
            return s;
        }
    }
}
