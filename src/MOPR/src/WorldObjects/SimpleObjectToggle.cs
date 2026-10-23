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

using UnityEngine;

using MOP.Common;
using MOP.Common.Enumerations;

namespace MOP.WorldObjects
{
    class SimpleObjectToggle : GenericObject
    {
        public SimpleObjectToggle(GameObject gameObject, DisableOn disableOn = DisableOn.Distance, int distance = 200) : base(gameObject, distance, disableOn) { }

        public override void Toggle(bool enabled)
        {
            if (DisableOn.HasFlag(DisableOn.IgnoreInQualityMode) && MopSettings.Mode == PerformanceMode.Quality)
            {
                enabled = true;
            }

            if (DisableOn.HasFlag(DisableOn.IgnoreInBalancedAndAbove) && MopSettings.Mode >= PerformanceMode.Balanced)
            {
                enabled = true;
            }

            if (this.gameObject != null && this.gameObject.activeSelf != enabled)
            {
                // MOP - REVIVAL 5.1.1: never switch off an object the player is in (a passenger in Pena's car under
                // TRAFFIC, in the drunk driver's car under NPC_CARS, in a vehicle of a mod): the player would vanish
                // with it. It is switched off on a later tick, once the player is out. Replaces the original MOP's
                // moving of those two cars out of their parents (see GameFixes).
                if (!enabled && IsPlayerInside()) return;


                // MOP - REVIVAL 5.1 (fix): world objects were switched without the restart guard, so FSMs in them that
                // read the save when they start (FSM audit: the cottage sauna stove, the Dingonbiisi clock, the
                // candles at the crash site) went back to the saved state each time MOP switched them on again.
                if (!enabled) Next.RestartGuard.BeforeDisable(this.gameObject, "World object " + this.gameObject.name);
                this.gameObject.SetActive(enabled);
                if (enabled) Next.RestartGuard.AfterEnable(this.gameObject);
            }
        }

        bool IsPlayerInside()
        {
            Transform player = Hypervisor.Instance != null ? Hypervisor.Instance.GetPlayer() : null;
            return player != null && player.IsChildOf(this.gameObject.transform);
        }
    }
}
