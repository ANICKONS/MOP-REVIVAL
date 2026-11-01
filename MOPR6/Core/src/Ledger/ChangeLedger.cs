// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The change ledger (requirement 5, design §3). Every change the mod makes to the game goes through Set: the ledger
// reads and keeps the original value the first time, writes the new one and remembers who owns the property.
//   * one owner per property: a second owner is refused (5.2; in a debug build an exception);
//   * Release / RestoreAll write the original back (5.3) - unless the game or a mod changed the property after us:
//     then their value stays and the entry is reported as External (5.4);
//   * the save barrier freezes the ledger: no new changes until it is thawed (6.1);
//   * RestoreAll returns properties in reverse order of their first change, objects (GoActive) parents first, then
//     the FSMs' restart flags (an object goes on before its paused FSMs may restart), the mod's own objects last
//     (they are destroyed).
// IPropertyAccess is the only way to the game: the Unity implementation in the mod, fakes in the tests.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Ledger
{
    public interface IPropertyAccess
    {
        /// <summary>Current value; false when the object no longer exists.</summary>
        bool TryGet(PropKey key, out PropValue value);
        /// <summary>Writes the value; false when the object no longer exists.</summary>
        bool TrySet(PropKey key, PropValue value);
        /// <summary>Depth in the hierarchy (0 = root), to switch parents on before children.</summary>
        int Depth(int target);
        /// <summary>Path of the object for logs.</summary>
        string Describe(int target);
    }

    public enum SetResult : byte
    {
        Ok,
        DeniedOwner,     // another owner holds this property
        TargetGone,
        Frozen           // the save barrier is up
    }

    public enum EntryState : byte
    {
        Held,
        External         // somebody else changed it after us: we will not write it back
    }

    public enum RestoreOutcome : byte
    {
        Restored,
        External,        // left as the game / a mod set it
        TargetGone,
        Failed           // the write did not stick (value after write != original)
    }

    public sealed class LedgerEntry
    {
        public PropKey Key;
        public string Owner;
        public PropValue Original;
        public PropValue Applied;
        public EntryState State;
        public int Seq;
        public PropValue ExternalValue;
        public string ExternalSource;
    }

    public struct RestoreRecord
    {
        public PropKey Key;
        public string Owner;
        public RestoreOutcome Outcome;
        public PropValue Original, Found;
        public override string ToString() => Key + " (" + Owner + "): " + Outcome + (Outcome == RestoreOutcome.Restored ? "" : ", found " + Found + ", original " + Original);
    }

    public sealed class ConflictException : Exception
    {
        public ConflictException(string message) : base(message) { }
    }

    public sealed class ChangeLedger
    {
        readonly IPropertyAccess access;
        readonly Dictionary<long, LedgerEntry> entries = new Dictionary<long, LedgerEntry>();
        readonly Action<string> log;
        int seq;

        /// <summary>Debug builds: a second owner throws instead of being logged (requirement 5.2).</summary>
        public bool ThrowOnConflict;
        public bool Frozen { get; private set; }
        public int Conflicts { get; private set; }
        public int ExternalChanges { get; private set; }

        /// <summary>An owner's property was changed by somebody else (the arbiter drops its reasons for it).</summary>
        public event Action<LedgerEntry> External;

        public ChangeLedger(IPropertyAccess access, Action<string> log)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.log = log;
        }

        public int Count => entries.Count;

        public LedgerEntry Find(PropKey key)
        {
            LedgerEntry e;
            return entries.TryGetValue(key.Packed, out e) ? e : null;
        }

        public IEnumerable<LedgerEntry> Entries => entries.Values;

        public SetResult Set(string owner, PropKey key, PropValue value)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("owner", nameof(owner));
            if (Frozen) return SetResult.Frozen;

            LedgerEntry e;
            if (entries.TryGetValue(key.Packed, out e))
            {
                if (e.Owner != owner)
                {
                    Conflicts++;
                    string msg = "ledger: " + owner + " wants " + Describe(key) + ", owned by " + e.Owner;
                    if (ThrowOnConflict) throw new ConflictException(msg);
                    Log(msg);
                    return SetResult.DeniedOwner;
                }
                if (e.State == EntryState.External) return SetResult.Ok;   // left to its new writer until released
                if (!access.TrySet(key, value)) return Gone(e);
                e.Applied = value;
                return SetResult.Ok;
            }

            PropValue original;
            if (!access.TryGet(key, out original)) return SetResult.TargetGone;
            if (!access.TrySet(key, value)) return SetResult.TargetGone;
            entries[key.Packed] = new LedgerEntry { Key = key, Owner = owner, Original = original, Applied = value, Seq = ++seq };
            return SetResult.Ok;
        }

        SetResult Gone(LedgerEntry e)
        {
            entries.Remove(e.Key.Packed);
            return SetResult.TargetGone;
        }

        /// <summary>The owner no longer needs the property: the original goes back (if nobody else changed it).</summary>
        public RestoreOutcome Release(string owner, PropKey key)
        {
            LedgerEntry e;
            if (!entries.TryGetValue(key.Packed, out e)) return RestoreOutcome.Restored;
            if (e.Owner != owner)
            {
                Conflicts++;
                string msg = "ledger: " + owner + " releases " + Describe(key) + ", owned by " + e.Owner;
                if (ThrowOnConflict) throw new ConflictException(msg);
                Log(msg);
                return RestoreOutcome.Failed;
            }
            entries.Remove(key.Packed);
            return Restore(e).Outcome;
        }

        public int ReleaseAll(string owner)
        {
            List<LedgerEntry> mine = new List<LedgerEntry>();
            foreach (LedgerEntry e in entries.Values) if (e.Owner == owner) mine.Add(e);
            RestoreOrdered(mine);
            return mine.Count;
        }

        /// <summary>Everything back (master switch, save barrier, requirement 5.3). The ledger is empty afterwards.</summary>
        public List<RestoreRecord> RestoreAll()
        {
            List<LedgerEntry> all = new List<LedgerEntry>(entries.Values);
            return RestoreOrdered(all);
        }

        List<RestoreRecord> RestoreOrdered(List<LedgerEntry> list)
        {
            foreach (LedgerEntry e in list) entries.Remove(e.Key.Packed);
            // 1. properties, newest first; 2. objects switched back on, parents before children; 3. the FSMs' restart
            // flags, newest first: an object switched off with its FSMs paused (RestartGuard, design §9.2) must be on
            // again before RestartOnEnable comes back, or its FSMs start over in OnEnable; 4. the mod's objects.
            List<LedgerEntry> props = new List<LedgerEntry>(), active = new List<LedgerEntry>(), restart = new List<LedgerEntry>(),
                              created = new List<LedgerEntry>();
            foreach (LedgerEntry e in list)
            {
                if (e.Key.Kind == PropKind.CreatedObject) created.Add(e);
                else if (e.Key.Kind == PropKind.GoActive) active.Add(e);
                else if (e.Key.Kind == PropKind.FsmRestartOnEnable) restart.Add(e);
                else props.Add(e);
            }
            props.Sort((a, b) => b.Seq.CompareTo(a.Seq));
            active.Sort((a, b) =>
            {
                int c = access.Depth(a.Key.Target).CompareTo(access.Depth(b.Key.Target));
                return c != 0 ? c : b.Seq.CompareTo(a.Seq);
            });
            restart.Sort((a, b) => b.Seq.CompareTo(a.Seq));
            created.Sort((a, b) => b.Seq.CompareTo(a.Seq));

            List<RestoreRecord> records = new List<RestoreRecord>(list.Count);
            foreach (LedgerEntry e in props) records.Add(Restore(e));
            foreach (LedgerEntry e in active) records.Add(Restore(e));
            foreach (LedgerEntry e in restart) records.Add(Restore(e));
            foreach (LedgerEntry e in created) records.Add(Restore(e));
            return records;
        }

        RestoreRecord Restore(LedgerEntry e)
        {
            RestoreRecord r = new RestoreRecord { Key = e.Key, Owner = e.Owner, Original = e.Original };
            PropValue now;
            if (!access.TryGet(e.Key, out now)) { r.Outcome = RestoreOutcome.TargetGone; return r; }
            r.Found = now;
            if (e.State == EntryState.External || now != e.Applied)
            {
                // Somebody else wrote it after us (5.4): their value stays.
                if (e.State != EntryState.External) NoteExternal(e, now, "found at restore");
                r.Outcome = RestoreOutcome.External;
                return r;
            }
            if (!access.TrySet(e.Key, e.Original)) { r.Outcome = RestoreOutcome.TargetGone; return r; }
            PropValue after;
            r.Outcome = access.TryGet(e.Key, out after) && after == e.Original ? RestoreOutcome.Restored : RestoreOutcome.Failed;
            if (r.Outcome == RestoreOutcome.Failed) { r.Found = after; Log("ledger: " + Describe(e.Key) + " did not go back to " + e.Original + " (now " + after + ")"); }
            return r;
        }

        /// <summary>
        /// A known writer of the game (an action hook) is about to change <paramref name="key"/> to
        /// <paramref name="value"/>: if we hold it, it is External from now on (even when the value equals ours).
        /// </summary>
        public void NoteExternalWrite(PropKey key, PropValue value, string source)
        {
            LedgerEntry e;
            if (!entries.TryGetValue(key.Packed, out e) || e.State == EntryState.External) return;
            NoteExternal(e, value, source);
        }

        void NoteExternal(LedgerEntry e, PropValue value, string source)
        {
            e.State = EntryState.External;
            e.ExternalValue = value;
            e.ExternalSource = source;
            ExternalChanges++;
            Log("ledger: " + Describe(e.Key) + " changed by " + source + " to " + value + " (" + e.Owner + " had " + e.Applied + "); it keeps that value");
            if (External != null) External(e);
        }

        /// <summary>
        /// Background check (design §3, mechanism 3): up to <paramref name="budget"/> held entries whose current value
        /// is not what we wrote become External. Returns how many drifted.
        /// </summary>
        public int CheckDrift(int budget, ref int cursor)
        {
            if (entries.Count == 0 || budget <= 0) return 0;
            List<LedgerEntry> list = new List<LedgerEntry>(entries.Values);
            int drifted = 0, n = Math.Min(budget, list.Count);
            for (int i = 0; i < n; i++)
            {
                cursor = (cursor + 1) % list.Count;
                LedgerEntry e = list[cursor];
                if (e.State == EntryState.External) continue;
                PropValue now;
                if (!access.TryGet(e.Key, out now)) { entries.Remove(e.Key.Packed); continue; }
                if (now == e.Applied) continue;
                NoteExternal(e, now, "an unknown writer");
                drifted++;
            }
            return drifted;
        }

        /// <summary>Held entries whose value is not what we wrote (Verify of design §2/§4).</summary>
        public List<LedgerEntry> Verify()
        {
            List<LedgerEntry> bad = new List<LedgerEntry>();
            foreach (LedgerEntry e in entries.Values)
            {
                if (e.State == EntryState.External) continue;
                PropValue now;
                if (!access.TryGet(e.Key, out now) || now != e.Applied) bad.Add(e);
            }
            return bad;
        }

        public void Freeze() { Frozen = true; }
        public void Thaw() { Frozen = false; }

        string Describe(PropKey key) => access.Describe(key.Target) + "." + key.Kind;

        void Log(string message)
        {
            if (log != null) log(message);
        }
    }
}
