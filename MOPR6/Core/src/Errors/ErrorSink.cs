// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Every exception of the mod goes here (requirement 12.1, design §12): once per place and type with the object and
// the stack, after that only counted. Place = "system/method" given by the caller. The Unity side gives an
// IErrorOutput (log file + console); the report (12.3) lists every key with its count.
// Shared code: compiled for net8.0 (tests, tools) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Text;

namespace MOPR6.Core.Errors
{
    /// <summary>Where the first occurrence of an error is written (the mod log; in tests a list).</summary>
    public interface IErrorOutput
    {
        void Write(ErrorRecord first);
    }

    public sealed class ErrorRecord
    {
        public string Place;          // "engine.fsmFastPath/Update.Prefix"
        public string Type;           // exception type name
        public string Message;
        public string Context;        // object path, FSM and state... (may be null)
        public string Stack;
        public int Count;
        public double FirstTime, LastTime;   // seconds (the caller's clock)

        public string Key => Place + "|" + Type;

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Place).Append(": ").Append(Type).Append(": ").Append(Message);
            if (!string.IsNullOrEmpty(Context)) sb.Append(" [").Append(Context).Append(']');
            if (Count > 1) sb.Append(" (x").Append(Count).Append(')');
            return sb.ToString();
        }
    }

    public sealed class ErrorSink
    {
        readonly Dictionary<string, ErrorRecord> records = new Dictionary<string, ErrorRecord>(StringComparer.Ordinal);
        readonly List<ErrorRecord> order = new List<ErrorRecord>();
        readonly IErrorOutput output;

        /// <summary>Distinct keys kept at most (a broken loop must not grow memory without bound).</summary>
        public int MaxKeys = 500;
        public int Total { get; private set; }
        public int Dropped { get; private set; }

        public ErrorSink(IErrorOutput output) { this.output = output; }

        /// <summary>Records one error; returns true when it is the first of its place and type (and was written).</summary>
        public bool Report(string place, Exception ex, string context, double now)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));
            return Report(place, ex.GetType().Name, ex.Message, context, ex.StackTrace, now);
        }

        public bool Report(string place, string type, string message, string context, string stack, double now)
        {
            Total++;
            string key = (place ?? "?") + "|" + (type ?? "?");
            ErrorRecord r;
            if (records.TryGetValue(key, out r))
            {
                r.Count++;
                r.LastTime = now;
                return false;
            }
            if (records.Count >= MaxKeys)
            {
                Dropped++;
                return false;
            }
            r = new ErrorRecord
            {
                Place = place ?? "?", Type = type ?? "?", Message = message ?? "", Context = context, Stack = stack,
                Count = 1, FirstTime = now, LastTime = now
            };
            records[key] = r;
            order.Add(r);
            if (output != null) output.Write(r);
            return true;
        }

        /// <summary>Every distinct error in the order of the first occurrence.</summary>
        public IList<ErrorRecord> Records => order.AsReadOnly();

        public void Clear()
        {
            records.Clear();
            order.Clear();
            Total = 0;
            Dropped = 0;
        }
    }
}
