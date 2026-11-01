// MSCDLSS - DLSS and FSR for My Summer Car
// Copyright (C) 2026 ANICKON
//
// The MSCLoader side: installs and starts the native plugin, reports what the system supports, puts the upscaler
// (NVIDIA DLSS or AMD FSR) on the player's camera, frame generation (DLSS or FSR) through the native bridge, console
// command "dlss". The image work is in the native plugin.

using System;
using System.Collections;
using System.IO;
using MSCLoader;
using UnityEngine;

namespace MSCDLSS
{
    public class DlssMod : Mod
    {
        public const string ModId = "MSCDLSS";
        public const string ModVersion = "0.4.0";

        public override string ID => ModId;
        public override string Name => "DLSS";
        public override string Author => "ANICKON";
        public override string Version => ModVersion;
        public override string Description => "NVIDIA DLSS and AMD FSR for My Summer Car (anti-aliasing, upscaling, frame generation, Reflex).";

        internal static DlssMod Instance { get; private set; }
        internal static string SettingsDir { get; private set; }
        internal static string AssetsDir { get; private set; }
        /// <summary>Upscaler wanted (DlssCamera.UpscalerDlss / UpscalerFsr; DLSS without an RTX card runs FSR).</summary>
        internal static int WantUpscaler = DlssCamera.UpscalerDlss;
        /// <summary>Mode wanted (DlssCamera.Mode*; survives the camera being made again on a load). Off = no upscaler.</summary>
        internal static int WantMode = DlssCamera.ModeDlaa;
        /// <summary>Frame generation wanted: 0 off, Native.FgKindDlss, Native.FgKindFsr.</summary>
        internal static int WantFgKind;
        /// <summary>DLSS frame generation: generated frames per real frame (1 = 2x ... 5 = 6x).</summary>
        internal static int WantFg = 1;
        /// <summary>The frame generation counter under the game's FPS counter (FgHud).</summary>
        internal static bool ShowFgFps = true;

        /// <summary>Generated frames per real frame wanted (0 = off; FSR makes one).</summary>
        internal static int FgFramesWanted => WantFgKind == 0 ? 0 : WantFgKind == Native.FgKindFsr ? 1 : Mathf.Clamp(WantFg, 1, 5);

        static bool started, commandAdded;
        internal static Runner Host { get; private set; }

        public override void ModSetup()
        {
            Instance = this;
            SetupFunction(Setup.OnMenuLoad, OnMenuLoad);
            SetupFunction(Setup.ModSettings, OnModSettings);
            SetupFunction(Setup.ModSettingsLoaded, ApplySettings);
        }

        static SettingsCheckBox fgFpsBox;
        static SettingsDropDownList upscalerList, dlssModeList, fsrModeList, fgTypeList, dlssFgList, reflexList;
        static SettingsText infoText, fsrFgText;
        internal static readonly string[] UpscalerChoices = { "Off (the game's FXAA)", "NVIDIA DLSS", "AMD FSR" };
        internal static readonly string[] DlssModeChoices = { "DLAA", "Quality", "Balanced", "Performance", "Ultra Performance" };
        internal static readonly string[] FsrModeChoices = { "Native AA", "Quality", "Balanced", "Performance", "Ultra Performance" };
        internal static readonly string[] FgTypeChoices = { "Off", "NVIDIA DLSS Frame Generation", "AMD FSR Frame Generation" };
        internal static readonly string[] DlssFgChoices = { "2x", "3x", "4x", "5x", "6x" };
        internal static readonly string[] ReflexChoices = { "Off", "On", "On + Boost" };

        /// <summary>Reflex wanted: 0 off, 1 on, 2 on + boost.</summary>
        internal static int WantReflex = 1;

        /// <summary>The upscaler chosen when nothing is saved yet: DLSS on a GeForce RTX, FSR on every other card.</summary>
        static int DefaultUpscalerIndex()
        {
            string name = SystemInfo.graphicsDeviceName ?? "";
            bool rtx = SystemInfo.graphicsDeviceVendorID == 0x10DE && name.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0;
            return rtx ? 1 : 2;
        }

