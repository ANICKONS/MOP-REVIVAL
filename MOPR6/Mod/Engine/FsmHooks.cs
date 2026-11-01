// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The one Harmony prefix of MOPR6 on FsmState.OnEnter, installed with the game scene and kept until the game quits.
// It runs before the state's loopCount++ and before its actions, and tells, in this order:
//   1. FsmIdle   - the FSM is not "clean" any more (the fast path and sleep rely on it; the mark must never miss an
//                  enter, so the hook stays installed even while no system uses it);
//   2. the script sleep and the parts of the world switched off (the idle stretch starts again; a sleeping FSM
//      entering a state was reached by a path nobody hooked);
//   3. SaveHooks - save points and SAVEGAME broadcasts (the save barrier, requirement 6).
// One prefix instead of one per system: FsmState.OnEnter runs for every state change of the game.

using System;
using System.Reflection;
using Harmony;
using HutongGames.PlayMaker;
using MOPR6.Diagnostics;

namespace MOPR6.Engine
{
    public static class FsmHooks
    {
        const string HarmonyId = "mopr.fsm";
        static HarmonyInstance harmony;

        public static bool Installed => harmony != null;

        /// <summary>Null on success, else the reason (MOPR6 then stays off: the save barrier needs this hook).</summary>
        public static string Install()
        {
            if (harmony != null) return null;
            MethodInfo onEnter = typeof(FsmState).GetMethod("OnEnter", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (onEnter == null) return "FsmState.OnEnter not found";
            if (typeof(FsmState).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic) == null) return "FsmState.fsm not found";
            HarmonyInstance h = HarmonyInstance.Create(HarmonyId);
            h.Patch(onEnter, new HarmonyMethod(typeof(FsmHooks).GetMethod(nameof(OnEnterPrefix), BindingFlags.Static | BindingFlags.NonPublic)), null, null);
            harmony = h;
            return null;
        }

        static void OnEnterPrefix(FsmState __instance, Fsm ___fsm)
        {
            // Must never throw into the game: each part reports its own errors.
            FsmIdle.OnEnter(___fsm);
            try { Sleep.FsmSleep.OnEnter(___fsm); }
            catch (Exception ex) { ModLog.Report("sleep/OnEnter", ex, __instance != null ? __instance.Name : null); }
            try { World.DormantIndex.StateEntered(___fsm); }
            catch (Exception ex) { ModLog.Report("world.deactivate/OnEnter", ex, __instance != null ? __instance.Name : null); }
            try { SaveHooks.OnEnter(__instance); }
            catch (Exception ex) { ModLog.Report("save/OnEnter", ex, __instance != null ? __instance.Name : null); }
        }
    }
}
