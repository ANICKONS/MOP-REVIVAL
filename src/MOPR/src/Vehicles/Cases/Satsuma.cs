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

using HutongGames.PlayMaker;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using MOP.FSM;
using MOP.FSM.Actions;
using MOP.Common;
using MOP.Common.Enumerations;
using MOP.Items.Cases;
using MOP.Vehicles.Managers.SatsumaManagers;
using MOP.Rules;
using MOP.Next;

namespace MOP.Vehicles.Cases
{
    class Satsuma : Vehicle
    {
        const int MinimumBolts = 163;

        static Satsuma instance;
        public static Satsuma Instance { get => instance; }

        // If true, prevents Satsuma physics from disabling.
        public bool IsSatsumaInInspectionArea;
        public bool IsSatsumaInParcFerme;

        // If is false, the first enabling script will be toggled.
        public bool AfterFirstEnable;

        // Objects that can be toggled.
        readonly Transform[] disableableObjects;

        // Whitelist of name of objects that should be disabled.
        readonly string[] whiteList = 
        { 
            "DriveTrigger", "SpawnPlayer", "CarShadowProjector", "Hooks", "CameraPivot", "PivotSeatR", "HookRear", "BeerCaseTarget",
            "HookFront", "GetInPivot", "TrafficTrigger", "Wipers", "WiperLeftPivot", "WiperRightPivot", "wipers_tap", "wipers_rod",
            "generalPivot", "CarRearMirrorPivot", "StagingWheel", "shadow_body", "NormalFront", "NormalSide", "NormalRear", "WindSide",
            "WindFront", "WindRear", "CoG", "Interior", "Body", "MiscParts", "Dashboard" 
        };
        readonly string[] ignoredRendererNames = { "Sphere", "Capsule", "Cube", "Mokia" };

        // Masked objets are the ones which bolts are hidden, until the overlaping object is detached.
        readonly string[] maskedObjectNames = 
        { 
            "MaskedClutchCover", "MaskedBearing2", "MaskedBearing3", "MaskedFlywheel", 
            "MaskedFlywheelRacing", "MaskedPiston2", "MaskedPiston3", "MaskedPiston4" 
        };

        // Renderers.
        // Renderes of the entire car.
        readonly List<Renderer> renderers;
        bool renderersOnCloseEnabled;

        // Pivot transform of hood.
        readonly Transform pivotHood;

        // Key object.
        readonly GameObject key;

        // Elements that are toggled uppon some situation.
        readonly List<SatsumaOnActionObjects> satsumaOnActionObjects;

        // Bolts that resetting will be disabled.
        List<SatsumaBoltsAntiReload> satsumaBoltsAntiReloads;
        bool partsUnglued;
        // Fix for elements for which the bolts are masked.
        readonly Dictionary<GameObject, bool> maskedElements;
        int maskedFixStages;

        // Dashboard gauges.
        readonly List<Material> dashboardMaterials;
        readonly FsmInt lightSelection;

        // Cooldown ticking object.
        readonly GameObject cooldownTick;

        // AI object.
        readonly PlayMakerFSM drivingAI;

        // Used for when the Fleetari has nicked the car.
        // It prevents the car teleporting back to the last position known to the player.
        bool hasBeenMovedByFleetari;

        internal Quaternion lastGoodRotation;
        internal Vector3 lastGoodPosition;
        bool lastGoodRotationSaved;

        // AIR TIME CHECKS
        // Frames left, where the game assumes that the car is still mid-air.
        private int framesLeftToSkipForGroundCheckDueToFlight;
        // Frames to skip when air-time has started.
        private const int FramesToSkip = 30;
        // Velocity magnitude above which the car body is considered as moving.
        private const float VelocityCheckDeadzone = 0.2f;

        /// <summary>
        /// Initialize class
        /// </summary>
        public Satsuma(string gameObject) : base(gameObject)
        {
            instance = this;
            vehicleType = VehiclesTypes.Satsuma;

            // MOP - REVIVAL: "satsuma_ignore" rule (full Satsuma overhaul mods, e.g. Satsuma LX).
            // MOP keeps a reference to the car (other systems compare item roots against it), but applies
            // no fixes, never toggles it, never touches its physics and never glues parts on save.
            if (IsPassive)
            {
                disableableObjects = new Transform[0];
                renderers = new List<Renderer>();
                satsumaOnActionObjects = new List<SatsumaOnActionObjects>();
                satsumaBoltsAntiReloads = new List<SatsumaBoltsAntiReload>();
                maskedElements = new Dictionary<GameObject, bool>();
                Toggle = IgnoreToggle;
                IsActive = false;
                ModConsole.Info("con.satsuma_excluded");
                return;
            }

            disableableObjects = GetDisableableChilds();

            Toggle = ToggleActive;

            // Get engine bay renderers
            pivotHood = this.gameObject.transform.Find("Body/pivot_hood");

            // Get all the other renderers
            renderers = GetRenderersToDisable();

            // Ignore Rule
            ObjectRule vehicleRule = RulesManager.Book.Object(this.gameObject.name);
            if (vehicleRule != ObjectRule.None)
            {
                Toggle = IgnoreToggle;

                if (vehicleRule == ObjectRule.Exclude)
                    IsActive = false;
            }

            lastGoodRotation = transform.localRotation;
            lastGoodPosition = transform.localPosition;

            // Adding components to normal and bucket seats.
            try
            {
                GameObject.Find("seat driver(Clone)").AddComponent<SatsumaSeatsManager>();
                GameObject.Find("seat passenger(Clone)").AddComponent<SatsumaSeatsManager>();
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_SEAT_FIX_ERROR");
            }

            try
            {
                GameObject bucketDriver = GameObject.Find("bucket seat driver(Clone)");
                GameObject bucketPassanger = GameObject.Find("bucket seat passenger(Clone)");
                if (bucketDriver == null)
                {
                    bucketDriver = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "bucket seat driver(Clone)"
                    && g.transform.parent.gameObject.name == "Parts");
                    bucketPassanger = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "bucket seat passenger(Clone)"
                    && g.transform.parent.gameObject.name == "Parts");
                }

