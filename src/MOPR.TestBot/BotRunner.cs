// Drives a test scenario: main menu -> Continue -> wait for the game (and MOP) to finish loading ->
// run the steps -> write report.json + done.flag -> quit WITHOUT saving.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace MOPR.TestBot
{
    class BotRunner : MonoBehaviour
    {
        public static BotRunner Instance { get; private set; }

        Scenario scenario;
        readonly Report report = new Report();
        readonly Dictionary<string, Dictionary<string, string>> snapshots = new Dictionary<string, Dictionary<string, string>>();
        float startTime;
        float lastHeartbeat;
        bool finished;

        public void Begin(string runFile)
        {
            Instance = this;
            scenario = JsonConvert.DeserializeObject<Scenario>(File.ReadAllText(runFile)) ?? new Scenario();
            report.Scenario = scenario.Name;
            report.Variant = scenario.Variant;
            report.StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Application.runInBackground = true; // the window may lose focus during the run
            startTime = Time.realtimeSinceStartup;
            lastHeartbeat = startTime;
            Log($"TestBot started: scenario '{scenario.Name}', variant '{scenario.Variant}'");
            StartCoroutine(Run());
        }

        void Update()
        {
            if (finished) return;
            float now = Time.realtimeSinceStartup;
            // Watchdog: the scenario coroutine died (exception) or the load never finished.
            if (now - lastHeartbeat > 180f || now - startTime > scenario.LoadTimeoutSeconds + 1800f)
                Finish("watchdog: scenario stopped responding");
        }

        void Beat() => lastHeartbeat = Time.realtimeSinceStartup;

        IEnumerator Run()
        {
            // 0. Tell the runner the menu is up, then wait until it closed the startup popups (welcome window).
            try { File.WriteAllText(Path.Combine(TestBotMod.Folder, "menu.flag"), Time.realtimeSinceStartup.ToString("0.0")); } catch { }
            string menuReady = Path.Combine(TestBotMod.Folder, "menu.ready");
            float readyDeadline = Time.realtimeSinceStartup + Mathf.Max(0f, scenario.MenuReadyTimeoutSeconds);
            while (!File.Exists(menuReady) && Time.realtimeSinceStartup < readyDeadline)
            {
                Beat();
                yield return new WaitForSeconds(0.5f);
            }
            Log(File.Exists(menuReady) ? "runner reports the menu is ready" : "menu.ready not received, continuing anyway");

            if (scenario.StayInMenu)
            {
                yield return StartCoroutine(RunSteps(scenario.Steps));
                Finish(null);
                yield break;
            }

            // 0. Steps in the main menu before the game is loaded (e.g. console commands, settings).
            if (scenario.MenuSteps.Count > 0)
            {
                Log("menu steps:");
                yield return StartCoroutine(RunSteps(scenario.MenuSteps));
            }

            // 1. Main menu: press Continue.
            yield return new WaitForSeconds(3f);
            int attempts = 0;
            while (ModLoader.CurrentScene == CurrentScene.MainMenu && attempts < 6)
            {
                Beat();
                attempts++;
                Log("pressing Continue (attempt " + attempts + ")");
                yield return StartCoroutine(PressContinue());
                for (int i = 0; i < 20 && ModLoader.CurrentScene == CurrentScene.MainMenu; i++)
                    yield return new WaitForSeconds(1f);
            }

            if (ModLoader.CurrentScene == CurrentScene.MainMenu)
            {
                Finish("could not leave the main menu");
                yield break;
            }

            // 2. Wait for the game scene and for the player to become controllable (MOP's load screen).
            float loadStart = Time.realtimeSinceStartup;
            float controllableSince = -1f;
            float nextLoadShot = loadStart + scenario.LoadShotInterval;
            int loadShots = 0;
            while (true)
            {
                Beat();
                if (scenario.LoadShotInterval > 0f && Time.realtimeSinceStartup >= nextLoadShot && loadShots < 40 && controllableSince < 0f)
                {
                    nextLoadShot = Time.realtimeSinceStartup + scenario.LoadShotInterval;
                    loadShots++;
                    Application.CaptureScreenshot(Path.Combine(TestBotMod.Folder, $"shot_load_{loadShots:00}.png"));
                }
                if (Time.realtimeSinceStartup - loadStart > scenario.LoadTimeoutSeconds)
                {
                    Finish("timeout while loading the game");
                    yield break;
                }

                bool ready = ModLoader.CurrentScene == CurrentScene.Game && PlayerControllable();
                if (ready)
                {
                    if (controllableSince < 0f) controllableSince = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - controllableSince >= 5f) break;
                }
                else controllableSince = -1f;

                yield return new WaitForSeconds(0.5f);
            }

            report.LoadSeconds = Time.realtimeSinceStartup - loadStart;
            report.MoprPresent = ModLoader.IsModPresent("MOPR");
            report.MoprVersion = ModLoader.GetModVersionByID("MOPR");
            report.Mods = ModLoader.LoadedMods.Where(m => !m.ID.StartsWith("MSCLoader_")).Select(m => m.ID + " " + m.Version + (m.isDisabled ? " (disabled)" : "")).ToList();
            Log($"game ready after {report.LoadSeconds:0.0} s, MOPR present: {report.MoprPresent} {report.MoprVersion}");

            for (float t = 0; t < scenario.SettleSeconds; t += 1f)
            {
                Beat();
                yield return new WaitForSeconds(1f);
            }

            // 3. Steps.
            yield return StartCoroutine(RunSteps(scenario.Steps));
            Finish(null);
        }

        IEnumerator RunSteps(List<Step> steps)
        {
            foreach (Step step in steps)
            {
                Beat();
                string op = (step.Op ?? "").ToLowerInvariant();
                Log($"step: {op} {step.Label}");
                switch (op)
                {
                    case "log":
                        Log(step.Text);
                        break;
                    case "wait":
                        for (float t = 0; t < step.Seconds; t += 1f) { Beat(); yield return new WaitForSeconds(1f); }
                        break;
                    case "teleport":
                        Teleport(step);
                        yield return new WaitForSeconds(1f);
                        break;
                    case "measure":
                        yield return StartCoroutine(Measure(step));
                        break;
                    case "profile":
                        yield return StartCoroutine(Profile(step));
                        break;
                    case "snapshot":
                        TakeSnapshot(step.Label);
                        break;
                    case "compare":
                        CompareSnapshots(step.A, step.B);
                        break;
                    case "engine":
                        {
                            string err = EngineBridge.SetMode(step.Mode);
                            EngineBridge.SetDetail(step.Detail);
                            Log($"engine mode -> {EngineBridge.Mode}" + (err != null ? " (error: " + err + ")" : ""));
                            yield return null;
                        }
                        break;
                    case "enginestats":
                        RecordEngine(step.Label);
                        break;
                    case "ab":
                        yield return StartCoroutine(AbTest(step));
                        break;
                    case "abtoggle":
                        yield return StartCoroutine(AbToggle(step));
                        break;
                    case "gcprobe":
                        for (int i = 0; i < 3; i++)
                        {
                            yield return null;
                            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                            GC.Collect();
                            sw.Stop();
                            report.GcPauseMs.Add((float)sw.Elapsed.TotalMilliseconds);
                            Log($"gcprobe: full collection took {sw.Elapsed.TotalMilliseconds:0.0} ms, heap {GC.GetTotalMemory(false) / 1048576f:0.0} MB");
                        }
                        break;
                    case "dumpui":
                        DumpScene(step.Label);
                        break;
                    case "find":
                        {
                            List<GameObject> found = FsmDumper.Find(step.Text, step.Max);
                            WriteArtifact("find_" + step.Label + ".txt", FsmDumper.ListObjects(found));
                            Log($"find {step.Label}: {found.Count} objects for '{step.Text}'");
                        }
                        break;
                    case "dumpfsm":
                        {
                            // Path: one object by hierarchy path (GameObject.Find, or a search when inactive); Text: name terms.
                            List<GameObject> roots = new List<GameObject>();
                            if (!string.IsNullOrEmpty(step.Path))
                            {
                                GameObject go = GameObject.Find(step.Path) ?? FsmDumper.Find(step.Path.Split('/').Last(), 0)
                                    .FirstOrDefault(g => PathOf(g.transform).EndsWith(step.Path));
                                if (go != null) roots.Add(go);
                            }
                            if (!string.IsNullOrEmpty(step.Text)) roots.AddRange(FsmDumper.Find(step.Text, step.Max));
                            string dump = FsmDumper.Dump(roots, step.Recursive);
                            WriteArtifact("dumpfsm_" + step.Label + ".txt", dump);
                            Log($"dumpfsm {step.Label}: {roots.Count} roots, {dump.Length / 1024} KB");
                        }
                        break;
                    case "fsmset":
                        {
                            string err = GameOps.SetVariable(step.Path, step.Fsm, step.Var, step.Value);
                            Log($"fsmset {step.Path}|{step.Fsm}|{step.Var} = {step.Value}" + (err != null ? "  ERROR: " + err : ""));
                        }
                        break;
                    case "fsmevent":
                        {
                            string err = GameOps.SendEvent(step.Path, step.Fsm, step.Event);
                            Log($"fsmevent {step.Path}|{step.Fsm} <- {step.Event}" + (err != null ? "  ERROR: " + err : ""));
                        }
                        break;
                    case "activate":
                        {
                            string err = GameOps.SetActive(step.Path, step.Active);
                            Log($"activate {step.Path} = {step.Active}" + (err != null ? "  ERROR: " + err : ""));
                        }
                        break;
                    case "align":
                        {
                            Vector3 offset = step.Pos != null && step.Pos.Length == 3 ? new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]) : Vector3.zero;
                            string err = GameOps.Align(step.Path, step.Anchor, step.Target, offset);
                            Log($"align {step.Path} ({step.Anchor ?? "self"}) -> {step.Target}" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "watch":
                        yield return StartCoroutine(Watch(step));
                        break;
                    case "screenshot":
                        {
                            // Written at the end of the frame; wait two frames so the file exists for the next step.
                            string file = Path.Combine(TestBotMod.Folder, "shot_" + (step.Label ?? "x") + ".png");
                            Application.CaptureScreenshot(file);
                            yield return null;
                            yield return null;
                            Log("screenshot " + step.Label);
                        }
                        break;
                    case "opensettings":
                        {
                            // Slide the mod menu in first (its open animation resets the view), then fill the page.
                            GameOps.SetModMenuOpened(true);
                            yield return new WaitForSeconds(1f);
                            string err = GameOps.OpenModSettings(step.Text);
                            Log($"opensettings {step.Text}" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForSeconds(1f);
                            Log("  " + GameOps.ModSettingsState());
                        }
                        break;
                    case "modmenu":
                        // Value "open" / "close": slide MSCLoader's mod list in or out (its arrow button).
                        GameOps.SetModMenuOpened(step.Value != "close");
                        Log("modmenu " + (step.Value ?? "open"));
                        yield return new WaitForSeconds(1f);
                        break;
                    case "closesettings":
                        {
                            string err = GameOps.CloseModSettings();
                            Log("closesettings" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForSeconds(0.5f);
                        }
                        break;
                    case "modsetting":
                        {
                            string err = GameOps.SetModSetting(step.Text, step.Var, step.Value, out string previous);
                            Log($"modsetting {step.Text}.{step.Var}: {previous} -> {step.Value}" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForSeconds(0.5f);
                        }
                        break;
                    case "console":
                        {
                            // An MSCLoader console command line, as typed by the user (e.g. "mopr reload all").
                            string err = GameOps.RunConsoleCommand(step.Text);
                            Log($"console {step.Text}" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForSeconds(0.5f);
                        }
                        break;
                    case "mopstate":
                        {
                            // What MOP decided to manage (objects, items, vehicles, places, sectors, Satsuma), for diffing builds.
                            string dump = GameOps.MoprState(out string summary);
                            WriteArtifact("mopstate_" + (step.Label ?? "x") + ".txt", dump);
                            Log($"mopstate {step.Label}: {summary}");
                        }
                        break;
                    case "dumpsettings":
                        {
                            string dump = GameOps.DumpModSettings(step.Text, out int live, out int stale);
                            WriteArtifact("settings_" + (step.Label ?? "x") + ".txt", dump);
                            Log($"dumpsettings {step.Label}: {live} live labels, {stale} differ from the setting name");
                        }
                        break;
                    case "scrollsettings":
                        {
                            float pos = 1f;
                            float.TryParse(step.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out pos);
                            string err = GameOps.ScrollModSettings(pos);
                            Log($"scrollsettings {pos}" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForSeconds(0.5f);
                        }
                        break;
                    case "renderers":
                        {
                            string err = GameOps.SetRenderers(step.Path, step.Active, out int n);
                            Log($"renderers {step.Path} = {step.Active} ({n})" + (err != null ? "  ERROR: " + err : ""));
                            yield return null;
                        }
                        break;
                    case "castshadows":
                        {
                            string err = GameOps.SetShadowCasting(step.Path, step.Active, out int n);
                            Log($"castshadows {step.Path} = {step.Active} ({n})" + (err != null ? "  ERROR: " + err : ""));
                            yield return null;
                        }
                        break;
                    case "quality":
                        {
                            string err = GameOps.SetQuality(step.Var, step.Value, out string previous);
                            Log($"quality {step.Var}: {previous} -> {step.Value}" + (err != null ? "  ERROR: " + err : ""));
                            yield return null;
                        }
                        break;
                    case "census":
                        Log($"census {step.Path}: " + GameOps.Census(step.Path));
                        break;
                    case "bakeshow":
                        {
                            // Active = true: bake Path (if not yet) and draw the combined meshes; false: originals, and drop the bake.
                            if (step.Active)
                            {
                                string err = GameOps.Bake(step.Path, out string summary);
                                Log("bake " + step.Path + ": " + (err ?? summary));
                                GameOps.ShowBaked(true);
                            }
                            else GameOps.Unbake();
                            yield return null;
                            yield return null;
                        }
                        break;
                    case "actiontypes":
                        {
                            string hist;
                            try { hist = GameOps.ActionTypes(step.Path); }
                            catch (Exception e) { hist = "error: " + e.Message; }
                            WriteArtifact("fsmhist_actions_" + (step.Label ?? "x") + ".txt", hist);
                            Log($"actiontypes {step.Label}: " + hist.Split('\n')[0]);
                        }
                        break;
                    case "fsmhist":
                        {
                            string hist = GameOps.FsmHistogram(step.Path, step.Max);
                            WriteArtifact("fsmhist_" + (step.Label ?? "x") + ".txt", hist);
                            Log($"fsmhist {step.Label}: " + hist.Split('\n')[0]);
                        }
                        break;
                    case "dummies":
                        GameOps.SetDummies(step.Max);
                        Log($"dummies: {step.Max} MonoBehaviours with empty Update + LateUpdate");
                        yield return null;
                        break;
                    case "colliders":
                        {
                            string err = GameOps.SetColliders(step.Path, step.Active, out int n);
                            Log($"colliders {step.Path} = {step.Active} ({n})" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "lights":
                        {
                            GameObject pl = GameObject.Find("PLAYER");
                            Log("lights: " + GameOps.Lights(pl != null ? pl.transform : null, step.Seconds > 0 ? step.Seconds : 40f));
                        }
                        break;
                    case "resolution":
                        {
                            // Pos = [width, height]; GPU-bound frames scale with the pixel count, CPU-bound ones do not.
                            if (step.Pos != null && step.Pos.Length >= 2)
                            {
                                Screen.SetResolution((int)step.Pos[0], (int)step.Pos[1], false);
                                for (int i = 0; i < 30; i++) yield return null;
                                Log($"resolution -> {Screen.width}x{Screen.height}");
                            }
                        }
                        break;
                    case "setmember":
                        {
                            string err = GameOps.SetMember(step.Path, step.Var, step.Value);
                            Log($"setmember {step.Path}|{step.Var} = {step.Value}" + (err != null ? "  ERROR: " + err : ""));
                        }
                        break;
                    case "place":
                        {
                            Vector3 offset = step.Pos != null && step.Pos.Length == 3 ? new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]) : Vector3.zero;
                            string err = GameOps.Place(step.Path, step.Target, offset);
                            Log($"place {step.Path} at {step.Target ?? "self"} + {offset}" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "callcount":
                        {
                            // Text = "Type:Method,Type:Method": calls per frame and time of each, for Seconds.
                            string err = CallCounter.Install(step.Text);
                            if (err != null) { Log("callcount: " + err); CallCounter.Uninstall(); break; }
                            yield return null;
                            CallCounter.Reset();
                            CallCounter.Counting = true;
                            int f0 = Time.frameCount;
                            for (float t = 0; t < Mathf.Max(1f, step.Seconds); t += 1f) { Beat(); yield return new WaitForSeconds(1f); }
                            CallCounter.Counting = false;
                            Log($"callcount {step.Label}: " + CallCounter.Summary(Time.frameCount - f0));
                            CallCounter.Uninstall();
                        }
                        break;
                    case "drive":
                        {
                            // Target = vehicle, Path = parent of the route waypoints, Speed = km/h. Runs in the background.
                            string err = Driver.Start(step.Target, step.Path, step.Max, step.Last, step.Speed, out int count);   // Max = first, Last = last waypoint
                            Log($"drive {step.Target} along {step.Path} ({count} waypoints) at {step.Speed:0} km/h" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "movecar":
                        {
                            // Target = vehicle, Pos = offset in its yaw frame: the EMPTY car is moved (heading kept).
                            Vector3 off = step.Pos != null && step.Pos.Length == 3 ? new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]) : Vector3.zero;
                            string err = Driver.MoveBy(step.Target, off, out int adopted);
                            Log($"movecar {step.Target} by {off} ({adopted} anti-shake pose(s) moved too)" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "placecar":
                        {
                            // Target = vehicle, Path = route: the EMPTY car is placed at the start of the route.
                            string err = Driver.Place(step.Target, step.Path, step.Max, out int adopted);   // Max = first waypoint
                            Log($"placecar {step.Target} at the start of {step.Path} ({adopted} anti-shake pose(s) moved too)" + (err != null ? "  ERROR: " + err : ""));
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "spin":
                        {
                            // Speed = degrees per second (0 stops): turns the standing player every frame, in the background.
                            string err = step.Speed != 0f ? Spinner.Start(step.Speed) : null;
                            if (step.Speed == 0f) Spinner.Stop();
                            Log($"spin {step.Speed:0} deg/s" + (err != null ? "  ERROR: " + err : ""));
                        }
                        break;
                    case "drivestop":
                        Log("drivestop: " + Driver.Status);
                        Driver.Stop();
                        break;
                    case "rendstate":
                        RenderState(step.Label, step.Path);
                        break;
                    case "rendcompare":
                        RenderCompare(step.A, step.B);
                        break;
                    case "camshot":
                        yield return StartCoroutine(CamShot(step));
                        break;
                    case "shadowinfo":
                        {
                            string info = ShadowInfo();
                            WriteArtifact("tree_shadows_" + (step.Label ?? "x") + ".txt", info);
                            Log("shadowinfo " + step.Label + ": " + info.Split('\n')[0]);
                        }
                        break;
                    case "shadowsweep":
                        yield return StartCoroutine(ShadowSweep(step));
                        break;
                    case "camstate":
                        {
                            Camera c = Camera.main;
                            Log(c == null ? "camstate: no main camera"
                                : $"camstate {step.Label}: {PathOf(c.transform)} far {c.farClipPlane:0.#} near {c.nearClipPlane:0.###} fov {c.fieldOfView:0.#}");
                        }
                        break;
                    case "meshclip":
                        MeshClip(step.Label, step.Path, step.Target, step.Anchor, step.Seconds);
                        break;
                    case "nudge":
                        {
                            // Path = object, Pos = offset in Target's frame (world if no Target).
                            GameObject go = GameOps.Resolve(step.Path);
                            GameObject frame = string.IsNullOrEmpty(step.Target) ? null : GameOps.Resolve(step.Target);
                            Vector3 v = step.Pos != null && step.Pos.Length == 3 ? new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]) : Vector3.zero;
                            if (go == null) { Log("nudge: not found " + step.Path); break; }
                            if (frame != null) v = frame.transform.TransformDirection(v);
                            go.transform.position += v;
                            Log($"nudge {PathOf(go.transform)} by {v.ToString("F4")} -> local {go.transform.localPosition.ToString("F4")}");
                        }
                        break;
                    case "rendtoggle":
                        {
                            // Path = renderer object. Text = when it is switched back on after being switched off now:
                            // "update" (next frame, from a coroutine), "precull" (inside the next Camera.onPreCull),
                            // "now" (immediately; also a repair test for a renderer that is on but never drawn).
                            GameObject go = GameOps.Resolve(step.Path);
                            Renderer r = go != null ? go.GetComponent<Renderer>() : null;
                            if (r == null) { Log("rendtoggle: no renderer at " + step.Path); break; }
                            string mode = string.IsNullOrEmpty(step.Text) ? "update" : step.Text;
                            r.enabled = false;
                            if (mode == "now") r.enabled = true;
                            else if (mode == "precull")
                            {
                                Camera.CameraCallback cb = null;
                                cb = cam => { Camera.onPreCull -= cb; if (r != null) r.enabled = true; };
                                Camera.onPreCull += cb;
                            }
                            yield return null;
                            if (mode == "update") r.enabled = true;
                            yield return null;
                            yield return null;
                            Log($"rendtoggle {mode} {PathOf(r.transform)}: enabled {r.enabled}, visible {r.isVisible}");
                        }
                        break;
                    case "push":
                        {
                            // Path = rigidbody object, Pos = velocity change in Target's frame (or world), applied at its centre of mass.
                            GameObject go = GameOps.Resolve(step.Path);
                            Rigidbody body = go != null ? go.GetComponentInParent<Rigidbody>() : null;
                            GameObject frame = string.IsNullOrEmpty(step.Target) ? null : GameOps.Resolve(step.Target);
                            Vector3 v = step.Pos != null && step.Pos.Length == 3 ? new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]) : Vector3.zero;
                            if (frame != null) v = frame.transform.TransformDirection(v);
                            if (body == null) { Log("push: no rigidbody at " + step.Path); break; }
                            bool wasKinematic = body.isKinematic;
                            body.WakeUp();
                            body.AddForce(v, ForceMode.VelocityChange);
                            Log($"push {PathOf(body.transform)} by {v} (kinematic {wasKinematic})");
                            yield return new WaitForFixedUpdate();
                        }
                        break;
                    case "viewdiff":
                        yield return StartCoroutine(ViewDiff(step));
                        break;
                    case "cameralist":
                        {
                            // Every enabled camera: what it renders and how (written as tree_<label>.txt).
                            StringBuilder sb = new StringBuilder();
                            foreach (Camera c in Camera.allCameras.OrderBy(c => c.depth))
                            {
                                RenderTexture rt = c.targetTexture;
                                float[] lcd = c.layerCullDistances;
                                int culledLayers = lcd != null ? lcd.Count(d => d > 0f) : 0;
                                sb.AppendLine($"{PathOf(c.transform)}: depth {c.depth}, mask 0x{c.cullingMask:X8}, fov {c.fieldOfView:0.#}, " +
                                    $"near {c.nearClipPlane:0.###}, far {c.farClipPlane:0.#}, target {(rt != null ? rt.width + "x" + rt.height : "screen")}, " +
                                    $"path {c.renderingPath} (actual {c.actualRenderingPath}), clear {c.clearFlags}, hdr {c.hdr}, occlusion {c.useOcclusionCulling}, " +
                                    $"layer cull distances on {culledLayers} layers, spherical {c.layerCullSpherical}, " +
                                    $"components: {string.Join(" ", c.GetComponents<Behaviour>().Where(b => b != null && b.enabled && !(b is Camera)).Select(b => b.GetType().Name).ToArray())}");
                            }
                            WriteArtifact("tree_" + step.Label + ".txt", sb.ToString());
                            Log($"cameralist {step.Label}: {Camera.allCameras.Length} cameras");
                        }
                        break;
                    case "colliderlist":
                        {
                            // Path = root: every collider below it (written as tree_<label>.txt) and a summary by layer.
                            GameObject go = GameOps.Resolve(step.Path);
                            if (go == null) { Log("colliderlist: not found " + step.Path); break; }
                            StringBuilder sb = new StringBuilder();
                            Dictionary<string, int> byKind = new Dictionary<string, int>();
                            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
                            {
                                string kind = LayerMask.LayerToName(c.gameObject.layer) + (c.isTrigger ? " trigger" : " solid") +
                                              (c.enabled && c.gameObject.activeInHierarchy ? "" : " (off)");
                                int n; byKind.TryGetValue(kind, out n); byKind[kind] = n + 1;
                                Rigidbody body = c.attachedRigidbody;
                                sb.AppendLine($"{PathOf(c.transform)}  {c.GetType().Name}  {kind}  body {(body != null ? body.name : "-")}");
                            }
                            WriteArtifact("tree_" + step.Label + ".txt", sb.ToString());
                            Log($"colliderlist {step.Label}: " + string.Join(", ", byKind.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value).ToArray()));
                        }
                        break;
                    case "tree":
                        {
                            // Path = root, Max = depth (default 3): every descendant with position and components.
                            GameObject go = GameOps.Resolve(step.Path);
                            if (go == null) { Log("tree: not found " + step.Path); break; }
                            StringBuilder sb = new StringBuilder();
                            TreeOf(go.transform, 0, step.Max > 0 ? step.Max : 3, sb);
                            WriteArtifact("tree_" + step.Label + ".txt", sb.ToString());
                            Log($"tree {step.Label}: written");
                        }
                        break;
                    case "staticbool":
                        {
                            // Path = "Namespace.Type.Member", Value = true/false.
                            string err = GameOps.StaticBoolSetter(step.Path, out Action<bool> set);
                            if (err == null) set(string.Equals(step.Value, "true", StringComparison.OrdinalIgnoreCase));
                            Log($"staticbool {step.Path} = {step.Value}" + (err != null ? "  ERROR: " + err : ""));
                        }
                        break;
                    case "call":
                        {
                            string err;
                            try { err = GameOps.Call(step.Text); }
                            catch (Exception e) { err = (e.InnerException ?? e).ToString(); }
                            Log($"call {step.Text}" + (err != null ? "  ERROR: " + err : " ok"));
                        }
                        break;
                    case "quit":
                        Finish(null);
                        yield break;
                    default:
                        Log("unknown step op: " + step.Op);
                        break;
                }
                if (finished) yield break;
            }
        }

        static void WriteArtifact(string fileName, string content)
        {
            try { File.WriteAllText(Path.Combine(TestBotMod.Folder, fileName), content); }
            catch (Exception e) { Debug.Log("[TestBot] cannot write " + fileName + ": " + e.Message); }
        }

        /// <summary>
        /// Writes the active objects of the current scene (depth 4) and every PlayMaker FSM on them with its
        /// active state to dumpui_&lt;label&gt;.txt. Used to identify menu popups.
        /// </summary>
        void DumpScene(string label)
        {
            StringBuilder sb = new StringBuilder();
            int lines = 0;
            foreach (GameObject root in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (root.transform.parent != null) continue;
                DumpObject(root.transform, 0, sb, ref lines);
            }
            string file = Path.Combine(TestBotMod.Folder, "dumpui_" + (label ?? "scene") + ".txt");
            try { File.WriteAllText(file, sb.ToString()); } catch { }
            Log($"dumpui {label}: {lines} active objects written");
        }

        /// <summary>
        /// Same view, feature off and on: Path = static bool (true = B). The main camera renders the same pose
        /// (relative to Target, e.g. the car, so physics jitter of the car does not count) at the end of three
        /// frames: A, A again (the noise floor: temporal effects, the world moving relative to the car) and B.
        /// Writes shot_[label]_a.png / _b.png and logs the share of differing pixels.
        /// </summary>
        IEnumerator ViewDiff(Step step)
        {
            string err = GameOps.StaticBoolSetter(step.Path, out Action<bool> set);
            Camera cam = Camera.main;
            if (err != null || cam == null) { Log("viewdiff: " + (err ?? "no main camera")); yield break; }
            GameObject refGo = string.IsNullOrEmpty(step.Target) ? null : GameOps.Resolve(step.Target);
            Transform refT = refGo != null ? refGo.transform : null;

            set(false);
            yield return null;
            yield return new WaitForEndOfFrame();
            Vector3 lp = refT != null ? refT.InverseTransformPoint(cam.transform.position) : cam.transform.position;
            Quaternion lr = refT != null ? Quaternion.Inverse(refT.rotation) * cam.transform.rotation : cam.transform.rotation;
            Texture2D a1 = RenderAt(cam, refT, lp, lr);
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D a2 = RenderAt(cam, refT, lp, lr);
            set(true);
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D b = RenderAt(cam, refT, lp, lr);
            set(false);

            float noise = DiffPercent(a1, a2), effect = DiffPercent(a1, b);
            try
            {
                File.WriteAllBytes(Path.Combine(TestBotMod.Folder, "shot_" + step.Label + "_a.png"), a1.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(TestBotMod.Folder, "shot_" + step.Label + "_b.png"), b.EncodeToPNG());
            }
            catch { }
            Destroy(a1); Destroy(a2); Destroy(b);
            Log($"viewdiff {step.Label}: A vs A {noise:0.000}% pixels differ (noise), A vs B {effect:0.000}% ({step.Path} = true)");
        }

        static Texture2D RenderAt(Camera cam, Transform refT, Vector3 lp, Quaternion lr)
        {
            Vector3 oldPos = cam.transform.position;
            Quaternion oldRot = cam.transform.rotation;
            int w = Screen.width, h = Screen.height;
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 24);
            RenderTexture oldTarget = cam.targetTexture;
            try
            {
                cam.transform.position = refT != null ? refT.TransformPoint(lp) : lp;
                cam.transform.rotation = refT != null ? refT.rotation * lr : lr;
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                Texture2D t = new Texture2D(w, h, TextureFormat.RGB24, false);
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t.Apply();
                return t;
            }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = oldTarget;
                cam.transform.position = oldPos;
                cam.transform.rotation = oldRot;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        static float DiffPercent(Texture2D a, Texture2D b)
        {
            Color32[] pa = a.GetPixels32(), pb = b.GetPixels32();
            int n = Math.Min(pa.Length, pb.Length), differ = 0;
            for (int i = 0; i < n; i++)
            {
                int d = Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Max(Math.Abs(pa[i].g - pb[i].g), Math.Abs(pa[i].b - pb[i].b)));
                if (d > 8) differ++;
            }
            return n > 0 ? 100f * differ / n : 0f;
        }

        static void TreeOf(Transform t, int depth, int maxDepth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " [off]")
              .Append("  pos").Append(t.position.ToString("F1"));
            foreach (Component c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) sb.Append(' ').Append(c is PlayMakerFSM f ? "FSM:" + f.FsmName : c.GetType().Name);
            sb.AppendLine();
            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; i++) TreeOf(t.GetChild(i), depth + 1, maxDepth, sb);
        }

        static void DumpObject(Transform t, int depth, StringBuilder sb, ref int lines)
        {
            if (!t.gameObject.activeInHierarchy || lines > 4000) return;
            lines++;
            sb.Append(' ', depth * 2).Append(t.name);
            foreach (PlayMakerFSM fsm in t.GetComponents<PlayMakerFSM>())
                sb.Append("  [").Append(fsm.FsmName).Append(": ").Append(fsm.ActiveStateName).Append(']');
            Collider c = t.GetComponent<Collider>();
            if (c != null && c.enabled) sb.Append("  {collider}");
            sb.AppendLine();
            if (depth >= 4) return;
            for (int i = 0; i < t.childCount; i++) DumpObject(t.GetChild(i), depth + 1, sb, ref lines);
        }

        // ----- Steps ----------------------------------------------------------------------------------

        IEnumerator PressContinue()
        {
            // Same technique MOP uses for its automatic scene restart (Common/MenuHelper.cs).
            GameObject ui = GameObject.Find("Interface");
            Transform t = ui != null ? ui.transform.Find("Buttons/ButtonContinue") : null;
            if (t == null || !t.gameObject.activeSelf)
            {
                Log("Continue button not available (no save?)");
                yield break;
            }

            PlayMakerFSM fsm = t.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "SetSize");
            FsmState state = fsm != null ? fsm.FsmStates.FirstOrDefault(s => s.Name == "Action") : null;
            if (fsm == null || state == null)
            {
                Log("Continue button FSM not found");
                yield break;
            }

            for (int i = 0; i < 100 && !state.ActionsLoaded; i++)
                yield return null;

            FsmStateAction[] actions = state.Actions;
            if (actions.Length > 2)
            {
                actions[2].Active = false;
                actions[2].Enabled = false;
                state.Actions = actions;
            }
            fsm.SendEvent("OVER");
            fsm.SendEvent("DOWN");
        }

        static bool PlayerControllable()
        {
            GameObject player = GameObject.Find("PLAYER");
            if (player == null) return false;
            CharacterController cc = player.GetComponent<CharacterController>();
            if (cc == null || !cc.enabled) return false;
            FsmBool inMenu = FsmVariables.GlobalVariables.FindFsmBool("PlayerInMenu");
            return inMenu == null || !inMenu.Value;
        }

        void Teleport(Step step)
        {
            GameObject player = GameObject.Find("PLAYER");
            if (player == null || step.Pos == null || step.Pos.Length != 3) { Log("teleport: no player or position"); return; }
            if (player.transform.parent != null) { Log("teleport skipped: player is attached to " + player.transform.parent.name); return; }

            Vector3 target = new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]);
            if (!string.IsNullOrEmpty(step.Path))
            {
                // Relative teleport: Pos is an offset from the object.
                GameObject anchor = GameOps.Resolve(step.Path);
                if (anchor == null) { Log("teleport: object not found: " + step.Path); return; }
                if (step.Local) target = Quaternion.Euler(0f, anchor.transform.eulerAngles.y, 0f) * target;
                target += anchor.transform.position;
            }

            // Heading: towards Target if given (plus Yaw as an extra turn, e.g. 180 = look away), else Yaw.
            float yaw = step.Yaw;
            if (!string.IsNullOrEmpty(step.Target))
            {
                GameObject look = GameOps.Resolve(step.Target);
                if (look != null)
                {
                    Vector3 d = look.transform.position - target;
                    yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + step.Yaw;
                }
            }

            CharacterController cc = player.GetComponent<CharacterController>();
            bool was = cc != null && cc.enabled;
            if (cc != null) cc.enabled = false;
            player.transform.position = target;
            player.transform.eulerAngles = new Vector3(0f, yaw, 0f);
            if (cc != null) cc.enabled = was;
            Log($"teleport -> ({target.x:0.0}, {target.y:0.0}, {target.z:0.0}) yaw {yaw:0}");
        }

        /// <summary>Samples the probes every Interval seconds for Seconds; writes watch_&lt;label&gt;.tsv and logs every row.</summary>
        IEnumerator Watch(Step step)
        {
            List<string> probes = step.Probes ?? new List<string>();
            List<string> names = probes.Select(p => { int eq = p.IndexOf('='); return eq > 0 && p.IndexOf('|') > eq ? p.Substring(0, eq) : p; }).ToList();
            StringBuilder tsv = new StringBuilder();
            tsv.Append("t\t").AppendLine(string.Join("\t", names.ToArray()));
            Log("watch " + step.Label + ": t | " + string.Join(" | ", names.ToArray()));

            GameObject player = GameObject.Find("PLAYER");
            float begin = Time.realtimeSinceStartup;
            float interval = Mathf.Max(0.1f, step.Interval);
            while (true)
            {
                float t = Time.realtimeSinceStartup - begin;
                string[] values = probes.Select(p => GameOps.Read(p, player != null ? player.transform : null)).ToArray();
                tsv.Append(t.ToString("0.0")).Append('\t').AppendLine(string.Join("\t", values));
                Log($"watch {step.Label} {t,5:0.0}: " + string.Join(" | ", values));
                if (t >= step.Seconds) break;
                Beat();
                yield return new WaitForSeconds(interval);
            }
            WriteArtifact("watch_" + step.Label + ".tsv", tsv.ToString());
        }

        IEnumerator Measure(Step step)
        {
            yield return null;
            List<float> frames = new List<float>(4096);
            int gc0 = SafeGcCount();
            float mem0 = GC.GetTotalMemory(false) / 1048576f;
            long prevMem = GC.GetTotalMemory(false), allocated = 0;
            float begin = Time.realtimeSinceStartup;
            float end = begin + Mathf.Max(1f, step.Seconds);
            int lastGc = gc0;
            List<float> gcFrames = new List<float>();
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                float frameMs = Time.unscaledDeltaTime * 1000f;
                frames.Add(frameMs);
                long mem = GC.GetTotalMemory(false);
                if (mem > prevMem) allocated += mem - prevMem;   // drops are collections
                prevMem = mem;
                int gcNow = SafeGcCount();
                if (gcNow != lastGc) { gcFrames.Add(frameMs); lastGc = gcNow; }   // the frame that contained the collection
                if (frames.Count % 60 == 0) Beat();
            }

            MeasureResult r = new MeasureResult { Label = step.Label, Seconds = step.Seconds, Frames = frames.Count };
            if (frames.Count > 0)
            {
                List<float> sorted = new List<float>(frames);
                sorted.Sort();
                float total = 0f;
                foreach (float f in frames) total += f;
                r.AvgFps = frames.Count / (total / 1000f);
                r.FrameMsP50 = Percentile(sorted, 0.50f);
                r.FrameMsP95 = Percentile(sorted, 0.95f);
                r.FrameMsP99 = Percentile(sorted, 0.99f);
                r.FrameMsMax = sorted[sorted.Count - 1];
                // 1% low: average FPS of the slowest 1% frames.
                int n = Mathf.Max(1, sorted.Count / 100);
                float slow = 0f;
                for (int i = sorted.Count - n; i < sorted.Count; i++) slow += sorted[i];
                r.Low1PercentFps = 1000f / (slow / n);
            }
            r.GcCollections = SafeGcCount() - gc0;
            r.GcFrameMs = gcFrames;
            r.AllocMBPerSec = allocated / 1048576f / Mathf.Max(0.001f, Time.realtimeSinceStartup - begin);
            r.ManagedMBStart = mem0;
            r.ManagedMBEnd = GC.GetTotalMemory(false) / 1048576f;
            r.ActiveFsms = PlayMakerFSM.FsmList.Count;
            Rigidbody[] rbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
            r.Rigidbodies = rbs.Length;
            r.AwakeRigidbodies = rbs.Count(b => !b.isKinematic && !b.IsSleeping());
            r.ActiveRenderers = UnityEngine.Object.FindObjectsOfType<Renderer>().Count(x => x.enabled && x.isVisible);
            GameObject player = GameObject.Find("PLAYER");
            if (player != null) { Vector3 p = player.transform.position; r.PlayerPos = new[] { p.x, p.y, p.z }; }
            r.EngineMode = EngineBridge.Mode;

            report.Measures.Add(r);
            Log($"measure {r.Label} [engine {r.EngineMode}]: {r.AvgFps:0.0} fps avg, 1% low {r.Low1PercentFps:0.0}, p99 {r.FrameMsP99:0.0} ms, GC {r.GcCollections}, " +
                $"alloc {r.AllocMBPerSec:0.00} MB/s, FSMs {r.ActiveFsms}, visible renderers {r.ActiveRenderers}");
        }

        IEnumerator Profile(Step step)
        {
            ScriptProfiler.TrackAlloc = step.Alloc;
            ScriptProfiler.Extended = step.Extended;
            ScriptProfiler.Begin(Log);
            if (step.Detail) ActionProfiler.Begin(Log);
            yield return null;
            bool probe = RenderProbe.Begin();
            int frames = 0;
            float total = 0f;
            float end = Time.realtimeSinceStartup + Mathf.Max(1f, step.Seconds);
            while (Time.realtimeSinceStartup < end)
            {
                if (step.Detail) ActionProfiler.FrameBoundary();
                yield return null;
                frames++;
                total += Time.unscaledDeltaTime * 1000f;
                if (frames % 60 == 0) Beat();
            }
            List<ProfileEntry> cameras = new List<ProfileEntry>();
            float renderMs = RenderProbe.End(frames, out int rendersX100, cameras, out float otherCamMs);
            ProfileResult r = ScriptProfiler.End(step.Label, step.Seconds, frames, frames > 0 ? total / frames : 0f);
            r.MainCameraRenderMs = probe ? renderMs : -1f;
            r.MainCameraRendersPerFrame = rendersX100 / 100f;
            r.OtherCamerasRenderMs = otherCamMs;
            r.ByCamera = cameras;
            if (cameras.Count > 0)
                Log($"cameras {step.Label}: " + string.Join(", ", cameras.Take(6).Select(c => $"{c.Name} {c.MsPerFrame:0.00} ms x{c.CallsPerFrame:0.0}").ToArray()));
            if (step.Detail)
            {
                ActionProfiler.End(r, frames);
                Log($"action profile {step.Label}: actions self {r.ActionSelfMsPerFrame:0.00} ms/frame, {r.ActionTypesInUse} types, {r.ActionMethodsPatched} methods");
            }
            if (step.Alloc)
                Log($"alloc profile {step.Label}: scripts {r.AllocKBPerSec:0.0} KB/s, actions (self) {r.ActionAllocKBPerSec:0.0} KB/s");
            ScriptProfiler.TrackAlloc = false;
            r.EngineMode = EngineBridge.Mode;
            report.Profiles.Add(r);
            Log($"profile {r.Label} [engine {r.EngineMode}]: frame {r.FrameMsAvg:0.00} ms, scripts {r.ScriptMsPerFrame:0.00} ms (PlayMaker {r.PlayMakerMsPerFrame:0.00} ms), " +
                $"corrected scripts {r.ScriptMsPerFrameCorrected:0.00} ms (PlayMaker {r.PlayMakerMsPerFrameCorrected:0.00} ms), {r.CallsPerFrame:0} calls/frame, " +
                $"main camera render {r.MainCameraRenderMs:0.00} ms ({r.MainCameraRendersPerFrame:0.00}/frame), {r.PatchedMethods} methods");
        }

        /// <summary>
        /// Interleaved A/B: switches the engine mode between A and B every Block seconds and attributes each frame
        /// to the active mode, so slow drifts (time of day, traffic) affect both sides equally.
        /// </summary>
        IEnumerator AbTest(Step step)
        {
            if (!EngineBridge.Available) { Log("ab: MOPR engine not available"); yield break; }
            string a = step.A ?? "off", b = step.B ?? "on";
            string previous = EngineBridge.Mode;
            float block = step.Block > 0f ? step.Block : 1f;
            List<float> fa = new List<float>(8192), fb = new List<float>(8192);

            bool onA = true;
            EngineBridge.SetMode(a);
            int skip = 3;
            float now = Time.realtimeSinceStartup;
            float end = now + Mathf.Max(2f, step.Seconds);
            float blockEnd = now + block;
            int switches = 0;
            long prevMem = GC.GetTotalMemory(false), allocA = 0, allocB = 0;
            while (true)
            {
                yield return null;
                now = Time.realtimeSinceStartup;
                if (now >= end) break;
                float ms = Time.unscaledDeltaTime * 1000f;
                long mem = GC.GetTotalMemory(false);
                long grown = mem > prevMem ? mem - prevMem : 0;
                prevMem = mem;
                if (skip > 0) skip--;
                else
                {
                    (onA ? fa : fb).Add(ms);
                    if (onA) allocA += grown; else allocB += grown;
                }
                if (now >= blockEnd)
                {
                    onA = !onA;
                    EngineBridge.SetMode(onA ? a : b);
                    switches++;
                    skip = 3;                       // the switch itself (Harmony patching) costs a few ms
                    blockEnd = now + block;
                    if (switches % 10 == 0) Beat();
                }
            }
            EngineBridge.SetMode(previous);

            AbResult r = new AbResult { Label = step.Label, A = a, B = b, Switches = switches };
            Summarize(fa, out r.FramesA, out r.AvgFpsA, out r.MedianMsA, out r.P95MsA);
            Summarize(fb, out r.FramesB, out r.AvgFpsB, out r.MedianMsB, out r.P95MsB);
            r.FpsGainPercent = r.AvgFpsA > 0f ? (r.AvgFpsB / r.AvgFpsA - 1f) * 100f : 0f;
            r.MedianGainPercent = r.MedianMsB > 0f ? (r.MedianMsA / r.MedianMsB - 1f) * 100f : 0f;
            float secA = 0f, secB = 0f;
            foreach (float f in fa) secA += f / 1000f;
            foreach (float f in fb) secB += f / 1000f;
            r.AllocMBPerSecA = secA > 0f ? allocA / 1048576f / secA : 0f;
            r.AllocMBPerSecB = secB > 0f ? allocB / 1048576f / secB : 0f;
            report.AbTests.Add(r);
            Log($"ab {r.Label}: {a} {r.AvgFpsA:0.0} fps (median {r.MedianMsA:0.00} ms, p95 {r.P95MsA:0.00}, alloc {r.AllocMBPerSecA:0.00} MB/s) vs {b} {r.AvgFpsB:0.0} fps " +
                $"(median {r.MedianMsB:0.00} ms, p95 {r.P95MsB:0.00}, alloc {r.AllocMBPerSecB:0.00} MB/s): avg {r.FpsGainPercent:+0.0;-0.0}%, median {r.MedianGainPercent:+0.0;-0.0}%, {switches} switches");
        }

        /// <summary>
        /// Interleaved A/B for a scene change instead of an engine mode: A = unchanged, B = Var applied to Path
        /// (renderers | colliders | castshadows | dummies), switched every Block seconds and restored at the end.
        /// </summary>
        IEnumerator AbToggle(Step step)
        {
            Action<bool> apply;
            string what = (step.Var ?? "").ToLowerInvariant();
            switch (what)
            {
                case "renderers": apply = b => GameOps.SetRenderers(step.Path, !b, out int _); break;
                case "colliders": apply = b => GameOps.SetColliders(step.Path, !b, out int _); break;
                case "castshadows": apply = b => GameOps.SetShadowCasting(step.Path, !b, out int _); break;
                case "dummies": apply = b => GameOps.SetDummies(b ? step.Max : 0); break;
                case "cameras": apply = b => GameOps.SetCameras(step.Path, !b, out int _); break;
                case "bake":
                    {
                        // B = draw the combined meshes instead of the original renderers.
                        string err = GameOps.Bake(step.Path, out string summary);
                        if (err != null) { Log("abtoggle: " + err); yield break; }
                        Log("bake " + step.Path + ": " + summary);
                        apply = b => GameOps.ShowBaked(b);
                    }
                    break;
                case "skipmethod":
                    {
                        // Path = "Namespace.Type:Method": B skips the method (Harmony prefix returning false).
                        string err = MethodSkipper.Install(step.Path);
                        if (err != null) { Log("abtoggle: " + err); yield break; }
                        apply = b => MethodSkipper.Skip = b;
                    }
                    break;
                case "staticbool":
                    {
                        // Path = "Namespace.Type.Member": B sets the static bool to true (A/B switch of a mod).
                        string err = GameOps.StaticBoolSetter(step.Path, out Action<bool> set);
                        if (err != null) { Log("abtoggle: " + err); yield break; }
                        apply = set;
                    }
                    break;
                default: Log("abtoggle: unknown Var " + step.Var); yield break;
            }

            float block = step.Block > 0f ? step.Block : 1f;
            List<float> fa = new List<float>(8192), fb = new List<float>(8192);
            bool onB = false;
            apply(false);
            int skip = 3, switches = 0;
            float now = Time.realtimeSinceStartup, end = now + Mathf.Max(2f, step.Seconds), blockEnd = now + block;
            while (true)
            {
                yield return null;
                now = Time.realtimeSinceStartup;
                if (now >= end) break;
                if (skip > 0) skip--;
                else (onB ? fb : fa).Add(Time.unscaledDeltaTime * 1000f);
                if (now >= blockEnd)
                {
                    onB = !onB;
                    apply(onB);
                    switches++;
                    skip = 3;                      // the switch itself costs a few ms
                    blockEnd = now + block;
                    if (switches % 10 == 0) Beat();
                }
            }
            apply(false);
            if (what == "skipmethod") MethodSkipper.Uninstall();
            if (what == "bake") GameOps.Unbake();

            AbResult r = new AbResult { Label = step.Label, A = "base", B = (what == "staticbool" ? "true: " : what + " off: ") + step.Path, Switches = switches };
            Summarize(fa, out r.FramesA, out r.AvgFpsA, out r.MedianMsA, out r.P95MsA);
            Summarize(fb, out r.FramesB, out r.AvgFpsB, out r.MedianMsB, out r.P95MsB);
            r.FpsGainPercent = r.AvgFpsA > 0f ? (r.AvgFpsB / r.AvgFpsA - 1f) * 100f : 0f;
            r.MedianGainPercent = r.MedianMsB > 0f ? (r.MedianMsA / r.MedianMsB - 1f) * 100f : 0f;
            report.AbTests.Add(r);
            Log($"abtoggle {r.Label}: base {r.AvgFpsA:0.0} fps (median {r.MedianMsA:0.00} ms) vs {r.B} {r.AvgFpsB:0.0} fps (median {r.MedianMsB:0.00} ms): " +
                $"median {r.MedianMsA - r.MedianMsB:+0.00;-0.00} ms ({r.MedianGainPercent:+0.0;-0.0}%), {switches} switches");
        }

        static void Summarize(List<float> frames, out int count, out float avgFps, out float median, out float p95)
        {
            count = frames.Count;
            avgFps = median = p95 = 0f;
            if (count == 0) return;
            float sum = 0f;
            foreach (float f in frames) sum += f;
            avgFps = count / (sum / 1000f);
            List<float> sorted = new List<float>(frames);
            sorted.Sort();
            median = Percentile(sorted, 0.5f);
            p95 = Percentile(sorted, 0.95f);
        }

        void RecordEngine(string label)
        {
            if (!EngineBridge.Available) { Log("enginestats: MOPR engine not available"); return; }
            EngineResult r = new EngineResult
            {
                Label = label,
                Mode = EngineBridge.Mode,
                Counters = EngineBridge.Counters(),
                ShadowMismatches = EngineBridge.ShadowMismatches,
                Report = EngineBridge.Report()
            };
            report.Engine.Add(r);
            Log($"enginestats {label}: mode {r.Mode}, shadow mismatches {r.ShadowMismatches}\n{r.Report}");
        }

        void TakeSnapshot(string label)
        {
            Dictionary<string, string> snap = StateSnapshot.Take();
            snapshots[label ?? "snapshot"] = snap;
            try { StateSnapshot.Write(snap, Path.Combine(TestBotMod.Folder, "snapshot_" + label + ".tsv")); } catch { }
            Log($"snapshot {label}: {snap.Count} values");
        }

        // ----- Renderer state snapshots (rendstate / rendcompare) ----------------------------------------

        readonly Dictionary<string, Dictionary<string, string>> renderSnaps = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>Every renderer under Path: "enabled active layer shadowMode" (MOP's own merged objects left out).</summary>
        void RenderState(string label, string path)
        {
            GameObject go = GameOps.Resolve(path);
            if (go == null) { Log("rendstate: not found " + path); return; }
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, int> dup = new Dictionary<string, int>(StringComparer.Ordinal);
            int off = 0;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.gameObject.name.StartsWith("MOP_")) continue;
                string key = PathOf(r.transform) + "#" + r.GetType().Name;
                dup.TryGetValue(key, out int n);
                dup[key] = n + 1;
                if (n > 0) key += "#" + n;
                d[key] = (r.enabled ? "on" : "OFF") + " " + (r.gameObject.activeInHierarchy ? "active" : "inactive") + " L" + r.gameObject.layer + " " + r.shadowCastingMode +
                         " " + (r.isVisible ? "vis" : "novis") + " " + DrawInfo(r);
                if (!r.enabled && r.gameObject.activeInHierarchy) off++;
            }
            renderSnaps[label] = d;
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in d.OrderBy(k => k.Key)) sb.Append(kv.Value).Append("  ").AppendLine(kv.Key);
            WriteArtifact("tree_rend_" + label + ".txt", sb.ToString());
            Log($"rendstate {label}: {d.Count} renderers, {off} switched off on active objects");
        }

        // ----- Free camera screenshot and mesh clipping (camshot / meshclip) ----------------------------

        /// <summary>
        /// Screenshot from a temporary camera (settings copied from the main camera, image effects not included):
        /// Pos = camera position in Target's frame, Path = object to look at (its renderer's centre; default
        /// Target), Max = field of view (default: the main camera's). Writes shot_cam_&lt;label&gt;.png.
        /// </summary>
        IEnumerator CamShot(Step step)
        {
            GameObject frame = GameOps.Resolve(step.Target);
            Camera main = Camera.main;
            if (frame == null || main == null) { Log("camshot: no frame " + step.Target + " or no main camera"); yield break; }
            Vector3 local = step.Pos != null && step.Pos.Length == 3 ? new Vector3(step.Pos[0], step.Pos[1], step.Pos[2]) : new Vector3(0f, 3f, 0f);
            // Anchor: Pos is an offset from that object, along Target's axes.
            GameObject anchor = string.IsNullOrEmpty(step.Anchor) ? null : GameOps.Resolve(step.Anchor);
            Vector3 pos = anchor != null ? anchor.transform.position + frame.transform.TransformDirection(local) : frame.transform.TransformPoint(local);
            GameObject lookGo = string.IsNullOrEmpty(step.Path) ? frame : GameOps.Resolve(step.Path);
            Renderer lookR = lookGo != null ? lookGo.GetComponent<Renderer>() : null;
            Vector3 look = lookR != null ? lookR.bounds.center : lookGo != null ? lookGo.transform.position : frame.transform.position;

            yield return new WaitForEndOfFrame();
            GameObject go = new GameObject("MOP_TestBotCamera");
            RenderTexture rt = null;
            Texture2D tex = null;
            string err = null, extra = "";
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(look - pos, frame.transform.up);
                if (step.Max > 0) cam.fieldOfView = step.Max;
                cam.nearClipPlane = 0.01f;
                const int w = 1280, h = 720;
                rt = new RenderTexture(w, h, 24);
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                Color32[] baseline = RenderPixels(cam, rt, tex);
                File.WriteAllBytes(Path.Combine(TestBotMod.Folder, "shot_cam_" + (step.Label ?? "x") + ".png"), tex.EncodeToPNG());

                // Text = "who": which renderer draws what is in view? Every enabled renderer under Path's root whose
                // bounds are within Seconds metres (default 0.3) of the look point is switched off for one render
                // (outside any camera callback) and the changed pixels are counted.
                if (step.Text == "who")
                {
                    float radius = step.Seconds > 0f ? step.Seconds : 0.3f;
                    Transform root = lookGo != null ? lookGo.transform.root : frame.transform.root;
                    List<KeyValuePair<string, int>> found = new List<KeyValuePair<string, int>>();
                    int tested = 0;
                    foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r == null || !r.enabled) continue;
                        Bounds b = r.bounds;
                        if (b.SqrDistance(look) > radius * radius) continue;
                        tested++;
                        r.enabled = false;
                        Color32[] px;
                        try { px = RenderPixels(cam, rt, tex); }
                        finally { r.enabled = true; }
                        int changed = 0;
                        for (int i = 0; i < px.Length; i++)
                        {
                            Color32 a = px[i], c = baseline[i];
                            if (Math.Abs(a.r - c.r) + Math.Abs(a.g - c.g) + Math.Abs(a.b - c.b) > 12) changed++;
                        }
                        if (changed > 0) found.Add(new KeyValuePair<string, int>(PathOf(r.transform) + "#" + r.GetType().Name, changed));
                    }
                    found.Sort((x, y) => y.Value.CompareTo(x.Value));
                    StringBuilder sb = new StringBuilder();
                    foreach (KeyValuePair<string, int> kv in found) sb.AppendLine(kv.Value + " px  " + kv.Key);
                    WriteArtifact("tree_who_" + (step.Label ?? "x") + ".txt", sb.ToString());
                    extra = $"; {tested} renderers within {radius} m tested, {found.Count} change the picture" +
                            (found.Count > 0 ? ", most: " + string.Join("; ", found.Take(5).Select(kv => kv.Value + " px " + kv.Key).ToArray()) : "");
                }
                cam.targetTexture = null;
            }
            catch (Exception e) { err = e.Message; }
            finally
            {
                UnityEngine.Object.Destroy(go);
                if (rt != null) UnityEngine.Object.Destroy(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            Log($"camshot {step.Label}: from {local} in {step.Target}, looking at {(lookGo != null ? lookGo.name : "?")}{extra}" + (err != null ? "  ERROR: " + err : ""));
        }

        /// <summary>QualitySettings shadow values, the main camera and every light that casts shadows.</summary>
        static string ShadowInfo()
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            int level = QualitySettings.GetQualityLevel();
            sb.Append("quality ").Append(level).Append(" (").Append(QualitySettings.names[level]).Append("), projection ").Append(QualitySettings.shadowProjection)
              .Append(", cascades ").Append(QualitySettings.shadowCascades).Append(", distance ").Append(QualitySettings.shadowDistance.ToString("0.#", ci))
              .Append(", split2 ").Append(QualitySettings.shadowCascade2Split.ToString("0.###", ci)).Append(", split4 ").Append(QualitySettings.shadowCascade4Split)
              .Append('\n');
            sb.Append("levels: ").Append(string.Join(", ", QualitySettings.names.Select((n, i) => i + "=" + n).ToArray())).Append('\n');
            Camera main = Camera.main;
            if (main != null)
                sb.Append("camera ").Append(PathOf(main.transform)).Append(": near ").Append(main.nearClipPlane.ToString("0.###", ci)).Append(", far ")
                  .Append(main.farClipPlane.ToString("0", ci)).Append(", fov ").Append(main.fieldOfView.ToString("0.#", ci)).Append(", path ")
                  .Append(main.actualRenderingPath).Append(", hdr ").Append(main.hdr).Append('\n');
            foreach (Light l in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (l == null || l.shadows == LightShadows.None || !l.gameObject.activeInHierarchy) continue;
                sb.Append(l.type).Append(' ').Append(PathOf(l.transform)).Append(": ").Append(l.shadows).Append(", enabled ").Append(l.enabled)
                  .Append(", strength ").Append(l.shadowStrength.ToString("0.##", ci)).Append(", bias ").Append(l.shadowBias.ToString("0.####", ci))
                  .Append(", normalBias ").Append(l.shadowNormalBias.ToString("0.###", ci)).Append(", softness ").Append(l.shadowSoftness.ToString("0.##", ci))
                  .Append(", softnessFade ").Append(l.shadowSoftnessFade.ToString("0.##", ci)).Append(", intensity ").Append(l.intensity.ToString("0.##", ci))
                  .Append(", rotation ").Append(l.transform.eulerAngles).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Shadow stability. A temporary camera starts at the main camera's pose and moves Max steps (default 24):
        /// Text "yaw" / "pitch" turns Seconds degrees per step (default 0.05), "move" slides sideways Seconds metres
        /// per step (default 0.02), "sun" keeps the camera and tilts the sun Seconds degrees per step (as the time of
        /// day does; restored afterwards).
        /// Every step is rendered with the sun's shadows and without them; the pixels the shadows darken form the
        /// shadow mask. Mask pixels that flip between two steps are counted: geometry moves well under a pixel per
        /// step, so that is the shadow edges crawling ("swimming staircase"), not the picture moving.
        /// Probes: variants "name: key=value; ..." with keys projection (close|stable), distance, cascades,
        /// split2, split4 (x,y,z), bias, normalBias, strength; each is applied, swept and restored. Default: the
        /// game's settings only. Fov: the test camera's field of view (zoom on an edge).
        /// Writes shot_sweep_&lt;label&gt;_&lt;variant&gt;.png: the most changed step's 256x144 region at 3x:
        /// before | after | flipped mask pixels.
        /// </summary>
        IEnumerator ShadowSweep(Step step)
        {
            Camera main = Camera.main;
            Light sun = null;
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && l.shadows != LightShadows.None && l.enabled) { sun = l; break; }
            if (main == null || sun == null) { Log("shadowsweep: no main camera or no shadow-casting sun"); yield break; }
            int steps = step.Max > 0 ? step.Max : 24;
            string kind = string.IsNullOrEmpty(step.Text) ? "yaw" : step.Text;
            float amount = step.Seconds > 0f ? step.Seconds : (kind == "move" ? 0.02f : 0.05f);
            List<string> variants = step.Probes != null && step.Probes.Count > 0 ? step.Probes : new List<string> { "game:" };
            Vector3 pos = main.transform.position;
            Quaternion start = main.transform.rotation;
            CultureInfo ci = CultureInfo.InvariantCulture;
            List<string> results = new List<string>();
            const int w = 1280, h = 720;

            foreach (string variant in variants)
            {
                int colon = variant.IndexOf(':');
                string name = colon > 0 ? variant.Substring(0, colon).Trim() : variant.Trim();
                string spec = colon > 0 ? variant.Substring(colon + 1) : "";

                // Saved game values (always restored, whatever the variant changed).
                int qualityLevel = QualitySettings.GetQualityLevel();
                ShadowProjection projection = QualitySettings.shadowProjection;
                float distance = QualitySettings.shadowDistance, split2 = QualitySettings.shadowCascade2Split;
                int cascades = QualitySettings.shadowCascades;
                Vector3 split4 = QualitySettings.shadowCascade4Split;
                float bias = sun.shadowBias, normalBias = sun.shadowNormalBias, strength = sun.shadowStrength;
                LightShadows shadows = sun.shadows;
                Quaternion sunStart = sun.transform.rotation;
                Quaternion tiltedStart = sunStart;

                yield return new WaitForEndOfFrame();
                GameObject go = new GameObject("MOP_TestBotCamera");
                RenderTexture rt = new RenderTexture(w, h, 24);
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                string err = null;
                long flippedTotal = 0, maskTotal = 0, edgeTotal = 0;
                int worst = -1, worstStep = 0, measured = 0;
                Color32[] worstBefore = null, worstAfter = null;
                bool[] worstMaskA = null, worstMaskB = null;
                try
                {
                    foreach (string kv in spec.Split(';'))
                    {
                        string[] p = kv.Split('=');
                        if (p.Length != 2) continue;
                        string key = p[0].Trim(), val = p[1].Trim();
                        switch (key)
                        {
                            case "projection": QualitySettings.shadowProjection = val == "close" ? ShadowProjection.CloseFit : ShadowProjection.StableFit; break;
                            case "distance": QualitySettings.shadowDistance = float.Parse(val, ci); break;
                            case "cascades": QualitySettings.shadowCascades = int.Parse(val, ci); break;
                            case "split2": QualitySettings.shadowCascade2Split = float.Parse(val, ci); break;
                            case "split4":
                                string[] s = val.Split(',');
                                QualitySettings.shadowCascade4Split = new Vector3(float.Parse(s[0], ci), float.Parse(s[1], ci), float.Parse(s[2], ci));
                                break;
                            case "bias": sun.shadowBias = float.Parse(val, ci); break;
                            case "normalBias": sun.shadowNormalBias = float.Parse(val, ci); break;
                            case "strength": sun.shadowStrength = float.Parse(val, ci); break;
                            case "sunTilt":
                                // Lower (positive) or raise the sun by this many degrees for the sweep (evening light).
                                sun.transform.rotation = sunStart * Quaternion.Euler(float.Parse(val, ci), 0f, 0f);
                                tiltedStart = sun.transform.rotation;
                                break;
                            case "quality":
                                // A whole quality level (shadow resolution can only be changed this way in Unity 5.0).
                                QualitySettings.SetQualityLevel(int.Parse(val, ci), false);
                                break;
                            default: throw new Exception("unknown key " + key);
                        }
                    }

                    Camera cam = go.AddComponent<Camera>();
                    cam.CopyFrom(main);
                    cam.enabled = false;
                    if (step.Fov > 0f) cam.fieldOfView = step.Fov;
                    bool[] previousMask = null;
                    Color32[] previousPx = null;
                    Quaternion previousRotation = start;
                    if (kind == "move") throw new Exception("move has parallax; only yaw, pitch and sun are measured");
                    for (int i = 0; i <= steps; i++)
                    {
                        go.transform.position = pos;
                        go.transform.rotation = start;
                        switch (kind)
                        {
                            case "pitch": go.transform.rotation = start * Quaternion.Euler(amount * i, 0f, 0f); break;
                            case "move": go.transform.position = pos + start * Vector3.right * (amount * i); break;
                            case "sun": sun.transform.rotation = tiltedStart * Quaternion.Euler(amount * i, 0f, 0f); break;
                            default: go.transform.rotation = start * Quaternion.Euler(0f, amount * i, 0f); break;
                        }
                        sun.shadows = shadows;
                        Color32[] lit = RenderPixels(cam, rt, tex);
                        sun.shadows = LightShadows.None;
                        Color32[] unshadowed = RenderPixels(cam, rt, tex);
                        sun.shadows = shadows;

                        bool[] mask = new bool[lit.Length];
                        int inShadow = 0;
                        for (int k = 0; k < lit.Length; k++)
                        {
                            Color32 a = lit[k], b = unshadowed[k];
                            if ((b.r + b.g + b.b) - (a.r + a.g + a.b) > 30) { mask[k] = true; inShadow++; }
                        }
                        maskTotal += inShadow;
                        // Shadow edge pixels (a 4-neighbour differs): clean shadows have few, acne bands and
                        // jagged "staircase" edges many.
                        for (int y = 1; y < h - 1; y++)
                            for (int x = 1; x < w - 1; x++)
                            {
                                int q = y * w + x;
                                bool a = mask[q];
                                if (mask[q - 1] != a || mask[q + 1] != a || mask[q - w] != a || mask[q + w] != a) edgeTotal++;
                            }
                        if (previousMask != null)
                        {
                            // A pure rotation maps every pixel of this step exactly onto the previous one (no
                            // parallax): compare each shadow pixel with where it was. Pixels within 1 px of a shadow
                            // edge of the previous step are skipped (sub-pixel resampling); what is left is shadow
                            // that moved over the geometry by more than a pixel.
                            Quaternion rel = Quaternion.Inverse(previousRotation) * go.transform.rotation;
                            float f = (h * 0.5f) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                            int flipped = 0;
                            bool[] flipMask = new bool[mask.Length];
                            for (int y = 1; y < h - 1; y++)
                                for (int x = 1; x < w - 1; x++)
                                {
                                    Vector3 d = rel * new Vector3((x + 0.5f - w * 0.5f) / f, (y + 0.5f - h * 0.5f) / f, 1f);
                                    if (d.z <= 0f) continue;
                                    int px0 = (int)(w * 0.5f + f * d.x / d.z), py0 = (int)(h * 0.5f + f * d.y / d.z);
                                    if (px0 < 1 || py0 < 1 || px0 >= w - 1 || py0 >= h - 1) continue;
                                    int q = py0 * w + px0;
                                    bool a = previousMask[q];
                                    if (previousMask[q - 1] != a || previousMask[q + 1] != a || previousMask[q - w] != a || previousMask[q + w] != a ||
                                        previousMask[q - w - 1] != a || previousMask[q - w + 1] != a || previousMask[q + w - 1] != a || previousMask[q + w + 1] != a)
                                        continue;
                                    if (mask[y * w + x] != a) { flipped++; flipMask[y * w + x] = true; }
                                }
                            flippedTotal += flipped;
                            measured++;
                            if (flipped > worst) { worst = flipped; worstStep = i; worstBefore = previousPx; worstAfter = lit; worstMaskA = flipMask; worstMaskB = mask; }
                        }
                        previousMask = mask;
                        previousPx = lit;
                        previousRotation = go.transform.rotation;
                    }
                    cam.targetTexture = null;

                    if (worstBefore != null)
                    {
                        // The 256x144 window with the most flipped pixels, at 3x: before | after | flips.
                        const int cw = 256, ch = 144, zoom = 3;
                        int bestX = 0, bestY = 0, bestCount = -1;
                        for (int y0 = 0; y0 + ch <= h; y0 += 36)
                            for (int x0 = 0; x0 + cw <= w; x0 += 64)
                            {
                                int c = 0;
                                for (int y = y0; y < y0 + ch; y += 2)
                                    for (int x = x0; x < x0 + cw; x += 2)
                                        if (worstMaskA[y * w + x]) c++;
                                if (c > bestCount) { bestCount = c; bestX = x0; bestY = y0; }
                            }
                        Texture2D sheet = new Texture2D(cw * zoom * 3 + 8, ch * zoom, TextureFormat.RGB24, false);
                        Color32[] outPx = new Color32[sheet.width * sheet.height];
                        for (int y = 0; y < ch * zoom; y++)
                            for (int x = 0; x < cw * zoom; x++)
                            {
                                int src = (bestY + y / zoom) * w + bestX + x / zoom;
                                Color32 b = worstBefore[src], a = worstAfter[src];
                                bool flip = worstMaskA[src];
                                outPx[y * sheet.width + x] = b;
                                outPx[y * sheet.width + x + cw * zoom + 4] = a;
                                outPx[y * sheet.width + x + 2 * (cw * zoom + 4)] = flip ? new Color32(255, 0, 0, 255)
                                    : worstMaskB[src] ? new Color32(40, 40, 60, 255) : new Color32((byte)(a.r / 2), (byte)(a.g / 2), (byte)(a.b / 2), 255);
                            }
                        sheet.SetPixels32(outPx);
                        sheet.Apply();
                        File.WriteAllBytes(Path.Combine(TestBotMod.Folder, $"shot_sweep_{step.Label ?? "x"}_{name}.png"), sheet.EncodeToPNG());
                        UnityEngine.Object.Destroy(sheet);
                    }
                }
                catch (Exception e) { err = e.Message; }
                finally
                {
                    if (QualitySettings.GetQualityLevel() != qualityLevel) QualitySettings.SetQualityLevel(qualityLevel, false);
                    QualitySettings.shadowProjection = projection;
                    QualitySettings.shadowDistance = distance;
                    QualitySettings.shadowCascades = cascades;
                    QualitySettings.shadowCascade2Split = split2;
                    QualitySettings.shadowCascade4Split = split4;
                    sun.shadowBias = bias;
                    sun.shadowNormalBias = normalBias;
                    sun.shadowStrength = strength;
                    sun.shadows = shadows;
                    sun.transform.rotation = sunStart;
                    UnityEngine.Object.Destroy(go);
                    UnityEngine.Object.Destroy(rt);
                    UnityEngine.Object.Destroy(tex);
                }
                results.Add(err != null ? $"{name}: ERROR {err}"
                    : $"{name}: {flippedTotal / Math.Max(1, measured)} shadow px flip/step (worst {worst}, step {worstStep}), {maskTotal / Math.Max(1, steps + 1)} px in shadow, {edgeTotal / Math.Max(1, steps + 1)} edge px");
            }
            Log($"shadowsweep {step.Label} ({steps} x {amount} {kind}, sun {PathOf(sun.transform)}): " + string.Join("; ", results.ToArray()));
        }

        static Color32[] RenderPixels(Camera cam, RenderTexture rt, Texture2D tex)
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            return tex.GetPixels32();
        }

        /// <summary>
        /// Does the mesh at Path stick out of the shell under Target (every other active MeshRenderer there)?
        /// Every vertex of Path is cast along the shell root's up axis against the shell triangles: a vertex with
        /// no shell surface above it (within 0.3 m) but one below it is outside, and sticks out by the distance
        /// to that surface. Writes tree_clip_&lt;label&gt;.txt and logs the summary.
        /// </summary>
        void MeshClip(string label, string path, string shellPath, string anchorPath, float radiusM)
        {
            GameObject inner = GameOps.Resolve(path), shell = GameOps.Resolve(shellPath);
            MeshFilter mf = inner != null ? inner.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null || shell == null) { Log("meshclip: missing " + path + " or " + shellPath); return; }
            Vector3 up = shell.transform.root.up;
            const float Reach = 0.3f;

            // Anchor + Seconds: only the vertices within Seconds metres of Anchor (for big meshes).
            GameObject anchor = string.IsNullOrEmpty(anchorPath) ? null : GameOps.Resolve(anchorPath);
            Renderer anchorR = anchor != null ? anchor.GetComponent<Renderer>() : null;
            Vector3 centre = anchorR != null ? anchorR.bounds.center : anchor != null ? anchor.transform.position : Vector3.zero;
            float radius = anchor != null ? (radiusM > 0f ? radiusM : 0.1f) : float.MaxValue;

            Vector3[] av = mf.sharedMesh.vertices;
            List<Vector3> awList = new List<Vector3>(av.Length);
            for (int i = 0; i < av.Length; i++)
            {
                Vector3 w = mf.transform.TransformPoint(av[i]);
                if (anchor == null || (w - centre).sqrMagnitude <= radius * radius) awList.Add(w);
            }
            if (awList.Count == 0) { Log($"meshclip {label}: no vertex of {path} within {radius} m of {anchorPath}"); return; }
            Vector3[] aw = awList.ToArray();
            Bounds box = new Bounds(aw[0], Vector3.zero);
            for (int i = 0; i < aw.Length; i++) box.Encapsulate(aw[i]);
            box.Expand(0.05f);

            // Shell triangles near the mesh (world space), with the mesh each belongs to.
            List<Vector3> tri = new List<Vector3>();
            List<string> triOwner = new List<string>();
            int meshes = 0;
            foreach (MeshRenderer r in shell.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (r == null || !r.enabled || r.gameObject == inner || r.transform.IsChildOf(inner.transform)) continue;
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) continue;   // never seen
                MeshFilter sf = r.GetComponent<MeshFilter>();
                if (sf == null || sf.sharedMesh == null) continue;
                Bounds rb = r.bounds;
                if (!rb.Intersects(new Bounds(box.center, box.size + up * (2 * Reach)))) continue;
                Vector3[] v;
                int[] t;
                try { v = sf.sharedMesh.vertices; t = sf.sharedMesh.triangles; }
                catch { continue; }   // not readable
                meshes++;
                string owner = PathOf(r.transform);
                Transform st = sf.transform;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 p0 = st.TransformPoint(v[t[i]]), p1 = st.TransformPoint(v[t[i + 1]]), p2 = st.TransformPoint(v[t[i + 2]]);
                    // Keep the triangle if its footprint overlaps the mesh footprint (within the reach along up).
                    Bounds tb = new Bounds(p0, Vector3.zero); tb.Encapsulate(p1); tb.Encapsulate(p2);
                    if (!tb.Intersects(new Bounds(box.center, box.size + new Vector3(Reach, Reach, Reach) * 2f))) continue;
                    tri.Add(p0); tri.Add(p1); tri.Add(p2);
                    // Which way the triangle faces (Unity: front = clockwise, Cross(b - a, c - a) points out of it).
                    triOwner.Add(owner + (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), up) >= 0f ? " (faces up)" : " (faces down)"));
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"meshclip {PathOf(inner.transform)} against {shellPath}: {aw.Length} vertices" +
                          (anchor != null ? $" within {radius} m of {centre.ToString("F4")}" : "") +
                          $", {tri.Count / 3} shell triangles from {meshes} meshes, up {up.ToString("F3")}");
            int outside = 0;
            float maxOut = 0f, minClear = float.MaxValue;
            string maxOutOwner = "-", minClearOwner = "-";
            for (int i = 0; i < aw.Length; i++)
            {
                float above = float.MaxValue, below = float.MaxValue, aboveUp = float.MaxValue;
                string aboveOwner = null, belowOwner = null;
                for (int k = 0; k < tri.Count; k += 3)
                {
                    float d;
                    if (RayTriangle(aw[i], up, tri[k], tri[k + 1], tri[k + 2], out d) && d < Reach)
                    {
                        if (d < above) { above = d; aboveOwner = triOwner[k / 3]; }
                        if (d < aboveUp && triOwner[k / 3].EndsWith("(faces up)")) aboveUp = d;
                    }
                    if (RayTriangle(aw[i], -up, tri[k], tri[k + 1], tri[k + 2], out d) && d < Reach && d < below) { below = d; belowOwner = triOwner[k / 3]; }
                }
                // Seen from above unless an up-facing surface covers it (a down-facing one is culled from above).
                bool isOut = aboveUp == float.MaxValue && below != float.MaxValue;
                if (isOut)
                {
                    outside++;
                    if (below > maxOut) { maxOut = below; maxOutOwner = belowOwner; }
                }
                else if (aboveUp != float.MaxValue && aboveUp < minClear) { minClear = aboveUp; minClearOwner = aboveOwner; }
                sb.AppendLine($"  v{i} {aw[i].ToString("F4")}  above {(above == float.MaxValue ? "-" : (above * 1000f).ToString("0.00") + " mm " + aboveOwner)}" +
                              $"  below {(below == float.MaxValue ? "-" : (below * 1000f).ToString("0.00") + " mm " + belowOwner)}{(isOut ? "  OUTSIDE" : "")}");
            }
            string summary = $"meshclip {label}: {outside} of {aw.Length} vertices outside the shell, sticking out up to {maxOut * 1000f:0.00} mm above {maxOutOwner}; " +
                             $"smallest clearance of the others {(minClear == float.MaxValue ? "-" : (minClear * 1000f).ToString("0.00") + " mm")} below {minClearOwner}";
            sb.Insert(0, summary + Environment.NewLine);
            WriteArtifact("tree_clip_" + label + ".txt", sb.ToString());
            Log(summary);
        }

        /// <summary>Möller–Trumbore, both faces: true if the ray from o along dir hits the triangle at distance d &gt; 0.</summary>
        static bool RayTriangle(Vector3 o, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float d)
        {
            d = 0f;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-9f && det < 1e-9f) return false;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            d = Vector3.Dot(e2, q) * inv;
            return d > 1e-6f;
        }

        /// <summary>What the renderer draws: mesh (name, id, vertices, local bounds) and materials (name, id).</summary>
        static string DrawInfo(Renderer r)
        {
            StringBuilder sb = new StringBuilder();
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null)
            {
                Mesh m = mf.sharedMesh;
                if (m == null) sb.Append("mesh=NULL");
                else
                {
                    Bounds b = m.bounds;
                    sb.Append("mesh=").Append(m.name).Append('/').Append(m.GetInstanceID()).Append('/').Append(m.vertexCount)
                      .Append(" lb=").Append(b.center.ToString("F3")).Append(b.size.ToString("F3"));
                }
            }
            Material[] mats = r.sharedMaterials;
            sb.Append(" mat=");
            if (mats == null || mats.Length == 0) sb.Append("NONE");
            else
                for (int i = 0; i < mats.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(mats[i] == null ? "NULL" : mats[i].name + "/" + mats[i].GetInstanceID() + "/q" + mats[i].renderQueue);
                }
            return sb.ToString();
        }

        /// <summary>Differences between two renderer snapshots; "lost" = drawn in A, switched off (but active) in B.</summary>
        void RenderCompare(string a, string b)
        {
            if (a == null || b == null || !renderSnaps.ContainsKey(a) || !renderSnaps.ContainsKey(b)) { Log($"rendcompare: missing {a} / {b}"); return; }
            Dictionary<string, string> da = renderSnaps[a], db = renderSnaps[b];
            StringBuilder sb = new StringBuilder();
            int changed = 0, lost = 0, culled = 0;
            foreach (KeyValuePair<string, string> kv in da.OrderBy(k => k.Key))
            {
                if (!db.TryGetValue(kv.Key, out string vb)) { sb.AppendLine("gone      " + kv.Key); continue; }
                if (vb == kv.Value) continue;
                changed++;
                bool isLost = kv.Value.StartsWith("on active") && vb.StartsWith("OFF active");
                // Enabled and active in both, seen by a camera before, culled now: something else hides it.
                bool isCulled = !isLost && kv.Value.StartsWith("on active") && vb.StartsWith("on active") &&
                                kv.Value.Contains(" vis ") && vb.Contains(" novis ");
                if (isLost) lost++;
                if (isCulled) culled++;
                sb.AppendLine((isLost ? "LOST      " : isCulled ? "CULLED    " : "changed   ") + kv.Key + "   " + kv.Value + " -> " + vb);
            }
            WriteArtifact("tree_rendcmp_" + a + "_" + b + ".txt", sb.ToString());
            Log($"rendcompare {a} -> {b}: {changed} changed, {lost} LOST (drawn before, switched off now on an active object), " +
                $"{culled} CULLED (on in both, seen before, not seen now)");
        }

        void CompareSnapshots(string a, string b)
        {
            if (a == null || b == null || !snapshots.ContainsKey(a) || !snapshots.ContainsKey(b)) { Log($"compare: missing snapshot {a} / {b}"); return; }
            CompareResult r = StateSnapshot.Compare(a, snapshots[a], b, snapshots[b]);
            report.Compares.Add(r);
            Log($"compare {a} -> {b}: {r.Changed} changed of {r.Common} common values (only {a}: {r.OnlyA}, only {b}: {r.OnlyB})");
        }

        // ----- Finish ---------------------------------------------------------------------------------

        void Finish(string error)
        {
            if (finished) return;
            finished = true;
            report.Error = error;
            report.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (error != null) Log("ERROR: " + error);

            try
            {
                File.WriteAllText(Path.Combine(TestBotMod.Folder, "report.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
                File.WriteAllText(Path.Combine(TestBotMod.Folder, "done.flag"), error ?? "ok");
            }
            catch (Exception e)
            {
                Debug.Log("[TestBot] cannot write report: " + e);
            }

            Log("quitting without saving");
            Application.Quit();
        }

        // ----- Helpers --------------------------------------------------------------------------------

        void Log(string message)
        {
            string line = $"[{Time.realtimeSinceStartup - startTime,7:0.0}] {message}";
            report.Log.Add(line);
            Debug.Log("[TestBot] " + line);
        }

        static int SafeGcCount()
        {
            try { return GC.CollectionCount(0); } catch { return -1; }
        }

        static float Percentile(List<float> sorted, float p)
        {
            if (sorted.Count == 0) return 0f;
            int i = Mathf.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1);
            return sorted[i];
        }

        public static string PathOf(Transform t)
        {
            if (t == null) return "";
            StringBuilder sb = new StringBuilder(t.name);
            Transform p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 64)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }
    }
}
