// Driving benchmark helper: moves a vehicle (with the player seated in it) along a route of waypoints, so
// "while driving" costs (car physics, cockpit and mirror cameras, world streaming) can be measured
// repeatably. It drives in the background: measure / profile / abtoggle steps run meanwhile.
//
// The car is not driven by its engine (that would need the key, gears and a steering controller, and a crash
// would end the run). Every physics step the car gets a horizontal velocity along its own heading, and a yaw
// rate that steers it towards a point 12 m ahead on the route (pure pursuit). Speed and yaw rate are rate
// limited like normal driving (the game kills the player on hard accelerations). Vertical motion, suspension,
// wheels and collisions stay real physics. At the end of the route the car stops, turns around in place and
// drives back (one pass = one lap).
//
// Never teleport the car with the player in it: "placecar" first, then get in, then "drive".

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MOPR.TestBot
{
    class Driver : MonoBehaviour
    {
        const float Lookahead = 12f;
        const float Accel = 2.5f, Decel = 4f;          // m/s^2
        const float LateralAccel = 4f;                 // m/s^2, limits the yaw rate at speed

        static Driver instance;

        Rigidbody rb;
        Transform car;
        List<Vector3> route;
        int segment;                                   // car is between route[segment] and route[segment + 1]
        float cruise, speed;
        bool turning;
        int laps;
        float distance;
        Vector3 lastPos;

        /// <summary>Probe: "static|MOPR.TestBot.Driver.Status".</summary>
        public static string Status => instance == null ? "stopped" :
            string.Format(CultureInfo.InvariantCulture, "lap {0}, segment {1}/{2}{3}, {4:0} km/h (cmd {5:0}), {6:0} m",
                instance.laps, instance.segment, instance.route.Count - 1, instance.turning ? " turning" : "",
                instance.rb != null ? instance.rb.velocity.magnitude * 3.6f : 0f, instance.speed * 3.6f, instance.distance);

        /// <summary>
        /// The route moved sideways by <paramref name="lateral"/> metres (+ = right of the direction of travel), each point
        /// along the bisector of its two segments. Teimo's bicycle route runs along the edge of the road: a car on it
        /// stands with its right wheels in the ditch.
        /// </summary>
        internal static List<Vector3> Shift(List<Vector3> points, float lateral)
        {
            if (points == null || lateral == 0f) return points;
            List<Vector3> moved = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 a = points[Mathf.Max(0, i - 1)], b = points[Mathf.Min(points.Count - 1, i + 1)];
                Vector3 dir = Flat(b - a);
                dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.forward;
                moved.Add(points[i] + new Vector3(dir.z, 0f, -dir.x) * lateral);
            }
            return moved;
        }

        /// <summary>
        /// For the log: number of points, length, ends, and the point nearest to an object; with first/last (Max/Last of
        /// the step) also that part of the route: its ends and length.
        /// </summary>
        internal static string Describe(string routePath, string nearPath, int first = 0, int last = 0)
        {
            List<Vector3> p = Route(routePath, out string err);
            if (p == null) return err;
            float len = 0f;
            for (int i = 1; i < p.Count; i++) len += Flat(p[i] - p[i - 1]).magnitude;
            string part = "";
            if (first > 0 || last > 0)
            {
                int a = Mathf.Clamp(first, 0, p.Count - 1), b = last <= 0 ? p.Count - 1 : Mathf.Clamp(last, a, p.Count - 1);
                float pl = 0f;
                for (int i = a + 1; i <= b; i++) pl += Flat(p[i] - p[i - 1]).magnitude;
                part = string.Format(CultureInfo.InvariantCulture, ", part #{0} {1} .. #{2} {3}: {4:0} m", a, p[a], b, p[b], pl);
            }
            string near = "";
            GameObject o = GameOps.Resolve(nearPath);
            if (o != null)
            {
                int best = 0;
                for (int i = 1; i < p.Count; i++)
                    if (Flat(p[i] - o.transform.position).sqrMagnitude < Flat(p[best] - o.transform.position).sqrMagnitude) best = i;
                near = string.Format(CultureInfo.InvariantCulture, ", nearest to {0}: #{1} {2} at {3:0} m", o.name, best, p[best], Flat(p[best] - o.transform.position).magnitude);
            }
            return string.Format(CultureInfo.InvariantCulture, "{0} points, {1:0} m, first {2}, last {3}{4}{5}", p.Count, len, p[0], p[p.Count - 1], near, part);
        }

        internal static List<Vector3> Route(string routePath, out string error)
        {
            error = null;
            GameObject parent = GameOps.Resolve(routePath);
            if (parent == null) { error = "route not found: " + routePath; return null; }
            List<Vector3> points = parent.transform.Cast<Transform>().OrderBy(t => NumberIn(t.name)).ThenBy(t => t.name)
                .Select(t => t.position).ToList();
            if (points.Count < 2) { error = "route has fewer than 2 waypoints"; return null; }
            return points;
        }

        /// <summary>Moves the (empty) vehicle by an offset in its own yaw frame, keeping its heading.</summary>
        public static string MoveBy(string vehiclePath, Vector3 localOffset, out int adopted)
        {
            adopted = 0;
            GameObject vehicle = GameOps.Resolve(vehiclePath);
            if (vehicle == null) return "vehicle not found: " + vehiclePath;
            Quaternion yaw = Quaternion.Euler(0f, vehicle.transform.eulerAngles.y, 0f);
            Vector3 pos = vehicle.transform.position + yaw * localOffset;
            Quaternion rot = yaw;
            vehicle.transform.position = pos;
            vehicle.transform.rotation = rot;
            Rigidbody body = vehicle.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = pos;
                body.rotation = rot;
                if (!body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
            adopted = AdoptPose(vehicle, pos, rot);
            return null;
        }

        /// <summary>MOP keeps a parked car at its last good pose (anti-shake): move that pose too.</summary>
        static int AdoptPose(GameObject vehicle, Vector3 pos, Quaternion rot)
        {
            int adopted = 0;
            List<object> owners = vehicle.GetComponents<Component>().Where(c => c != null).Cast<object>().ToList();
            object mopSatsuma = MopSatsuma();
            if (mopSatsuma != null) owners.Add(mopSatsuma);
            foreach (object c in owners)
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo p = c.GetType().GetField("lastGoodPosition", F);
                FieldInfo r = c.GetType().GetField("lastGoodRotation", F);
                if (p == null || r == null || p.FieldType != typeof(Vector3) || r.FieldType != typeof(Quaternion)) continue;
                p.SetValue(c, pos);
                r.SetValue(c, rot);
                adopted++;
            }
            return adopted;
        }

        /// <summary>Places the (empty) vehicle at waypoint "first" of the route (moved sideways by lateral m), facing along it.</summary>
        public static string Place(string vehiclePath, string routePath, int first, out int adopted, float lateral = 0f)
        {
            adopted = 0;
            GameObject vehicle = GameOps.Resolve(vehiclePath);
            if (vehicle == null) return "vehicle not found: " + vehiclePath;
            Rigidbody body = vehicle.GetComponent<Rigidbody>();
            List<Vector3> points = Shift(Route(routePath, out string err), lateral);
            if (points == null) return err;
            first = Mathf.Clamp(first, 0, points.Count - 2);
            Vector3 dir = Flat(points[first + 1] - points[first]);
            Quaternion rot = Quaternion.LookRotation(dir.sqrMagnitude > 0.01f ? dir.normalized : Vector3.forward, Vector3.up);
            Vector3 pos = points[first] + Vector3.up * 0.5f;
            vehicle.transform.position = pos;
            vehicle.transform.rotation = rot;
            if (body != null)
            {
                body.position = pos;
                body.rotation = rot;
                if (!body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }

            // MOP keeps a parked car with frozen physics at its last good pose (anti-shake): move that pose too,
            // otherwise the car is snapped back on the next loop tick.
            List<object> owners = vehicle.GetComponents<Component>().Where(c => c != null).Cast<object>().ToList();
            object mopSatsuma = MopSatsuma();
            if (mopSatsuma != null) owners.Add(mopSatsuma);
            foreach (object c in owners)
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                FieldInfo p = c.GetType().GetField("lastGoodPosition", F);
                FieldInfo r = c.GetType().GetField("lastGoodRotation", F);
                if (p == null || r == null || p.FieldType != typeof(Vector3) || r.FieldType != typeof(Quaternion)) continue;
                p.SetValue(c, pos);
                r.SetValue(c, rot);
                adopted++;
            }
            return null;
        }

        /// <summary>MOP's Satsuma controller (MOP.Vehicles.Cases.Satsuma.Instance), if MOP is loaded.</summary>
        static object MopSatsuma()
        {
            foreach (Assembly a in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t;
                try { t = a.GetType("MOP.Vehicles.Cases.Satsuma", false); } catch { continue; }
                if (t == null) continue;
                PropertyInfo instance = t.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo field = instance == null ? t.GetField("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
                return instance != null ? instance.GetValue(null, null) : field?.GetValue(null);
            }
            return null;
        }

        /// <summary>Starts driving from where the car is (it must be near the route).</summary>
        public static string Start(string vehiclePath, string routePath, int first, int last, float speedKmh, out int count)
        {
            Stop();
            count = 0;
            GameObject vehicle = GameOps.Resolve(vehiclePath);
            if (vehicle == null) return "vehicle not found: " + vehiclePath;
            Rigidbody body = vehicle.GetComponent<Rigidbody>();
            if (body == null) return "vehicle has no rigidbody";
            List<Vector3> points = Route(routePath, out string err);
            if (points == null) return err;
            first = Mathf.Clamp(first, 0, points.Count - 2);
            last = last <= 0 ? points.Count - 1 : Mathf.Clamp(last, first + 1, points.Count - 1);
            points = points.Skip(first).Take(last - first + 1).ToList();      // the part of the route that is on the road

            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float d = Flat(points[i] - vehicle.transform.position).magnitude;
                if (d < best) { best = d; nearest = i; }
            }
            if (best > 40f) return $"the car is {best:0} m from the route; use placecar first";

            GameObject host = new GameObject("MOPR_TestBot_Driver");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<Driver>();
            instance.rb = body;
            instance.car = vehicle.transform;
            instance.route = points;
            instance.segment = Mathf.Min(nearest, points.Count - 2);
            instance.cruise = Mathf.Max(1f, speedKmh) / 3.6f;
            instance.lastPos = body.position;
            count = points.Count;
            return null;
        }

        public static void Stop()
        {
            if (instance == null) return;
            Destroy(instance.gameObject);
            instance = null;
        }

        static int NumberIn(string name)
        {
            Match m = Regex.Match(name, @"(\d+)\s*$");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>Point Lookahead metres ahead of the projection of pos on the route.</summary>
        Vector3 Ahead(Vector3 pos, out float remaining)
        {
            // Advance the segment while the car is past its end.
            while (segment < route.Count - 2)
            {
                Vector3 a = Flat(route[segment]), b = Flat(route[segment + 1]);
                if (Vector3.Dot(Flat(pos) - b, b - a) < 0f) break;
                segment++;
            }
            Vector3 s0 = Flat(route[segment]), s1 = Flat(route[segment + 1]);
            Vector3 seg = s1 - s0;
            float t = Mathf.Clamp01(Vector3.Dot(Flat(pos) - s0, seg) / Mathf.Max(0.01f, seg.sqrMagnitude));
            Vector3 p = s0 + seg * t;
            float left = Lookahead;
            int i = segment;
            Vector3 target = p;
            Vector3 from = p;
            while (true)
            {
                Vector3 to = Flat(route[i + 1]);
                float len = (to - from).magnitude;
                if (len >= left) { target = from + (to - from).normalized * left; break; }
                left -= len;
                from = to;
                i++;
                if (i >= route.Count - 1) { target = to; break; }
            }
            // Distance to the end of the route along it.
            remaining = (s1 - p).magnitude;
            for (int k = segment + 1; k < route.Count - 1; k++) remaining += Flat(route[k + 1] - route[k]).magnitude;
            return target;
        }

        void FixedUpdate()
        {
            if (rb == null || car == null) { Stop(); return; }
            float dt = Time.fixedDeltaTime;
            Vector3 pos = rb.position;
            distance += Flat(pos - lastPos).magnitude;
            lastPos = pos;

            Vector3 forward = Flat(car.forward).normalized;
            Vector3 target = Ahead(pos, out float remaining);
            Vector3 dir = Flat(target - pos);
            dir = dir.sqrMagnitude > 0.01f ? dir.normalized : forward;
            float yawError = Vector3.Angle(forward, dir) * Mathf.Sign(Vector3.Cross(forward, dir).y);

            float wanted;
            if (turning)
            {
                // Stopped at the end of the route: turn around in place, then drive back.
                wanted = 0f;
                if (Mathf.Abs(yawError) < 10f) turning = false;
            }
            else
            {
                // Slow down for curves and for the end of the route.
                float curve = Mathf.Clamp(1f - Mathf.Abs(yawError) / 70f, 0.35f, 1f);
                float stop = Mathf.Sqrt(2f * Decel * 0.6f * Mathf.Max(0f, remaining - 4f));
                wanted = Mathf.Min(cruise * curve, stop);
                if (remaining < 5f && speed < 0.3f)
                {
                    route.Reverse();
                    segment = 0;
                    laps++;
                    turning = true;
                }
            }
            speed = wanted > speed ? Mathf.Min(wanted, speed + Accel * dt) : Mathf.Max(wanted, speed - Decel * dt);

            Vector3 v = forward * speed;
            rb.velocity = new Vector3(v.x, rb.velocity.y, v.z);

            float maxYaw = turning ? 0.5f : Mathf.Min(0.8f, LateralAccel / Mathf.Max(speed, 1f));
            Vector3 av = rb.angularVelocity;
            rb.angularVelocity = new Vector3(av.x, Mathf.Clamp(yawError * Mathf.Deg2Rad * 2.5f, -maxYaw, maxYaw), av.z);
        }
    }
}