                bucketDriver?.AddComponent<SatsumaSeatsManager>();
                bucketPassanger?.AddComponent<SatsumaSeatsManager>();
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_BUCKET_SEAT_FIX_ERROR");
            }

            // Fix for mechanical wear of the car.
            try
            {
                transform.Find("CarSimulation/MechanicalWear").gameObject.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_MECHANICAL_WEAR_FIX_ERROR");
            }

            // Fix for not working handbrake after respawn.
            try
            {
                GameObject.Find("HandBrake").GetComponent<PlayMakerFSM>().enabled = true;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_HANDBRAKE_ERROR");
            }

            // Fixes handbrake lever position.
            try
            {
                transform.Find("MiscParts/HandBrake/handbrake(xxxxx)/handbrake lever").GetPlayMaker("Use").Fsm.RestartOnEnable = false;
            }
            catch
            {
                ExceptionManager.New(new System.Exception("handbrake lever fix"), false, "Unable to fix handbrake lever.");
            }

            // Battery terminal.
            // For some reason in the vanilla game, the negative battery terminal sometimes gets disabled, and the game doesn't allow to reenable it.
            try
            {
                transform.Find("Wiring").GetPlayMaker("Status").GetState("Disable battery wires").AddAction(new CustomBatteryDisable());
            }
            catch
            {
                ExceptionManager.New(new System.Exception("battery terminal fix"), false, "Unable to fix battery terminal wire.");
            }

            BoltsFix();

            // Fixes car body color resetting to default.
            try
            {
                transform.Find("Body/car body(xxxxx)").GetPlayMaker("Paint").Fsm.RestartOnEnable = false;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_BODY_PAINT_FIX_ERROR");
            }

            // Wiping Load for alternator belts, oil filters, spark plugs and batteries.
            GameObject[] parts = Resources.FindObjectsOfTypeAll<GameObject>()
                                .Where(obj => obj.name.ContainsAny("alternator belt(Clone)", "oil filter(Clone)", "spark plug(Clone)", "battery(Clone)"))
                                .ToArray();
            foreach (GameObject part in parts)
            {
                try
                {
                    if (part.transform.root.gameObject.name == "GAZ24(1420kg)") continue;

                    PlayMakerFSM useFsm = part.GetPlayMaker("Use");
                    FsmState state1 = useFsm.GetState("State 1");
                    List<FsmStateAction> emptyState1 = state1.Actions.ToList();
                    emptyState1.Insert(0, new CustomStop());
                    state1.Actions = emptyState1.ToArray();
                    state1.SaveActions();

                    useFsm.GetState("Load").Fsm.RestartOnEnable = false;
                }
                catch (System.Exception ex)
                {
                    ExceptionManager.New(ex, true, "SATSUMA_STATE1_WIPE_ERROR");
                }
            }

            // Fix for cd player disabling other vehicles radio.
            try
            {
                Resources.FindObjectsOfTypeAll<GameObject>()
                .First(g => g.name == "cd player(Clone)" && g.GetComponent<PlayMakerFSM>() != null && g.GetComponent<MeshRenderer>() != null)
                .transform.Find("ButtonsCD/RadioVolume").GetPlayMaker("Knob").Fsm.RestartOnEnable = false;
            }
            catch
            {
                ExceptionManager.New(new System.Exception("FSM RadioCD Fix"), false, "Unable to fix cd player(Clone).");
            }

