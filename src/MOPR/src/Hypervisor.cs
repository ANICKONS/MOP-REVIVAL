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
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using MOP.Managers;
using MOP.Common;
using MOP.Common.Enumerations;
using MOP.FSM;
using MOP.FSM.Actions;
using MOP.Items;
using MOP.Vehicles;
using MOP.Vehicles.Cases;
using MOP.Helpers;
using MOP.Rules;
using MOP.WorldObjects;
using MOP.Next;
using MOP.Localization;
using HutongGames.PlayMaker;

namespace MOP
{
    /// <summary>
    /// Controls all objects in the game world.
    /// </summary>
    internal class Hypervisor : MonoBehaviour
    {
        static Hypervisor instance;

        /// <summary>The game's own cars under TRAFFIC/VehiclesHighway (two LAMOREs and three VICTROs share a name).</summary>
        static readonly string[] VanillaHighwayCars = { "TRUCK", "SVOBODA", "LAMORE", "VICTRO", "MENACE", "FITTAN", "POLSA" };
        public static Hypervisor Instance => instance;

        Transform player;

        // Managers
        PlaceManager placeManager;
        WorldObjectManager worldObjectManager;
        private bool isPlayerAtYard;
        private bool inSectorMode;

        #region Loading Variables
        readonly CharacterController playerController;
        bool itemInitializationDelayDone;
        bool isFinishedCheckingSatsuma;

        const int WaitForPhysicsToSettleTime = 2; // How many seconds to wait for the physics of MSC to "settle".
        const int WaitForSatsumaCheckTime = 10; // How many seconds will the MOP wait for SatsumaIsLoaded check to finish.
        const int LoadScreenWorkaroundTime = 20; // How long the game will wait for the loading to finish (if it fails).

        readonly LoadScreen loadScreen;
        #endregion

        readonly Queue<ItemBehaviour> itemsToRemove = new Queue<ItemBehaviour>();
        readonly Queue<ItemBehaviour> itemsToEnable = new Queue<ItemBehaviour>();
        
        private GameObject computerSystem;
        private float distance;
        private float toggleDistance;

        readonly string[] trafficVehicleRoots = { "NPC_CARS", "TRAFFIC", "RALLY" };
        public string[] TrafficVehicleRoots => trafficVehicleRoots;
        GameObject traffic, trafficHighway, trafficDirt;

        private GameObject dumbObjectsParent;

        private Hypervisor()
        {
            instance = this;

            // Rules are read again at every load: a file edited or a mod switched on or off in the menu applies now.
            RulesManager.Load(true);

            MopSettings.LoadedOnce = true;

            // Load loadscreen.
            // MOP - REVIVAL 5.1: IMGUI screen without the asset bundle (see LoadScreen.cs). The workaround
            // coroutine starts even if the screen fails, because it also releases the frozen player.
            try
            {
                loadScreen = LoadScreen.Show();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "LOAD_SCREEN_ERROR");
            }
            loadScreenWorkaround = InfiniteLoadscreenWorkaround();
            StartCoroutine(loadScreenWorkaround);

            // Disable player controller and pretend like he's in the main menu.
            playerController = GameObject.Find("PLAYER").GetComponent<CharacterController>();
            playerController.enabled = false;
            FsmManager.PlayerInMenu = true;

            ExceptionManager.SessionTimeStart = DateTime.Now;

#if DEBUG
            ToggleDebugMode();
#endif

            // Start the delayed initialization routine
            StartCoroutine(DelayedInitializaitonRoutine());
        }

        #region MOP Initialization
        IEnumerator DelayedInitializaitonRoutine()
        {
            // MOP 3.10 started from SecondPassOnLoad, i.e. after every mod's phase 3. MOP - REVIVAL starts
            // from PostLoad, so it waits until MSCLoader is surely done: it yields at most one frame per
            // remaining mod in phase 3. Then the original 200 frame settle delay applies.
            int frames = Mathf.Max(200, LoaderFramesToFinish());
            SetLoadPhase(LoadPhase.WaitingForMods);
            for (int i = 0; i < frames; i++)
            {
                SetLoadProgress((float)i / frames);
                yield return null;
            }

            // Check if the Satsuma has been loaded completely by the game.
            // If not, restart the scene at least once.
            SetLoadPhase(LoadPhase.CheckingSatsuma);
            CheckIfSatsumaIsLoaded();

            // Wait either for the isFinishedCheckingSatsuma to be set to true,
            // or wait WaitForSastumaCheckTime seconds to break.
            for (int i = 0; i < WaitForSatsumaCheckTime; ++i)
            {
                if (isFinishedCheckingSatsuma)
                    break;

                SetLoadProgress((float)i / WaitForSatsumaCheckTime);
                yield return new WaitForSeconds(1);
            }

            // MOP - REVIVAL: Initialize() blocks for a moment; draw its caption first.
            SetLoadPhase(LoadPhase.Optimizing);
            yield return null;

            Initialize();
            SetLoadPhase(LoadPhase.FinalTouches);
        }

        // MOP - REVIVAL: the loading screen is optional; MOP never depends on it.
        void SetLoadPhase(LoadPhase phase)
        {
            if (loadScreen != null) loadScreen.SetPhase(phase);
        }

        void SetLoadProgress(float fraction)
        {
            if (loadScreen != null) loadScreen.SetProgress(fraction);
        }

        /// <summary>
        /// Performs a check that makes sure that Satsuma is loaded by the game.
        /// </summary>
        void CheckIfSatsumaIsLoaded()
        {
            bool satsumaIsLoaded = false;

            // MOP - REVIVAL: with the rule "satsuma": "exclude" the Satsuma belongs to another mod: never inspect or reload for it.
            if (RulesManager.Book.SatsumaExclude)
            {
                isFinishedCheckingSatsuma = true;
                return;
            }

            if (SaveManager.IsCarAssembledWithMSCEditor())
            {
                MSCLoader.ModUI.ShowMessage(Localization.Loc.T("msg.msceditor"), Localization.Loc.T("msg.title.warning"));
            }
            else
            {
                try
                {
                    satsumaIsLoaded = SaveManager.IsSatsumaLoadedCompletely();
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, true, "SATSUMA_IS_LOADED_ERROR");
                }

                // For testing purposes.
                if (MopSettings.ForceLoadRestart)
                {
                    satsumaIsLoaded = false;
                }

                if (!satsumaIsLoaded)
                {
                    if (MopSettings.GameFixStatus == GameFixStatus.None)
                    {
                        SetLoadPhase(LoadPhase.Restarting);
                        StartCoroutine(GameRestartCoroutine());
                        return;
                    }

                    // Fix already has been attempted? Show error!
                    MSCLoader.ModUI.ShowMessage(Localization.Loc.T("msg.satsuma_not_loaded"), Localization.Loc.T("msg.title"));
                }
            }

