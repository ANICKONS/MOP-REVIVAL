// Modern Optimization Plugin
// Copyright(C) 2019-2022 Athlon

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using System.Collections.Generic;
using HutongGames.PlayMaker;

using MOP.Managers;
using MOP.Common;
using MOP.Common.Enumerations;
using MOP.FSM;
using MOP.FSM.Actions;
using MOP.Vehicles.Cases;
using MOP.Vehicles.Managers;
using MOP.Vehicles.Managers.SatsumaManagers;
using MOP.Helpers;

namespace MOP
{
    class GameFixes : MonoBehaviour
    {
        // This class fixes shit done by ToplessGun.
        // Also some caused by MOP itself...

        static GameFixes instance;
        public static GameFixes Instance { get => instance; }

        public bool HoodFixDone { get; private set; }

        public GameFixes()
        {
            instance = this;
        }

        public void MainFixes()
        {
            // GT Grille resetting fix.
            try
            {
                PlayMakerFSM[] gtGrille = GameObject.Find("grille gt(Clone)").GetComponents<PlayMakerFSM>();
                foreach (var fsm in gtGrille)
                    fsm.Fsm.RestartOnEnable = false;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "GT_GRILLE_ERROR");
            }

            Transform buildings = null;
            Transform perajarvi = null;
            // Random fixes.
            // Find house of Teimo and detach it from Perajarvi, so it can be loaded and unloaded separately
            try
            {
                buildings = GameObject.Find("Buildings").transform;
                perajarvi = GameObject.Find("PERAJARVI").transform;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, true, "PERAJARVI_FIXES_BUILDINGS_FIND_ERROR");
            }

            if (buildings != null && perajarvi != null)
            {
                //SetParent(perajarvi, buildings, "HouseRintama4");
                //SetParent(perajarvi, buildings, "ChickenHouse");
                //SetParent(perajarvi, buildings, "HouseOld5");
                SetParent(perajarvi, buildings, "HouseRintama3");
                SetParent(perajarvi, buildings, "HouseSmall3");
                SetParent(null, "CHURCHWALL");
                SetParent(null, "DINGONBIISI");

                // Perajarvi fixes for multiple objects with the same name.
                // Instead of being the part of Perajarvi, we're changing it to be the part of Buildings.
                Transform[] perajarviChilds = perajarvi.GetComponentsInChildren<Transform>();
                for (int i = 0; i < perajarviChilds.Length; i++)
                {
                    if (perajarviChilds[i] == null) continue;

                    string objName = perajarviChilds[i].gameObject.name;

                    if (objName.Contains("silo") || objName == "MailBox" || objName == "Greenhouse")
                    {
                        SetParent(buildings, perajarviChilds[i]);
                        continue;
                    }
                }

                // Fix for floppies at Jokke's new house
                while (perajarvi.transform.Find("TerraceHouse/diskette(itemx)") != null)
                {
                    Transform diskette = perajarvi.transform.Find("TerraceHouse/diskette(itemx)");
                    if (diskette != null && diskette.parent != null)
                    {
                        SetParent(null, diskette);
                    }
                }

                // Fix for Jokke's house furnitures clipping through floor
                SetParent(perajarvi, null, "TerraceHouse/Colliders");
            }