            // Fix radio.
            try
            {
                Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "radio(Clone)" && g.transform.root.gameObject.name != "JAIL")
                .transform.Find("ButtonsRadio/RadioVolume").GetPlayMaker("Knob").Fsm.RestartOnEnable = false;
            }
            catch
            {
                ExceptionManager.New(new System.Exception("FSM Radio Fix"), false, "Unable to fix radio(Clone).");
            }

            // Fix for window grille paint resetting to the default.
            try
            {
                Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "window grille(Clone)" && g.GetPlayMaker("Paint") != null)
                    .GetPlayMaker("Paint").Fsm.RestartOnEnable = false;
            }
            catch
            {
                ExceptionManager.New(new System.Exception("window grille fix"), false, "Unable to fix window grille(Clone).");
            }

            // Apply hood fix.
            try
            {
                GameFixes.Instance.HoodFix(transform.Find("Body/pivot_hood"));
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_HOOD_FIX_ERROR");
            }
            // Rear bumper detachng fix.
            try
            {
                GameObject rearBumper = GameObject.Find("bumper rear(Clone)");
                GameObject databaseBumper = GameObject.Find("Database/DatabaseBody/Bumper_Rear");
                databaseBumper.SetActive(false);
                databaseBumper.SetActive(true);
                RearBumperBehaviour behaviour = rearBumper.AddComponent<RearBumperBehaviour>();
                rearBumper.GetPlayMaker("Removal").GetState("Remove part").AddAction(new CustomSatsumaBumperDetach(behaviour));
                transform.Find("Body/trigger_bumper_rear").GetPlayMaker("Assembly").GetState("Assemble 2").AddAction(new CustomSatsumaBumperAttach(behaviour));
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_REAR_BUMPER_DETACH_FIX_ERROR");
            }
            // Fix suspension adding a weight to the car on each car respawn.
            IEnumerable<GameObject> suspensionParts = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(g => g.name.ContainsAny("strut", "coil spring", "shock absorber")
                         && g.transform.root != null
                         && g.transform.root == this.gameObject.transform);
            foreach (GameObject part in suspensionParts)
            {
                try
                {
                    part.AddComponent<SatsumaPartMassManager>();
                }
                catch
                {
                    ExceptionManager.New(new System.Exception("SatsumaPartMassManager: Suspension"), false, "SatsumaPartsMassManager: Adding to suspension parts");
                }
            }
            // Appparently not only suspension fucks over the Satsuma...
            GameObject[] others = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(g => g.name.EqualsAny("grille gt(Clone)", "grille(Clone)", "subwoofer panel(Clone)",
                "seat rear(Clone)", "amplifier(Clone)", "rearlight(leftx)", "rearlight(right)", "racing radiator(xxxxx)", "radiator(xxxxx)",
                "radiator hose1(xxxxx)", "radiator hose3(xxxxx)", "marker light left(xxxxx)", "marker light right(xxxxx)", "antenna(leftx)",
                "antenna(right)", "dashboard(Clone)")).ToArray();
            foreach (GameObject other in others)
            {
                try
                {
                    other.AddComponent<SatsumaPartMassManager>();
                }
                catch
                {
                    ExceptionManager.New(new System.Exception("SatsumaPartMassManager: Others"), false, "SatsumaPartsMassManager: Others adding.");
                }
            }

            try
            {
                key = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "steering_column2" && g.transform.root == this.transform).transform.Find("Ignition/Keys/Key").gameObject;
            }
            catch
            {
                ExceptionManager.New(new System.Exception("SatsumKey: Cannot Locate Key"), false, "SatsumaPartsMassManager: Key Error");
            }

            try
            {
                cooldownTick = GameObject.Find("block(Clone)").transform.Find("CooldownTick").gameObject;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_COOLDOWN_TICK_ERROR");
            }

            // Fix for reg plates Z fighting.
            try
            {
                GameObject.Find("bootlid(Clone)").transform.Find("RegPlateRear").gameObject.GetComponent<Renderer>().material.renderQueue = 100;
                GameObject.Find("bumper front(Clone)").transform.Find("RegPlateFront").gameObject.GetComponent<Renderer>().material.renderQueue = 100;

                // Z-fighting of the Satsuma dashboard meters.
                if (!RulesManager.Book.SatsumaKeepRenderers)
                {
                    dashboardMaterials = new List<Material>
                    {
                        GameObject.Find("dashboard meters(Clone)").transform.Find("Gauges/Fuel/needle_small").gameObject.GetComponent<Renderer>().material,
                        GameObject.Find("dashboard meters(Clone)").transform.Find("Gauges/Temp/needle_small").gameObject.GetComponent<Renderer>().material,
                        GameObject.Find("dashboard meters(Clone)").transform.Find("Gauges/Speedometer/needle_large").gameObject.GetComponent<Renderer>().material,
                        GameObject.Find("rpm gauge(Clone)").transform.Find("Pivot/needle").gameObject.GetComponent<Renderer>().material,
                        GameObject.Find("clock gauge(Clone)").transform.Find("ClockCar/hour/needle_hour").gameObject.GetComponent<Renderer>().material,
                        GameObject.Find("clock gauge(Clone)").transform.Find("ClockCar/minute/needle_minute").gameObject.GetComponent<Renderer>().material
                    };

                    foreach (Material mat in dashboardMaterials)
                        mat.renderQueue = 100;


                    lightSelection = Resources.FindObjectsOfTypeAll<GameObject>()
                        .First(g => g.name == "dashboard meters(Clone)").transform.Find("Knobs/ButtonsDash/LightModes")
                        .gameObject.GetComponent<PlayMakerFSM>().FsmVariables.GetFsmInt("Selection");
                }
            }
            catch { }

            try
            {
                // Fixing the door hinges getting stuck.
                GameObject.Find("door left(Clone)").AddComponent<SatsumaHingeManager>();
                GameObject.Find("door right(Clone)").AddComponent<SatsumaHingeManager>();
                GameObject.Find("bootlid(Clone)").AddComponent<SatsumaHingeManager>();

                // Fixes doors resetting it's paint colour.
                GameObject.Find("door left(Clone)").GetPlayMaker("Paint").Fsm.RestartOnEnable = false;
                GameObject.Find("door right(Clone)").GetPlayMaker("Paint").Fsm.RestartOnEnable = false;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_DOOR_FIX_ERROR");
            }
            // Setup stuff that gets disabled using ToggleElements
            try
            {
                satsumaOnActionObjects = new List<SatsumaOnActionObjects>
                {
                    new SatsumaOnActionObjects(transform.Find("CarSimulation/MechanicalWear").gameObject, SatsumaEnableOn.OnEngine),
                    new SatsumaOnActionObjects(transform.Find("CarSimulation/Fixes").gameObject, SatsumaEnableOn.OnEngine),
                    new SatsumaOnActionObjects(transform.Find("CarSimulation/DynoDistance").gameObject, SatsumaEnableOn.OnEngine),
                    new SatsumaOnActionObjects(transform.Find("CarSimulation/RandomBolt").gameObject, SatsumaEnableOn.OnEngine),
                    new SatsumaOnActionObjects(transform.Find("RainScript").gameObject, SatsumaEnableOn.OnPlayerClose),
                    new SatsumaOnActionObjects(transform.Find("DriverHeadPivot").gameObject, SatsumaEnableOn.OnPlayerClose),
                    new SatsumaOnActionObjects(transform.Find("AirIntake").gameObject, SatsumaEnableOn.OnPlayerClose),
                    new SatsumaOnActionObjects(this.gameObject.GetPlayMaker("ButtonShifter"), SatsumaEnableOn.OnPlayerClose),
                    new SatsumaOnActionObjects(transform.Find("Chassis").gameObject, SatsumaEnableOn.OnPlayerFar)
                };
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_ON_ACTION_OBJECTS_ERROR");
            }
            // Replace on assemble sound playing with custom script.
            try
            {
                PlayMakerFSM blockBoltCheck = GameObject.Find("block(Clone)").GetPlayMaker("BoltCheck");
                FsmState boltsONState = blockBoltCheck.GetState("Bolts ON");
                FsmStateAction[] boltsONActions = boltsONState.Actions;
                boltsONActions[1] = new MasterAudioAssembleCustom();
                boltsONState.Actions = boltsONActions;
                boltsONState.SaveActions();
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_BOLTS_AUDIO_ERROR");
            }
            // Disable the "Fix Collider" state in FSM Setup, so it won't make items fall through the car.
            try
            {
                this.gameObject.GetPlayMaker("Setup").Fsm.RestartOnEnable = false;

                MeshCollider bootFloor = transform.Find("Colliders/collider_floor3").gameObject.GetComponent<MeshCollider>();
                bootFloor.isTrigger = false;
                bootFloor.enabled = true;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_COLLIDER_SETUP_ERROR");
            }
            // Fixes driver dying way too easily from small impacts (hopefully).
            try
            {
                transform.Find("DriverHeadPivot").GetComponent<Rigidbody>().collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_DRIVER_HEAD_PIVOT_ERROR");
            }

            // Masked Elements.
            // Those are objects that are disabled by default, so their bolts are not easily accessible.
            try
            {
                maskedElements = new Dictionary<GameObject, bool>();
                foreach (string obj in maskedObjectNames)
                {
                    GameObject gm = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == obj);
                    maskedElements.Add(gm, key.activeSelf);
                }
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_MASKED_ELEMENTS_ERROR");
            }

            // It is not used right now by the game, but it is safe to assume that one day it might be used.
            try
            {
                drivingAI = transform.Find("AI")?.GetPlayMaker("Driving");
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_AI_ERROR");
            }

            // radiator hose 3
            try
            {
                GameObject radiatorHosePart = Resources.FindObjectsOfTypeAll<GameObject>().First(g => g.name == "radiator hose3(xxxxx)");
                if (radiatorHosePart)
                {
                    radiatorHosePart.AddComponent<SatsumaRadiatorHoseFix>();
                }
            }
            catch
            {
                throw new System.Exception("Radiator hose 3 error");
            }

            // Windscreen fixes :)
            try
            {
                Transform windscreen = transform.Find("Body/Windshield");
                if (!windscreen)
                {
                    throw new System.Exception("Couldn't find Windscreen.");
                }
                SatsumaWindscreenFixer swf = windscreen.gameObject.AddComponent<SatsumaWindscreenFixer>();
                // And now we are hooking up the windscreen job in repairshop.
                Transform windshieldJob = GameObject.Find("REPAIRSHOP").transform.Find("Jobs/Windshield");
                if (!windshieldJob)
                {
                    throw new System.Exception("Couldn't find windshield job.");
                }

                if (!windshieldJob.parent.gameObject.activeSelf)
                {
                    windshieldJob.parent.gameObject.SetActive(true);
                    windshieldJob.parent.gameObject.SetActive(false);
                }

                PlayMakerFSM windshieldJobFSM = windshieldJob.gameObject.GetComponent<PlayMakerFSM>();
                List<FsmStateAction> wait1Actions = windshieldJobFSM.GetState("Wait1").Actions.ToList();
                wait1Actions.Insert(0, new WindscreenRepairJob(swf));
                windshieldJobFSM.GetState("Wait1").Actions = wait1Actions.ToArray();
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "WINDSCREEN REPAIR FIX ERROR");
            }

            // Fire extinguisher holder.
            try
            {
                GameObject extinguisherHolder = transform.Find("Interior/fire extinguisher holder(xxxxx)").gameObject;
                foreach (PlayMakerFSM fsm in extinguisherHolder.GetComponents<PlayMakerFSM>())
                {
                    fsm.Fsm.RestartOnEnable = false;
                }
            }
            catch
            {
                throw new System.Exception("Fire extinguisher holder error");
            }

            // Odometer resetting fix
            try
            {
                GameObject odometer = GameObject.Find("dashboard meters(Clone)/Gauges/Odometer");
                if (odometer.GetComponent<PlayMakerFSM>() == null)
                {
                    ModConsole.Debug("Can't reset Odometer FSM. Perhaps a mod has removed it?");
                }
                else
                {
                    odometer.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
                }
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, false, "SATS_ODOMETER_RESET_FIX_ERROR");
            }

            // New bolt enabling/disabling.
            try
            {
                GameObject subframeBolts = transform.Find("Chassis/sub frame(xxxxx)/Bolts")?.gameObject;
                GameObject triggerSubframe = transform.Find("Chassis/trigger_subframe")?.gameObject;
                if (triggerSubframe == null || subframeBolts == null)
                {
                    throw new MissingReferenceException("Could not find trigger_subframe or subframe.");
                }

                bool isSubframeTriggerActive = triggerSubframe.activeSelf;
                triggerSubframe.SetActive(true);

                PlayMakerFSM triggerSubframeFSM = triggerSubframe.GetPlayMaker("Assembly");
                if (triggerSubframeFSM == null)
                {
                    throw new MissingReferenceException("Could not find Assembly FSM.");
                }

                triggerSubframeFSM.GetState("Assemble").AddAction(new CustomToggleAllBoltsAction(subframeBolts, true));
                triggerSubframe.SetActive(isSubframeTriggerActive);
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, false, "SUBFRAME_BOLTS_ERROR");
            }

            // Fix exhaust muffler trigger.
            try
            {
                transform.Find("MiscParts/pivot_exhaust_muffler").gameObject.AddComponent<SatsumaExhaustMufflerFix>();
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, false, "EXHAUST_MUFFLER_FIX_ERROR");
            }

            try
            {
                GameObject block = GameObject.Find("block(Clone)");
                block.AddComponent<SatsumaHingeManager>();

            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_BLOCK_FIND_FAILURE");
            }

            try
            {
                transform.Find("DriverHeadPivot").gameObject.AddComponent<SatsumaHingeManager>();
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_DRIVER_HEAD_PIVOT_FAILURE");
            }

            if (MopSettings.Mode != PerformanceMode.Safe && this.IsActive)
            {
                this.ForceToggleUnityCar(false);
            }

            if (MopSettings.GenerateToggledItemsListDebug)
            {
                ToggledItemsListGenerator.CreateSatsumaList(disableableObjects);
            }

            rb.isKinematic = true;
        }

        /// <summary>
        /// Enable or disable car
        /// </summary>
        internal override void ToggleActive(bool enabled)
        {
            if (IsPassive) return;

            if (this.IsPlayerInThisCar())
            {
                enabled = true;
            }

            // Applying hood fix after first enabling.
            if (!AfterFirstEnable && enabled)
            {
                AfterFirstEnable = true;
            }

            // Force enable if AI is driving the car.
            if (drivingAI != null && drivingAI.enabled)
            {
                enabled = true;
            }

            // If the car is left in ParcFerme, the renderes may not re-enable, so just in case we force them to re-enable.
            if (IsSatsumaInParcFerme && !renderers[0].enabled)
                RenderersCulling(enabled);

            if (!RulesManager.Book.SatsumaKeepRenderers)
            {
                bool dashIllumination = false;
                if (IsKeyInserted())
                {
                    dashIllumination = lightSelection != null && lightSelection.Value > 0;
                }

                ToggleDashIllumination(dashIllumination);
            }

            // Don't run the code, if the value is the same
            if (gameObject == null || disableableObjects[0].gameObject.activeSelf == enabled)
            {
                return;
            }

            if (!enabled)
            {
                // MOP - REVIVAL 5.1: local, like everywhere else these are written and read (ForceRotation applies
                // them as local values). World values here moved a parented car to the wrong place.
                lastGoodRotation = transform.localRotation;
                lastGoodPosition = transform.localPosition;
            }

            if (!GameFixes.Instance.HoodFixDone)
                enabled = true;

            for (int i = 0; i < disableableObjects.Length; i++)
            {
                if (disableableObjects[i] == null)
                    continue;

                bool isElementEnabled = IsSatsumaInParcFerme || enabled;
                SetActiveGuarded(disableableObjects[i].gameObject, isElementEnabled, "Satsuma");
            }

            RenderersCulling(enabled);
        }

        /// <summary>
        /// MOP - REVIVAL: true when the "satsuma_ignore" rule is active. Evaluated from the base constructor too,
        /// so it only depends on static state.
        /// </summary>
        public override bool IsPassive => RulesManager.Book.SatsumaExclude;

        public override void ToggleUnityCar(bool enabled)
        {
            if (IsPassive) return;

            // Satsuma in inspection zone and mod wants to disable it?
            // Set enabled to true, so it won't be disabled.
            if (!enabled && IsSatsumaInInspectionArea || IsRopeHooked() || !IsOnGround())
                enabled = true;

            base.ToggleUnityCar(enabled);

            // We're completly freezing Satsuma, so it won't flip (hopefully...).
            SaveCarPosition(enabled);
        }

        public override void ForceToggleUnityCar(bool enabled)
        {
            if (IsPassive) return;
            base.ForceToggleUnityCar(enabled);

            // We're completly freezing Satsuma, so it won't flip (hopefully...).
            SaveCarPosition(enabled);
        }

        private void SaveCarPosition(bool enabled)
        {
            if (!enabled && !lastGoodRotationSaved)
            {
                lastGoodRotationSaved = true;
                lastGoodRotation = transform.localRotation;
                lastGoodPosition = transform.localPosition;
            }

            if (enabled)
            {
                lastGoodRotationSaved = false;
            }

            rb.constraints = enabled ? RigidbodyConstraints.None : RigidbodyConstraints.FreezePosition;
        }
        
        public override bool IsOnGround()
        {
            // Wheel not attached? Check for drivetrain torque and rigidbody velocity.
            if (!wheel.enabled)
            {
                // Car body is moving? Reset the time where we assume that the car body is moving.
                if (rb.velocity.magnitude > VelocityCheckDeadzone)
                {
                    framesLeftToSkipForGroundCheckDueToFlight = FramesToSkip;
                }

                // If we're still asuming the car body is moving, return FALSE, and decrease the counter by 1.
                if (framesLeftToSkipForGroundCheckDueToFlight > 0)
                {
                    framesLeftToSkipForGroundCheckDueToFlight--;
                    return false;
                }

                return drivetrain.torque == 0 && rb.velocity.sqrMagnitude <= VelocityCheckDeadzone;
            }

            return wheel.onGroundDown;
        }

        /// <summary>
        /// Get list of disableable childs.
        /// It looks for objects that contian the name from the whiteList
        /// </summary>
        /// <returns></returns>
        internal Transform[] GetDisableableChilds()
        {
            Transform[] childs = gameObject.GetComponentsInChildren<Transform>(true).Where(t => t.gameObject.name.ContainsAny(whiteList)).ToArray();

            if (RulesManager.Book.HasObjectRules)
            {
                childs = childs.Where(t => !RulesManager.Book.HasObjectRule(t.gameObject.name)).ToArray();
            }

            return childs;
        }

        /// <summary>
        /// Returns true, if any hood is attached to the car.
        /// </summary>
        /// <returns></returns>
        private bool IsHoodAttached()
        {
            return pivotHood.childCount > 0;
        }

        /// <summary>
        /// Toggles all renderers of the Satsuma,
        /// </summary>
        /// <param name="enabled">If set to true, the renderers are enabled.</param>
        private void RenderersCulling(bool enabled)
        {
            // MOP - REVIVAL 5.1: MOP is about to set every renderer of the car: hand them back from the merger first.
            Next.VehicleMeshMerge.UnmergeSatsuma("renderer culling");

            if (RulesManager.Book.SatsumaKeepRenderers) enabled = true;
            renderersShownByMop = enabled;

            for (int i = 0; i < renderers.Count; i++)
            {
                try
                {
                    if (renderers[i] == null)
                        continue;

                    // Skip renderer if its root is not Satsuma.
                    if (renderers[i].transform.root.gameObject != this.gameObject)
                        continue;

                    renderers[i].enabled = enabled;
                }
                catch (System.Exception ex)
                {
                    ExceptionManager.New(ex, true, "SATSUMA_RENDERER_TOGGLE_ISSUE");
                }
            }
        }

        /// <summary>
        /// We're forcing to keep his rotation.
        /// For some reason it keeps shaking otherwise.
        /// </summary>
        public void ForceRotation()
        {
            if (IsPassive || MopSettings.Mode == PerformanceMode.Safe || carDynamics == null || IsPlayerInThisCar() || IsRopeHooked()) return;

            if (!hasBeenMovedByFleetari && !carDynamics.enabled)
            {
                // MOP - REVIVAL 5.1 (fix, original MOP bug): only small drift is undone. When something else moved
                // the frozen car on purpose (the game, a teleport or garage mod, a tow), MOP put it straight back
                // to where it was frozen - in the same frame (TestBot b3-flip: lifted 0.5 m, set down at the rally
                // parc ferme 1.8 km away: back at home at once). A move of more than 0.25 m or 5 degrees is now
                // taken as the car's new resting place.
                if ((transform.localPosition - lastGoodPosition).sqrMagnitude > AdoptMoveDistance * AdoptMoveDistance ||
                    Quaternion.Angle(transform.localRotation, lastGoodRotation) > AdoptMoveAngle)
                {
                    lastGoodPosition = transform.localPosition;
                    lastGoodRotation = transform.localRotation;
                    return;
                }

                transform.localRotation = lastGoodRotation;
                transform.localPosition = lastGoodPosition;
            }
        }

        const float AdoptMoveDistance = 0.25f;
        const float AdoptMoveAngle = 5f;

        /// <summary>
        /// MOP - REVIVAL 5.1: true while MOP draws the car (the last RenderersCulling call enabled them). Tracked
        /// as MOP's own decision, not read from a renderer: the mesh merger switches the originals off.
        /// </summary>
        internal bool AreRenderersShown => renderersShownByMop;
        bool renderersShownByMop = true;

        /// <summary>
        /// MOP - REVIVAL 5.1: conditions under which the car may be drawn as merged meshes. The merger follows
        /// the body and checks every part relative to it, so physics may be on (player within a few metres).
        /// Not while the player sits in it: measured from the driver's seat (TestBot, 1 s A/B blocks), merging
        /// saves nothing there and the per-frame check makes it 0.2 ms slower. Inspection, the rally parc fermé,
        /// Fleetari's towing and the AI driver are left alone too (rare, and other code moves the car there).
        /// </summary>
        internal bool IsMergeable()
        {
            if (IsPassive || RulesManager.Book.SatsumaKeepRenderers) return false;
            if (!gameObject.activeInHierarchy) return false;
            if (IsMoving() || IsPlayerInThisCar()) return false;
            if (IsSatsumaInInspectionArea || IsSatsumaInParcFerme || hasBeenMovedByFleetari) return false;
            if (drivingAI != null && drivingAI.enabled) return false;
            return AreRenderersShown;
        }

        /// <summary>
        /// Returns true, if the key object is active.
        /// </summary>
        /// <returns></returns>
        public bool IsKeyInserted()
        {
            if (key == null)
            {
                return true;
            }

            return key.activeSelf;
        }

        /// <summary>
        /// Toggles some of the elements of that car, depneding on the of player to Satsuma.
        /// </summary>
        /// <param name="distance"></param>
        public void ToggleElements(float distance)
        {
            if (IsPassive) return;

            try
            {
                bool onEngine = distance < 2;
                bool onClose = distance <= 10 * MopSettings.ActiveDistanceMultiplicationValue;
                bool onFar = distance <= 20 * MopSettings.ActiveDistanceMultiplicationValue;

                if (Toggle == IgnoreToggle || IsKeyInserted() || IsSatsumaInInspectionArea || IsMoving() || (drivingAI != null && drivingAI.enabled))
                {
                    onEngine = true;
                    onClose = true;
                    onFar = true;
                }

                for (int i = 0; i < satsumaOnActionObjects.Count; i++)
                {
                    SatsumaOnActionObjects sao = satsumaOnActionObjects[i];
                    if (sao == null)
                    {
                        continue;
                    }

                    bool on = sao.EnableOn == SatsumaEnableOn.OnEngine ? onEngine
                            : sao.EnableOn == SatsumaEnableOn.OnPlayerClose ? onClose : onFar;

                    PlayMakerFSM fsm = sao.FSM;
                    if (fsm != null && fsm.enabled != on)
                    {
                        // MOP - REVIVAL 5.1: through the restart guard, like every other object MOP switches.
                        if (!on) RestartGuard.BeforeDisableComponent(fsm, "Satsuma");
                        fsm.enabled = on;
                        if (on) RestartGuard.AfterEnableComponent(fsm);
                    }

                    GameObject gm = sao.GameObject;
                    if (gm != null)
                    {
                        SetActiveGuarded(gm, on, "Satsuma");
                    }
                }

                if (onEngine)
                {
                    // This script fixes the issue with bolts staying unbolted, with parts internally being fully bolted.
                    if (maskedFixStages < 2)
                    {
                        for (int i = 0; i < maskedElements.Count; i++)
                        {
                            GameObject gm = maskedElements.ElementAt(i).Key;
                            if (gm == null)
                            {
                                continue;
                            }

                            switch (maskedFixStages)
                            {
                                case 0:
                                    gm.SetActive(true);
                                    break;
                                case 1:
                                    gm.SetActive(maskedElements.ElementAt(i).Value);
                                    break;
                            }
                        }
                        maskedFixStages++;
                    }
                }
                else
                {
                    cooldownTick.SetActive(false);
                }

                if (onClose)
                {
                    if (!renderersOnCloseEnabled)
                    {
                        renderersOnCloseEnabled = true;
                        RenderersCulling(true);
                    }
                }
                else
                {
                    renderersOnCloseEnabled = false;
                }

                if (onFar)
                {
                    hasBeenMovedByFleetari = false;
                    UnglueAll();
                }
            }
            catch (System.Exception ex)
            {
                ExceptionManager.New(ex, true, "SATSUMA_TOGGLE_ELEMENTS_ERROR");
            }
        }

        /// <summary>
        /// MOP - REVIVAL 5.1 (fix): the Satsuma switched its parts (Body, MiscParts, Dashboard, Interior, Chassis,
        /// CarSimulation...) with a plain SetActive, so the restart guard never saw them: an FSM in there that reads
        /// the save when it starts read it again every time MOP switched the car back on. The FSM audit lists e.g. the
        /// dashboard light mode switch, the radio tuner and the windshield (its tax sticker and damage). Only on a
        /// change of state, so the per-tick calls of ToggleElements cost a comparison.
        /// </summary>
        static void SetActiveGuarded(GameObject go, bool active, string owner)
        {
            if (go.activeSelf == active) return;
            if (!active) RestartGuard.BeforeDisable(go, owner);
            go.SetActive(active);
            if (active) RestartGuard.AfterEnable(go);
        }

        /// <summary>
        /// Toggles Satsuma's dashboard illumination.
        /// </summary>
        /// <param name="enabled"></param>
        private void ToggleDashIllumination(bool enabled)
        {
            if ((dashboardMaterials == null) || (dashboardMaterials[0].GetFloat("_Intensity") == 0 && !enabled) || (dashboardMaterials[0].GetFloat("_Intensity") == 0.2f && enabled))
            {
                return;
            }

            for (int i = 0; i < dashboardMaterials.Count; i++)
            {
                dashboardMaterials[i].SetFloat("_Intensity", enabled ? 0.2f : 0);
            }
        }

        public GameObject GetCarBody()
        {
            return transform.Find("Body/car body(xxxxx)").gameObject;
        }

        public void FleetariIsMovingCar()
        {
            hasBeenMovedByFleetari = true;
        }

        internal void AddPart(SatsumaBoltsAntiReload i)
        {
            satsumaBoltsAntiReloads.Add(i);
            // 5.1: the object's name, to tell in the log which entries are gone when the parts are unglued.
            string name;
            try { name = i != null && i.gameObject != null ? i.gameObject.name : "?"; } catch { name = "?"; }
            satsumaBoltsNames.Add(name);
        }

        readonly List<string> satsumaBoltsNames = new List<string>();
        // Entries BoltsFix added itself. Any entry after them was registered by a copy of a part that already had the
        // component (Instantiate runs the constructor again, before the copy has a name).
        int boltsAddedByMopr;

        /// <summary>
        /// Original MOP: the parts on the car are "glued" while it loads (their joints can't break) and the bolt FSMs
        /// don't restart when MOP switches them back on. The first time the player is far from the car, the joints get
        /// their real strength back. Entries whose component no longer exists by then (the game destroyed the object:
        /// its DUMMY_ placeholders, copies of parts) are simply dropped - harmless, so only in the log.
        /// </summary>
        private void UnglueAll()
        {
            if (partsUnglued) return;
            partsUnglued = true;

            List<int> gone = new List<int>();
            for (int i = 0; i < satsumaBoltsAntiReloads.Count; ++i)
            {
                if (satsumaBoltsAntiReloads[i] == null)
                {
                    gone.Add(i);
                    continue;
                }
                satsumaBoltsAntiReloads[i].Unglue();
            }

            if (gone.Count > 0)
            {
                List<string> names = new List<string>();
                int copies = 0;
                foreach (int i in gone)
                {
                    if (i >= boltsAddedByMopr) copies++;
                    else names.Add(i < satsumaBoltsNames.Count ? satsumaBoltsNames[i] : "?");
                }
                ModConsole.Debug($"Unglued the Satsuma parts; {gone.Count} of {satsumaBoltsAntiReloads.Count} entries were already gone " +
                                 $"(destroyed by the game): {(names.Count > 0 ? string.Join(", ", names.ToArray()) : "-")}" +
                                 (copies > 0 ? $"; {copies} copies of parts made after the load" : ""));
            }
            for (int k = gone.Count - 1; k >= 0; k--)
            {
                satsumaBoltsAntiReloads.RemoveAt(gone[k]);
                if (gone[k] < satsumaBoltsNames.Count) satsumaBoltsNames.RemoveAt(gone[k]);
            }
        }

        /// <summary>
        /// Executed when game is being saved, makes all items permamently attached to the car.
        /// </summary>
        public void OnSaveGlueAll()
        {
            if (IsPassive) return;

            for (int i = 0; i < satsumaBoltsAntiReloads.Count; ++i)
            {
                if (satsumaBoltsAntiReloads[i] == null)
                {
                    continue;
                }

                satsumaBoltsAntiReloads[i].enabled = true;
                satsumaBoltsAntiReloads[i].Glue();

                PlayMakerFSM removalFSM = satsumaBoltsAntiReloads[i].gameObject.GetPlayMaker("Removal");
                if (removalFSM)
                {
                    Object.Destroy(removalFSM);
                }
            }

            ModConsole.Debug("Glued all Satsuma parts.");
        }

        private List<Renderer> GetRenderersToDisable()
        {
            List<Renderer> renderers = gameObject.transform.GetComponentsInChildren<Renderer>(true).Where(r => !r.gameObject.name.ContainsAny(ignoredRendererNames)).ToList();

            if (RulesManager.Book.HasObjectRules)
            {
                renderers = renderers.Where(r => !RulesManager.Book.HasObjectRule(r.gameObject.name)).ToList();
            }

            List<Renderer> toReturn = new List<Renderer>();
            foreach (Renderer r in renderers)
            {
                try
                {
                    MeshFilter f = r.GetComponent<MeshFilter>();
                    // Check if object is a primitive cube.
                    // If not, add it toReturn list.
                    if (!(f && f.sharedMesh.name == "Cube" && f.mesh.subMeshCount == 1 && f.mesh.vertexCount == 24))
                    {
                        toReturn.Add(r);
                    }
                }
                catch { }
            }

            return toReturn;
        }

        protected override void ApplyHingeManager()
        {
            return;
        }

        // This class tries to repair the bolts resetting.
        private void BoltsFix()
        {
            // Get all bolts.
            satsumaBoltsAntiReloads = new List<SatsumaBoltsAntiReload>();
            satsumaBoltsNames.Clear();
            GameObject[] bolts = Resources.FindObjectsOfTypeAll<GameObject>().Where(obj => obj.name == "BoltPM").ToArray();
            foreach (GameObject bolt in bolts)
            {
                Transform parent = bolt.transform.parent;

                // Elements with that name have their BoltCheck parents as a grandparent (water_pump and Hooks are exception)
                if (parent.name.EqualsAny("Bolts", "_Motor", "water_pump_pulley_mesh", "Hooks", "hooks"))
                {
                    // Great-great grandparents for these ones.
                    if (parent.parent.gameObject.name.EqualsAny("water_pump_pulley_mesh", "Hooks", "hooks"))
                    {
                        parent = parent.parent.parent;
                    }
                    else
                    {
                        parent = parent.parent;
                    }
                }
                // Great-great grandparents for these too...
                else if (parent.name.ContainsAny("Masked", "ValveAdjust"))
                {
                    parent = parent.parent.parent;
                }
                // Sibling of bolt if the parent is IK_wishbone.
                else if (parent.name.StartsWith("IK_wishbone_"))
                {
                    string suffix = parent.gameObject.name.Replace("IK_wishbone_", "");
                    parent = parent.Find($"wishbone {suffix}(xxxxx)");
                }

                // Skip those if the assigned parent is one of these.
                if (parent.name.ContainsAny("bolts_shock", "shock_bottom", "halfshaft_", "pivot_steering_arm_", "OFFSET", "pivot_shock_"))
                {
                    continue;
                }

                // Skip if the parent is Pivot and the grandparent name starts with "alternator".
                if (parent.name == "Pivot" && parent.parent.gameObject.name.StartsWith("alternator"))
                {
                    continue;
                }

                if (parent.gameObject.name == "hood(Clone)")
                    continue;

                if (parent.gameObject.GetComponent<SatsumaBoltsAntiReload>() == null)
                {
                    SatsumaBoltsAntiReload s = parent.gameObject.AddComponent<SatsumaBoltsAntiReload>();
                }
            }

            // Halfshafts hook.
            GameObject[] halfshafts = Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "halfshaft(xxxxx)").ToArray();
            foreach (GameObject halfshaft in halfshafts)
            {
                if (halfshaft.GetComponent<SatsumaBoltsAntiReload>() == null)
                    halfshaft.AddComponent<SatsumaBoltsAntiReload>();
            }

            // Engine Block.
            GameObject.Find("block(Clone)").AddComponent<SatsumaBoltsAntiReload>();

            // Steering rods.
            transform.Find("Chassis/steering rod fr(xxxxx)").gameObject.AddComponent<SatsumaBoltsAntiReload>();
            transform.Find("Chassis/steering rod fl(xxxxx)").gameObject.AddComponent<SatsumaBoltsAntiReload>();

            // Fuel line.
            transform.Find("MiscParts/fuel line(xxxxx)").gameObject.AddComponent<SatsumaBoltsAntiReload>();

            boltsAddedByMopr = satsumaBoltsAntiReloads.Count;
            ModConsole.Debug($"Found {satsumaBoltsAntiReloads.Count} bolts.");
            // If there's less bolts found than the value, warn user.
            if (satsumaBoltsAntiReloads.Count < MinimumBolts)
            {
                ModConsole.Warn("con.bolts_few", satsumaBoltsAntiReloads.Count, MinimumBolts);
            }
        }

        protected override void LoadColliders()
        {
            return;
        }
    }
}