            isFinishedCheckingSatsuma = true;
        }

        /// <summary>
        /// This routine restarts the game. Called by CheckIfSatsumaIsLoaded, after it detects that the Satsuma has not been loaded fully.
        /// </summary>
        /// <returns></returns>
        IEnumerator GameRestartCoroutine()
        {
            // We must wait for MSCLoader to finish loading, otherwise it will break.
            // MOP - REVIVAL: MSCLoader 1.4 renamed the loading canvas ("...(Clone)"), so the original lookup
            // threw and the restart never happened. Wait by frames instead (MSCLoader yields one frame per mod),
            // plus the old dialog check when it can be found.
            ModConsole.Debug("Waiting for the MSCLoader to finish to load...");
            int frames = LoaderFramesToFinish();
            for (int i = 0; i < frames; i++)
                yield return null;

            GameObject loadingCanvas = GameObject.Find("MSCLoader Canvas loading") ?? GameObject.Find("MSCLoader Canvas loading(Clone)");
            Transform loadingDialog = loadingCanvas != null ? loadingCanvas.transform.Find("MSCLoader loading dialog") : null;
            if (loadingDialog != null)
            {
                float timeout = Time.realtimeSinceStartup + 30f;
                while (loadingDialog.gameObject.activeSelf && Time.realtimeSinceStartup < timeout)
                    yield return null;
            }
            yield return null;

            MopSettings.GameFixStatus = GameFixStatus.DoFix;
            ModConsole.Info("con.restart_scene");
            Application.LoadLevel(1);
        }

        /// <summary>
        /// Upper bound of frames MSCLoader still needs to finish loading mods after MOP's PostLoad:
        /// it yields one frame per mod in phase 3 (PostLoad + legacy SecondPassOnLoad) plus a few more.
        /// </summary>
        static int LoaderFramesToFinish()
        {
            int mods = MSCLoader.ModLoader.LoadedMods != null ? MSCLoader.ModLoader.LoadedMods.Count : 0;
            return mods * 2 + 10;
        }

        /// <summary>
        /// MOP - REVIVAL 5.1 (fix, original MOP bug): adds one world object in its own try. MOP 3.10 added each list
        /// in a single try, so the first failure skipped every object after it (WORLD_OBJECTS_1/2_INITIALIZAITON_FAIL):
        /// an object a rule excludes came back as null and setting its MinimumToggleDistance threw, and so did a
        /// keepActive object without a renderer. With BetterMSC or LoppeHouse about 20 world objects (PERAJARVI,
        /// DANCEHALL, AIRPORT, RAILROAD...) were then never managed.
        /// </summary>
        GenericObject AddWorldObject(string name, DisableOn disableOn, int distance = 200, int minimumToggleDistance = 0, bool silent = false)
        {
            try
            {
                GenericObject obj = worldObjectManager.Add(name, disableOn, distance, silent: silent);
                if (obj != null && minimumToggleDistance > 0)
                    obj.MinimumToggleDistance = minimumToggleDistance;
                return obj;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "WORLD_OBJECT_ERROR | " + name);
                return null;
            }
        }

        void Initialize()
        {
            ModConsole.Info("con.loading");
            RestartGuard.Reset();
            ExceptionManager.ResetSession();

            dumbObjectsParent = new GameObject("MOP_DumbObjects");
            dumbObjectsParent.transform.SetParent(this.transform);

            // Initialize the worldObjectManager list
            worldObjectManager = new WorldObjectManager();

            // Looking for player and yard
            player = GameObject.Find("PLAYER").transform;

            // Add GameFixes MonoBehaviour.
            try
            {
                gameObject.AddComponent<GameFixes>();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, $"GAME_FIXES_INITIALIZAITON | {ex}");
            }

            // Loading vehicles
            VehicleManager.Instance.Initialize();

            // World Objects
            // MOP - REVIVAL 5.1: one try per object (AddWorldObject), see there.
            AddWorldObject("CABIN", DisableOn.Distance, 200, 300);
            AddWorldObject("COTTAGE", DisableOn.Distance, 400, 400);
            AddWorldObject("DANCEHALL", DisableOn.Distance, 500);
            AddWorldObject("PERAJARVI", DisableOn.Distance | DisableOn.IgnoreInQualityMode, 600, 600);
            AddWorldObject("SOCCER", DisableOn.Distance);
            AddWorldObject("WATERFACILITY", DisableOn.Distance, 300, 300);
            AddWorldObject("StrawberryField", DisableOn.Distance, 400);
            AddWorldObject("MAP/Buildings/DINGONBIISI", DisableOn.Distance | DisableOn.IgnoreInBalancedAndAbove, 400);
            AddWorldObject("RALLY/PartsSalesman", DisableOn.Distance, 400);
            AddWorldObject("LakeSmallBottom1", DisableOn.Distance, 500);
            AddWorldObject("machine", DisableOn.Distance, 200, silent: true);
            try
            {
                SkidmarkObject skidmark = new SkidmarkObject(GameObject.Find("Skidmarks"), 0);
                worldObjectManager.Add(skidmark);
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "WORLD_OBJECT_ERROR | Skidmarks");
            }
            ModConsole.Debug("World objects (1) loaded");

            // Initialize places.
            placeManager = new PlaceManager();

            // Fixes
            GameFixes.Instance.MainFixes();

            //Things that should be enabled when out of proximity of the house
            AddWorldObject("NPC_CARS", DisableOn.PlayerInHome);
            AddWorldObject("TRAFFIC", DisableOn.PlayerInHome);
            AddWorldObject("VehiclesHighway", DisableOn.PlayerInHome | DisableOn.DoNotEnableWhenLeavingHome);
            AddWorldObject("VehiclesDirtRoad", DisableOn.PlayerInHome);
            AddWorldObject("TRAIN", DisableOn.PlayerInHome | DisableOn.IgnoreInQualityMode);
            AddWorldObject("Buildings", DisableOn.PlayerInHome);
            AddWorldObject("TrafficSigns", DisableOn.PlayerInHome);
            AddWorldObject("StreetLights", DisableOn.PlayerInHome);
            AddWorldObject("HUMANS", DisableOn.PlayerInHome);
            AddWorldObject("TRACKFIELD", DisableOn.PlayerInHome);
            AddWorldObject("SkijumpHill", DisableOn.PlayerInHome | DisableOn.IgnoreInQualityMode);
            AddWorldObject("Factory", DisableOn.PlayerInHome);
            AddWorldObject("WHEAT", DisableOn.PlayerInHome);
            AddWorldObject("RAILROAD", DisableOn.PlayerInHome);
            AddWorldObject("AIRPORT", DisableOn.PlayerInHome);
            AddWorldObject("RAILROAD_TUNNEL", DisableOn.PlayerInHome);
            AddWorldObject("PierDancehall", DisableOn.PlayerInHome);
            AddWorldObject("PierRiver", DisableOn.PlayerInHome);
            AddWorldObject("PierStore", DisableOn.PlayerInHome);
            AddWorldObject("BRIDGE_dirt", DisableOn.PlayerInHome);
            AddWorldObject("BRIDGE_highway", DisableOn.PlayerInHome);
            AddWorldObject("BirdTower", DisableOn.Distance, 400);
            AddWorldObject("RYKIPOHJA", DisableOn.PlayerInHome);
            AddWorldObject("COMPUTER", DisableOn.PlayerAwayFromHome, silent: true);
            ModConsole.Debug("World objects (2) loaded");

            // Adding area check if Satsuma is in the inspection's area
            try
            {
                SatsumaInArea inspectionArea = GameObject.Find("INSPECTION").AddComponent<SatsumaInArea>();
                inspectionArea.Initialize(new Vector3(20, 20, 20));
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_AREA_CHECK_INSPECTION_FAIL");
            }

            // Check for when Satsuma is on the lifter
            try
            {
                SatsumaInArea lifterArea = GameObject.Find("REPAIRSHOP/Lifter/Platform").AddComponent<SatsumaInArea>();
                lifterArea.Initialize(new Vector3(5, 5, 5));
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "SATSUMA_AREA_CHECK_REPAIRSHOP_FAIL");
            }

            // Area for the parc ferme.
            try
            {
                GameObject parcFermeTrigger = new GameObject("MOP_ParcFermeTrigger");
                parcFermeTrigger.transform.parent = GameObject.Find("RALLY").transform.Find("Scenery");
                parcFermeTrigger.transform.position = new Vector3(-1383f, 3f, 1260f);
                SatsumaInArea parcFerme = parcFermeTrigger.AddComponent<SatsumaInArea>();
                parcFerme.Initialize(new Vector3(41, 12, 35));
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "PARC_FERME_TRIGGER_FAIL");
            }

            ModConsole.Debug("Satsuma triggers loaded");

            // Jokke's furnitures.
            // Only renderers are going to be toggled.
            try
            {
                if (GameObject.Find("tv(Clo01)"))
                {
                    string[] furnitures = { "tv(Clo01)", "chair(Clo02)", "chair(Clo05)", "bench(Clo01)",
                                            "bench(Clo02)", "table(Clo02)", "table(Clo03)", "table(Clo04)",
                                            "table(Clo05)", "desk(Clo01)", "arm chair(Clo01)" };

                    foreach (string furniture in furnitures)
                    {
                        GameObject g = GameObject.Find(furniture);
                        if (g)
                        {
                            g.transform.parent = null;
                            worldObjectManager.Add(g, DisableOn.Distance, 100, ToggleModes.Renderer);
                        }
                    }

                    ModConsole.Debug("Jokke's furnitures found and loaded");
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "JOKKE_FURNITURE_ERROR");
            }

            // Haybales.
            // First we null out the prevent it from reloading the position of haybales.
            try
            {
                GameObject haybalesParent = GameObject.Find("JOBS/HayBales");
                if (haybalesParent != null)
                {
                    haybalesParent.GetComponent<PlayMakerFSM>().Fsm.RestartOnEnable = false;
                    // And now we add all child haybale to world objects.
                    foreach (Transform haybale in haybalesParent.transform.GetComponentInChildren<Transform>())
                    {
                        //worldObjectManager.Add(haybale.gameObject.name, DisableOn.Distance | DisableOn.IgnoreInQualityMode, 120);
                        ItemBehaviour t = haybale.gameObject.AddComponent<ItemBehaviour>();
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "HAYBALES_FIX_ERROR");
            }

            // Logwalls
            try
            {
                // MOP - REVIVAL 5.1: Add returns null for an object a rule excludes (here and below).
                foreach (GameObject wall in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name == "LogwallLarge"))
                {
                    var logWall = worldObjectManager.Add(wall, DisableOn.Distance, 300);
                    if (logWall != null) logWall.MinimumToggleDistance = 200;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "LOGWALL_LOAD_ERROR");
            }

            // Perajarvi Church.
            try
            {
                if (MopSettings.Mode != PerformanceMode.Performance)
                {
                    GameObject church = GameObject.Find("PERAJARVI").transform.Find("CHURCH").gameObject;
                    church.transform.parent = null;
                    GameObject churchLOD = church.transform.Find("LOD").gameObject;
                    church.GetComponent<PlayMakerFSM>().enabled = false;
                    var churchObject = worldObjectManager.Add(churchLOD, DisableOn.Distance, 300);
                    if (churchObject != null) churchObject.MinimumToggleDistance = 200;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "CHURCH_LOD_ERROR");
            }

            // Lake houses.
            try
            {
                if (MopSettings.Mode == PerformanceMode.Quality)
                {
                    GameObject.Find("PERAJARVI").transform.Find("TerraceHouse").transform.parent = null;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "LAKE_HOUSE_ERROR");
            }

            // VehiclesHighway renderers.
            // MOP - REVIVAL 5.1 (fix): only the game's own highway cars. Mods put their own objects there too (Traffic
            // Car Expansion: its car spawner and the container of its cars, both without renderers at load). MOP
            // threw on the first of them (TRAFFIC_VEHICLES_ERROR, "Couldn't find the renderers") and skipped the rest
            // of the list and the switch-on below. Such objects belong to their mod; each car is added on its own.
            try
            {
                Transform vehiclesHighway = GameObject.Find("TRAFFIC").transform.Find("VehiclesHighway");
                List<string> foreign = new List<string>();
                foreach (var f in vehiclesHighway.GetComponentsInChildren<Transform>(true).Where(f => f.parent == vehiclesHighway))
                {
                    if (!VanillaHighwayCars.Contains(f.name))
                    {
                        foreign.Add(f.name);
                        continue;
                    }

                    try
                    {
                        var traffic = worldObjectManager.Add(f.gameObject, DisableOn.Distance, 600, ToggleModes.MultipleRenderers);
                        if (traffic != null) traffic.MinimumToggleDistance = 400;
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, "TRAFFIC_VEHICLE_ERROR_" + f.name);
                    }
                }

                // Also we gonna fix the lag on initial traffic load.
                // MOP - REVIVAL 5.1: not when a mod has its own objects there. Switching the highway on makes the mod act
                // (Traffic Car Expansion spawns 10-25 cars when its spawner is switched on), and at home MOPR switches
                // the highway off again right away.
                if (foreign.Count == 0)
                    vehiclesHighway.gameObject.SetActive(true);
                else
                    ModConsole.Debug("Highway traffic: left to their mods: " + string.Join(", ", foreign.ToArray()));
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TRAFFIC_VEHICLES_ERROR");
            }

            // FITTAN renderers.
            try
            {
                var fittan = worldObjectManager.Add(GameObject.Find("TRAFFIC").transform.Find("VehiclesDirtRoad/Rally/FITTAN").gameObject, DisableOn.Distance, 600, ToggleModes.MultipleRenderers);
                if (fittan != null) fittan.MinimumToggleDistance = 400;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "FITTAN_RENDERERS_ERROR");
            }

            // Cabin: do not disable Cabin's house and burned house on Quality mode.
            try
            {
                if (MopSettings.Mode >= PerformanceMode.Quality)
                {
                    Transform cabin = GameObject.Find("CABIN").transform;
                    cabin.Find("Cabin").parent = null;
                    cabin.Find("BurntHouse").parent = null;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "CABIN_DETAILS_QUALITY");
            }

            // Cottage: Do not disable main renderer in Quality mode.
            try
            {
                if (MopSettings.Mode >= PerformanceMode.Balanced)
                {
                    Transform cottage = GameObject.Find("COTTAGE").transform;
                    cottage.Find("MESH").parent = null;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "COTTAGE_DETAILS_QUALITY");
            }

            // DANCEHALL: Visible from the distance.
            try
            {
                if (MopSettings.Mode >= PerformanceMode.Balanced)
                {
                    GameObject.Find("DANCEHALL").transform.Find("Building").parent = null;
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "DANCEHALL_BULDINGS_VISIBILITY_ERROR");
            }

            // Initialize Items class
            try
            {
                ItemsManager.Instance.Initialize();
                ModConsole.Debug("Items class initialized");
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, true, "ITEMS_CLASS_ERROR");
            }

            HookPreSaveGame();

            // Rules: objects of other mods MOP should manage ("manage"), then new parents ("reparent").
            RuleBook rules = RulesManager.Book;
            foreach (ManageRule v in rules.Manage)
            {
                try
                {
                    GameObject g = GameObject.Find(v.Name);
                    if (g == null)
                    {
                        ModConsole.Err("con.rules.manage_missing", v.Name, v.Kind);
                        continue;
                    }

                    switch (v.Kind)
                    {
                        case ManageKind.Objects:
                            worldObjectManager.Add(g, DisableOn.Distance, 200, ToggleModes.Simple);
                            break;
                        case ManageKind.Renderers:
                            worldObjectManager.Add(g, DisableOn.Distance, 200, ToggleModes.Renderer);
                            break;
                        case ManageKind.Items:
                            if (g.GetComponent<ItemBehaviour>() == null)
                                g.AddComponent<ItemBehaviour>();
                            break;
                        case ManageKind.Vehicles:
                        case ManageKind.VehiclePhysics:
                            Vehicle veh = new Vehicle(v.Name);
                            VehicleManager.Instance.Add(veh);
                            if (v.Kind == ManageKind.VehiclePhysics)
                                veh.Toggle = veh.ToggleUnityCar;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, "TOGGLE_RULES_LOAD_ERROR");
                }
            }

            foreach (ReparentRule rule in rules.Reparent)
            {
                try
                {
                    GameObject obj = GameObject.Find(rule.Name);
                    if (obj == null)
                    {
                        throw new Exception($"Object {rule.Name} doesn't exist.");
                    }

                    if (rule.Parent == null)
                    {
                        obj.transform.parent = null;
                    }
                    else
                    {
                        GameObject parent = GameObject.Find(rule.Parent);
                        if (parent == null)
                        {
                            throw new Exception($"Parent {rule.Parent} doesn't exist.");
                        }

                        obj.transform.parent = parent.transform;
                    }
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, "CHANGE_PARENT_RULE_ERROR");
                }
            }

            // Initialzie sector manager
            try
            {
                gameObject.AddComponent<SectorManager>();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, true, "SECTOR_MANAGER_ERROR");
            }

            // Add DynamicDrawDistance component.
            try
            {
                gameObject.AddComponent<DynamicDrawDistance>();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "DYNAMIC_DRAW_DISTANCE_LOAD_ERROR");
            }

            if (MopSettings.Mode != PerformanceMode.Safe)
            {
                try
                {
                    ToggleAll(false, ToggleAllMode.OnLoad);
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, "TOGGLING_ALL_ERROR");
                }
            }

            // Locate computer system.
            try
            {
                GameObject computer = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g.name == "COMPUTER");
                if (computer != null)
                {
                    computerSystem = computer.transform.Find("SYSTEM").gameObject;
                    GameObject.Find("Systems").transform.Find("OptionsMenu").gameObject.AddComponent<MopPauseMenuHandler>();
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "COMPUTER_SYSTEM_ERROR");
            }

            try
            {
                // MOP - REVIVAL 5.1.1 (fix, original MOP bug): a rule that excludes the traffic (Call Your Cousin:
                // TRAFFIC, VehiclesDirtRoad) left these null, and Update threw a NullReferenceException every frame
                // in Balanced and Quality mode. Update only reads whether the traffic is on, so the objects themselves
                // are used when MOPR doesn't manage them.
                GameObject trafficRoot = GameObject.Find("TRAFFIC");
                traffic = worldObjectManager.Get("TRAFFIC") ?? trafficRoot;
                trafficDirt = worldObjectManager.Get("VehiclesDirtRoad") ?? trafficRoot?.transform.Find("VehiclesDirtRoad")?.gameObject;
                trafficHighway = worldObjectManager.Get("VehiclesHighway") ?? trafficRoot?.transform.Find("VehiclesHighway")?.gameObject;
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TRAFFIC_LOOKUP_ERROR");
            }

            // Initialize the coroutines.
            Startup();

            // If generate-list command is set to true, generate the list of items that are disabled by MOP.
            if (MopSettings.GenerateToggledItemsListDebug)
            {
                ToggledItemsListGenerator.CreateWorldList(WorldObjectManager.Instance.GetAll);
                ToggledItemsListGenerator.CreateVehicleList(VehicleManager.Instance.GetAll);
                ToggledItemsListGenerator.CreateItemsList(ItemsManager.Instance.GetAll);
                ToggledItemsListGenerator.CreatePlacesList(PlaceManager.Instance.GetAll);
            }
        }
        
        public void Startup()
        {
            currentLoop = LoopRoutine();
            StartCoroutine(currentLoop);
            currentControlCoroutine = ControlCoroutine();
            StartCoroutine(currentControlCoroutine);

            LogLoadStatus();
            Resources.UnloadUnusedAssets();
            GC.Collect();
        }

        /// <summary>
        /// MOP - REVIVAL 5.1: a short, colored summary when the game is ready (like MOPR 4.x): what MOP manages
        /// and which of its optimizations are on.
        /// </summary>
        static void LogLoadStatus()
        {
            try
            {
                ModConsole.Success("con.loaded");

                new ModConsole.Block("con.sum.managed")
                    .Line("con.sum.count", Loc.Word("con.sum.world_objects"), Count(WorldObjectManager.Instance?.Count))
                    .Line("con.sum.count", Loc.Word("con.sum.items"), Count(ItemsManager.Instance?.Count))
                    .Line("con.sum.count", Loc.Word("con.sum.vehicles"), Count(VehicleManager.Instance?.Count))
                    .Line("con.sum.count", Loc.Word("con.sum.places"), Count(PlaceManager.Instance?.Count))
                    .Print();

                bool engine = MopMenu.EngineFastPath != null && MopMenu.EngineFastPath.GetValue();
                ModConsole.Block mode = new ModConsole.Block("con.sum.mode", Loc.Word(ModeKey(MopSettings.Mode), MoprColors.AccentHex))
                    .Line("con.sum.flag", Loc.Word("con.name.render"), Loc.OnOff(MopMenu.RenderOptimizerEnabled != null && MopMenu.RenderOptimizerEnabled.GetValue()))
                    .Line("con.sum.flag", Loc.Word("con.name.scripts"), Loc.OnOff(engine))
                    .Line("con.sum.flag", Loc.Word("con.name.rays"), Loc.OnOff(Next.Engine.SharedMouseRay.Installed));
                // Only when a supported mod is installed (5.1.1).
                if (Next.Engine.ModScripts.Installed)
                    mode.Line("con.sum.flag", Loc.Word("con.name.mod_scripts"), Loc.OnOff(true));
                mode.Line("con.sum.flag", Loc.Word("con.name.merge"), Loc.OnOff(VehicleMeshMerge.Enabled))
                    .Print();

                new ModConsole.Block("con.sum.experimental")
                    .Line("con.sum.flag", Loc.Word("con.name.engine_bay"), Loc.OnOff(MopMenu.ExperimentalEngineBay != null && MopMenu.ExperimentalEngineBay.GetValue()))
                    .Line("con.sum.flag", Loc.Word("con.name.mirrors"), Loc.OnOff(MopMenu.ExperimentalMirrorRate != null && MopMenu.ExperimentalMirrorRate.GetValue()))
                    .Print();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "LOAD_STATUS");
            }
        }

        static string Count(int? n) => MoprColors.Accent(n.HasValue ? n.Value.ToString() : "-");

        /// <summary>The settings-page name of a performance mode (menu.mode.*), also used by the console.</summary>
        internal static string ModeKey(PerformanceMode mode)
        {
            switch (mode)
            {
                case PerformanceMode.Performance: return "menu.mode.performance";
                case PerformanceMode.Quality: return "menu.mode.quality";
                case PerformanceMode.Safe: return "menu.mode.safe";
                default: return "menu.mode.balanced";
            }
        }