        void OnModSettings()
        {
            Settings.AddHeader("Upscaling and anti-aliasing");
            Settings.AddText("NVIDIA DLSS (GeForce RTX) or AMD FSR (most video cards with DirectX 12; FSR 4 on Radeon RX 6000 " +
                             "and newer, FSR 3.1 elsewhere). DLAA / Native AA: anti-aliasing at the game's resolution (replaces " +
                             "the game's FXAA). Quality / Balanced / Performance / Ultra Performance: the game is drawn at 67 / " +
                             "58 / 50 / 33% of the screen's resolution and brought up to it. That only helps when the video card " +
                             "is the limit (high resolutions, weaker cards); My Summer Car mostly waits for the processor.");
            upscalerList = Settings.AddDropDownList("upscaler", "Upscaler", UpscalerChoices, DefaultUpscalerIndex(), ApplySettings);
            dlssModeList = Settings.AddDropDownList("dlssmode", "DLSS mode", DlssModeChoices, 0, ApplySettings);
            fsrModeList = Settings.AddDropDownList("fsrmode", "FSR mode", FsrModeChoices, 0, ApplySettings);
            infoText = Settings.AddText("Video card: " + SystemInfo.graphicsDeviceName + ". What runs on it is shown here once the game has started.");
            Settings.AddHeader("Frame Generation");
            Settings.AddText("Shows generated frames between the game's own ones: smoother picture, about one frame more input " +
                             "delay. DLSS Frame Generation: GeForce RTX 40 / 50 (3x and more: RTX 50). FSR Frame Generation: 2x, " +
                             "most video cards with DirectX 12. Needs the game in a window or a borderless window " +
                             "(-popupwindow), not exclusive fullscreen. More frames than the monitor's refresh rate are not shown.");
            fgTypeList = Settings.AddDropDownList("fgtype", "Frame Generation", FgTypeChoices, 0, ApplySettings);
            dlssFgList = Settings.AddDropDownList("dlssfg", "DLSS multiplier", DlssFgChoices, 0, ApplySettings);
            fsrFgText = Settings.AddText("FSR Frame Generation shows one generated frame between every two of the game's (2x).");
            Settings.AddText("FG counter: a line \"FG\" under the game's FPS counter with the frames shown per second " +
                             "(the game's own and the generated ones). Visible while the game's FPS counter is on " +
                             "(game options) and frame generation runs.");
            fgFpsBox = Settings.AddCheckBox("fgfps", "Show the FG counter under the game's FPS", true, ApplySettings);
            Settings.AddHeader("Reflex");
            Settings.AddText("NVIDIA Reflex (GeForce GTX 900 and newer): the game starts each frame as late as the video card " +
                             "allows, so the picture is closer to your input. Boost also keeps the video card's clocks up when " +
                             "it has little to do (this game: most of the time).");
            reflexList = Settings.AddDropDownList("reflex", "Reflex", ReflexChoices, 1, ApplySettings);
            UpdateVisibility();
        }

        /// <summary>The settings page's choices (also on load).</summary>
        internal static void ApplySettings()
        {
            if (upscalerList != null)
            {
                int u = upscalerList.GetSelectedItemIndex();
                SettingsDropDownList modes = u == 2 ? fsrModeList : dlssModeList;
                int m = modes != null ? Mathf.Clamp(modes.GetSelectedItemIndex(), 0, 4) : 0;
                WantUpscaler = u == 2 ? DlssCamera.UpscalerFsr : DlssCamera.UpscalerDlss;
                WantMode = u <= 0 ? DlssCamera.ModeOff : DlssCamera.ModeDlaa + m;
            }
            if (fgTypeList != null)
            {
                int f = fgTypeList.GetSelectedItemIndex();
                WantFgKind = f == 1 ? Native.FgKindDlss : f == 2 ? Native.FgKindFsr : 0;
                if (dlssFgList != null) WantFg = 1 + Mathf.Clamp(dlssFgList.GetSelectedItemIndex(), 0, 4);
            }
            if (fgFpsBox != null) ShowFgFps = fgFpsBox.GetValue();
            if (reflexList != null) WantReflex = Mathf.Clamp(reflexList.GetSelectedItemIndex(), 0, 2);
            UpdateVisibility();
            ApplyToCamera();
        }

        /// <summary>The wanted upscaler and frame generation to the camera and the bridge (settings or console).</summary>
        internal static void ApplyToCamera()
        {
            DlssCamera d = DlssCamera.Current;
            if (d != null)
            {
                d.Mode = WantMode;
                d.Upscaler = WantUpscaler;
                d.FgFrames = FgFramesWanted;
            }
            if (Host != null)
            {
                Host.FgDirty = true;
                Host.ReflexDirty = true;
            }
        }

        // Only the chosen upscaler's modes, only the chosen frame generation's options.
        static void UpdateVisibility()
        {
            int u = upscalerList != null ? upscalerList.GetSelectedItemIndex() : 1;
            int f = fgTypeList != null ? fgTypeList.GetSelectedItemIndex() : 0;
            SetVisible(dlssModeList, u == 1);
            SetVisible(fsrModeList, u == 2);
            SetVisible(dlssFgList, f == 1);
            SetVisible(fsrFgText, f == 2);
        }

        static void SetVisible(ModSetting s, bool visible)
        {
            if (s == null) return;
            try { s.SetVisibility(visible); }
            catch (Exception e) { ModConsole.Warning("DLSS: settings page: " + e.Message); }
        }

