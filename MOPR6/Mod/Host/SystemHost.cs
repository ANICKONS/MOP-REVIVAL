// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The system host (design §2): installs, removes, suspends and resumes the optimization systems, ticks them, and
// isolates their failures (requirement 12.2). A failing system is taken out at the end of the frame with its changes
// returned; the others go on. The master switch (13.2) removes all of them in reverse order and restores the ledger.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using MOPR6.Core.Host;
using MOPR6.Diagnostics;
using MOPR6.Ledger;
using UnityEngine;

namespace MOPR6.Host
{
    public static class SystemHost
    {
        static readonly List<Optimization> systems = new List<Optimization>();
        static readonly List<Optimization> faulted = new List<Optimization>();
        static HostBehaviour behaviour;
        public const float TickSeconds = 0.25f;

        public static IList<Optimization> Systems => systems.AsReadOnly();

        public static void Register(Optimization system)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            foreach (Optimization s in systems)
                if (s.Id == system.Id) throw new InvalidOperationException("system registered twice: " + system.Id);
            systems.Add(system);
        }

        public static Optimization Find(string id)
        {
            foreach (Optimization s in systems) if (s.Id == id) return s;
            return null;
        }

        /// <summary>Creates the host object of the game scene (ticks and the end-of-frame failure handling).</summary>
        public static void StartScene()
        {
            // "Save and quit" ends the scene with the barrier up: the systems were suspended for the save, the new
            // scene starts without a barrier (SaveHooks.StartScene), so they go on.
            if (SaveBarrierActive)
            {
                ModLog.Line("new scene after a save: systems resume");
                ResumeAll();
            }
            foreach (Optimization s in systems)
            {
                try { s.OnScene(); }
                catch (Exception ex) { ModLog.Report(s.Id + "/Scene", ex); }
            }
            if (behaviour != null) return;
            GameObject go = new GameObject("MOPR_Host");
            behaviour = go.AddComponent<HostBehaviour>();
        }

        public static string Install(Optimization s, SystemMode mode)
        {
            if (s.State.Status == SystemStatus.Installed || s.State.Status == SystemStatus.Suspended) return null;
            if (s.State.Status == SystemStatus.Tripped) s.Failures.Reset();
            string err;
            try { err = s.OnInstall(mode); }
            catch (Exception ex)
            {
                ModLog.Report(s.Id + "/Install", ex);
                err = ex.GetType().Name + ": " + ex.Message;
            }
            if (err != null)
            {
                SafeRemove(s);
                ModLog.Warn(s.Id + " not installed: " + err);
                return err;
            }
            s.Faulted = false;
            s.State.Install(mode);
            if (SaveBarrierActive) Suspend(s, "save");
            ModLog.Line(s.Id + " installed (" + mode + ")");
            return null;
        }

        public static void Remove(Optimization s)
        {
            if (s.State.Status != SystemStatus.Installed && s.State.Status != SystemStatus.Suspended) return;
            SafeRemove(s);
            s.State.Remove();
            ModLog.Line(s.Id + " removed");
        }

        static void SafeRemove(Optimization s)
        {
            try { s.OnRemove(); }
            catch (Exception ex) { ModLog.Report(s.Id + "/Remove", ex); }
            // Whatever the system left behind in the ledger goes back too.
            Arbiters.RemoveSystem(s.Id);
        }

        static void Suspend(Optimization s, string reason)
        {
            if (s.State.Status != SystemStatus.Installed) return;
            try { s.OnSuspend(reason); }
            catch (Exception ex) { ModLog.Report(s.Id + "/Suspend", ex); }
            Arbiters.RemoveSystem(s.Id);
            s.State.Suspend(reason);
        }

        public static bool SaveBarrierActive { get; private set; }

        /// <summary>The save barrier went up: every system stops applying, in reverse order of registration.</summary>
        public static void SuspendAll(string reason)
        {
            SaveBarrierActive = true;
            for (int i = systems.Count - 1; i >= 0; i--) Suspend(systems[i], reason);
        }

