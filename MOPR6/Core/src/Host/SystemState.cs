// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Life cycle of one optimization system (design §2):
//   NotInstalled --Install--> Installed(On | Verify) <--Suspend/Resume--> Suspended
//   Installed / Suspended --Remove--> Removed (= NotInstalled again, may be installed later)
//   Installed / Suspended --Trip--> Tripped (failure policy: removed, its changes returned; Install again resets it)
// The machine only says which transitions are legal; the Unity side does the work and calls these in order.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;

namespace MOPR6.Core.Host
{
    public enum SystemStatus : byte
    {
        NotInstalled,
        Installed,
        Suspended,
        Tripped
    }

    public enum SystemMode : byte
    {
        On,       // the optimization applies
        Verify    // the original always runs, the claim is checked (requirement 7.2)
    }

    public sealed class SystemState
    {
        public readonly string Id;
        public SystemStatus Status { get; private set; }
        public SystemMode Mode { get; private set; }
        /// <summary>Why the last Suspend happened ("save", "master switch"...), or null.</summary>
        public string SuspendReason { get; private set; }
        /// <summary>Why the system tripped, or null.</summary>
        public string TripReason { get; private set; }
        public int Installs { get; private set; }

        public SystemState(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("A system needs an id.", nameof(id));
            Id = id;
        }

        /// <summary>The system's hooks may change the game (installed and not suspended).</summary>
        public bool Live => Status == SystemStatus.Installed;

        public void Install(SystemMode mode)
        {
            if (Status == SystemStatus.Installed || Status == SystemStatus.Suspended)
                throw new InvalidOperationException(Id + " is installed already (" + Status + ")");
            Status = SystemStatus.Installed;
            Mode = mode;
            SuspendReason = null;
            TripReason = null;
            Installs++;
        }

        /// <summary>Switches On / Verify without a reinstall (the Unity side reinstalls its patches if it must).</summary>
        public void SetMode(SystemMode mode)
        {
            if (Status != SystemStatus.Installed && Status != SystemStatus.Suspended)
                throw new InvalidOperationException(Id + " is not installed (" + Status + ")");
            Mode = mode;
        }

        public void Suspend(string reason)
        {
            if (Status == SystemStatus.Suspended) { SuspendReason = reason; return; }   // nested reasons: the last one wins
            if (Status != SystemStatus.Installed) return;                                // nothing to suspend
            Status = SystemStatus.Suspended;
            SuspendReason = reason;
        }

        public void Resume()
        {
            if (Status != SystemStatus.Suspended) return;
            Status = SystemStatus.Installed;
            SuspendReason = null;
        }

        public void Remove()
        {
            if (Status == SystemStatus.Tripped) return;   // stays tripped until installed again
            Status = SystemStatus.NotInstalled;
            SuspendReason = null;
        }

        public void Trip(string reason)
        {
            if (Status == SystemStatus.NotInstalled || Status == SystemStatus.Tripped) return;
            Status = SystemStatus.Tripped;
            TripReason = reason;
            SuspendReason = null;
        }

        public override string ToString() =>
            Id + ": " + Status + (Status == SystemStatus.Installed || Status == SystemStatus.Suspended ? " (" + Mode + ")" : "") +
            (SuspendReason != null ? ", suspended: " + SuspendReason : "") + (TripReason != null ? ", tripped: " + TripReason : "");
    }
}
