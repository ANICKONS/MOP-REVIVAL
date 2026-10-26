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
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using MOP.Vehicles;
using MOP.Vehicles.Cases;
using MOP.Common;
using MOP.Common.Enumerations;
using MOP.Common.Interfaces;

namespace MOP.Managers
{
    class VehicleManager : IManager<Vehicle>
    {
        private static VehicleManager instance;
        public static VehicleManager Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new VehicleManager();
                }
                return instance;
            }
        }

        public Vehicle this[int index] => vehicles[index];

        // Vehicles object names of My Summer Car.
        readonly string[] vehicleArrayMSC =
        {
            "SATSUMA(557kg, 248)",
            "HAYOSIKO(1500kg, 250)",
            "JONNEZ ES(Clone)",
            "FLATBED",
            "KEKMET(350-400psi)",
            "RCO_RUSCKO12(270)",
            "FERNDALE(1630kg)",
            "GIFU(750/450psi)",
            "BOAT",
            "COMBINE(350-400psi)"
        };

        private List<Vehicle> vehicles;

        private VehicleManager() { }

        public void Initialize()
        {
            // Loading vehicles
            vehicles = new List<Vehicle>();
            string[] activeVehicleArray = vehicleArrayMSC;
            
            foreach (string vehicle in activeVehicleArray)
            {
                try
                {
                    if (GameObject.Find(vehicle) == null)
                    {
                        ModConsole.Debug("Unable to locate vehicle " + vehicle);
                        continue;
                    }

                    Vehicle newVehicle = null;
                    switch (vehicle)
                    {
                        default:
                            newVehicle = new Vehicle(vehicle);
                            break;
                        case "SATSUMA(557kg, 248)":
                            newVehicle = new Satsuma(vehicle);
                            break;
                        case "BOAT":
                            newVehicle = new Boat(vehicle);
                            break;
                        case "COMBINE(350-400psi)":
                            newVehicle = new Combine(vehicle);
                            break;
                        case "HAYOSIKO(1500kg, 250)":
                            newVehicle = new Hayosiko(vehicle);
                            break;
                        case "KEKMET(350-400psi)":
                            newVehicle = new Kekmet(vehicle);
                            break;
                        case "FLATBED":
                            newVehicle = new Flatbed(vehicle);
                            break;
                        case "RCO_RUSCKO12(270)":
                            newVehicle = new Ruscko(vehicle);
                            break;
                        case "FERNDALE(1630kg)":
                            newVehicle = new Ferndale(vehicle);
                            break;
                        case "GIFU(750/450psi)":
                            newVehicle = new Gifu(vehicle);
                            break;
                        case "JONNEZ ES(Clone)":
                            newVehicle = new Jonnez(vehicle);
                            break;
                    }

                    vehicles.Add(newVehicle);
                }
                catch (Exception ex)
                {
                    ExceptionManager.New(ex, false, $"VEHICLE_LOAD_ERROR_{vehicle}");
                }
            }

            ModConsole.Debug("Vehicles initialized");
        }

        public Vehicle Add(Vehicle vehicle)
        {
            vehicles.Add(vehicle);
            return vehicle;
        }

        public int Count => vehicles.Count;

        public List<Vehicle> GetAll => vehicles;

        public void RemoveAt(int index)
        {
            vehicles.RemoveAt(index);
        }

        public Vehicle GetVehicle(VehiclesTypes vehicleType)
        {
            if (vehicleType == VehiclesTypes.Generic)
            {
                return null;
            }

            return vehicles.FirstOrDefault(f => f.VehicleType == vehicleType);
        }

        public void Remove(Vehicle vehicle)
        {
            if (vehicles.Contains(vehicle))
            {
                vehicles.Remove(vehicle);
            }
        }

        public int EnabledCount
        {
            get
            {
                int enabled = 0;
                foreach (Vehicle veh in vehicles)
                {
                    if (veh.gameObject.activeSelf)
                    {
                        enabled++;
                    }
                }
                return enabled;
            }
        }

        public bool IsInVanilaGame(Vehicle veh)
        {
            return vehicleArrayMSC.Contains(veh.gameObject.name);
        }

        // ===== Tow ropes (MOP - REVIVAL 5.1.1) ==============================================================
        // A tow rope is a SpringJoint on the body of one hook (the car, or the tractor's RopePoint) to the other
        // car's body: the game's rope does it (TestBot tow-vanilla: SpringJoint on the van, to the Satsuma) and
        // so does Better MSC's own rope, which leaves the hooks' "Attached" false. Both ends of such a joint count
        // as towed. Only SpringJoints, on the bodies of the vehicles' hooks: nothing else of the cars is one.

        readonly HashSet<Rigidbody> towLinked = new HashSet<Rigidbody>();
        readonly List<SpringJoint> jointBuffer = new List<SpringJoint>();
        int lastTowLinkCount = -1;

        /// <summary>Finds the tow rope joints on the vehicles' hook bodies. Once per MOP tick, before the vehicles.</summary>
        public void UpdateTowLinks()
        {
            towLinked.Clear();
            for (int i = 0; i < vehicles.Count; i++)
            {
                Vehicle v = vehicles[i];
                if (v == null || v.gameObject == null) continue;
                Rigidbody[] bodies = v.TowBodies;
                for (int b = 0; b < bodies.Length; b++)
                {
                    Rigidbody body = bodies[b];
                    if (body == null) continue;
                    body.GetComponents(jointBuffer);
                    for (int j = 0; j < jointBuffer.Count; j++)
                    {
                        SpringJoint joint = jointBuffer[j];
                        if (joint == null || joint.connectedBody == null || joint.connectedBody == body) continue;
                        // A rope joins two vehicles; a joint between two parts of one vehicle is not a rope.
                        if (joint.connectedBody.transform.root == body.transform.root) continue;
                        towLinked.Add(body);
                        towLinked.Add(joint.connectedBody);
                    }
                }
            }
            jointBuffer.Clear();

            if (towLinked.Count != lastTowLinkCount)
            {
                lastTowLinkCount = towLinked.Count;
                ModConsole.Debug(towLinked.Count == 0 ? "Tow rope: none."
                    : "Tow rope: " + string.Join(", ", towLinked.Where(r => r != null).Select(r => r.name).ToArray()));
            }
        }

        /// <summary>True when one of the bodies is an end of a tow rope (as of the last UpdateTowLinks).</summary>
        public bool IsTowLinked(Rigidbody[] bodies)
        {
            if (bodies == null || towLinked.Count == 0) return false;
            for (int i = 0; i < bodies.Length; i++)
                if (bodies[i] != null && towLinked.Contains(bodies[i])) return true;
            return false;
        }
    }
}