#endregion
#region Save Game Actions
        /// <summary>
        /// Looks for gamobject named SAVEGAME, and hooks PreSaveGame into them.
        /// </summary>
        void HookPreSaveGame()
        {
            try
            {
                IEnumerable<GameObject> saveGames = Resources.FindObjectsOfTypeAll<GameObject>()
                    .Where(obj => obj.name.Contains("SAVEGAME"));

                // MOP - REVIVAL: every save point is hooked in isolation. Originally one failing object threw out
                // of the whole method, so the save points after it (and the death save) silently stayed unhooked:
                // saving there wrote items and vehicles in MOP's disabled state. The hook count is exact now.
                GameObject[] saveGameArray = saveGames.ToArray();
                int hooked = 0, failed = 0;
                for (int i = 0; i < saveGameArray.Length; i++)
                {
                    GameObject savegame = saveGameArray[i];
                    bool useInnactiveFix = false;
                    bool isJail = false;

                    try
                    {
                        if (!savegame.activeSelf)
                        {
                            useInnactiveFix = true;
                            savegame.SetActive(true);
                        }

                        if (savegame.transform.parent != null && savegame.transform.parent.name == "JAIL" && !savegame.transform.parent.gameObject.activeSelf)
                        {
                            useInnactiveFix = true;
                            isJail = true;
                            savegame.transform.parent.gameObject.SetActive(true);
                        }

                        if (savegame.FsmInject("Mute audio", PreSaveGame))
                            hooked++;
                        else
                        {
                            failed++;
                            ModConsole.Warn("con.save.no_mute_state", savegame.Path());
                        }
                        savegame.FsmInject("Wait for click", SaveManager.RemoveReadOnlyAttribute);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        ExceptionManager.New(ex, false, $"SAVE_HOOK_ERROR_{savegame.name}");
                    }
                    finally
                    {
                        // Restore the original activity even if hooking failed.
                        try
                        {
                            if (useInnactiveFix)
                            {
                                if (isJail)
                                    savegame.transform.parent.gameObject.SetActive(false);
                                else
                                    savegame.SetActive(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            ExceptionManager.New(ex, false, "SAVE_HOOK_RESTORE_ERROR");
                        }
                    }
                }

                // Hooking up on death save.
                try
                {
                    GameObject onDeathSaveObject = new GameObject("MOP_OnDeathSave");
                    onDeathSaveObject.transform.parent = GameObject.Find("Systems").transform.Find("Death/GameOverScreen");
                    OnDeathBehaviour behaviour = onDeathSaveObject.AddComponent<OnDeathBehaviour>();
                    behaviour.Initialize(PreSaveGame);
                    hooked++;
                }
                catch (Exception ex)
                {
                    failed++;
                    ExceptionManager.New(ex, false, "SAVE_HOOK_DEATH_ERROR");
                }

                // Adding custom action to state that will trigger PreSaveGame, if the player picks up the phone with large Suski.
                try
                {
                    GameObject telephone = GameObject.Find("Telephone");
                    if (telephone != null)
                    {
                        telephone.transform.Find("Logic/UseHandle").GetComponent<PlayMakerFSM>().GetState("Pick phone").InsertAction(0, new CustomSuskiLargeFlip());
                        hooked++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    ExceptionManager.New(ex, false, "SAVE_HOOK_TELEPHONE_ERROR");
                }

                if (failed == 0)
                    ModConsole.Debug($"Hooked {hooked} save points!");
                else
                    ModConsole.Err("con.save.hook_failed", hooked, failed);
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, true, "SAVE_HOOK_ERROR");
            }
        }

        /// <summary>
        /// This void is initialized before the player decides to save the game.
        /// </summary>
        public void PreSaveGame()
        {
            ModConsole.Debug("Initializing Pre-Save Actions...");
            SaveManager.ReleaseSave();
            MopSettings.IsModActive = false;
            StopCoroutine(currentLoop);
            StopCoroutine(currentControlCoroutine);
            
            SaveManager.RemoveReadOnlyAttribute();
            ItemsManager.Instance.OnSave();

            // MOP - REVIVAL: the render optimizer restores every visual value it changed first.
            try { RenderOptimizer.Instance?.OnPreSave(); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "PRESAVE_RENDER_OPTIMIZER"); }
            // MOP - REVIVAL 5.1: every original Satsuma renderer back on before anything is saved.
            VehicleMeshMerge.ReleaseAll();
            try { EngineBayCulling.OnPreSave(); }
            catch (Exception ex) { ExceptionManager.New(ex, false, "PRESAVE_ENGINE_BAY"); }

            ToggleAll(true, ToggleAllMode.OnSave);

            // MOP - REVIVAL: every FSM is back to its original restart behaviour before the game saves,
            // and a read-only verification reports anything managed by MOP that stayed disabled.
            RestartGuard.ReleaseAll();
            SaveBarrier.Verify();
            ModConsole.Debug("Pre-Save Actions Completed!");
        }

        public void DelayedPreSave()
        {
            if (currentDelayedSaveRoutine != null)
            {
                StopCoroutine(currentDelayedSaveRoutine);
            }

            currentDelayedSaveRoutine = DelayedSaveRoutine();
            StartCoroutine(currentDelayedSaveRoutine);
        }

        private IEnumerator currentDelayedSaveRoutine;
        IEnumerator DelayedSaveRoutine()
        {
            yield return new WaitForSeconds(1);
            if (FsmManager.IsSuskiLargeCall())
                PreSaveGame();
        }
#endregion
#region Updating
        /// <summary>
        /// This coroutine runs
        /// </summary>
        private IEnumerator currentLoop;
        IEnumerator LoopRoutine()
        {
            MopSettings.IsModActive = true;

            int waitTimer = 0;

            while (MopSettings.IsModActive)
            {
                // Ticks make sure that MOP is still up and running.
                // If the ticks didn't update, that means this routine stopped.
                ++ticks;

                if (!IsItemInitializationDone())
                {
                    // We are slightly delaying the initialization, so all items have chance to set in place, because fuck MSC and its physics.
                    waitTimer++;
                    SetLoadProgress((float)waitTimer / WaitForPhysicsToSettleTime);
                    if (waitTimer >= WaitForPhysicsToSettleTime)
                    {
                        FinishLoading();
                    }
                }

                isPlayerAtYard = MopMenu.ActiveDistance.GetValue() <= 1 ? Vector3.Distance(player.position, placeManager[0].transform.position) < 100
                    : Vector3.Distance(player.position, placeManager[0].transform.position) < 100 * MopSettings.ActiveDistanceMultiplicationValue;

                // When player is in any of the sectors, MOP will act like the player is at yard.
                if (SectorManager.Instance.IsPlayerInSector())
                {
                    inSectorMode = true;
                    isPlayerAtYard = true;
                }
                else
                {
                    inSectorMode = false;
                }

                yield return null;

                int i;
                long half = worldObjectManager.Count >> 1;
                // World Objects.
                for (i = 0; i < worldObjectManager.Count; ++i)
                {
                    if (i == half)
                        yield return null;

                    try
                    {
                        GenericObject worldObject = worldObjectManager[i];
                        GameObject gm = worldObject.GameObject;

                        // Check if object was destroyed (mostly intended for AI pedastrians).
                        if (gm == null)
                        {
                            worldObjectManager.Remove(worldObject);
                            continue;
                        }

                        // MOP - REVIVAL: GameObject.name allocates a new string on every call. It is only read when
                        // one of the checks below actually needs it (same results, no garbage per object per tick).
                        string name = null;

                        if (SectorManager.Instance.IsPlayerInSector() && SectorManager.Instance.SectorRulesContains(name = gm.name))
                        {
                            gm.SetActive(true);
                            continue;
                        }

                        // Should the object be disabled when the player leaves the house?
                        if (worldObject.DisableOn.HasFlag(DisableOn.PlayerAwayFromHome) || worldObject.DisableOn.HasFlag(DisableOn.PlayerInHome))
                        {
                            if (inSectorMode && (name ?? (name = gm.name)) == "NPC_CARS")
                                continue;

                            // (Name first, as in MOP 3.10: computerSystem may be missing.)
                            if ((name ?? (name = gm.name)) == "COMPUTER" && computerSystem.activeSelf)
                                continue;

                            bool enableObject = worldObject.DisableOn.HasFlag(DisableOn.PlayerAwayFromHome) ? isPlayerAtYard : !isPlayerAtYard;

                            if (worldObject.DisableOn.HasFlag(DisableOn.DoNotEnableWhenLeavingHome) && enableObject && !isPlayerAtYard)
                            {
                                continue;
                            }

                            worldObject.Toggle(worldObject.DisableOn.HasFlag(DisableOn.PlayerAwayFromHome) ? isPlayerAtYard : !isPlayerAtYard);
                        }
                        else if (worldObject.DisableOn.HasFlag(DisableOn.Distance))
                        {                      
                            // The object will be disabled, if the player is in the range of that object.
                            worldObject.Toggle(IsGenericObjectEnabled(worldObject));
                        }
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, "WORLD_OBJECT_TOGGLE_ERROR");
                    }
                }

                // Safe mode prevents toggling elemenets that MAY case some issues (vehicles, items, etc.)
                if (MopSettings.Mode == PerformanceMode.Safe)
                {
                    yield return new WaitForSeconds(.7f);
                    continue;
                }

                // So we create two separate lists - one is meant to enable, and second is ment to disable them,
                // Why?
                // If we enable items before enabling vehicle inside of which these items are supposed to be, they'll fall through to ground.
                // And the opposite happens if we disable vehicles before disabling items.
                // So if we are disabling items, we need to do that BEFORE we disable vehicles.
                // And we need to enable items AFTER we enable vehicles.
                half = ItemsManager.Instance.Count >> 1;
                for (i = 0; i < ItemsManager.Instance.Count; ++i)
                {
                    if (i == half)
                        yield return null;

                    // Safe check if somehow the i gets bigger than array length.
                    if (i >= ItemsManager.Instance.Count) break;

                    try
                    {

                        ItemBehaviour item = ItemsManager.Instance[i];

                        if (item == null || item.gameObject == null)
                        {
                            itemsToRemove.Enqueue(item);
                            continue;
                        }

                        float distance = Vector3.Distance(player.position, item.transform.position);

                        // Check the mode in what MOP is supposed to run and adjust to it.
                        bool toEnable;
                        if (MopSettings.Mode == PerformanceMode.Performance)
                        {
                            toEnable = IsEnabled(distance, FsmManager.IsPlayerInCar() && !isPlayerAtYard ? 20 : 50);
                        }
                        else
                        {
                            toEnable = IsEnabled(distance);
                        }


                        if (toEnable)
                        {
                            item.ToggleChangeFix();

                            // Load object's position AFTER MOP starts to actually function,
                            // AND item is meant to be enabled.
                            if (ticks > 1 && !item.WasTransformLoaded)
                            {
                                item.LoadTransform();
                            }

                            if (item.ActiveSelf) continue;
                            itemsToEnable.Enqueue(item);
                        }
                        else
                        {
                            if (item.LodObject != null)
                            {
                                item.ToggleLOD(distance < item.LodObject.ToggleDistance);
                            }
                            if (!item.ActiveSelf) continue;
                            item.Toggle(false);
                        }

                        if (item.rb != null && item.rb.IsSleeping())
                        {
                            if (CompatibilityManager.IsInBackpack(item)) continue;
                            item.rb.isKinematic = true;
                        }
                        
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, "ITEM_TOGGLE_GATHER_ERROR");
                    }
                }

                // Vehicles
                half = VehicleManager.Instance.Count >> 1;
                for (i = 0; i < VehicleManager.Instance.Count; ++i)
                {
                    if (i == half)
                        yield return null;

                    try
                    {
                        Vehicle vehicle = VehicleManager.Instance[i];
                        if (vehicle == null)
                        {
                            VehicleManager.Instance.RemoveAt(i);
                            continue;
                        }

                        distance = Vector3.Distance(player.transform.position, vehicle.transform.position);
                        toggleDistance = MopMenu.ActiveDistance.GetValue() <= 1
                            ? MopSettings.UnityCarActiveDistance : MopSettings.UnityCarActiveDistance * MopSettings.ActiveDistanceMultiplicationValue;

                        switch (vehicle.VehicleType)
                        {
                            case VehiclesTypes.Satsuma:
                                Satsuma.Instance.ToggleElements(distance);
                                vehicle.ToggleEventSounds(distance < 3);
                                break;
                            case VehiclesTypes.Jonnez:
                                vehicle.ToggleEventSounds(distance < 2);
                                break;
                        }

                        bool isVehicleEnabled = IsVehicleEnabled(distance);
                        vehicle.ToggleUnityCar(IsVehiclePhysicsEnabled(distance, toggleDistance));
                        vehicle.Toggle(isVehicleEnabled);

                        if (!isVehicleEnabled)
                        {
                            vehicle.ToggleLOD(distance < 300);
                        }
                        else
                        {
                            vehicle.ToggleLOD(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, $"VEHICLE_TOGGLE_ERROR_{i}");
                    }
                }

                // Items To Enable
                while (itemsToEnable.Count > 0)
                {
                    try
                    {
                        ItemBehaviour behaviour = itemsToEnable.Dequeue();
                        behaviour.Toggle(true);
                        behaviour.ToggleLOD(false);
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, "ITEM_TOGGLE_ENABLE_ERROR");
                    }
                }

                // Places
                int full = placeManager.Count;
                half = full >> 1;
                for (i = 0; i < full; ++i)
                {
                    if (i == half)
                        yield return null;

                    try
                    {
                        if (SectorManager.Instance.IsPlayerInSector() && SectorManager.Instance.SectorRulesContains(placeManager[i].GetName()))
                        {
                            continue;
                        }

                        placeManager[i].ToggleActive(IsPlaceEnabled(placeManager[i].transform, placeManager[i].GetToggleDistance()));
                    }
                    catch (Exception ex)
                    {
                        ExceptionManager.New(ex, false, $"PLACE_TOGGLE_ERROR_{i}");
                    }
                }

                // Remove items that don't exist anymore.
                while (itemsToRemove.Count > 0)
                {
                    ItemsManager.Instance.Remove(itemsToRemove.Dequeue());
                }

                yield return new WaitForSeconds(.7f);

                if (retries > 0 && !restartSucceedMessaged)
                {
                    restartSucceedMessaged = true;
                    ModConsole.Success("con.restart_succeeded");
                }
            }
        }

        void Update()
        {
#if DEBUG
            if (Input.GetKeyDown(KeyCode.F5))
            {
                PreSaveGame();
                Application.LoadLevel(1);
            }

            if (Input.GetKeyDown(KeyCode.F6))
            {
                PreSaveGame();
            }

            if (Input.GetKeyDown(KeyCode.F7))
            {
                SaveManager.RemoveReadOnlyAttribute();
            }
            
            if (Input.GetKeyDown(KeyCode.PageUp))
            {
                FinishLoading();
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                List<ItemBehaviour> b = ItemsManager.Instance.GetAll;
                foreach (ItemBehaviour i in b)
                {
                    if (i.name.Contains("r20 battery box"))
                    {
                        i.LoadTransform();
                    }
                }
            }

#endif
            if (!MopSettings.IsModActive) return;

            Satsuma.Instance?.ForceRotation();

            if (MopSettings.Mode > PerformanceMode.Performance)
            {
                // Pointless to checck if traffic is about to collide with other vehicles, if traffic is disabled.
                // (A missing object - a mod removed it - means there is no such traffic.)
                if (traffic == null || !traffic.activeSelf)
                {
                    return;
                }

                // Same for traffic highway and dirt road traffic.
                if ((trafficHighway == null || !trafficHighway.activeSelf) && (trafficDirt == null || !trafficDirt.activeSelf))
                {
                    return;
                }

                for (int i = 0; i < VehicleManager.Instance.Count; ++i)
                {
                    if (VehicleManager.Instance[i] == null) continue;
                    if (!VehicleManager.Instance[i].IsActive) continue;
                    // Vehicle is not on the dirt road, nor on highway.
                    // It's very unlikely that it will be hit by anything.
                    if (Vector3.Distance(Vector3.zero, VehicleManager.Instance[i].transform.position) < 1000) continue;

                    if (VehicleManager.Instance[i].IsTrafficCarInArea())
                    {
                        VehicleManager.Instance[i].ToggleUnityCar(true);
                    }
                }
            }
        }

        #endregion
        #region Item enabling checks
        const int MinimumItemDistance = 10;
        const int MinimumItemOutsideDistance = 30;
        const int DefaultItemToggleDistance = 75;
        const int DummyItemDistane = 200;
        // 0.25 = 18 | 0.5 = 37 | 0.75 = 56 | 1 = 75 | 2 = 150 | 4 = 300

        const int MinimumVehicleDistance = 20;
        const int MinimumVehicleOutsideDistance = 50;
        const int DefaultVehicleDistance = 125;
        const int PerformanceModeVehicleDistance = 30;
        // 0.25 = 31 | 0.5 = 62 | 0.75 = 93 | 1 = 125 | 2 = 250 | 4 = 500

        const int MinimumPlaceDistance = 175;

        /// <summary>
        /// Checks if the object is supposed to be enabled by calculating the distance between player and target.
        /// </summary>
        /// <param name="target">Target object.</param>
        /// <param name="toggleDistance">Distance below which the object should be enabled (default 200 units).</param>
        bool IsEnabled(float distance, float toggleDistance = DefaultItemToggleDistance)
        {
            if (toggleDistance == 0)
            {
                return false;
            }

            if (inSectorMode)
                toggleDistance = 20;

            toggleDistance *= MopSettings.ActiveDistanceMultiplicationValue;

            if (inSectorMode)
            {
                if (toggleDistance < MinimumItemDistance)
                {
                    toggleDistance = MinimumItemDistance;
                }
            }
            else
            {
                if (toggleDistance < MinimumItemOutsideDistance)
                {
                    toggleDistance = MinimumItemOutsideDistance;
                }
            }
            

            return distance < toggleDistance;
        }

        bool IsGenericObjectEnabled(GenericObject obj)
        {
            if (obj.Distance == -1)
            {
                return false;
            }

            float toggleDistance = obj.Distance;
            if (obj.DisableOn.HasFlag(DisableOn.AlwaysUse1xDistance))
            {
                toggleDistance *= MopMenu.ActiveDistance.GetValue() <= 0 ? 0.5f : 0.1f;
                return Vector3.Distance(player.transform.position, obj.transform.position) < toggleDistance;
            }

            if (inSectorMode)
            {
                toggleDistance *= MopMenu.ActiveDistance.GetValue() <= 0 ? 0.5f : 0.1f;
            }

            toggleDistance *= MopSettings.ActiveDistanceMultiplicationValue;
            if (toggleDistance < obj.MinimumToggleDistance && !inSectorMode)
            {
                toggleDistance = obj.MinimumToggleDistance;
            }

            return Vector3.Distance(player.transform.position, obj.transform.position) < toggleDistance;
        }

        bool IsVehicleEnabled(float distance, float toggleDistance = DefaultVehicleDistance)
        {
            if (inSectorMode)
            {
                toggleDistance = MinimumVehicleDistance;
            }
            else if (MopSettings.Mode == PerformanceMode.Performance)
            {
                toggleDistance = PerformanceModeVehicleDistance;
            }

            toggleDistance *= MopSettings.ActiveDistanceMultiplicationValue;

            if (inSectorMode)
            {
                if (toggleDistance < MinimumVehicleDistance)
                {
                    toggleDistance = MinimumVehicleDistance;
                }
            }
            else
            {
                if (toggleDistance < MinimumVehicleOutsideDistance)
                {
                    toggleDistance = MinimumVehicleOutsideDistance;
                }
            }

            return distance < toggleDistance;
        }

        bool IsVehiclePhysicsEnabled(float distance, float toggleDistance = 200)
        {
            return distance < toggleDistance;
        }

        bool IsPlaceEnabled(Transform target, float toggleDistance = 200)
        {
            toggleDistance *= MopSettings.ActiveDistanceMultiplicationValue;
            if (toggleDistance < MinimumPlaceDistance)
            {
                toggleDistance = MinimumPlaceDistance;
            }
            return Vector3.Distance(player.transform.position, target.position) < toggleDistance;
        }
