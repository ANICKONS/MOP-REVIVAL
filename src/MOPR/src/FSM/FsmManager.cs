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
using System.Reflection;
using UnityEngine;

using MOP.Common.Enumerations;
using MOP.Managers;

namespace MOP.FSM
{
    static class FsmManager
    {
        /// <summary>
        /// Forgets every cached FSM variable and object (called in the main menu, before the next load).
        /// </summary>
        public static void ResetAll()
        {
            // MOP - REVIVAL 5.1 (fix, original MOP bug): GetFields() without flags only returns PUBLIC fields, and
            // every cache below is private, so nothing was ever reset. After "quit to menu -> Continue" MOP kept
            // reading the variables of the destroyed scene: frozen old values (hood and battery bolts, trailer,
            // Hayosiko key, combine, the Suski phone call, the clock).
            FieldInfo[] fields = typeof(FsmManager).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo field in fields)
            {
                if (field.IsLiteral || field.IsInitOnly || field.FieldType.IsValueType) continue;
                field.SetValue(null, null);
            }
        }

        static FsmInt uncleStage;
        /// <summary>
        /// Checks if the player has the keys to the Hayosiko.
        /// </summary>
        /// <returns></returns>
        public static bool PlayerHasHayosikoKey()
        {
            // Store Uncle's PlayMakerFSM for later
            if (uncleStage == null)
                uncleStage = GameObject.Find("UNCLE").GetComponent<PlayMakerFSM>().FsmVariables.GetFsmInt("UncleStage");
             
            return uncleStage.Value == 5;
        }

        static FsmBool gtGrilleInstalled;
        /// <summary>
        /// Checks if GT Grille is attached to the car.
        /// </summary>
        /// <returns></returns>
        public static bool IsGTGrilleInstalled()
        {
            if (gtGrilleInstalled == null)
                gtGrilleInstalled = GameObject.Find("Database").transform.Find("DatabaseOrders/GrilleGT").gameObject.GetComponent<PlayMakerFSM>().FsmVariables.GetFsmBool("Installed");

            return gtGrilleInstalled.Value;
        }

        static FsmBool order;
        /// <summary>
        /// Checks if repair shop job has been ordered.
        /// </summary>
        /// <returns></returns>
        public static bool IsRepairshopJobOrdered()
        {
            if (order == null)
                order = GameObject.Find("REPAIRSHOP/Order").GetComponent<PlayMakerFSM>().FsmVariables.GetFsmBool("_Order");

            return order.Value;
        }

        static FsmString playerCurrentVehicle;
        /// <summary>
        /// Checks PlayerCurrentVehicle global value. If it says "Satsuma", that means player sits in Satsuma.
        /// </summary>
        /// <returns></returns>
        public static bool IsPlayerInSatsuma()
        {
            if (playerCurrentVehicle == null)
                playerCurrentVehicle = PlayMakerGlobals.Instance.Variables.FindFsmString("PlayerCurrentVehicle");

            return playerCurrentVehicle.Value == "Satsuma";
        }

        public static bool IsPlayerInCar()
        {
            if (playerCurrentVehicle == null)
                playerCurrentVehicle = PlayMakerGlobals.Instance.Variables.FindFsmString("PlayerCurrentVehicle");

            return playerCurrentVehicle.Value.Length > 0;
        }

        static FsmInt farmJobStage;
        /// <summary>
        /// Checks if the JobStage of Farm Job has rechead 3rd stage.
        /// On 3rd stage, the combine is available to the player.
        /// </summary>
        /// <returns></returns>
        public static bool IsCombineAvailable()
        {
            // MOP - REVIVAL 5.1 (fix): no null checks before; while the farm job object or its JobStage variable
            // can't be found, every MOP tick threw (VEHICLE_TOGGLE_ERROR_9). Not found = not available yet: MOP
            // leaves the combine alone and looks again on the next tick.
            if (farmJobStage == null)
            {
                GameObject job = GameObject.Find("JOBS/Farm/Job");
                PlayMakerFSM fsm = job != null ? job.GetComponent<PlayMakerFSM>() : null;
                farmJobStage = fsm != null ? fsm.FsmVariables.FindFsmInt("JobStage") : null;
                if (farmJobStage == null) return false;
            }

            return farmJobStage.Value >= 3;
        }