        /// <summary>What runs on this video card, under the upscaler settings (after the native start).</summary>
        internal static void UpdateInfo()
        {
            if (infoText == null) return;
            try { infoText.SetValue(Info()); }
            catch (Exception e) { ModConsole.Warning("DLSS: settings page: " + e.Message); }
        }

        internal static string Info()
        {
            if (!Native.Loaded) return "The native plugin is not loaded: nothing runs.";
            string fsrUp = First(Native.GetText(Native.Text.FsrUpVersions));
            string fsrFg = First(Native.GetText(Native.Text.FsrFgVersions));
            int dlssFg = Native.Get(Native.Key.FgAvailable) != 0 ? Mathf.Max(1, Native.Get(Native.Key.FgMultiFrameMax)) + 1 : 0;
            return "Video card: " + SystemInfo.graphicsDeviceName + ". " +
                   "DLSS: " + (DlssCamera.DlssAvailable ? "yes" : "no") + ". " +
                   "DLSS Frame Generation: " + (dlssFg > 0 ? "up to " + dlssFg + "x" : "no") + ". " +
                   "FSR: " + (DlssCamera.FsrAvailable && fsrUp.Length > 0 ? fsrUp + (fsrUp.StartsWith("4") ? " (ML)" : "") : "no") + ". " +
                   "FSR Frame Generation: " + (Native.Get(Native.Key.FsrFgAvailable) == 1 && fsrFg.Length > 0 ? fsrFg : "no") + ". " +
                   "Reflex: " + (Native.Get(Native.Key.ReflexAvailable) == 1 ? "yes" : "no") + ".";
        }

        static string First(string list)
        {
            if (string.IsNullOrEmpty(list)) return "";
            int c = list.IndexOf(',');
            return (c >= 0 ? list.Substring(0, c) : list).Trim();
        }

        void OnMenuLoad()
        {
            if (!commandAdded)
            {
                commandAdded = true;
                ConsoleCommand.Add(new DlssCommand());
            }
            if (started) return;
            started = true;
            SettingsDir = ModLoader.GetModSettingsFolder(this);
            AssetsDir = ModLoader.GetModAssetsFolder(this);
            try { Directory.CreateDirectory(SettingsDir); } catch { }

            string err = Installer.EnsurePlugin(AssetsDir);
            if (err != null) { ModConsole.Error("DLSS: " + err); return; }
            if (!Native.Load(SettingsDir, AssetsDir)) { ModConsole.Error("DLSS: " + Native.LoadError); return; }

            GameObject go = new GameObject("MSCDLSS");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Host = go.AddComponent<Runner>();
            Host.StartCoroutine(Host.Init());
        }

        static string FgName(int kind, int frames) =>
            frames <= 0 || kind == 0 ? "off" : (kind == Native.FgKindFsr ? "FSR " : "DLSS ") + (frames + 1) + "x";

