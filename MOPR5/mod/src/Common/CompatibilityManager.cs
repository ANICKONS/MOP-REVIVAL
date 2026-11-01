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

using MSCLoader;
using UnityEngine;
using MOP.Items;

namespace MOP.Common
{
    static class CompatibilityManager
    {
        // This script manages the compatibility with other mods
       
        // Use this method of ensuring compatibility ONLY in specific cases,
        // that are not supported by Rule Files.

        // CarryMore
        // https://www.racedepartment.com/downloads/carry-more-backpack-alternative.22396/
        static bool CarryMore { get; set; }
        
        // Advanced Backpack
        static bool AdvancedBackpack { get; set; }
        static readonly Vector3 AdvancedBackpackPosition = new Vector3(630f, 10f, 1140f);
        const int AdvancedBackpackDistance = 30;

        // MOP - REVIVAL 5.1: inventory mods that keep the stored items high above the map. Nothing of the game is ever
        // up there (the terrain stays below about 100 m), so an item that high is held by such a mod. Yet Another
        // Inventory Mod (YAIM) keeps its items kinematic at (0, 1000, 0) and only moves them on pickup and drop.
        // MOPR used to freeze them there as "far away" and, when YAIM dropped one in front of the player, put a
        // physics-only item (bucket, fish trap, pike...) back where it had frozen it: into the sky, where it fell
        // onto the map 1.4 km away (TestBot yaim-tce: 999.6 m one second after the drop).
        const float StorageHeight = 500f;

        // MOP - REVIVAL: the original MOP ("MOP") would optimize the same objects twice.
        static readonly string[] incompatibleMods = { "MOP", "KruFPS", "ImproveFPS", "OptimizeMSC", "ZZDisableAll", "DisableAll" };

        public static void Initialize()
        {
            CarryMore = global::MOP.FSM.LoaderCompat.GetMod("CarryMore") != null;
            AdvancedBackpack = global::MOP.FSM.LoaderCompat.GetMod("AdvancedBackpack") != null;
        }

        /// <summary>
        /// True if an inventory mod holds the item: MOPR leaves it alone (no switching, no freezing). 5.1: every
        /// installed mod is checked (before, Advanced Backpack was not when CarryMore was there too), plus any item
        /// high above the map (YAIM and other mods that store items in the sky).
        /// </summary>
        public static bool IsInBackpack(ItemBehaviour behaviour)
        {
            Vector3 position = behaviour.transform.position;
            if (position.y > StorageHeight) return true;
            if (CarryMore && position.y < -900) return true;
            if (AdvancedBackpack && (position - AdvancedBackpackPosition).sqrMagnitude < AdvancedBackpackDistance * AdvancedBackpackDistance) return true;
            return false;
        }

        public static bool IsConfilctingModPresent(out string conflictingModName)
        {
            foreach (string a in incompatibleMods)
            {
                if (global::MOP.FSM.LoaderCompat.GetMod(a) != null)
                {
                    conflictingModName = global::MOP.FSM.LoaderCompat.GetMod(a).Name;
                    return true;
                }
            }

            conflictingModName = "";
            return false;
        }

        public static bool IsMySummerCar => Application.productName == "My Summer Car";


    }
}
