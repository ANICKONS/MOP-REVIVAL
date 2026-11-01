// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// When a failing system is taken out (requirement 12.2, design §2): more than MaxFailures failures within Window
// seconds (sliding window) -> trip. One policy per system; time comes from the caller (Time.realtimeSinceStartup
// in the game, a number in tests).
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Errors
{
    public sealed class FailurePolicy
    {
        public const int DefaultMaxFailures = 3;
        public const double DefaultWindowSeconds = 60;

        readonly Queue<double> times = new Queue<double>();
        public readonly int MaxFailures;
        public readonly double WindowSeconds;

        public int TotalFailures { get; private set; }
        public bool Tripped { get; private set; }

        public FailurePolicy() : this(DefaultMaxFailures, DefaultWindowSeconds) { }

        public FailurePolicy(int maxFailures, double windowSeconds)
        {
            if (maxFailures < 0) throw new ArgumentOutOfRangeException(nameof(maxFailures));
            if (windowSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(windowSeconds));
            MaxFailures = maxFailures;
            WindowSeconds = windowSeconds;
        }

        /// <summary>Counts one failure at <paramref name="now"/>; returns true when this failure trips the system.</summary>
        public bool Fail(double now)
        {
            TotalFailures++;
            while (times.Count > 0 && now - times.Peek() > WindowSeconds) times.Dequeue();
            times.Enqueue(now);
            if (Tripped || times.Count <= MaxFailures) return false;
            Tripped = true;
            return true;
        }

        /// <summary>Failures inside the window ending at <paramref name="now"/>.</summary>
        public int RecentFailures(double now)
        {
            int n = 0;
            foreach (double t in times) if (now - t <= WindowSeconds) n++;
            return n;
        }

        /// <summary>The system was installed again by the player: a fresh start.</summary>
        public void Reset()
        {
            times.Clear();
            Tripped = false;
        }
    }
}
