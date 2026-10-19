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

using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using MSCLoader;
using UnityEngine;

using MOP.Common.Enumerations;

namespace MOP.Common
{
    static class MopSettings
    {        
        public static bool IsModActive { get; set; } // This is the master switch of MOP. If deactivated, all functions will freeze.
        public static PerformanceMode Mode;        
        public const int UnityCarActiveDistance = 5; // Distance after which car physics is toggled.
        public static bool GenerateToggledItemsListDebug; // Debugging functionality.
        public static bool LoadedOnce; // Tracks if the game has been fully loaded at least once.
        
        public static GameFixStatus GameFixStatus;
        public static bool ForceLoadRestart;
        
        // Restarts manager.
        internal static int Restarts = 0;
        internal const int MaxRestarts = 5;
        internal static bool RestartWarningShown = false;

        static float shadowDistanceOriginalValue;
        static int vsyncCount = -1;

        // MopVersionInfo
        public static string DataFile => Path.Combine(MOP.ModConfigPath, "MopData.json");
        static MopData loadedData;

        /// <summary>
        /// MOP - REVIVAL 5.1: the mode the current game was set up with. Part of a mode is built at load
        /// (buildings detached from their LOD groups, indoor sectors, item LOD models); the rest (toggle
        /// distances, skidmarks, draw distance, traffic) follows Mode live.
        /// </summary>
        public static PerformanceMode LoadedMode;

        /// <summary>The mode selected on the settings page (may differ from Mode while Safe mode waits for the menu).</summary>
        internal static PerformanceMode SelectedMode()
        {
            if (MopMenu.ModePerformance != null && MopMenu.ModePerformance.GetValue()) return PerformanceMode.Performance;
            if (MopMenu.ModeQuality != null && MopMenu.ModeQuality.GetValue()) return PerformanceMode.Quality;
            if (MopMenu.ModeSafe != null && MopMenu.ModeSafe.GetValue()) return PerformanceMode.Safe;
            return PerformanceMode.Balanced;
        }

        internal static void UpdatePerformanceMode()
        {
            // MODES
            // Show the warning about safe mode, if the player disables safe mode and is not in main menu.
            if (ModLoader.CurrentScene != CurrentScene.MainMenu)
            {
                if ((Mode == PerformanceMode.Safe && MopMenu.ModeSafe.GetValue() == false) || (Mode != PerformanceMode.Safe && MopMenu.ModeSafe.GetValue() == true))
                {
                    ModUI.ShowMessage(Localization.Loc.T(MopMenu.ModeSafe.GetValue() ? "msg.safe_mode_on_later" : "msg.safe_mode_off_later"),
                                      Localization.Loc.T("msg.title"));
                    MopMenu.RefreshRestartNotice();
                    return;
                }
            }

            Mode = SelectedMode();
            MopMenu.RefreshRestartNotice();
        }

        internal static void UpdateFramerateLimiter()
        {
            Application.targetFrameRate = MopMenu.LimitFramerate.GetValue() ? MopMenu.FramerateLimiter.GetValue() : -1;
        }

        internal static void UpdateShadows()
        {
            // Shadow distance.
            if (shadowDistanceOriginalValue == 0)
                shadowDistanceOriginalValue = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = MopMenu.ShadowDistance.GetValue();
        }

        public static void UpdateMiscSettings()
        {
            ToggleBackgroundRunning();

            // Vsync fix.
            if (vsyncCount == -1)
                vsyncCount = QualitySettings.vSyncCount;
            else
                QualitySettings.vSyncCount = vsyncCount;
        }

        /// <summary>
        /// Returns the value that is used to multiplify the active distance of an object.
        /// So for example, if the default active distance of the object is 200 units,
        /// and the multiplication value is 0.5, the actual active distance will be 100 units.
        /// </summary>
        public static float ActiveDistanceMultiplicationValue
        {
            get
            {
                switch (MopMenu.ActiveDistance.GetValue())
                {
                    case 0:
                        return 0.5f;
                    case 1:
                        return 0.75f;
                    default: // 1
                        return 1;
                    case 3:
                        return 2;
                    case 4:
                        return 4;
                }
            }
        }

        public static void EnableSafeMode()
        {
            Mode = PerformanceMode.Safe;
        }

        internal static void ToggleBackgroundRunning()
        {
            Application.runInBackground = MopMenu.KeepRunningInBackground.GetValue();
        }

        static JsonSerializerSettings GetNewSettings()
        {
            JsonSerializerSettings settings = new JsonSerializerSettings
            {
                DateTimeZoneHandling = DateTimeZoneHandling.Utc
            };
            return settings;
        }

        public static void WriteData(MopData data)
        {
            string json = JsonConvert.SerializeObject(data, GetNewSettings());
            StreamWriter writer = new StreamWriter(DataFile);
            writer.Write(json);
            writer.Close();
        }

        static MopData ReadData()
        {
            if (!File.Exists(DataFile))
            {
                MopData newRules = new MopData
                {
                    LastModList = new List<string>()
                };
                return newRules;
            }

            StreamReader reader = new StreamReader(DataFile);
            string content = reader.ReadToEnd();
            reader.Close();

            MopData rules = JsonConvert.DeserializeObject<MopData>(content, GetNewSettings());
            return rules;
        }

        public static MopData Data
        {
            get
            {
                if (loadedData == null)
                {
                    loadedData = ReadData();
                }

                return loadedData;
            }
        }
    }
}
