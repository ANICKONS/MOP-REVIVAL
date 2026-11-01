// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// MSCLoader 1.4 turned several APIs used by MOP 3.10 into compile errors ([Obsolete(..., true)]).
// They still exist in the binary, so the original MOP.dll keeps working, but new code cannot call them.
// These helpers reproduce EXACTLY what MSCLoader 1.4.2 does inside those obsolete methods today,
// so MOP's behaviour stays identical to how MOP 3.10.4 runs on the current loader.

using System;
using System.Linq;
using UnityEngine;
using MSCLoader;

namespace MOP.FSM
{
    static class LoaderCompat
    {
        /// <summary>
        /// Same as the obsolete MSCLoader FsmHook.FsmInject(GameObject, string, Action):
        /// for every PlayMakerFSM on the object that has a state with that name, the hook is inserted
        /// as the FIRST action of that state (MSCLoader 1.4.2: FsmInject(stateName, hook, false, 0)).
        /// </summary>
        public static void FsmHookInject(GameObject gameObject, string stateName, Action hook)
        {
            if (gameObject == null) throw new ArgumentNullException(nameof(gameObject), "FsmHookInject: object is null (" + stateName + ")");

            PlayMakerFSM[] pm = gameObject.GetComponents<PlayMakerFSM>();
            if (pm == null) return;

            for (int i = 0; i < pm.Length; i++)
            {
                if (pm[i] == null || pm[i].FsmStates == null) continue;
                for (int j = 0; j < pm[i].FsmStates.Length; j++)
                {
                    if (pm[i].FsmStates[j].Name == stateName)
                    {
                        // Same call as MSCLoader 1.4.2. It silently fails on states without actions; we log it.
                        if (!MSCLoader.PlayMakerExtensions.FsmInject(pm[i], stateName, hook, false, 0, false))
                            ModConsole.Warn("con.fsm_hook_failed", stateName, pm[i].FsmName, gameObject.name);
                    }
                }
            }
        }

        /// <summary>
        /// Same as the obsolete MSCLoader ModLoader.GetMod(string): the loaded, enabled mod with this ID, or null.
        /// </summary>
        public static Mod GetMod(string modId)
        {
            if (string.IsNullOrEmpty(modId) || ModLoader.LoadedMods == null) return null;
            return ModLoader.LoadedMods.FirstOrDefault(m => m != null && m.ID.Equals(modId) && !m.isDisabled);
        }
    }
}