        internal static string Status()
        {
            if (!Native.Loaded) return "DLSS: native plugin not loaded" + (Native.LoadError != null ? " (" + Native.LoadError + ")" : "");
            int state = Native.Get(Native.Key.InitState);
            string s = "DLSS " + ModVersion + ": device type " + Native.Get(Native.Key.DeviceType) +
                       " (source " + Native.Get(Native.Key.DeviceSource) + "), render events " + Native.Get(Native.Key.RenderEvents) +
                       ", feature level 0x" + Native.Get(Native.Key.FeatureLevel).ToString("X") +
                       ", D3D11.4 " + (Native.Get(Native.Key.HasD3d11_4) != 0) + "\n";
            if (state != 2) return s + "  NGX: " + (state == 0 ? "not started" : "starting");
            s += "  DLSS/DLAA (D3D11): NGX 0x" + Native.Get(Native.Key.Ngx11Result).ToString("X8") +
                 ", available " + (Native.Get(Native.Key.SrAvailable) != 0) +
                 ", needs driver " + (Native.Get(Native.Key.SrNeedsDriver) != 0) + " (min " + Native.Get(Native.Key.SrMinDriverMajor) + "." + Native.Get(Native.Key.SrMinDriverMinor) + ")" +
                 ", init 0x" + Native.Get(Native.Key.SrInitResult).ToString("X8") + "\n";
            s += "  DLSS Frame Generation (D3D12 bridge): device 0x" + Native.Get(Native.Key.D3d12Result).ToString("X8") +
                 ", NGX 0x" + Native.Get(Native.Key.Ngx12Result).ToString("X8") +
                 ", available " + (Native.Get(Native.Key.FgAvailable) != 0) +
                 ", max generated frames " + Native.Get(Native.Key.FgMultiFrameMax) +
                 ", needs driver " + (Native.Get(Native.Key.FgNeedsDriver) != 0) + " (min " + Native.Get(Native.Key.FgMinDriverMajor) + "." + Native.Get(Native.Key.FgMinDriverMinor) + ")" +
                 ", init 0x" + Native.Get(Native.Key.FgInitResult).ToString("X8") + "\n";
            s += "  FSR (D3D12): loader " + Native.Get(Native.Key.FsrLoaded) +
                 ", upscaler [" + Native.GetText(Native.Text.FsrUpVersions) + "] in use " + Or(Native.GetText(Native.Text.FsrUpActive)) +
                 ", frame generation [" + Native.GetText(Native.Text.FsrFgVersions) + "] in use " + Or(Native.GetText(Native.Text.FsrFgActive)) +
                 ", swap chain [" + Native.GetText(Native.Text.FsrScVersions) + "] in use " + Or(Native.GetText(Native.Text.FsrScActive)) + "\n";
            DlssCamera d = DlssCamera.Current;
            s += "  Upscaler: wanted " + DlssCamera.NameOf(WantUpscaler, WantMode) + ", runs " + (d != null ? d.ModeLabel : "-") +
                 ", camera " + (d != null ? Diag.Path(d.transform) : "none") +
                 ", running " + (d != null && d.Running) + (d != null && d.Running ? " at " + d.RenderSize : "") +
                 "; DLSS create 0x" + Native.Get(Native.Key.DlssCreateResult).ToString("X8") +
                 ", evaluations " + Native.Get(Native.Key.DlssEvals) + ", failures " + Native.Get(Native.Key.DlssFails) +
                 " (last 0x" + Native.Get(Native.Key.DlssEvalResult).ToString("X8") + ")" +
                 "; FSR create " + Native.Get(Native.Key.FsrCreateResult) + ", evaluations " + Native.Get(Native.Key.FsrEvals) +
                 ", failures " + Native.Get(Native.Key.FsrFails) + " (last " + Native.Get(Native.Key.FsrLastResult) + ")" +
                 ", moving bodies " + (d != null ? d.BodyCount + (d.UseBodies ? "" : " (ignored)") + ", in " + (d.Carrier ?? "-") : "-") + "\n";
            int fgKind = Native.Get(Native.Key.FgKind);
            s += "  Frame generation: wanted " + FgName(WantFgKind, FgFramesWanted) + ", runs " +
                 (Host != null ? FgName(Host.FgKindApplied, Host.FgApplied) : "-") +
                 ", path " + (fgKind == Native.FgKindFsr ? "FSR" : fgKind == Native.FgKindDlss ? "DLSS" : "none") +
                 ", stage " + Native.Get(Native.Key.FgStage) +
                 ", real " + Native.Get(Native.Key.FgReal) + ", generated " + Native.Get(Native.Key.FgGenerated) +
                 ", failures " + Native.Get(Native.Key.FgFails) + " (DLSS 0x" + Native.Get(Native.Key.FgLastResult).ToString("X8") +
                 ", FSR " + Native.Get(Native.Key.FsrFgLastResult) + ")" +
                 ", real interval " + (Native.Get(Native.Key.FgIntervalUs) / 1000f).ToString("0.00") + " ms\n";
            s += "  Reflex: wanted " + ReflexChoices[WantReflex] + ", available " + Native.Get(Native.Key.ReflexAvailable) +
                 ", mode set " + Native.Get(Native.Key.ReflexMode) + (Host != null ? ", " + Host.ReflexLine : "") + "\n";
            if (Host != null)
                s += "  Camera between frames: " + Host.CheckedFrames + " frames checked, target left set " + Host.TargetLeftSet +
                     ", centre ray off the camera axis by up to " + Host.MaxRayAngle.ToString("0.000") + " deg, mip bias " +
                     MipBias.Current.ToString("0.00") + " on " + MipBias.Textures + " textures";
            return s;
        }

        static string Or(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        internal static void ProbeMainCamera()
        {
            Camera c = Camera.main;
            if (c == null) { ModConsole.Warning("DLSS: no main camera"); return; }
            if (c.GetComponent<TargetProbe>() == null) c.gameObject.AddComponent<TargetProbe>();
            Print("DLSS: probing " + Diag.Path(c.transform) + " (see mscdlss.log)");
        }

        /// <summary>Console and the plugin's log file (mscdlss.log).</summary>
        internal static void Print(string text)
        {
            ModConsole.Print(text);
            Native.Log(text);
        }
    }

    sealed class Runner : MonoBehaviour
    {
        public Spinner Spin;
        /// <summary>Frame generation wanted changed: applied on the next Update.</summary>
        public bool FgDirty = true;
        /// <summary>What runs: generated frames (0 = off) and the path (Native.FgKind*).</summary>
        public int FgApplied, FgKindApplied;
        float nextFgCheck;
        string fgNote, upscaleNote;

