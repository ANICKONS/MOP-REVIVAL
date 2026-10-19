using System.Collections.Generic;

namespace MOPR.TestBot
{
    /// <summary>Test scenario, read from run.json (written by scripts\test-run.ps1).</summary>
    public class Scenario
    {
        public string Name = "unnamed";
        public string Variant = "";               // label of the build under test (e.g. "vanilla", "mopr5")
        public float SettleSeconds = 30f;         // after the player became controllable
        public float LoadTimeoutSeconds = 900f;
        public bool StayInMenu;                   // run the steps in the main menu (no Continue)
        public float MenuReadyTimeoutSeconds = 30f; // wait for the runner's menu.ready (startup popups handled)
        public float LoadShotInterval;            // > 0: screenshot every N seconds while the game loads (shot_load_N.png)
        public List<Step> MenuSteps = new List<Step>();   // run in the main menu before Continue (not with StayInMenu)
        public List<Step> Steps = new List<Step>();
    }

    public class Step
    {
        // teleport | wait | measure | profile | snapshot | compare | quit | log
        public string Op;
        public string Label;
        public float Seconds;
        public float[] Pos;                       // teleport target (x, y, z)
        public float Yaw;                         // teleport heading in degrees
        public float Pitch = 0f;                  // camera pitch in degrees (0 = horizon)
        public string A, B;                       // compare: snapshot labels
        public string Text;                       // log
        public string Mode;                       // engine: off | on | shadow
        public bool Detail;                       // engine: blocker statistics; profile: also profile PlayMaker actions
        public bool Alloc;                        // profile: also sample managed allocations
        public bool Extended;                     // profile: also OnGUI, physics messages, all coroutines
        public float Block = 1f;                  // ab: seconds per block (A and B = engine modes)
        public string Path;                       // dumpfsm: hierarchy path of the root object; fsmset/fsmevent/activate/align: object;
                                                  // teleport: Pos is an offset from this object
        public int Max;                           // find/dumpfsm: max objects by name (0 = all)
        public bool Recursive = true;             // dumpfsm: include children
        // Repro steps (GameOps.cs).
        public string Fsm;                        // fsmset/fsmevent: FSM name
        public string Var;                        // fsmset: variable name
        public string Value;                      // fsmset: value as text (gameObject: hierarchy path, vector3: "x,y,z")
        public string Event;                      // fsmevent: event name
        public bool Active = true;                // activate
        public string Anchor;                     // align: descendant of Path that is placed on Target (default: Path itself)
        public string Target;                     // align: object to align to
        public List<string> Probes;               // watch: values to sample (see GameOps)
        public float Interval = 1f;               // watch: seconds between samples
        public bool Local;                        // teleport with Path: the offset is in the object's yaw frame
        public float Fov;                         // shadowsweep: field of view of the test camera (0 = the main camera's)
        public float Speed = 60f;                 // drive: km/h
        public int Last;                          // drive: last waypoint of the route (0 = the end); Max = first
    }

    // ---------------------------------------------------------------------------------------------
    // Report
    // ---------------------------------------------------------------------------------------------

    public class Report
    {
        public string Scenario;
        public string Variant;
        public string StartedAt;
        public string FinishedAt;
        public bool MoprPresent;
        public string MoprVersion;
        public List<string> Mods = new List<string>();
        public List<string> Log = new List<string>();
        public List<MeasureResult> Measures = new List<MeasureResult>();
        public List<ProfileResult> Profiles = new List<ProfileResult>();
        public List<CompareResult> Compares = new List<CompareResult>();
        public List<EngineResult> Engine = new List<EngineResult>();
        public List<AbResult> AbTests = new List<AbResult>();
        public List<float> GcPauseMs = new List<float>();
        public string Error;
        public float LoadSeconds;
    }

