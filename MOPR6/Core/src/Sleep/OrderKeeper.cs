// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The place of an FSM in PlayMaker's list of enabled FSMs (requirement 8.4, design §7.4). In the game the list is
// ordered by the time of each FSM's last OnEnable: OnEnable appends, OnDisable removes without reordering. Every FSM
// gets a stamp - at install its index in the list, afterwards a counter value from every OnEnable that is not one of
// MOPR6's wakes - so the list stays sorted by stamp. A sleeping FSM keeps its stamp: when it wakes, OnEnable appends
// it, and it is moved to LowerBound(stamp), exactly where it would be had it never left the list.
// Shared code: compiled for net8.0 (tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;

namespace MOPR6.Core.Sleep
{
    public sealed class OrderKeeper<T> where T : class
    {
        readonly Dictionary<T, long> stamps;
        long counter;

        public OrderKeeper(IEqualityComparer<T> comparer = null)
        {
            stamps = new Dictionary<T, long>(comparer ?? EqualityComparer<T>.Default);
        }

        public int Count => stamps.Count;

        /// <summary>Install: the list as it is now gets stamps in its own order.</summary>
        public void Reset(IList<T> list)
        {
            stamps.Clear();
            counter = 0;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null) stamps[list[i]] = ++counter;
        }

        /// <summary>A normal OnEnable (not a wake): the FSM went to the end of the list.</summary>
        public void Enabled(T item) => stamps[item] = ++counter;

        public bool TryGetStamp(T item, out long stamp) => stamps.TryGetValue(item, out stamp);

        public void Forget(T item) => stamps.Remove(item);

        /// <summary>
        /// A woken <paramref name="item"/> was just appended (last element of <paramref name="list"/>): moves it to its
        /// place. Returns its new index, or -1 when it has no stamp or is not the last element (nothing moved).
        /// </summary>
        public int Restore(List<T> list, T item)
        {
            long stamp;
            int last = list.Count - 1;
            if (last < 0 || !ReferenceEquals(list[last], item) || !stamps.TryGetValue(item, out stamp)) return -1;
            int index = LowerBound(list, last, stamp);
            if (index == last) return last;
            list.RemoveAt(last);
            list.Insert(index, item);
            return index;
        }

        /// <summary>First index in [0, count) whose stamp is greater than <paramref name="stamp"/> (items without a stamp count as newest).</summary>
        public int LowerBound(IList<T> list, int count, long stamp)
        {
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                long s;
                if (!stamps.TryGetValue(list[mid], out s)) s = long.MaxValue;
                if (s < stamp) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Places where the list breaks stamp order: items with a smaller stamp than the item before them (0 = the list
        /// is as the game would have it). One item in a wrong place counts once, not once per item it jumped over.
        /// </summary>
        public int Violations(IList<T> list)
        {
            int n = 0;
            long prev = long.MinValue;
            for (int i = 0; i < list.Count; i++)
            {
                long s;
                if (list[i] == null || !stamps.TryGetValue(list[i], out s)) continue;
                if (s < prev) n++;
                prev = s;
            }
            return n;
        }

        /// <summary>Drops items the predicate says are gone (destroyed objects).</summary>
        public int Purge(Predicate<T> gone)
        {
            List<T> dead = new List<T>();
            foreach (T k in stamps.Keys) if (gone(k)) dead.Add(k);
            foreach (T k in dead) stamps.Remove(k);
            return dead.Count;
        }
    }
}