        // What runs for what is wanted; a note in the console when they differ (once per change). The bridge shows its
        // frames in a child window: exclusive fullscreen would hide them.
        void ApplyFg()
        {
            if (Native.Get(Native.Key.InitState) != 2) return;
            if (!FgDirty && Time.realtimeSinceStartup < nextFgCheck) return;
            nextFgCheck = Time.realtimeSinceStartup + 1f;
            FgDirty = false;

            int mode = DlssMod.WantMode, up = DlssMod.WantUpscaler, runs = DlssCamera.Resolve(up, mode);
            string un = null;
            if (mode != DlssCamera.ModeOff && up == DlssCamera.UpscalerDlss && runs == DlssCamera.UpscalerFsr)
                un = "DLSS: DLSS needs a GeForce RTX video card: " + DlssCamera.NameOf(runs, mode) + " runs instead";
            else if (mode != DlssCamera.ModeOff && runs == 0)
                un = "DLSS: no upscaler runs on this video card (DLSS needs a GeForce RTX, FSR DirectX 12)";
            if (un != upscaleNote)
            {
                upscaleNote = un;
                if (un != null) DlssMod.Print(un);
            }

            int kind = DlssMod.WantFgKind, want = DlssMod.FgFramesWanted;
            bool dlssOk = Native.Get(Native.Key.FgAvailable) != 0, fsrOk = Native.Get(Native.Key.FsrFgAvailable) == 1;
            string note = null;
            if (want > 0 && kind == Native.FgKindDlss && !dlssOk)
            {
                if (fsrOk)
                {
                    kind = Native.FgKindFsr;
                    want = 1;
                    note = "DLSS: DLSS Frame Generation needs a GeForce RTX 40 / 50: FSR Frame Generation (2x) runs instead";
                }
                else
                {
                    want = 0;
                    note = "DLSS: frame generation is not available on this video card / driver";
                }
            }
            else if (want > 0 && kind == Native.FgKindFsr && !fsrOk)
            {
                want = 0;
                note = "DLSS: FSR Frame Generation is not available on this video card (it needs DirectX 12)";
            }
            if (want > 0 && kind == Native.FgKindDlss)
            {
                int max = Mathf.Max(1, Native.Get(Native.Key.FgMultiFrameMax));
                if (want > max)
                {
                    note = "DLSS: this video card generates up to " + (max + 1) + "x";
                    want = max;
                }
            }
            if (want > 0 && Screen.fullScreen)
            {
                note = "DLSS: frame generation waits for a window: switch the game to windowed (or start it with -popupwindow for a borderless window)";
                want = 0;
            }
            if (note != fgNote)
            {
                fgNote = note;
                if (note != null) DlssMod.Print(note);
            }
            if (want == FgApplied && (want == 0 || kind == FgKindApplied)) return;
            FgApplied = want;
            FgKindApplied = want > 0 ? kind : 0;
            Native.FgSetMode(want > 0 ? kind : Native.FgKindDlss, want);
        }

        /// <summary>Reflex wanted changed: applied on the next Update.</summary>
        public bool ReflexDirty = true;
        /// <summary>The last latency report, for the status line.</summary>
        public string ReflexLine = "no report yet";
        readonly float[] reflexReport = new float[8];
        float nextReflexReport;
        int frameMarked = -1;
        int reflexApplied = -1;

        void ApplyReflex()
        {
            if (Native.Get(Native.Key.InitState) != 2) return;
            if (ReflexDirty && Native.Get(Native.Key.ReflexAvailable) == 1)
            {
                ReflexDirty = false;
                if (DlssMod.WantReflex != reflexApplied)
                {
                    reflexApplied = DlssMod.WantReflex;
                    Native.ReflexSetMode(reflexApplied);
                }
            }
            if (Time.realtimeSinceStartup >= nextReflexReport)
            {
                nextReflexReport = Time.realtimeSinceStartup + 5f;
                ReflexReport(false);
            }
        }

        /// <summary>The driver's latency report (also into the log); print = to the console as well.</summary>
        public void ReflexReport(bool print)
        {
            if (Native.ReflexReport(reflexReport) <= 0) { if (print) DlssMod.Print("DLSS Reflex: no report yet"); return; }
            float[] r = reflexReport;
            ReflexLine = "latency " + r[1].ToString("0.0") + " ms (simulation start -> picture done), GPU busy " +
                         r[2].ToString("0.0") + " of " + r[6].ToString("0.0") + " ms per frame, simulation " + r[4].ToString("0.0") +
                         " ms, submit " + r[5].ToString("0.0") + " ms";
            if (print) DlssMod.Print("DLSS Reflex (" + DlssMod.ReflexChoices[DlssMod.WantReflex] + "): " + ReflexLine);
        }