        public static void ResumeAll()
        {
            SaveBarrierActive = false;
            foreach (Optimization s in systems)
            {
                if (s.State.Status != SystemStatus.Suspended) continue;
                s.State.Resume();
                try { s.OnResume(); }
                catch (Exception ex) { s.Fail("Resume", ex); }
            }
        }

        /// <summary>Master switch (requirement 13.2): everything out, everything back. Returns the changes that could not be returned.</summary>
        public static int StopAll()
        {
            for (int i = systems.Count - 1; i >= 0; i--) Remove(systems[i]);
            return Arbiters.RestoreAll("master switch").Count;
        }

        /// <summary>
        /// The hooks the systems rely on are installed (first game load, MoprMod.OnPostLoad): from now on systems may
        /// be installed, also from the main menu.
        /// </summary>
        public static bool Ready { get; private set; }

        public static void MarkReady() { Ready = true; }

        /// <summary>Installs <paramref name="s"/> in <paramref name="mode"/>, reinstalling it when it runs in another mode. Null or the error.</summary>
        public static string SetMode(Optimization s, SystemMode mode)
        {
            if (s.State.Status == SystemStatus.Installed || s.State.Status == SystemStatus.Suspended)
            {
                if (s.State.Mode == mode) return null;
                Remove(s);
            }
            return Install(s, mode);
        }

        internal static void FailureNoticed(Optimization s)
        {
            if (!faulted.Contains(s)) faulted.Add(s);
        }

        /// <summary>End of frame: decide about every system that failed during it.</summary>
        static void HandleFailures()
        {
            if (faulted.Count == 0) return;
            Optimization[] list = faulted.ToArray();
            faulted.Clear();
            double now = ModLog.Now();
            foreach (Optimization s in list)
            {
                if (s.Failures.Fail(now))
                {
                    int count = s.Failures.TotalFailures;
                    SafeRemove(s);
                    s.State.Trip(count + " failures, the last " + s.Failures.RecentFailures(now) + " within " + s.Failures.WindowSeconds + " s");
                    ModLog.Warn(Localization.Loc.F("con.tripped", s.Title, ModLog.FilePath));
                }
                else
                {
                    // Not tripped yet: the system continues (its hooks resume work after this frame).
                    s.Faulted = false;
                }
            }
        }

        /// <summary>Ledger entries checked against the scene per 2 s (design §3, mechanism 3).</summary>
        public const int DriftBudget = 128;
        static int ticks;

        static void Tick()
        {
            double now = ModLog.Now();
            foreach (Optimization s in systems)
            {
                if (!s.Live) continue;
                try { s.OnTick(now); }
                catch (Exception ex) { s.Fail("Tick", ex); }
            }
            if (++ticks % 8 == 0 && !SaveBarrierActive)
            {
                try { Arbiters.CheckDrift(DriftBudget); }
                catch (Exception ex) { ModLog.Report("ledger/CheckDrift", ex); }
            }
        }

        /// <summary>Verify mismatches of every system so far.</summary>
        public static long Mismatches()
        {
            long n = 0;
            foreach (Optimization s in systems) n += s.Mismatches;
            return n;
        }

        public static string Status()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Optimization s in systems)
            {
                sb.Append(s.State).Append(", failures ").Append(s.Failures.TotalFailures).Append('\n');
                s.Report(sb);
            }
            return sb.ToString();
        }

        sealed class HostBehaviour : MonoBehaviour
        {
            float nextTick;

            void Update()
            {
                // The mod's clock (ModLog.Now: unscaled game time), so a tick falls on the same frame in every
                // deterministic harness run.
                float now = Time.unscaledTime;
                if (now >= nextTick)
                {
                    nextTick = now + TickSeconds;
                    Tick();
                    SaveHooks.Tick();
                }
            }

            void Start() { StartCoroutine(EndOfFrame()); }

            IEnumerator EndOfFrame()
            {
                WaitForEndOfFrame wait = new WaitForEndOfFrame();
                while (true)
                {
                    yield return wait;
                    HandleFailures();
                }
            }

            void OnDestroy()
            {
                if (behaviour == this) behaviour = null;
            }
        }
    }
}
