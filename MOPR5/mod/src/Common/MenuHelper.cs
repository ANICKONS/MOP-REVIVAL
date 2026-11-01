// MOP - REVIVAL
// Copyright(C) 2019-2023 Athlon, 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Main-menu helper. Replaces the original BugReporter MonoBehaviour, which also zipped logs with
// Ionic.Zip.dll — a library MSCLoader 1.3.6+ no longer ships (it would crash on load).
// Bug reports now go through MSCLoader's built-in "Report bug" form (sends logs to the author).

using System.Collections;
using UnityEngine;

using MOP.FSM;

namespace MOP.Common
{
    class MenuHelper : MonoBehaviour
    {
        static MenuHelper instance;
        public static MenuHelper Instance => instance;

        void Awake()
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
            StartCoroutine(CheckSaveSlots());
        }

        IEnumerator CheckSaveSlots()
        {
            yield return null;

            // Compatibility with the Save Slots mod: re-verify the save whenever a different slot is picked.
            GameObject saveSlotsCanvas = GameObject.Find("SaveSlotsCanvas(Clone)");
            if (saveSlotsCanvas != null)
            {
                Transform list = saveSlotsCanvas.transform.Find("SaveSlotsUI/Saves/SavesList");
                if (list != null && list.gameObject.GetComponent<SaveSlotsSupport>() == null)
                    list.gameObject.AddComponent<SaveSlotsSupport>();
            }
        }

        /// <summary>
        /// Presses "Continue" in the main menu. Used once after MOP detected that the Satsuma
        /// was not fully loaded by the game, so the scene is loaded a second time.
        /// </summary>
        public void RestartGame()
        {
            StartCoroutine(DelayedRestart());
        }

        IEnumerator DelayedRestart()
        {
            yield return null;

            ModConsole.Info("con.restart_procedure");

            GameObject interfaceObj = GameObject.Find("Interface");
            Transform continueTransform = interfaceObj != null ? interfaceObj.transform.Find("Buttons/ButtonContinue") : null;
            if (continueTransform == null || !continueTransform.gameObject.activeSelf)
                yield break;

            GameObject buttonContinue = continueTransform.gameObject;
            PlayMakerFSM fsm = buttonContinue.GetPlayMaker("SetSize");
            if (fsm == null)
                yield break;

            var state = fsm.GetState("Action");
            if (state == null)
                yield break;

            while (!state.ActionsLoaded)
                yield return null;

            var actions = state.Actions;
            if (actions.Length > 2)
            {
                actions[2].Active = false;
                actions[2].Enabled = false;
                state.Actions = actions;
            }

            fsm.SendEvent("OVER");
            fsm.SendEvent("DOWN");
        }
    }
}