        // Reflex markers: the first camera of the frame ends the simulation and starts the render submission.
        void OnAnyPreCull(Camera c)
        {
            if (frameMarked == Time.frameCount) return;
            frameMarked = Time.frameCount;
            Native.ReflexMarker(Native.MarkerSimulationEnd);
        }

        void OnDestroy()
        {
            Camera.onPreCull -= OnAnyPreCull;
        }

        public IEnumerator Init()
        {
            yield return new WaitForEndOfFrame();
            // Unity calls UnitySetGraphicsDevice when it loads the plugin from Plugins; else take the device from a texture.
            if (Native.Get(Native.Key.DeviceType) < 0 && !Native.DeviceFromTexture(Texture2D.whiteTexture))
            {
                ModConsole.Error("DLSS: no Direct3D 11 device");
                yield break;
            }
            Native.Issue(Native.EventInit);
            float until = Time.realtimeSinceStartup + 10f;
            while (Native.Get(Native.Key.InitState) != 2 && Time.realtimeSinceStartup < until) yield return null;
            if (Native.Get(Native.Key.RenderEvents) == 0)
                ModConsole.Error("DLSS: the render thread did not call the plugin (GL.IssuePluginEvent) - plugin not loaded from Plugins?");
            DlssMod.Print(DlssMod.Status());
            DlssMod.Print("DLSS: " + DlssMod.Info());
            DlssMod.UpdateInfo();
            Camera.onPreCull += OnAnyPreCull;
            StartCoroutine(EndOfFrame());
        }

        // Frame generation takes the finished picture here: every camera and the UI are drawn, Present is next.
        // Reflex: the frame's render submission ends here; the next frame starts at the game's message pump.
        IEnumerator EndOfFrame()
        {
            WaitForEndOfFrame wait = new WaitForEndOfFrame();
            for (;;)
            {
                yield return wait;
                if (FgApplied > 0) Native.Issue(Native.EventCaptureFg);
                Native.ReflexMarker(Native.MarkerRenderSubmitEnd);
                // Every camera has drawn: the player's camera has its screen target again whatever happened.
                if (DlssCamera.Current != null) DlssCamera.Current.AfterCamera();
            }
        }

        void Awake()
        {
            Spin = gameObject.AddComponent<Spinner>();
            gameObject.AddComponent<FgHud>();
        }

        void Update()
        {
            string lines = Native.Drain();
            if (lines != null) foreach (string l in lines.Split('\n')) if (l.Length > 0) ModConsole.Print(l);

            // The player's camera exists in the game scene only (and is made again on every load).
            if (DlssCamera.Current == null && Application.loadedLevelName == "GAME")
            {
                Camera c = Camera.main;
                if (c != null && c.name == "FPSCamera")
                {
                    DlssCamera.Attach(c);
                    DlssMod.ApplyToCamera();
                }
            }
            ApplyFg();
            ApplyReflex();
            Native.FgTick();

            // What the game's scripts see of the camera between frames (upscaling sets a target during rendering only).
            DlssCamera cur = DlssCamera.Current;
            if (cur != null && cur.Running)
            {
                CheckedFrames++;
                if (cur.TargetSet) TargetLeftSet++;
                float a = cur.CenterRayAngle;
                if (a > MaxRayAngle) MaxRayAngle = a;
            }
        }

        public int CheckedFrames, TargetLeftSet;
        public float MaxRayAngle;



