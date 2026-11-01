// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// When a part of the world may be switched off and when it must come back (requirement 10.2, design §9.2), as far as
// distance and time are concerned. The Unity side asks MayDeactivate for an awake part far from the player; null means
// "yes, if its cover allows". A part is switched off only after the player stayed farther than its distance for
// FarSeconds, not within CooldownSeconds after it last came back, and never while banned: more than MaxEventWakes wakes
// by events (anything but the player coming near) within WakeWindow seconds ban it for BanSeconds, doubled on every
// new ban up to MaxBanSeconds. It comes back when the player is nearer than WakeFactor x its distance (hysteresis).
// Shared code: compiled for net8.0 (tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.World
{
    public sealed class DormancyPolicy
    {
        public double FarSeconds = 10;
        public double CooldownSeconds = 30;
        public int MaxEventWakes = 3;
        public double WakeWindow = 300;
        public double BanSeconds = 300;
        public double MaxBanSeconds = 3600;
        public float WakeFactor = 0.9f;

        sealed class Record
        {
            public double FarSince = -1;
            public double WokeAt = double.NegativeInfinity;
            public double BannedUntil = -1;
            public double Ban;
            public string BanReason;
            public readonly Queue<double> Wakes = new Queue<double>();
        }

        readonly Dictionary<string, Record> records = new Dictionary<string, Record>(StringComparer.Ordinal);

        public int Bans { get; private set; }

        Record Get(string unit)
        {
            Record r;
            if (!records.TryGetValue(unit, out r)) records[unit] = r = new Record();
            return r;
        }

        /// <summary>
        /// An awake part, <paramref name="distance"/> m from the player, switched off from <paramref name="off"/> m: null
        /// when it may be switched off now (the cover decides the rest), else why not.
        /// </summary>
        public string MayDeactivate(string unit, float distance, float off, double now)
        {
            Record r = Get(unit);
            if (distance < off) { r.FarSince = -1; return "near"; }
            if (r.FarSince < 0) r.FarSince = now;
            if (r.BannedUntil > now) return "banned (" + r.BanReason + ")";
            if (now - r.FarSince < FarSeconds) return "far for less than " + FarSeconds + " s";
            if (now - r.WokeAt < CooldownSeconds) return "came back less than " + CooldownSeconds + " s ago";
            return null;
        }

        /// <summary>A switched-off part must come back: the player is nearer than WakeFactor x its distance.</summary>
        public bool MustWake(float distance, float off) => distance < off * WakeFactor;

        /// <summary>
        /// The part came back at <paramref name="now"/>. <paramref name="byEvent"/>: not because the player came near.
        /// True when this wake banned it.
        /// </summary>
        public bool Woken(string unit, double now, bool byEvent, string reason = null)
        {
            Record r = Get(unit);
            r.WokeAt = now;
            r.FarSince = -1;
            if (!byEvent) return false;
            r.Wakes.Enqueue(now);
            while (r.Wakes.Count > 0 && now - r.Wakes.Peek() > WakeWindow) r.Wakes.Dequeue();
            if (r.Wakes.Count <= MaxEventWakes) return false;
            Ban(unit, now, "restless" + (reason != null ? ", last " + reason : ""));
            return true;
        }

        /// <summary>The part may not be switched off for a while (restless, or the game took it over).</summary>
        public void Ban(string unit, double now, string reason)
        {
            Record r = Get(unit);
            r.Ban = r.Ban <= 0 ? BanSeconds : Math.Min(MaxBanSeconds, r.Ban * 2);
            r.BannedUntil = now + r.Ban;
            r.BanReason = reason;
            r.Wakes.Clear();
            Bans++;
        }

        public bool IsBanned(string unit, double now)
        {
            Record r;
            return records.TryGetValue(unit, out r) && r.BannedUntil > now;
        }

        /// <summary>Current ban length in seconds (0 = never banned).</summary>
        public double BanLength(string unit)
        {
            Record r;
            return records.TryGetValue(unit, out r) ? r.Ban : 0;
        }

        public void Clear()
        {
            records.Clear();
            Bans = 0;
        }
    }
}