        static FsmBool hoodBolted;
        public static bool IsStockHoodBolted()
        {
            if (hoodBolted == null)
                hoodBolted = GameObject.Find("Database/DatabaseBody/Hood").GetComponent<PlayMakerFSM>().FsmVariables.GetFsmBool("Bolted");

            return hoodBolted.Value;
        }

        static FsmBool fiberHoodBolted;
        public static bool IsFiberHoodBolted()
        {
            if (fiberHoodBolted == null)
                fiberHoodBolted = GameObject.Find("Database/DatabaseOrders/Fiberglass Hood").GetComponent<PlayMakerFSM>().FsmVariables.GetFsmBool("Bolted");

            return fiberHoodBolted.Value;
        }

        static GameObject triggerHood;
        public static void ForceHoodAssemble()
        {
            if (triggerHood == null)
                triggerHood = VehicleManager.Instance.GetVehicle(VehiclesTypes.Satsuma).transform.Find("Body/trigger_hood").gameObject;

            triggerHood.SetActive(true);
            triggerHood.GetComponent<PlayMakerFSM>().SendEvent("ASSEMBLE");
        }

        static GameObject triggerBumperRear;
        public static void ForceRearBumperAssemble()
        {
            if (triggerBumperRear == null)
                triggerBumperRear = VehicleManager.Instance.GetVehicle(VehiclesTypes.Satsuma).transform.Find("Body/trigger_bumper_rear").gameObject;

            triggerBumperRear.SetActive(true);
            triggerBumperRear.GetComponent<PlayMakerFSM>().SendEvent("ASSEMBLE");
        }

        static GameObject kekmetTrailerRemove;
        public static bool IsTrailerAttached()
        {
            if (kekmetTrailerRemove == null)
                kekmetTrailerRemove = VehicleManager.Instance.GetVehicle(VehiclesTypes.Kekmet).transform.Find("Trailer/Remove").gameObject;

            return kekmetTrailerRemove.activeSelf;
        }

        static FsmBool playerHelmet;
        public static bool PlayerHelmetOn()
        {
            if (playerHelmet == null)
                playerHelmet = PlayMakerGlobals.Instance.Variables.GetFsmBool("PlayerHelmet");

            return playerHelmet.Value;
        }

        static FsmFloat drawDistance;
        public static float GetDrawDistance()
        {
            if (drawDistance == null)
                drawDistance = GameObject.Find("Systems/Options").GetPlayMaker("GFX").FsmVariables.GetFsmFloat("DrawDistance");

            return drawDistance.Value;
        }

        static FsmBool suskiLarge;
        public static bool IsSuskiLargeCall()
        {
            if (suskiLarge == null)
                suskiLarge = GameObject.Find("Telephone/Logic/UseHandle").GetComponent<PlayMakerFSM>().FsmVariables.GetFsmBool("SuskiLarge");

            return suskiLarge.Value;
        }

        static FsmBool playerInMenu;
        public static bool PlayerInMenu
        {            
            get
            {
                if (playerInMenu == null)
                    playerInMenu = PlayMakerGlobals.Instance.Variables.GetFsmBool("PlayerInMenu");

                return playerInMenu.Value;
            }
            set
            {
                if (playerInMenu == null)
                    playerInMenu = PlayMakerGlobals.Instance.Variables.GetFsmBool("PlayerInMenu");

                playerInMenu.Value = value; 
            }
        }

        static FsmBool playerComputer;
        public static bool PlayerComputer
        {
            get
            {
                if (playerComputer == null)
                    playerComputer = PlayMakerGlobals.Instance.Variables.GetFsmBool("PlayerComputer");

                return playerComputer.Value;
            }
        }

        static FsmBool shadowsHouse;
        public static bool ShadowsHouse
        {
            get
            {
                if (shadowsHouse == null)
                    shadowsHouse = GameObject.Find("Systems").transform.Find("Options").GetPlayMaker("GFX").FsmVariables.GetFsmBool("ShadowsHouse");

                return shadowsHouse.Value;
            }
        }

        static FsmFloat hour;
        public static int Hour
        {
            get
            {
                if (hour == null)
                    hour = GameObject.Find("MAP/SUN/Pivot/SUN").GetPlayMaker("Clock").FsmVariables.GetFsmFloat("Hours");

                return (int)hour.Value;
            }
        }
    }
}