        static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        static IEnumerator Seconds(float s)
        {
            float until = Time.realtimeSinceStartup + s;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        /// <summary>The world frozen (time scale 0): screenshots of the same picture with the upscaler off and with the
        /// four jitter directions, two of each a few frames apart (a wrong direction shakes and blurs).</summary>
        public IEnumerator Shots(string label, float yaw)
        {
            DlssCamera d = DlssCamera.Current;
            if (d == null) { DlssMod.Print("DLSS shots: no camera"); yield break; }
            float scale = Time.timeScale;
            int mode = d.Mode;
            int shotMode = mode != DlssCamera.ModeOff ? mode : DlssCamera.ModeDlaa;
            float sx = d.JitterSignX, sy = d.JitterSignY;
            Time.timeScale = 0f;
            Spin.YawPerSecond = yaw;
            Spin.UpPerSecond = 0f;
            string[] names = { "off", "pp", "np", "pn", "nn" };
            float[] xs = { 1f, 1f, -1f, 1f, -1f };
            float[] ys = { 1f, 1f, 1f, -1f, -1f };
            for (int i = 0; i < names.Length; i++)
            {
                d.Mode = i > 0 ? shotMode : DlssCamera.ModeOff;
                d.JitterSignX = xs[i];
                d.JitterSignY = ys[i];
                d.ResetHistory();
                yield return StartCoroutine(Seconds(1.5f));
                Application.CaptureScreenshot(Path.Combine(DlssMod.SettingsDir, "shot_" + label + "_" + names[i] + "_a.png"));
                yield return StartCoroutine(Frames(3));
                Application.CaptureScreenshot(Path.Combine(DlssMod.SettingsDir, "shot_" + label + "_" + names[i] + "_b.png"));
                yield return StartCoroutine(Frames(2));
            }
            Spin.YawPerSecond = 0f;
            d.Mode = mode;
            d.JitterSignX = sx;
            d.JitterSignY = sy;
            Time.timeScale = scale;
            DlssMod.Print("DLSS shots " + label + ": done (" + names.Length + " x 2, " + d.ModeLabel + ")");
        }

        /// <summary>The world frozen, the player turning and moving up and down: the reprojection test for every sign
        /// combination of the motion vectors. The right one has the smallest error with motion.</summary>
        public IEnumerator MvSweep()
        {
            DlssCamera d = DlssCamera.Current;
            if (d == null) { DlssMod.Print("DLSS mvsweep: no camera"); yield break; }
            float scale = Time.timeScale;
            int mode = d.Mode;
            float mx = d.MvSignX, my = d.MvSignY;
            Time.timeScale = 0f;
            if (mode == DlssCamera.ModeOff) d.Mode = DlssCamera.ModeDlaa;
            d.Test = true;
            float[] xs = { 1f, 1f, -1f, -1f };
            float[] ys = { 1f, -1f, 1f, -1f };
            for (int i = 0; i < xs.Length; i++)
            {
                d.MvSignX = xs[i];
                d.MvSignY = ys[i];
                DlssMod.Print("DLSS mvsweep: motion sign " + xs[i] + " " + ys[i]);
                Spin.YawPerSecond = 30f;
                Spin.UpPerSecond = 0.6f;
                yield return StartCoroutine(Seconds(2.5f));
            }
            Spin.YawPerSecond = 0f;
            Spin.UpPerSecond = 0f;
            DlssMod.Print("DLSS mvsweep: still camera");
            d.MvSignX = mx;
            d.MvSignY = my;
            yield return StartCoroutine(Seconds(1.5f));
            d.Test = false;
            d.Mode = mode;
            Time.timeScale = scale;
            DlssMod.Print("DLSS mvsweep: done");
        }
    }

    public sealed class DlssCommand : ConsoleCommand
    {
        public override string Name => "dlss";
        public override string Help => "DLSS: dlss status | info | on | off | mode off|dlaa|quality|balanced|performance|ultra | fsr off|native|quality|balanced|performance|ultra|debug on/off | reflex off|on|boost|stats | fg off|2..6|fsr|dump|flip on/off | fgfps on|off | cameras [label] | probe | jitter sx sy | mvsign sx sy | mvtest on|off | bodies on|off | spin yaw up | freeze on|off | shots label [yaw] | mvsweep";
        public override bool ShowInHelp => true;

        static float F(string[] a, int i, float d)
        {
            float v;
            return a.Length > i && float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : d;
        }

