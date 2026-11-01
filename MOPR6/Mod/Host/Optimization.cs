// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// One optimization system (design §2). A system derives from Optimization, implements OnInstall / OnRemove /
// OnSuspend / OnResume and wraps every entry point (Harmony prefix, tick, event) like this:
//
//     static bool Prefix(PlayMakerFSM __instance)
//     {
//         if (!Me.Live) return true;                                    // removed, suspended or tripped: original
//         try { return Body(__instance); }
//         catch (Exception ex) { Me.Fail("Prefix", ex, Path(__instance)); return true; }
//     }
//
// Fail records the error and stops the system at once (Live = false, so every hook runs the original); the host
// takes it out at the end of the frame - never inside the patched call - when the failure policy says so (more than
// 3 failures in 60 s), otherwise it continues after the next tick.

using System;
using System.Text;
using MOPR6.Core.Errors;
using MOPR6.Core.Host;
using MOPR6.Diagnostics;

namespace MOPR6.Host
{
    public enum Tier : byte
    {
        Exact,          // provably changes nothing in the game's behaviour (requirement 7.1)
        Experimental,   // measured, verified where possible, off by default (7.5)
        Render,         // changes only how the scene is drawn, through the render arbiters (requirement 9)
        Sleep,          // script sleep (requirement 8): exact by proof, with a verify mode like the exact systems
        World,          // world management (requirement 10): not exact, every action through the ledger, checked by the harness
        Test            // the harness's probe: never on the settings page, never installed by it
    }

    public abstract class Optimization
    {
        public readonly SystemState State;
        public readonly FailurePolicy Failures = new FailurePolicy();
        public abstract string Title { get; }
        public abstract Tier Tier { get; }
        /// <summary>Set by Fail until the host has looked at it: the hooks skip their work.</summary>
        internal bool Faulted;

        protected Optimization(string id) { State = new SystemState(id); }

        public string Id => State.Id;

        /// <summary>The hooks may do their work now.</summary>
        public bool Live => State.Live && !Faulted;

        /// <summary>Installs patches and state. Returns null on success, else the reason (the system stays out).</summary>
        protected internal abstract string OnInstall(SystemMode mode);
        /// <summary>Removes every patch and returns every change (ledger reasons) of the system.</summary>
        protected internal abstract void OnRemove();
        /// <summary>Stops applying and returns its changes (the save barrier).</summary>
        protected internal virtual void OnSuspend(string reason) { }
        protected internal virtual void OnResume() { }
        /// <summary>Called by the host every 0.25 s while Live.</summary>
        protected internal virtual void OnTick(double now) { }
        /// <summary>A new game scene: drop every reference into the old one (installed or not).</summary>
        protected internal virtual void OnScene() { }
        /// <summary>Counters and verify mismatches for the report.</summary>
        public virtual void Report(StringBuilder sb) { }
        /// <summary>Verify mode: claims that turned out wrong so far (requirement 7.2: must stay 0).</summary>
        public virtual long Mismatches => 0;
        /// <summary>Installed by default (exact systems; the settings page comes with task 27).</summary>
        public virtual bool DefaultOn => false;

        /// <summary>Called from a hook's catch: records the error and stops the system's hooks until the host decides.</summary>
        public void Fail(string place, Exception ex, string context = null)
        {
            Faulted = true;
            ModLog.Report(Id + "/" + place, ex, context);
            SystemHost.FailureNoticed(this);
        }
    }
}