            // Fix for cottage items disappearing when moved
            try
            {
                GameObject.Find("coffee pan(itemx)").transform.parent = null;
                GameObject.Find("lantern(itemx)").transform.parent = null;
                GameObject.Find("coffee cup(itemx)").transform.parent = null;
                GameObject.Find("camera(itemx)").transform.parent = null;
                GameObject.Find("fireworks bag(itemx)").transform.parent = null;
                // Fix for fishing areas
                GameObject.Find("FishAreaAVERAGE").transform.parent = null;
                GameObject.Find("FishAreaBAD").transform.parent = null;
                GameObject.Find("FishAreaGOOD").transform.parent = null;
                GameObject.Find("FishAreaGOOD2").transform.parent = null;
                // Fix for strawberry field mailboxes
                //GameObject.Find("JOBS").transform.Find("StrawberryField/LOD/MailBox").parent = null;
                //GameObject.Find("JOBS").transform.Find("StrawberryField/LOD/MailBox").parent = null;
                // Fix for items left on cottage chimney clipping through it on first load of cottage
                GameObject.Find("COTTAGE").transform.Find("MESH/Cottage_chimney").parent = null;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "ITEMS_FIXES_ERROR");
            }

            // MOP - REVIVAL 5.1.1 (fix, original MOP behaviour): MOP 3.10 hooked "Player in car" of Pena's car
            // (TRAFFIC/VehiclesDirtRoad/Rally/FITTAN) and the drunk driver's car (NPC_CARS/KUSKI) and moved the car to
            // the scene root when the player got in as a passenger, so switching TRAFFIC / NPC_CARS off at home would
            // not take the player with it. The car never went back, and everything that finds it by its place in the
            // scene lost it. Call Your Cousin checks "the player is under TRAFFIC" to know the player got in; with the
            // car moved out it never saw it and kept Pena stopped at the house (it stops him every physics frame
            // while the player is within 10 m), so the car did not move with the player inside. Now nothing is moved:
            // a world object the player is in is never switched off (SimpleObjectToggle).

            // Fixed Ventii bet resetting to default on cabin load.
            Transform cabin = GameObject.Find("CABIN").transform;
            try
            {
                cabin.Find("Cabin/Ventti/Table/GameManager").GetPlayMaker("Use").Fsm.RestartOnEnable = false;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "VENTII_FIX_ERROR");
            }

            // Cabin door resetting fix.
            try
            {
                cabin.Find("Cabin/Door/Pivot/Handle").gameObject.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "CABIN_DOOR_RESET_FIX_ERROR");
            }

            // Junk cars - setting Load game to null.
            int junkCarCounter = 1;
            try
            {
                for (; GameObject.Find($"JunkCar{junkCarCounter}") != null; junkCarCounter++)
                {
                    GameObject junk = GameObject.Find($"JunkCar{junkCarCounter}");
                    junk.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;

                    WorldObjectManager.Instance.Add(junk, DisableOn.Distance);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, $"JUNK_CARS_{junkCarCounter}_ERROR");;
            }

            // Toggle Humans (apart from Farmer and Fighter2).
            // MOP - REVIVAL 5.1 (fixes, original MOP bugs):
            // - "Randomizer" (the walkers of the December 2023 update: Kale, Julli, Kristian, Unto, Rauno, Alpo) is
            //   left alone. It sits at (0, 0, 0), so MOP only enabled it within 150 m of that point, i.e. at home,
            //   where MOP switches the whole HUMANS off: the walkers never appeared anywhere. Each walker's own
            //   FSM already checks its distance to the player.
            // - One try per human: one failure (e.g. a rule for an object without a renderer) skipped the rest.
            try
            {
                GameObject humans = GameObject.Find("HUMANS");
                foreach (Transform t in humans.GetComponentsInChildren<Transform>().Where(t => t.parent == humans.transform))
                {
                    if (t.gameObject.name.EqualsAny("HUMANS", "Fighter2", "Farmer", "FighterPub", "Randomizer"))
                        continue;

                    try
                    {
                        var human = WorldObjectManager.Instance.Add(t.gameObject, DisableOn.Distance);
                        if (human != null)
                            human.MinimumToggleDistance = 150;
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, "HUMANS_ERROR | " + t.gameObject.name);
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "HUMANS_ERROR");
            }

            // Fixes wasp hives resetting to on load values.
            try
            {
                GameObject[] wasphives = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "WaspHive").ToArray();
                foreach (GameObject wasphive in wasphives)
                {
                    wasphive.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "WASPHIVES_ERROR");
            }

            // Disabling the script that sets the kinematic state of Satsuma to False.
            // MOP - REVIVAL: skipped with "satsuma_ignore" - MOP doesn't manage the Satsuma's physics then,
            // so the vanilla actions must stay, otherwise nothing would ever make the car dynamic again.
            if (!Rules.RulesManager.Book.SatsumaExclude)
            {
                try
                {
                    GameObject hand = GameObject.Find("PLAYER/Pivot/AnimPivot/Camera/FPSCamera/1Hand_Assemble/Hand");
                    PlayMakerFSM pickUp = hand.GetPlayMaker("PickUp");

                    pickUp.GetState("Drop part").RemoveAction(0);
                    pickUp.GetState("Drop part 2").RemoveAction(0);
                    pickUp.GetState("Tool picked").RemoveAction(2);
                    pickUp.GetState("Drop tool").RemoveAction(0);
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, true, "SATSUMA_HAND_BS_FIX");
                }
            }

            // Preventing mattres from being disabled.
            try
            {
                Transform mattres = GameObject.Find("DINGONBIISI").transform.Find("mattres");
                if (mattres != null)
                    mattres.parent = null;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "MANSION_MATTRES_ERROR");
            }

            // Item anti clip for cottage.
            try
            {
                GameObject area = new GameObject("MOP_ItemAntiClip");
                area.transform.position = new Vector3(-848.3f, -5.4f, 505.5f);
                area.transform.eulerAngles = new Vector3(0, 343.0013f, 0);
                area.AddComponent<ItemAntiClip>();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, true, "ITEM_ANTICLIP_ERROR");
            }

            // Z-fighting fix for wristwatch.
            try
            {
                GameObject player = GameObject.Find("PLAYER");

                if (player)
                {
                    Transform hour = player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Watch/Animate/BreathAnim/WristwatchHand/Clock/Pivot/Hour/hour");
                    if (hour)
                    {
                        hour.GetComponent<Renderer>().material.renderQueue = 3001;
                    }

                    Transform minute = player.transform.Find("Pivot/AnimPivot/Camera/FPSCamera/FPSCamera/Watch/Animate/BreathAnim/WristwatchHand/Clock/Pivot/Minute/minute");
                    if (minute)
                    {
                        minute.GetComponent<Renderer>().material.renderQueue = 3002;
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "HANDWATCH_RENDERER_QUEUE_ERROR");
            }

            // Adds roll fix to the bus.
            try
            {
                GameObject bus = GameObject.Find("BUS");
                if (bus)
                {
                    bus.AddComponent<BusRollFix>();
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "BUS_ROLL_FIX_ERROR");
            }

            // Fixes bedroom window wrap resetting to default value.
            try
            {
                Transform triggerWindowWrap = GameObject.Find("YARD").transform.Find("Building/BEDROOM1/trigger_window_wrap");
                if (triggerWindowWrap != null)
                    triggerWindowWrap.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TRIGGER_WINDOW_WRAP_ERROR");
            }

            // Fixes diskette ejecting not wokring.
            try
            {
                GameObject triggerDiskette = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == "TriggerDiskette");

                if (triggerDiskette != null)
                {
                    triggerDiskette.GetPlayMaker("Assembly").Fsm.RestartOnEnable = false;
                }
                else
                {
                    ModConsole.Debug("Trigger diskette was null");
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TRIGGER_DISKETTE_ERROR");
            }

            // Fixed computer memory resetting.
            try
            {
                GameObject triggerPlayMode = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == "TriggerPlayMode");
                if (triggerPlayMode != null)
                {
                    Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "TriggerPlayMode").GetPlayMaker("PlayerTrigger").Fsm.RestartOnEnable = false;
                }
                else
                {
                    ModConsole.Debug("Trigger play mode was null");
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TRIGGER_PLAY_MODE_ERROR");
            }

            // Fixes berry picking skill resetting to default.
            try
            {
                GameObject.Find("JOBS").transform.Find("StrawberryField").gameObject.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "STRAWBERRY_FIELD_FSM");
            }

            // GrandmaHiker fixes.
            try
            {
                GameObject grandmaHiker = GameObject.Find("GrannyHiker");
                if (grandmaHiker)
                {
                    GameObject skeleton = grandmaHiker.transform.Find("Char/skeleton").gameObject;

                    PlayMakerFSM logicFSM = grandmaHiker.GetPlayMaker("Logic");

                    FsmState openDoorFsmState = logicFSM.GetState("Open door");
                    List<FsmStateAction> openDoorActions = openDoorFsmState.Actions.ToList();
                    openDoorActions.Add(new GrandmaHiker(skeleton, false));
                    openDoorFsmState.Actions = openDoorActions.ToArray();

                    FsmState setMass2State = logicFSM.GetState("Set mass 2");
                    List<FsmStateAction> setMass2Actions = setMass2State.Actions.ToList();
                    setMass2Actions.Add(new GrandmaHiker(skeleton, true));
                    setMass2State.Actions = setMass2Actions.ToArray();
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "GRANDMA_HIKER_FIXES");
            }

            // Construction site
            try
            {
                Transform construction = GameObject.Find("PERAJARVI").transform.Find("ConstructionSite");
                if (construction)
                {
                    construction.parent = null;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "PERAJARVI_CONSTRUCTION_ERROR");
            }

            // Satsuma parts trigger fix (not with "satsuma_ignore": the Satsuma belongs to another mod then).
            try
            {
                if (!Rules.RulesManager.Book.SatsumaExclude)
                    gameObject.AddComponent<SatsumaTriggerFixer>();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_TRIGGER_FIXER_ERROR");
            }

            // MOP - REVIVAL 5.1: the interior lamp's housing sticks out of the Satsuma's roof (vanilla model).
            try
            {
                if (!Rules.RulesManager.Book.SatsumaExclude)
                    StartCoroutine(RoofLampFixWhenReady());
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_ROOF_LAMP_FIX");
            }

            // MailBox fix
            try
            {
                foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "MailBox"))
                {
                    Transform hatch = g.transform.Find("BoxHatch");
                    if (hatch)
                    {
                        hatch.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "MAILBOX_ERROR");
            }

            ModConsole.Debug("Finished applying fixes");
        }

        /// <summary>
        /// MOP - REVIVAL 5.1: the roof lamp fix needs the car body in place, which the game attaches during
        /// loading. Runs once MOP's item initialization is done, before the mesh merger first looks at the car
        /// (it waits 3 s longer), so the merged roof already has the fixed housing.
        /// </summary>
        IEnumerator RoofLampFixWhenReady()
        {
            while (Hypervisor.Instance == null || !Hypervisor.Instance.IsItemInitializationDone())
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(1f);
            try
            {
                GameObject satsuma = GameObject.Find("SATSUMA(557kg, 248)");
                if (satsuma != null)
                {
                    // Not expected to be merged yet; if it is, the merged roof must not keep the old housing.
                    Next.VehicleMeshMerge.UnmergeSatsuma("roof lamp fix");
                    Next.RoofLampFix.Apply(satsuma.transform);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_ROOF_LAMP_FIX");
            }
        }

        /// <summary>
        /// Fixes hood popping out of the car on game load.
        /// </summary>
        public void HoodFix(Transform hoodPivot)
        {
            StartCoroutine(HoodFixCoroutine(hoodPivot));
        }

        IEnumerator HoodFixCoroutine(Transform hoodPivot)
        {
            yield return new WaitForSeconds(2);

            // Hood
            Transform hood = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "hood(Clone)").transform;
            CustomPlayMakerFixedUpdate hoodFixedUpdate = hood.gameObject.AddComponent<CustomPlayMakerFixedUpdate>();

            // Fiber Hood
            GameObject fiberHood = Resources.FindObjectsOfTypeAll<GameObject>()
                .First(obj => obj.name == "fiberglass hood(Clone)"
                && obj.GetComponent<PlayMakerFSM>() != null
                && obj.GetComponent<MeshCollider>() != null);

            int retries = 0;
            if (FsmManager.IsStockHoodBolted() && hood.parent != hoodPivot)
            {
                hood.gameObject.SetActive(true);

                while (hood.parent != hoodPivot)
                {
                    // Satsuma got disabled while trying to fix the hood.
                    // Attempt to fix it later.
                    if (!hoodPivot.gameObject.activeSelf)
                    {
                        yield break;
                    }

                    FsmManager.ForceHoodAssemble();
                    yield return null;

                    // If 10 retries failed, quit the loop.
                    retries++;
                    if (retries == 10)
                    {
                        break;
                    }
                }
            }

            hoodFixedUpdate.StartFixedUpdate();

            if (fiberHood != null && FsmManager.IsFiberHoodBolted() && fiberHood.transform.parent != hoodPivot)
            {
                retries = 0;
                while (fiberHood.transform.parent != hoodPivot)
                {
                    // Satsuma got disabled while trying to fix the hood.
                    // Attempt to fix it later.
                    if (!hoodPivot.gameObject.activeSelf)
                    {
                        Satsuma.Instance.AfterFirstEnable = false;
                        yield break;
                    }

                    FsmManager.ForceHoodAssemble();
                    yield return null;

                    // If 10 retries failed, quit the loop.
                    retries++;
                    if (retries == 60)
                    {
                        break;
                    }
                }
            }

            hood.gameObject.AddComponent<SatsumaBoltsAntiReload>();
            fiberHood.gameObject.AddComponent<SatsumaBoltsAntiReload>();

            // Adds delayed initialization for hood hinge.
            if (hood.gameObject.GetComponent<DelayedHingeManager>() == null)
                hood.gameObject.AddComponent<DelayedHingeManager>();

            // Fix for hood not being able to be closed.
            if (hood.gameObject.GetComponent<SatsumaCustomHoodUse>() == null)
                hood.gameObject.AddComponent<SatsumaCustomHoodUse>();

            // MOP - REVIVAL 5.1: the "battery popping out" fix of MOP 3.10 is removed. Satsuma.cs passed the battery
            // trigger and pivot in swapped order, so its condition (trigger_battery.parent == null) was never true
            // and it never ran. Made to run, its ASSEMBLE event resets the battery bolts (found in MOPR 4.x).

            HoodFixDone = true;
        }

        internal void ForceDetachTrailer()
        {
            StartCoroutine(ForceDetachTrailerRoutine());
        }

        IEnumerator ForceDetachTrailerRoutine()
        {
            while (!MopSettings.IsModActive) yield return null;
            for (int i = 0; i < 10; i++)
            {
                PlayMakerFSM.BroadcastEvent("TRAILERDETACH");
                yield return null;
            }
        }

        void SetParent(Transform root, Transform newParent, string objectName)
        {
            try
            {
                root.Find(objectName).parent = newParent;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, $"GAMEFIXES_PARENTCHANGING\n{root.gameObject.name} => {objectName} => {(newParent == null ? "null" : newParent.gameObject.name)}");
            }
        }

        void SetParent(Transform newParent, string objectName)
        {
            try
            {
                GameObject.Find(objectName).transform.parent = newParent;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, $"GAMEFIXES_PARENTCHANGING_{objectName}");
            }
        }

        void SetParent(Transform newParent, Transform obj)
        {
            try
            {
                obj.parent = newParent;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, $"GAMEFIXES_PARENTCHANGING_UNKNOWN_TRANSFORM");
            }
        }
    }
}
