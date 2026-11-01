// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The save barrier as a state machine (requirement 6, design §4). The Unity side calls:
//   Raise(source)        a save point / death / the phone starts a save (synchronously, before any Save* action);
//                        true = newly raised: suspend every system, wake sleepers, restore the ledger, verify
//   Written()            the game broadcast SAVEGAME (the files are being written)
//   Tick(now, idle)      every poll (0.25 s); idle = the save FSM is back in its waiting state
//   Finished()           a save without quitting is complete (the game stays in the scene)
//   Unloaded()           the scene is gone (save and quit to the menu)
// Tick answers Resume when the save was cancelled - the save FSM went back to waiting without SAVEGAME, or nothing
// was written for CancelSeconds - or when a save without quitting finished. Requirement 6.4: resumed at most 2 s
// after the cancel (poll 0.25 s + CancelSeconds 1.5 s).
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;

namespace MOPR6.Core.Save
{
    public enum BarrierState : byte
    {
        Live,        // systems apply their changes
        Holding,     // raised: everything is vanilla, the save may be writing
        Finishing    // written and finished without a quit: resume on the next tick
    }

    public enum BarrierAction : byte
    {
        None,
        Resume
    }

    public sealed class SaveBarrier
    {
        public const double DefaultCancelSeconds = 1.5;

        public readonly double CancelSeconds;
        public BarrierState State { get; private set; }
        public string Source { get; private set; }
        public bool WrittenSeen { get; private set; }
        public double RaisedAt { get; private set; }
        public int Raises { get; private set; }
        public int Cancels { get; private set; }

        public SaveBarrier() : this(DefaultCancelSeconds) { }

        public SaveBarrier(double cancelSeconds)
        {
            if (cancelSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(cancelSeconds));
            CancelSeconds = cancelSeconds;
        }

        /// <summary>True when the barrier goes up now (false: it is up already).</summary>
        public bool Raise(string source, double now)
        {
            if (State != BarrierState.Live) return false;
            State = BarrierState.Holding;
            Source = source;
            WrittenSeen = false;
            RaisedAt = now;
            Raises++;
            return true;
        }

        public void Written()
        {
            if (State == BarrierState.Holding) WrittenSeen = true;
        }

        public void Finished()
        {
            if (State == BarrierState.Holding) State = BarrierState.Finishing;
        }

        public void Unloaded()
        {
            State = BarrierState.Live;
            Source = null;
            WrittenSeen = false;
        }

        /// <param name="savePointIdle">The FSM that started the save is back in its waiting state.</param>
        public BarrierAction Tick(double now, bool savePointIdle)
        {
            switch (State)
            {
                case BarrierState.Finishing:
                    return Lower();
                case BarrierState.Holding:
                    if (WrittenSeen) return BarrierAction.None;   // writing: wait for Finished or Unloaded
                    if (savePointIdle || now - RaisedAt >= CancelSeconds)
                    {
                        Cancels++;
                        return Lower();
                    }
                    return BarrierAction.None;
                default:
                    return BarrierAction.None;
            }
        }

        BarrierAction Lower()
        {
            State = BarrierState.Live;
            Source = null;
            WrittenSeen = false;
            return BarrierAction.Resume;
        }
    }
}