    public class AbResult
    {
        public string Label, A, B;
        public int Switches;
        public int FramesA, FramesB;
        public float AvgFpsA, AvgFpsB, MedianMsA, MedianMsB, P95MsA, P95MsB;
        public float AllocMBPerSecA, AllocMBPerSecB;
        public float FpsGainPercent;              // B vs A, average FPS
        public float MedianGainPercent;           // B vs A, median frame time
    }

    public class EngineResult
    {
        public string Label;                      // label of the step that produced it
        public string Mode;
        public string Counters;                   // key=value;... (EngineLab.Counters)
        public long ShadowMismatches;
        public string Report;                     // human-readable EngineLab.Report
    }

    public class MeasureResult
    {
        public string Label;
        public float Seconds;
        public int Frames;
        public float AvgFps;
        public float Low1PercentFps;
        public float FrameMsP50, FrameMsP95, FrameMsP99, FrameMsMax;
        public int GcCollections;
        public float AllocMBPerSec;               // managed allocation rate (sum of heap growth between frames)
        public List<float> GcFrameMs = new List<float>();   // duration of each frame that contained a collection
        public float ManagedMBStart, ManagedMBEnd;
        public int ActiveFsms;
        public int Rigidbodies, AwakeRigidbodies;
        public int ActiveRenderers;
        public float[] PlayerPos;
        public string EngineMode;                 // MOPR engine fast path mode during the measurement
    }

    public class ProfileResult
    {
        public string Label;
        public float Seconds;
        public int Frames;
        public float FrameMsAvg;
        public float ScriptMsPerFrame;            // sum of all measured script entry points
        public float PlayMakerMsPerFrame;
        public float ScriptMsPerFrameCorrected;   // minus the calibrated profiler overhead
        public float PlayMakerMsPerFrameCorrected;
        public float CallsPerFrame;
        public float OverheadInnerUs;             // profiler result for an empty method
        public float OverheadTotalUs;             // real extra wall time per patched call
        public string EngineMode;
        public float AllocKBPerSec;               // Alloc profile: heap growth inside measured script calls
        public float ActionAllocKBPerSec;
        public List<AllocEntry> ByAllocType = new List<AllocEntry>();
        public List<AllocEntry> ByAllocFsmState = new List<AllocEntry>();
        public List<AllocEntry> ByAllocAction = new List<AllocEntry>();
        public float MainCameraRenderMs;          // OnPreCull -> OnPostRender of Camera.main, per frame
        public float MainCameraRendersPerFrame;
        public float OtherCamerasRenderMs;        // same for every other camera enabled at the start of the profile
        public List<ProfileEntry> ByCamera = new List<ProfileEntry>();
        public float ActionSelfMsPerFrame;        // Detail: sum of the self time of all PlayMaker actions
        public int ActionTypesInUse, ActionMethodsPatched;
        public List<ActionEntry> ByAction = new List<ActionEntry>();
        public int PatchedMethods;
        public int PatchFailures;
        public List<ProfileEntry> ByType = new List<ProfileEntry>();
        public List<ProfileEntry> ByFsm = new List<ProfileEntry>();
        public List<ProfileEntry> ByFsmRoot = new List<ProfileEntry>();
        public List<ProfileEntry> ByFsmState = new List<ProfileEntry>();
    }

    public class ProfileEntry
    {
        public string Name;
        public float MsPerFrame;
        public float CallsPerFrame;
        public float UsPerCall;
        public float CorrectedMsPerFrame;
        public float CorrectedUsPerCall;
    }

    public class AllocEntry
    {
        public string Name;
        public float KBPerSec;
        public float BytesPerCall;                // statistical (block granularity)
    }

    public class ActionEntry
    {
        public string Name;                       // ConcreteActionType.Hook
        public float SelfMsPerFrame;
        public float InclMsPerFrame;
        public float CallsPerFrame;
        public float SelfUsPerCall;
    }

    public class CompareResult
    {
        public string A, B;
        public int KeysA, KeysB, Common, Changed, OnlyA, OnlyB;
        public List<string> Changes = new List<string>();
    }
}