#endregion
#region System Control & Crash Protection
        private int ticks;
        public int Tick { get => ticks; }
        private int lastTick;
        private int retries;
        private const int MaxRetries = 3;
        private bool restartSucceedMessaged;
        private IEnumerator currentControlCoroutine;

        /// <summary>
        /// Every 10 seconds check if the coroutine is still active.
        /// If not, try to restart it.
        /// It is checked by two values - ticks and lastTick
        /// Ticks are added by coroutine. If the value is different than the lastTick, everything is okay.
        /// If the ticks and lastTick is the same, that means coroutine stopped.
        /// </summary>
        /// <returns></returns>
        IEnumerator ControlCoroutine()
        {
            while (MopSettings.IsModActive)
            {
                yield return new WaitForSeconds(10);

                if (lastTick == ticks)
                {
                    if (retries >= MaxRetries)
                    {
                        ModConsole.Err("con.restart_failed");
                        ModConsole.Err("con.report_bug");
                        try { ToggleAll(true); } catch { }
                        MopSettings.EnableSafeMode();
                        yield break;
                    }

                    retries++;
                    restartSucceedMessaged = false;
                    ModConsole.Warn("con.stopped_working", retries, MaxRetries);
                    StopCoroutine(currentLoop);
                    currentLoop = LoopRoutine();
                    StartCoroutine(currentLoop);
                }
                else
                {
                    lastTick = ticks;
                }
            }
        }
