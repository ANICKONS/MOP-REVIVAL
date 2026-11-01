// Real driving for the harness: the bot drives a car through the game's own input layer, like a player at the
// keyboard. Throttle, brake, clutch and steering are cInput axes, gear changes are cInput keys (VirtualInput.cs; the
// input lock keeps the real keyboard and mouse away). The car controller (AxisCarController), the drivetrain, the
// pedal, clutch and gearbox FSMs and the engine simulation all run as for a player - unlike Driver.cs, which pushes
// the car with a velocity and keeps the engine off.
// The engine must be running before "autodrive" (the scenario starts it through the ignition FSM).
//
// Control, computed at the end of every frame for the next one (the same frames in every run):
//   * the route is prepared once: heading at every point, signed curvature (heading change per metre, smoothed over
//     three points) and the highest speed for it (2.2 m/s^2 lateral on the dirt road);
//   * steering (Stanley, front axle): heading error to the route 2-8 m ahead + atan(1.2 * cross-track / (2 + v))
//     + the curve's own steering angle atan(wheelbase * curvature), scaled to the front wheels' steering angle;
//   * speed: the lowest of the cruise speed, every curve within 150 m ahead (reachable with 1.8 m/s^2 braking), the
//     end of the route, and less when more than 1.5 m off the route; throttle P + I on the speed error, brake when
//     0.7 m/s too fast;
//   * gears (manual box): first from a standstill; up at 3800 rpm only on straights and not while slowing down, down
//     below 1500 rpm or when the engine labours (full throttle below 2100 rpm and too slow). With the game's
//     automatic clutch no pedal; otherwise clutch in, the key, clutch out over 0.25 s. H-shifter: the gear's button;
//   * at the end of the route (or "autodrivestop"): brake to a stop, neutral, brake held.
// Run 4 of diag-drive (pure pursuit 15 m ahead, curve speed from the heading change over 35 m) drifted 3.7 m off a
// gentle left bend with the steering at -0.2 and hit the bank at 47 km/h; this is the replacement.
// The trace (every 10 frames) and the prepared route are written by "autodrivestop" (autodrive_trace.tsv,
// autodrive_route.tsv). Reads the axis and key names and the normalisation flags from the car's AxisCarController,
// so the player's settings (wheel, pad) do not matter.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MOPR.TestBot
{
    class AutoDriver : MonoBehaviour
    {
        enum Phase { Prepare, Launch, Drive, Shift, Stopping, Stopped, Stalled }

        const float Wheelbase = 2.3f, FrontAxle = 1.15f;
        const float LateralAccel = 2.2f, CurveDecel = 1.8f, StopDecel = 2.5f, PlanAhead = 150f;
        const float StanleyGain = 1.2f, StanleySoft = 2f;
        const float UpshiftRpm = 3800f, DownshiftRpm = 1500f, LabourRpm = 2100f, LaunchRpm = 900f, StallRpm = 350f;
        const int ShiftClutchFrames = 10, ShiftReleaseFrames = 15, LaunchFrames = 90, ShiftCooldown = 45, TraceEvery = 10;
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly string[] ShifterButtons = { "reverse", "neutral", "first", "second", "third", "fourth", "fifth", "sixth" };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static AutoDriver instance;

        Rigidbody rb;
        Transform car;
        Component drivetrain, controller, steerWheel;
        FieldInfo fGear, fRpm, fChanging, fRatios, fNeutral;
        bool shifter, automatic, autoClutch;
        string thrAxis, brkAxis, steerAxis, clutchAxis, hbAxis, upKey, downKey;
        bool thrNorm, thrExp, brkNorm, brkExp, cluNorm, cluExp;
        float maxSteer = 33f;

        // The prepared route (flat): points, segment lengths and headings (deg, atan2(x, z): + = to the right),
        // distance along the route at every point, heading at every point, signed curvature (1/m, + = right turn),
        // highest speed (m/s).
        Vector3[] pts;
        float[] segLen, segHead, cum, pointHead, kappa, vmax;
        int segment;
        float cruise, sNow, segT;
        Phase phase, afterShift;
        int phaseFrame, shiftTarget, nextShiftFrame, frames, launchHold, releaseFrame = -1, lastHitFrame = -1000;
        // Game time in the current phase. The launch gives up after LaunchGiveUp seconds, not after a number of frames:
        // at 220 FPS 900 frames were 4 s, too short for the Satsuma to reach 2 m/s (bench, 2026-10-31). In the
        // deterministic runs (60 frames per game second) 15 s is the same 900 frames as before.
        float phaseTime;
        const float LaunchGiveUp = 15f;
        float throttleI, throttle, brake, clutch, steer, distance, wanted, offRoute, lastSpeed, headingError, kappaAhead;
        bool stopRequested, everRunning;
        Vector3 lastPos;
        string heldShifterButton;

        public static string LastEvent = "-";
        static readonly StringBuilder trace = new StringBuilder();
        static string routeTable = "";

        public static bool Running => instance != null;
        /// <summary>Every 10 frames of the last drive (written by "autodrivestop").</summary>
        public static string Trace => trace.ToString();
        /// <summary>The prepared route of the last drive: curvature and speed at every point.</summary>
        public static string RouteTable => routeTable;

        /// <summary>Probe: "static|MOPR.TestBot.AutoDriver.Status".</summary>
        public static string Status => instance == null ? "not driving (" + LastEvent + ")" : instance.Describe();

        string Describe() => string.Format(Inv,
            "{0} seg {1}/{2}, {3:0.0} km/h (want {4:0}), gear {5}, {6:0} rpm, thr {7:0.00} brk {8:0.00} clu {9:0.00} steer {10:+0.00;-0.00}, hb {11:0.00}, off {12:+0.0;-0.0} m, head {13:+0;-0} deg, {14:0} m",
            phase, segment, pts.Length - 1, rb.velocity.magnitude * 3.6f, wanted * 3.6f, Gear, Rpm, throttle, brake, clutch, steer,
            Get<float>(controller, "handbrakeInput"), offRoute, headingError, distance);

        int Gear => (int)fGear.GetValue(drivetrain);
        float Rpm => (float)fRpm.GetValue(drivetrain);
        bool Changing => (bool)fChanging.GetValue(drivetrain);
        float[] Ratios => (float[])fRatios.GetValue(drivetrain);
        int Neutral => (int)fNeutral.GetValue(drivetrain);
        int First => Neutral + 1;

        static Component ComponentNamed(GameObject go, params string[] names) =>
            go.GetComponents<Component>().FirstOrDefault(c => c != null && names.Contains(c.GetType().Name));

        static T Get<T>(object o, string field) { FieldInfo f = o.GetType().GetField(field, F); return f != null && f.GetValue(o) is T v ? v : default(T); }

        public static string Start(string vehiclePath, string routePath, int first, int last, float speedKmh, float lateral, out string config)
        {
            Stop("restarted");
            config = null;
            GameObject vehicle = GameOps.Resolve(vehiclePath);
            if (vehicle == null) return "vehicle not found: " + vehiclePath;
            Rigidbody body = vehicle.GetComponent<Rigidbody>();
            Component dt = ComponentNamed(vehicle, "Drivetrain");
            Component cc = ComponentNamed(vehicle, "AxisCarController");
            if (body == null || dt == null || cc == null) return "the vehicle needs a Rigidbody, a Drivetrain and an AxisCarController";
            List<Vector3> points = Driver.Shift(Driver.Route(routePath, out string err), lateral);
            if (points == null) return err;
            first = Mathf.Clamp(first, 0, points.Count - 2);
            last = last <= 0 ? points.Count - 1 : Mathf.Clamp(last, first + 1, points.Count - 1);
            points = points.Skip(first).Take(last - first + 1).ToList();
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float d = Flat(points[i] - vehicle.transform.position).magnitude;
                if (d < best) { best = d; nearest = i; }
            }
            if (best > 40f) return $"the car is {best:0} m from the route; use placecar first";

            if (!InputLock.On)
            {
                InputLock.Install();
                InputLock.On = true;
            }
            VirtualInput.Active = true;

            GameObject host = new GameObject("MOPR_TestBot_AutoDriver");
            DontDestroyOnLoad(host);
            AutoDriver a = host.AddComponent<AutoDriver>();
            a.rb = body;
            a.car = vehicle.transform;
            a.drivetrain = dt;
            a.controller = cc;
            a.fGear = dt.GetType().GetField("gear", F);
            a.fRpm = dt.GetType().GetField("rpm", F);
            a.fChanging = dt.GetType().GetField("changingGear", F);
            a.fRatios = dt.GetType().GetField("gearRatios", F);
            a.fNeutral = dt.GetType().GetField("neutral", F);
            if (a.fGear == null || a.fRpm == null || a.fChanging == null || a.fRatios == null || a.fNeutral == null)
            {
                Destroy(host);
                return "Drivetrain fields not found";
            }
            a.shifter = Get<bool>(dt, "shifter");
            a.automatic = Get<bool>(dt, "automatic");
            a.autoClutch = Get<bool>(dt, "autoClutch");
            a.thrAxis = Get<string>(cc, "throttleAxis");
            a.brkAxis = Get<string>(cc, "brakeAxis");
            a.steerAxis = Get<string>(cc, "steerAxis");
            a.clutchAxis = Get<string>(cc, "clutchAxis");
            a.hbAxis = Get<string>(cc, "handbrakeAxis");
            a.upKey = Get<string>(cc, "shiftUpButton");
            a.downKey = Get<string>(cc, "shiftDownButton");
            a.thrNorm = Get<bool>(cc, "normalizeThrottleInput");
            a.thrExp = Get<bool>(cc, "exponentialThrottleInput");
            a.brkNorm = Get<bool>(cc, "normalizeBrakesInput");
            a.brkExp = Get<bool>(cc, "exponentialBrakesInput");
            a.cluNorm = Get<bool>(cc, "normalizeClutchInput");
            a.cluExp = Get<bool>(cc, "exponentialClutchInput");
            if (string.IsNullOrEmpty(a.thrAxis) || string.IsNullOrEmpty(a.steerAxis))
            {
                // The seat FSM (PlayerTrigger "Player in car") sets the axis names; without them the car ignores input.
                Destroy(host);
                return "the car's input axes are not set - is the player in the driver's seat?";
            }
            a.steerWheel = vehicle.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "Wheel" &&
                                                                                                  Get<float>(c, "maxSteeringAngle") > 1f);
            if (a.steerWheel != null) a.maxSteer = Get<float>(a.steerWheel, "maxSteeringAngle");
            a.cruise = Mathf.Max(5f, speedKmh) / 3.6f;
            a.PrepareRoute(points);
            a.segment = Mathf.Min(nearest, points.Count - 2);
            a.lastPos = body.position;
            a.phase = Phase.Prepare;
            instance = a;
            trace.Length = 0;
            trace.Append("frame\ts\tx\tz\tkmh\twant\tsteer\thead\toff\tkappa\tgear\trpm\tthr\tbrk\tup\tphase\n");
            int sharp = 0;
            float slowest = float.MaxValue;
            for (int i = 0; i < a.vmax.Length; i++)
            {
                if (a.vmax[i] < a.cruise - 0.1f) sharp++;
                slowest = Mathf.Min(slowest, a.vmax[i]);
            }
            config = string.Format(Inv,
                "axes thr '{0}'{1} brk '{2}'{3} steer '{4}' clutch '{5}'{6} hb '{7}', keys '{8}'/'{9}', {10}{11}{12}, gear {13} (neutral {14}), " +
                "ratios [{15}], max steer {16:0} deg, {17:0} rpm, route {18} points / {19:0} m from {20:0} m, {21} curve points below cruise (slowest {22:0} km/h)",
                a.thrAxis, a.thrNorm ? " (norm)" : "", a.brkAxis, a.brkNorm ? " (norm)" : "", a.steerAxis, a.clutchAxis, a.cluNorm ? " (norm)" : "", a.hbAxis,
                a.upKey, a.downKey, a.automatic ? "automatic" : "manual", a.shifter ? ", H-shifter" : "", a.autoClutch ? ", auto clutch" : "",
                a.Gear, a.Neutral, string.Join(",", a.Ratios.Select(r => r.ToString("0.##", Inv)).ToArray()),
                a.maxSteer, a.Rpm, points.Count, a.cum[a.cum.Length - 1], best, sharp, slowest * 3.6f);
            LastEvent = "started";
            return null;
        }

        /// <summary>Headings, curvature and the speed limit of every point (see the header).</summary>
        void PrepareRoute(List<Vector3> points)
        {
            int n = points.Count;
            pts = points.Select(Flat).ToArray();
            segLen = new float[n - 1];
            segHead = new float[n - 1];
            cum = new float[n];
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 d = pts[i + 1] - pts[i];
                segLen[i] = Mathf.Max(0.05f, d.magnitude);
                segHead[i] = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                cum[i + 1] = cum[i] + segLen[i];
            }
            pointHead = new float[n];
            float[] k = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (i == 0) { pointHead[i] = segHead[0]; continue; }
                if (i == n - 1) { pointHead[i] = segHead[n - 2]; continue; }
                float turn = Mathf.DeltaAngle(segHead[i - 1], segHead[i]);
                pointHead[i] = segHead[i - 1] + turn * 0.5f;
                k[i] = turn * Mathf.Deg2Rad / (0.5f * (segLen[i - 1] + segLen[i]));
            }
            kappa = new float[n];
            vmax = new float[n];
            StringBuilder sb = new StringBuilder("i\ts\tx\tz\thead\tkappa\tvmax_kmh\n");
            for (int i = 0; i < n; i++)
            {
                float km = i > 0 ? k[i - 1] : k[i], kp = i < n - 1 ? k[i + 1] : k[i];
                kappa[i] = 0.25f * km + 0.5f * k[i] + 0.25f * kp;
                float abs = 0.25f * Mathf.Abs(km) + 0.5f * Mathf.Abs(k[i]) + 0.25f * Mathf.Abs(kp);
                vmax[i] = Mathf.Min(cruise, Mathf.Sqrt(LateralAccel / Mathf.Max(abs, 1e-4f)));
                sb.AppendFormat(Inv, "{0}\t{1:0.0}\t{2:0.0}\t{3:0.0}\t{4:0.0}\t{5:0.0000}\t{6:0.0}\n", i, cum[i], pts[i].x, pts[i].z, pointHead[i], kappa[i], vmax[i] * 3.6f);
            }
            routeTable = sb.ToString();
        }

        /// <summary>Brakes to a stop (the driver stays until it stands, then holds the brake in neutral).</summary>
        public static void RequestStop()
        {
            if (instance != null) instance.stopRequested = true;
        }

        /// <summary>Gone at once: every pedal and key released.</summary>
        public static void Stop(string why)
        {
            if (instance == null) return;
            LastEvent = why + " at " + instance.Describe();
            Destroy(instance.gameObject);
            instance = null;
            VirtualInput.ReleaseAll();
        }

        void Start() { StartCoroutine(Loop()); }

        IEnumerator Loop()
        {
            WaitForEndOfFrame end = new WaitForEndOfFrame();
            while (true)
            {
                yield return end;
                if (rb == null || car == null || drivetrain == null) { Stop("vehicle gone"); yield break; }
                try { Tick(); }
                catch (Exception e)
                {
                    Debug.Log("[TestBot] autodrive: " + e.GetType().Name + ": " + e.Message);
                    Stop("error " + e.GetType().Name);
                    yield break;
                }
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        void Enter(Phase p)
        {
            phase = p;
            phaseFrame = 0;
            phaseTime = 0f;
            releaseFrame = -1;
        }

        /// <summary>The car's front axle on the route: segment, fraction, distance along, cross-track (+ = right).</summary>
        void Track(Vector3 front)
        {
            // Forward only: the car never drives the route backwards.
            while (segment < pts.Length - 2 && Vector3.Dot(front - pts[segment + 1], pts[segment + 1] - pts[segment]) >= 0f) segment++;
            Vector3 a = pts[segment], d = pts[segment + 1] - a;
            segT = Mathf.Clamp01(Vector3.Dot(front - a, d) / Mathf.Max(0.01f, d.sqrMagnitude));
            sNow = cum[segment] + segT * segLen[segment];
            Vector3 dir = d.normalized;
            offRoute = Vector3.Dot(front - (a + d * segT), new Vector3(dir.z, 0f, -dir.x));
        }

        /// <summary>Heading (deg) and signed curvature of the route at distance <paramref name="s"/> along it.</summary>
        void PathAt(float s, out float heading, out float k)
        {
            s = Mathf.Clamp(s, 0f, cum[cum.Length - 1]);
            int i = segment;
            while (i < pts.Length - 2 && cum[i + 1] < s) i++;
            float u = Mathf.Clamp01((s - cum[i]) / segLen[i]);
            heading = pointHead[i] + Mathf.DeltaAngle(pointHead[i], pointHead[i + 1]) * u;
            k = Mathf.Lerp(kappa[i], kappa[i + 1], u);
        }

        /// <summary>The highest speed now: every curve point within PlanAhead m, reachable with CurveDecel braking.</summary>
        float PlannedSpeed()
        {
            float v = Mathf.Lerp(vmax[segment], vmax[segment + 1], segT);
            for (int j = segment + 1; j < pts.Length && cum[j] - sNow <= PlanAhead; j++)
            {
                float d = Mathf.Max(0f, cum[j] - sNow - 3f);
                v = Mathf.Min(v, Mathf.Sqrt(vmax[j] * vmax[j] + 2f * CurveDecel * d));
            }
            return v;
        }

        void Tick()
        {
            frames++;
            phaseFrame++;
            float dt = Mathf.Max(1e-4f, Time.deltaTime);
            phaseTime += Time.deltaTime;
            Vector3 pos = rb.position;
            distance += Flat(pos - lastPos).magnitude;
            lastPos = pos;
            Vector3 forward = Flat(car.forward);
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
            float speed = Vector3.Dot(rb.velocity, car.forward);           // signed, m/s
            float v = Mathf.Abs(speed);
            float rpm = Rpm;
            if (rpm > 600f) everRunning = true;
            if (steerWheel != null)
            {
                float m = Get<float>(steerWheel, "maxSteeringAngle");
                if (m > 1f) maxSteer = m;
            }

            // Steering (Stanley at the front axle).
            Track(Flat(pos) + forward * FrontAxle);
            float carHead = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            PathAt(sNow + Mathf.Clamp(2f + 0.35f * v, 2f, 8f), out float pathHead, out kappaAhead);
            headingError = Mathf.DeltaAngle(carHead, pathHead);
            float delta = headingError
                        - Mathf.Atan(StanleyGain * offRoute / (StanleySoft + v)) * Mathf.Rad2Deg
                        + Mathf.Atan(Wheelbase * kappaAhead) * Mathf.Rad2Deg;
            steer = Mathf.Clamp(delta / Mathf.Max(5f, maxSteer), -1f, 1f);

            // Speed.
            float remaining = cum[cum.Length - 1] - sNow;
            wanted = Mathf.Min(cruise, PlannedSpeed());
            wanted = Mathf.Min(wanted, Mathf.Sqrt(2f * StopDecel * Mathf.Max(0f, remaining - 6f)));
            float off = Mathf.Abs(offRoute);
            if (off > 1.5f) wanted = Mathf.Min(wanted, Mathf.Max(3f, cruise * (1f - (off - 1.5f) / 3f)));
            if ((remaining < 6f || stopRequested) && phase != Phase.Stopped && phase != Phase.Stalled && phase != Phase.Stopping) Enter(Phase.Stopping);

            // Engine died while in gear with the clutch out: the scenario's start sequence is not repeated here.
            if (everRunning && rpm < StallRpm && phase != Phase.Stopped && phase != Phase.Stalled && phase != Phase.Prepare)
            {
                LastEvent = "stalled at " + Describe();
                Debug.Log("[TestBot] autodrive: " + LastEvent);
                Enter(Phase.Stalled);
            }
            // On its side or roof (a crash): nothing to drive any more.
            if (car.up.y < 0.5f && phase != Phase.Stalled && phase != Phase.Stopped)
            {
                LastEvent = "OVERTURNED at " + Describe() + string.Format(Inv, ", pos {0}", rb.position);
                Debug.Log("[TestBot] autodrive: " + LastEvent);
                Enter(Phase.Stalled);
            }
            // A sudden loss of speed without braking (more than 12 m/s^2): something was hit.
            if ((lastSpeed - speed) / dt > 12f && lastSpeed > 3f && brake < 0.5f && frames - lastHitFrame > 30)
            {
                lastHitFrame = frames;
                Debug.Log(string.Format(Inv, "[TestBot] autodrive: HIT something at {0}, s {1:0} m: {2:0.0} -> {3:0.0} km/h, {4}",
                                        rb.position, sNow, lastSpeed * 3.6f, speed * 3.6f, Describe()));
            }
            lastSpeed = speed;

            switch (phase)
            {
                case Phase.Prepare:
                    // Clutch in (the automatic clutch: no pedal), brake on, first gear.
                    throttle = 0f; brake = 0.5f; clutch = autoClutch ? 0f : 1f;
                    if (phaseFrame > 10 && SelectGear(First)) { throttleI = 0.3f; Enter(Phase.Launch); }
                    break;

                case Phase.Launch:
                    {
                        brake = 0f;
                        throttle = Mathf.Min(0.45f, 0.15f + phaseFrame * 0.004f);
                        if (phaseFrame == 1 && Get<float>(controller, "handbrakeInput") > 0.05f)
                            LastEvent = "WARNING handbrake on (" + Get<float>(controller, "handbrakeInput").ToString("0.00", Inv) + "): the car cannot pull away";
                        if (autoClutch)
                        {
                            // The game's automatic clutch (option "AutoClutch"): no pedal, it engages from 1500 rpm.
                            clutch = 0f;
                            if (speed > 2f) { throttleI = throttle; Enter(Phase.Drive); }
                            else if (phaseTime > LaunchGiveUp)
                            {
                                LastEvent = "cannot pull away (engine off? handbrake?) at " + Describe();
                                Enter(Phase.Stopped);
                            }
                            break;
                        }
                        // Release the clutch over LaunchFrames, but not while the engine is close to stalling.
                        if (rpm < 700f) launchHold += 2;
                        else if (rpm < LaunchRpm) launchHold++;
                        launchHold = Mathf.Min(launchHold, phaseFrame);
                        float progress = Mathf.Clamp01((phaseFrame - launchHold) / (float)LaunchFrames);
                        clutch = 1f - progress;
                        if (progress >= 1f && speed > 2f) { throttleI = throttle; Enter(Phase.Drive); }
                        else if (phaseTime > LaunchGiveUp)
                        {
                            LastEvent = "cannot pull away (engine off? handbrake?) at " + Describe();
                            Enter(Phase.Stopped);
                        }
                    }
                    break;

                case Phase.Drive:
                    {
                        clutch = 0f;
                        float err = wanted - speed;
                        throttleI = Mathf.Clamp(throttleI + 0.12f * err * dt, 0.05f, 0.7f);
                        throttle = Mathf.Clamp(throttleI + 0.12f * err, 0f, 0.85f);
                        brake = err < -0.7f ? Mathf.Clamp(0.1f + 0.15f * (-err - 0.7f), 0f, 0.7f) : 0f;
                        if (brake > 0f) { throttle = 0f; throttleI = Mathf.Min(throttleI, 0.3f); }
                        if (!automatic && frames >= nextShiftFrame) PlanShift(rpm, speed);
                        if (speed < 1.5f && wanted > 3f && phaseFrame > 120) { launchHold = 0; Enter(Phase.Prepare); }   // stuck: start again
                    }
                    break;

                case Phase.Shift:
                    {
                        throttle = 0f;
                        // Slowing down for a curve during a shift: brake too.
                        brake = wanted < speed - 0.7f ? Mathf.Clamp(0.1f + 0.15f * (speed - wanted - 0.7f), 0f, 0.7f) : 0f;
                        if (autoClutch)
                        {
                            // Off the throttle, the key, wait until the box has the gear (the drivetrain works the clutch).
                            clutch = 0f;
                            if (phaseFrame >= 3 && Gear != shiftTarget) SelectGear(shiftTarget);
                            if (phaseFrame > 6 && ((Gear == shiftTarget && !Changing) || phaseFrame > 90)) { nextShiftFrame = frames + ShiftCooldown; Enter(afterShift); }
                            break;
                        }
                        if (releaseFrame < 0)
                        {
                            clutch = 1f;
                            if (phaseFrame >= 3 && Gear != shiftTarget) SelectGear(shiftTarget);
                            if (phaseFrame > ShiftClutchFrames && (Gear == shiftTarget || phaseFrame > ShiftClutchFrames + 30) && !Changing)
                                releaseFrame = phaseFrame;
                        }
                        else
                        {
                            clutch = Mathf.Clamp01(1f - (phaseFrame - releaseFrame) / (float)ShiftReleaseFrames);
                            if (clutch <= 0f) { nextShiftFrame = frames + ShiftCooldown; Enter(afterShift); }
                        }
                    }
                    break;

                case Phase.Stopping:
                    {
                        throttle = 0f;
                        brake = Mathf.Clamp(0.25f + 0.08f * v, 0f, 0.8f);
                        clutch = !autoClutch && (v < 4f || rpm < 1200f) ? 1f : 0f;
                        if (v < 0.3f && phaseFrame > 20)
                        {
                            Enter(Phase.Stopped);
                            LastEvent = "stopped at " + Describe();
                        }
                    }
                    break;

                case Phase.Stopped:
                case Phase.Stalled:
                    throttle = 0f;
                    brake = 0.6f;
                    clutch = autoClutch ? 0f : 1f;
                    if (!automatic && Gear != Neutral && phaseFrame > 5) SelectGear(Neutral);
                    else if (Gear == Neutral && phaseFrame > 30) clutch = 0f;
                    break;
            }

            // Automatic box with auto reverse: brake at a standstill would select reverse.
            if (automatic && v < 0.6f && phase != Phase.Stopping) brake = 0f;

            VirtualInput.SetAxis(thrAxis, Raw(throttle, thrNorm, thrExp));
            VirtualInput.SetAxis(brkAxis, Raw(brake, brkNorm, brkExp));
            VirtualInput.SetAxis(clutchAxis, Raw(clutch, cluNorm, cluExp));
            VirtualInput.SetAxis(steerAxis, steer);
            VirtualInput.SetAxis(hbAxis, Raw(0f, false, false));

            if (frames % TraceEvery == 0 && trace.Length < 2000000)
                trace.AppendFormat(Inv, "{0}\t{1:0.0}\t{2:0.0}\t{3:0.0}\t{4:0.0}\t{5:0.0}\t{6:+0.00;-0.00}\t{7:+0.0;-0.0}\t{8:+0.00;-0.00}\t{9:+0.0000;-0.0000}\t{10}\t{11:0}\t{12:0.00}\t{13:0.00}\t{14:0.000}\t{15}\n",
                                   Time.frameCount, sNow, pos.x, pos.z, speed * 3.6f, wanted * 3.6f, steer, headingError, offRoute, kappaAhead,
                                   Gear, rpm, throttle, brake, car.up.y, phase);
        }

        /// <summary>Axis value the controller turns into <paramref name="x"/> (normalise: (v+1)/2, exponential: v*v).</summary>
        static float Raw(float x, bool normalize, bool exponential)
        {
            x = Mathf.Clamp01(x);
            if (exponential) x = Mathf.Sqrt(x);
            return normalize ? 2f * x - 1f : x;
        }

        void PlanShift(float rpm, float speed)
        {
            float[] ratios = Ratios;
            int g = Gear;
            if (g < First || g >= ratios.Length || ratios[g] <= 0f) return;
            // Up only on a straight and not while slowing down for a curve.
            bool calm = Mathf.Abs(steer) < 0.15f && wanted > speed - 0.5f;
            bool labouring = throttle >= 0.8f && rpm < LabourRpm && speed < wanted - 1.5f;
            int target = -1;
            if (calm && rpm > UpshiftRpm && g + 1 < ratios.Length && ratios[g + 1] > 0f && rpm * ratios[g + 1] / ratios[g] > 1800f) target = g + 1;
            else if ((rpm < DownshiftRpm || labouring) && g > First && rpm * ratios[g - 1] / ratios[g] < 4200f) target = g - 1;
            if (target < 0) return;
            shiftTarget = target;
            afterShift = Phase.Drive;
            Enter(Phase.Shift);
        }

        /// <summary>One step towards <paramref name="target"/>; true when the box is in it.</summary>
        bool SelectGear(int target)
        {
            int g = Gear;
            if (shifter)
            {
                string want = target >= 0 && target < ShifterButtons.Length && target != Neutral ? ShifterButtons[target] : null;
                if (want != heldShifterButton)
                {
                    if (heldShifterButton != null) VirtualInput.Hold(heldShifterButton, false);
                    if (want != null) VirtualInput.Hold(want, true);
                    heldShifterButton = want;
                }
                return g == target && !Changing;
            }
            if (automatic) return true;
            if (g == target && !Changing) return true;
            if (Changing || frames < nextShiftFrame) return false;
            VirtualInput.Press(target > g ? upKey : downKey, 2);
            nextShiftFrame = frames + 8;
            return false;
        }
    }
}
