// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// When an idle FSM may go to sleep (requirement 8.1, design §7.1), as far as time is concerned: the Unity side says
// "idle in the same state now" or "something happened" (a state was entered, it was woken); the policy says yes only
// after the FSM stayed idle in that state for at least MinFrames frames AND MinSeconds seconds (hysteresis), and
// never for a "restless" FSM: more than MaxWakes wakes within WakeWindow seconds bans it for BanSeconds, doubled
// on every new ban (up to MaxBanSeconds) and halved again after a quiet ban length.
// Shared code: compiled for net8.0 (tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Sleep
{
    public sealed class SleepPolicy
    {
        public int MinFrames = 30;
        public double MinSeconds = 0.5;
        public int MaxWakes = 5;
        public double WakeWindow = 10;
        public double BanSeconds = 30;
        public double MaxBanSeconds = 600;

        sealed class Record
        {
            public int IdleSinceFrame = -1;
            public double IdleSinceTime;
            public object State;                    // the active state when the idle stretch began
            public readonly Queue<double> Wakes = new Queue<double>();
            public double BannedUntil = -1;
            public double Ban;                      // current ban length (0 = never banned)
            public double LastBanEnd;
        }

        readonly Dictionary<object, Record> records;

        public SleepPolicy(IEqualityComparer<object> comparer = null)
        {
            records = new Dictionary<object, Record>(comparer ?? EqualityComparer<object>.Default);
        }

        public int Count => records.Count;
        public int Bans { get; private set; }

        Record Get(object fsm)
        {
            Record r;
            if (!records.TryGetValue(fsm, out r)) records[fsm] = r = new Record();
            return r;
        }

        /// <summary>The FSM is idle now in <paramref name="state"/>. True when it may sleep now.</summary>
        public bool Idle(object fsm, object state, int frame, double now)
        {
            Record r = Get(fsm);
            if (r.BannedUntil > now) return false;
            if (r.IdleSinceFrame < 0 || !ReferenceEquals(r.State, state))
            {
                r.IdleSinceFrame = frame;
                r.IdleSinceTime = now;
                r.State = state;
                return false;
            }
            return frame - r.IdleSinceFrame >= MinFrames && now - r.IdleSinceTime >= MinSeconds;
        }

        /// <summary>Something happened to the FSM (a state was entered, it is busy): the idle stretch starts again.</summary>
        public void Busy(object fsm)
        {
            Record r;
            if (records.TryGetValue(fsm, out r)) r.IdleSinceFrame = -1;
        }

        /// <summary>The sleeping FSM was woken at <paramref name="now"/>. True if it is restless (banned from now on).</summary>
        public bool Woken(object fsm, double now)
        {
            Record r = Get(fsm);
            r.IdleSinceFrame = -1;
            r.Wakes.Enqueue(now);
            while (r.Wakes.Count > 0 && now - r.Wakes.Peek() > WakeWindow) r.Wakes.Dequeue();
            if (r.Wakes.Count <= MaxWakes) return false;
            // Quiet for a whole ban length since the last ban ended: the next ban is shorter again.
            if (r.Ban > 0 && now - r.LastBanEnd > r.Ban) r.Ban = Math.Max(BanSeconds, r.Ban / 2);
            r.Ban = r.Ban <= 0 ? BanSeconds : Math.Min(MaxBanSeconds, r.Ban * 2);
            r.BannedUntil = now + r.Ban;
            r.LastBanEnd = r.BannedUntil;
            r.Wakes.Clear();
            Bans++;
            return true;
        }

        public bool IsBanned(object fsm, double now)
        {
            Record r;
            return records.TryGetValue(fsm, out r) && r.BannedUntil > now;
        }

        /// <summary>Forgets one FSM (destroyed) or all (a new scene).</summary>
        public void Forget(object fsm) => records.Remove(fsm);

        public void Clear()
        {
            records.Clear();
            Bans = 0;
        }
    }
}