#endregion

        /// <summary>
        /// Toggles on all objects.
        /// </summary>
        public void ToggleAll(bool enabled, ToggleAllMode mode = ToggleAllMode.Default)
        {
            ModConsole.Debug($"Toggling all to {enabled.ToString().ToUpper()} in mode {mode.ToString().ToUpper()}");

            // World objects
            for (int i = 0; i < worldObjectManager.Count; i++)
            {
                try
                {
                    worldObjectManager[i]?.Toggle(enabled);
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, "TOGGLE_ALL_WORLD_OBJECTS_ERROR");
                }
            }

            ModConsole.Debug("Toggled WORLD OBJECTS");

            if (MopSettings.Mode == PerformanceMode.Safe) return;

            // Items
            for (int i = 0; i < ItemsManager.Instance.Count; i++)
            {
                if (i >= ItemsManager.Instance.Count) break;

                try
                {
                    ItemBehaviour item = ItemsManager.Instance[i];
                    item.Toggle(enabled);

                    // We're freezing the object on save, so it won't move at all.
                    if (mode == ToggleAllMode.OnSave)
                    {
                        item.gameObject.SetActive(true);
                        item.Freeze();
                        item.SaveGame();
                        item.ToggleLOD(false);
                    }
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, "TOGGLE_ALL_ITEMS_ERROR");
                }
            }

            ModConsole.Debug("Toggled ITEMS");

            // Find all kilju, emptyca, empty juice container, and force empty them if applicable
            try
            {
                foreach (GameObject bottle in Resources.FindObjectsOfTypeAll<GameObject>().Where(g => g.name.ContainsAny("kilju", "emptyca", "empty plastic can")))
                {
                    bottle.GetComponent<ItemBehaviour>()?.ResetKiljuContainer();
                    if (mode == ToggleAllMode.OnSave)
                    {
                        bottle.SetActive(true);
                        bottle.GetComponent<ItemBehaviour>()?.SaveGame();
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "KILJU_RESET_FORCE_ERROR");
            }

            ModConsole.Debug("Toggled KILJU");

            // Vehicles
            for (int i = 0; i < VehicleManager.Instance.Count; i++)
            {
                try
                {
                    Vehicle veh = VehicleManager.Instance[i];
                    veh.Toggle(enabled);

                    if (mode == ToggleAllMode.OnLoad)
                    {
                        VehicleManager.Instance[i].ForceToggleUnityCar(false);
                    }
                    else if (mode == ToggleAllMode.OnSave)
                    {
                        VehicleManager.Instance[i].ToggleUnityCar(enabled);
                        VehicleManager.Instance[i].Freeze();
                    }
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, $"TOGGLE_ALL_VEHICLE_ERROR_{i}");
                }
            }

            try
            {
                if (mode == ToggleAllMode.OnSave)
                {
                    Satsuma.Instance.OnSaveGlueAll();
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TOGGLE_SATSUMA_GLUE_ALL_ERROR");
            }

            ModConsole.Debug("Toggled VEHICLES");

            // Places
            for (int i = 0; i < placeManager.Count; i++)
            {
                try
                {
                    placeManager[i].ToggleActive(enabled);
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, $"TOGGLE_ALL_PLACES_{i}");
                }
            }

            ModConsole.Debug("Toggled PLACES");

            // Force teleport kilju bottles.
            try
            {
                if (mode == ToggleAllMode.OnSave)
                {
                    GameObject canTrigger = ItemsManager.Instance.GetCanTrigger();
                    if (canTrigger)
                    {
                        if (!canTrigger.transform.parent.gameObject.activeSelf)
                        {
                            canTrigger.transform.parent.gameObject.SetActive(true);
                        }
                        canTrigger.GetComponent<PlayMakerFSM>().SendEvent("STOP");
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TOGGLE_ALL_JOBS_DRUNK");
            }

            ModConsole.Debug("Toggled KILJU TELEPORT");

            // ToggleElements class of Satsuma.
            try
            {
                if (mode == ToggleAllMode.OnSave)
                {
                    Satsuma.Instance.ToggleElements(0);
                }
                else
                {
                    Satsuma.Instance.ToggleElements(enabled ? 0 : 10000);
                }
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "TOGGLE_ALL_SATSUMA_TOGGLE_ELEMENTS");
            }

            ModConsole.Debug("Toggled SATSUMA ELEMENTS");
            ModConsole.Debug("Toggle done!");
        }

        public Transform GetPlayer()
        {
            return player;
        }

        public bool IsInSector()
        {
            return inSectorMode;
        }

        public bool IsItemInitializationDone()
        {
            return itemInitializationDelayDone;
        }

        void FinishLoading()
        {
            if (loadScreenWorkaround != null)
            {
                StopCoroutine(loadScreenWorkaround);
            }

            itemInitializationDelayDone = true;

            // MOP - REVIVAL: release the player first; nothing below may keep him frozen.
            if (playerController != null)
                playerController.enabled = true;
            FsmManager.PlayerInMenu = false;

            try
            {
                // Unity null check: the screen may already be gone (scene change, earlier call).
                if (loadScreen != null)
                    loadScreen.Deactivate();
            }
            catch (Exception ex)
            {
                ExceptionManager.New(ex, false, "LOAD_SCREEN_HIDE_ERROR");
            }

            GameObject vh = GameObject.Find("VehiclesHighway");
            if (vh)
            {
                vh.SetActive(false);
            }
        }

        private readonly IEnumerator loadScreenWorkaround;
        IEnumerator InfiniteLoadscreenWorkaround()
        {
            yield return new WaitForSeconds(LoadScreenWorkaroundTime);
            if (FsmManager.PlayerInMenu)
            {
                ModConsole.Err("con.load_timeout");
                ModConsole.Err("con.report_bug");
                FinishLoading();
                Satsuma.Instance?.ToggleActive(true);
                Satsuma.Instance?.ForceToggleUnityCar(true);
                FsmManager.PlayerInMenu = false;
            }
        }

        internal void ToggleDebugMode()
        {
            if (GetComponent<DebugTools.DebugMonitor>() == null)
            {
                gameObject.AddComponent<DebugTools.DebugMonitor>();
            }
            else
            {
                Destroy(GetComponent<DebugTools.DebugMonitor>());
            }
        }

        public Transform DumbObjectParent => dumbObjectsParent.transform;
    }
}
