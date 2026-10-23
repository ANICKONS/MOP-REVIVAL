// Call Your Cousin (sykovanz) through reflection: "call" asks Pena to come to the house the way the mod's home phone
// does (CallManager.GoToPickup on the RouteHome waypoints, without the random "no answer"), "state" prints where his
// car is and what its AI is doing. Pena's car is TRAFFIC/VehiclesDirtRoad/Rally/FITTAN.

using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class CycProbe
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        static Type Find(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType(name, false); } catch { return null; } })
                .FirstOrDefault(t => t != null);

        static object StaticMember(Type t, string name)
        {
            FieldInfo f = t.GetField(name, Any);
            if (f != null) return f.GetValue(null);
            PropertyInfo p = t.GetProperty(name, Any);
            return p != null ? p.GetValue(null, null) : null;
        }

        /// <summary>True when Pena reached the house and waits there (waypoint past the target, standing still).</summary>
        public static bool Arrived()
        {
            Type utils = Find("CallYourCousin.Utils");
            if (utils == null) return false;
            GameObject car = StaticMember(utils, "car") as GameObject;
            FsmInt wp = StaticMember(utils, "carCurrentWaypointIndex") as FsmInt;
            Rigidbody rb = car != null ? car.GetComponent<Rigidbody>() : null;
            int target = HomeTarget(utils);
            return car != null && wp != null && target >= 0 && wp.Value >= target && rb != null && rb.velocity.magnitude < 0.3f;
        }

        public static string Run(string action, float seconds = 0f)
        {
            Type utils = Find("CallYourCousin.Utils");
            if (utils == null) return "Call Your Cousin is not loaded";
            switch ((action ?? "").ToLowerInvariant())
            {
                case "call": return Call(utils) ?? State(utils);
                case "state": return State(utils);
                case "hold": return Hold(utils, seconds) ?? State(utils);
                default: return "unknown action " + action + " (call, state, hold)";
            }
        }

        /// <summary>Keeps Pena's car stopped for <paramref name="seconds"/> the way the mod does (its StopCar every physics frame).</summary>
        static string Hold(Type utils, float seconds)
        {
            MonoBehaviour manager = StaticMember(utils, "callManager") as MonoBehaviour;
            if (manager == null) return "no CallManager";
            MethodInfo stop = manager.GetType().GetMethod("StopCar", Any);
            if (stop == null) return "no StopCar";
            manager.StartCoroutine(HoldRoutine(manager, stop, seconds));
            return null;
        }

        static IEnumerator HoldRoutine(MonoBehaviour manager, MethodInfo stop, float seconds)
        {
            float t = 0f;
            while (t < seconds)
            {
                stop.Invoke(manager, new object[] { true });
                t += Time.fixedDeltaTime;
                yield return new WaitForFixedUpdate();
            }
            stop.Invoke(manager, new object[] { false });
        }

        static int HomeTarget(Type utils)
        {
            GameObject home = StaticMember(utils, "homeManager") as GameObject;
            Type homeType = Find("CallYourCousin.Home");
            if (home == null || homeType == null) return -1;
            Component c = home.GetComponentsInChildren(homeType, true).FirstOrDefault();
            return c != null ? (int)homeType.GetField("targetWaypointIndex", Any).GetValue(c) : -1;
        }

        static string Call(Type utils)
        {
            MonoBehaviour manager = StaticMember(utils, "callManager") as MonoBehaviour;
            if (manager == null) return "no CallManager";
            int target = HomeTarget(utils);
            if (target < 0) return "no Home target waypoint";
            MethodInfo m = manager.GetType().GetMethod("GoToPickup", Any);
            IEnumerator routine = (IEnumerator)m.Invoke(manager, new object[] { target, true });
            manager.StartCoroutine(routine);
            return null;
        }

        static string State(Type utils)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            GameObject car = StaticMember(utils, "car") as GameObject;
            if (car == null) return "no car";
            Transform player = StaticMember(utils, "playerTransform") as Transform;
            FsmInt wp = StaticMember(utils, "carCurrentWaypointIndex") as FsmInt;
            FsmGameObject route = StaticMember(utils, "carCurrentRoute") as FsmGameObject;
            Rigidbody rb = car.GetComponent<Rigidbody>();
            PlayMakerFSM throttle = car.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Throttle");
            PlayMakerFSM nav = car.transform.Find("Navigation")?.GetComponent<PlayMakerFSM>();
            FsmBool stop = throttle != null ? throttle.FsmVariables.FindFsmBool("PlayerStop") : null;

            Vector3 p = car.transform.position;
            string s = string.Format(ci, "car=({0:0.0},{1:0.0},{2:0.0}) active={3} speed={4:0.0}km/h kinematic={5}",
                                     p.x, p.y, p.z, car.activeInHierarchy, rb != null ? rb.velocity.magnitude * 3.6f : -1f, rb != null && rb.isKinematic);
            s += " route=" + (route != null && route.Value != null ? route.Value.name : "-") + " wp=" + (wp != null ? wp.Value.ToString(ci) : "-") + "/" + HomeTarget(utils);
            s += " throttle=" + (throttle != null ? (throttle.enabled ? "" : "(off)") + throttle.ActiveStateName : "-") + " stop=" + (stop != null ? stop.Value.ToString() : "-");
            s += " nav=" + (nav != null ? (nav.enabled ? "" : "(off)") + nav.ActiveStateName : "-");
            if (player != null)
                s += string.Format(ci, " fromPlayer={0:0.0}m playerRoot={1} playerParent={2}", Vector3.Distance(p, player.position),
                                   player.root.name, player.parent != null ? player.parent.name : "-");
            return s;
        }
    }
}