        public override void Run(string[] args)
        {
            string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
            DlssCamera d = DlssCamera.Current;
            Runner host = DlssMod.Host;
            try
            {
                                DlssMod.WantSharpness = x;
                                DlssMod.ApplyToCamera();
                                // With an upscaler the state line answers; without one the value waits for it.
                                if (DlssCamera.Resolve(DlssMod.WantUpscaler, DlssMod.WantMode) == 0) Say.Info("con.sharpness", Loc.Sharpness(x));
                            }tus": DlssMod.Print(DlssMod.Status()); break;
                    case "info": DlssMod.Print("DLSS: " + DlssMod.Info()); break;
                    case "on":
                    case "off":
                    case "mode":
                    case "fsr":
                        {
                            // dlss on (DLAA) | off | mode off|dlaa|quality|... (DLSS) | fsr off|native|quality|... (FSR) | fsr debug on|off
                            string v = sub == "mode" || sub == "fsr" ? (args.Length > 1 ? args[1].ToLowerInvariant() : "") : sub == "on" ? "dlaa" : "off";
                            if (sub == "fsr" && v == "debug")
                            {
                                Native.FsrSetDebug(args.Length < 3 || args[2] != "off");
                                break;
                            }
                            int m = DlssCamera.ModeFromName(v);
                            if (m < 0) { DlssMod.Print("DLSS: modes: off, dlaa / native, quality, balanced, performance, ultra"); break; }
                            DlssMod.WantMode = m;
                            if (m != DlssCamera.ModeOff) DlssMod.WantUpscaler = sub == "fsr" ? DlssCamera.UpscalerFsr : DlssCamera.UpscalerDlss;
                            DlssMod.ApplyToCamera();
                            DlssMod.Print("DLSS: " + DlssCamera.NameOf(DlssMod.WantUpscaler, m) + (d == null ? " (no camera yet)" : ""));
                            break;
                        }
                    case "reflex":
                        {
                            // dlss reflex off | on | boost | stats
                            string v = args.Length > 1 ? args[1].ToLowerInvariant() : "stats";
                            int m = v == "off" ? 0 : v == "on" ? 1 : v == "boost" ? 2 : -1;
                            if (m >= 0)
                            {
                                DlssMod.WantReflex = m;
                                if (host != null) host.ReflexDirty = true;
                            }
                            else if (host != null) host.ReflexReport(true);
                            break;
                        }
                    case "cameras": DlssMod.Print("DLSS: " + Diag.DumpCameras(DlssMod.SettingsDir, args.Length > 1 ? args[1] : "now")); break;
                    case "probe": DlssMod.ProbeMainCamera(); break;
                    case "jitter":
                        if (d == null) break;
                        d.JitterSignX = F(args, 1, 1f);
                        d.JitterSignY = F(args, 2, 1f);
                        d.ResetHistory();
                        DlssMod.Print("DLSS: jitter sign " + d.JitterSignX + " " + d.JitterSignY);
                        break;
                    case "mvsign":
                        if (d == null) break;
                        d.MvSignX = F(args, 1, 1f);
                        d.MvSignY = F(args, 2, 1f);
                        DlssMod.Print("DLSS: motion sign " + d.MvSignX + " " + d.MvSignY);
                        break;
                    case "mvtest":
                        if (d != null) d.Test = args.Length < 2 || args[1] != "off";
                        break;
                    case "fg":
                        {
                            // dlss fg off | 2 | 3 | 4 | 5 | 6 (DLSS, the multiplier) | fsr (FSR 2x) | dump | flip on|off | gen | hudless
                            string v = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                            if (v == "dump") { Native.FgDump(DlssMod.SettingsDir); DlssMod.Print("DLSS FG: dump requested"); break; }
                            if (v == "flip") { if (d != null) d.FgFlip = args.Length < 3 || args[2] != "off"; DlssMod.Print("DLSS FG: flip " + (d != null && d.FgFlip)); break; }
                            if (v == "gen") { Native.FgSetGenerate(args.Length < 3 || args[2] != "off"); break; }
                            if (v == "hudless") { Native.FgSetHudless(args.Length < 3 || args[2] != "off"); break; }
                            int x;
                            if (v == "off") DlssMod.WantFgKind = 0;
                            else if (v == "fsr") DlssMod.WantFgKind = Native.FgKindFsr;
                            else if (int.TryParse(v, out x) && x >= 2 && x <= 6)
                            {
                                DlssMod.WantFgKind = Native.FgKindDlss;
                                DlssMod.WantFg = x - 1;
                            }
                            else { DlssMod.Print(DlssMod.Status()); break; }
                            DlssMod.ApplyToCamera();
                            break;
                        }
                    case "mipbias":
                        if (d != null) d.MipBiasExtra = F(args, 1, 0f);
                        DlssMod.Print("DLSS: extra mip bias " + (d != null ? d.MipBiasExtra : 0f) + " (now " + MipBias.Current.ToString("0.00") +
                                      " on " + MipBias.Textures + " textures)");
                        break;
                    case "fgfps":
                        DlssMod.ShowFgFps = args.Length < 2 || args[1] != "off";
                        DlssMod.Print("DLSS: FG counter " + (DlssMod.ShowFgFps ? "on" : "off"));
                        break;
                    case "bodies":
                        if (d != null) d.UseBodies = args.Length < 2 || args[1] != "off";
                        DlssMod.Print("DLSS: moving bodies in the motion vectors " + (d != null && d.UseBodies));
                        break;
                    case "spin":
                        if (host == null) break;
                        host.Spin.YawPerSecond = F(args, 1, 0f);
                        host.Spin.UpPerSecond = F(args, 2, 0f);
                        break;
                    case "freeze": Time.timeScale = args.Length > 1 && args[1] == "off" ? 1f : 0f; break;
                    case "shots": if (host != null) host.StartCoroutine(host.Shots(args.Length > 1 ? args[1] : "now", F(args, 2, 0f))); break;
                    case "mvsweep": if (host != null) host.StartCoroutine(host.MvSweep()); break;
                    default: ModConsole.Print(Help); break;
                }
            }
            catch (Exception e)
            {
                ModConsole.Error("DLSS: " + sub + ": " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
