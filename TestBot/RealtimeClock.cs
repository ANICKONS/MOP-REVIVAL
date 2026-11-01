// MOPR.TestBot - deterministic "real time" for PlayMaker (MOPR6 equivalence harness).
//
// PlayMaker's real-time actions (Wait realTime, Ease*, Animate*, Blink, Flicker, camera fades...) all read
// FsmTime.RealtimeSinceStartup, which follows the wall clock. In a deterministic run every frame is 1/CaptureFps s of
// game time, but a frame takes as long as the computer needs: a faster build (any optimization) reaches the end of
// a real-time Wait at a later frame. Example: Database/PlayerDatabase "Simulation" (Wait {SimulationRate} realTime)
// adds hunger, thirst, stress... in steps; with MOPR6's script sleep (about 20% faster frames) a step fell after a
// snapshot instead of before it.
// Scenario.VirtualRealtime: from the moment captureFramerate is set, FsmTime.RealtimeSinceStartup counts frames:
// the real time at that moment plus 1/CaptureFps per frame since, whatever Time.timeScale is (as real time
// ignores it). Not Time.unscaledTime: in this Unity it follows the wall clock even with captureFramerate (measured:
// 0.849 s and 0.790 s per 60 frames in a vanilla and a mod run). Unity's own Time.realtimeSinceStartup (iTween,
// MasterAudio) stays the wall clock: it is an internal call, not patchable here.
// Vanilla runs and mod runs of a scenario must agree on this setting (new vanilla baselines when it changes).

using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using UnityEngine;

namespace MOPR.TestBot
{
    static class RealtimeClock
    {
        const string HarmonyId = "mopr.testbot.realtime";
        static HarmonyInstance harmony;
        public static bool On;
        static int frame0;
        static double base0, step;

        public static string Install(int fps)
        {
            if (harmony != null) return "already installed";
            frame0 = Time.frameCount;
            base0 = Time.realtimeSinceStartup;
            step = 1.0 / Math.Max(1, fps);
            PropertyInfo p = typeof(FsmTime).GetProperty("RealtimeSinceStartup", BindingFlags.Static | BindingFlags.Public);
            MethodInfo getter = p != null ? p.GetGetMethod() : null;
            if (getter == null) return "FsmTime.RealtimeSinceStartup not found";
            try
            {
                harmony = HarmonyInstance.Create(HarmonyId);
                harmony.Patch(getter, new HarmonyMethod(typeof(RealtimeClock).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            }
            catch (Exception ex)
            {
                harmony = null;
                return "patch failed: " + ex.Message;
            }
            On = true;
            return null;
        }

        static bool Prefix(ref float __result)
        {
            if (!On) return true;
            __result = (float)(base0 + (Time.frameCount - frame0) * step);
            return false;
        }
    }
}
