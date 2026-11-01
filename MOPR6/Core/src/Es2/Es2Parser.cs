// MOPR6
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Reader of Easy Save 2 files (the game's save format), for comparing saves written with and without the mod
// (requirement 3.2). It runs on the host, outside the game: ES2.LoadAll inside the game would create a Transform
// or collider in the scene for every such tag (ES2Type.GetOrCreate) and drops the names of what it read.
//
// The format follows the decompiled ES2.dll shipped with My Summer Car (ES2Writer.WriteHeader / Write* /
// WriteTerminator, ES2Reader.Next / ReadHeader / ReadAll):
//   file    = tag* [0]
//   tag     = 126, name (BinaryWriter string: 7-bit length + UTF-8), int32 length of the rest, header, payload, 123
//   header  = [127 = encrypted] [collection: 81 array | 82 dictionary | 83 list | 84 hash set | 85 queue | 86 stack]
//             255, int32 value type hash [, int32 key type hash: dictionaries]
//             (the reader also accepts the older form with one type key byte < 81 instead of 255 + hash)
//   payload = value                                     single
//           | int32 n, n values                         array
//           | byte, int32 n, n values                   list, hash set, queue, stack
//           | byte, byte, int32 n, n (key, value)       dictionary
// The type hash is ES2Type.GetHash(type.ToString()) (Es2Types). Values of the types the game uses are decoded;
// anything else (meshes, textures, colliders, the game's own ES2 user types, encrypted tags, a payload that does
// not end where the tag says it ends) becomes one Raw entry: length + MD5 of the payload bytes, so a difference is
// still found, just without detail.
// Shared code: compiled for net8.0 (tools, tests) and into the mod (net35, C# 9) - keep to .NET 3.5 APIs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MOPR6.Core.Es2
{
    public enum Es2ValueKind : byte
    {
        Bool,
        Integer,     // byte, sbyte, short, ushort, int, uint, long, ulong, char (as its code)
        Float,       // float, double, decimal
        String,
        Vector,      // Vector2/3/4, Rect, Bounds, Color32 (components in Numbers)
        Quaternion,
        Color,
        Raw          // not decoded: "len:<bytes> md5:<hex>"
    }

    /// <summary>One comparable value of a save file: a tag, an element of a collection or a part of a Transform.</summary>
    public sealed class Es2Entry
    {
        public string Key;
        public Es2ValueKind Kind;
        /// <summary>Canonical text (floats as round-trip "R", invariant culture).</summary>
        public string Text;
        /// <summary>Numeric components for Bool (0/1), Integer, Float, Vector, Quaternion, Color; null otherwise.</summary>
        public double[] Numbers;

        public override string ToString() => Key + " = " + Text;
    }

    public sealed class Es2ParseResult
    {
        public readonly List<Es2Entry> Entries = new List<Es2Entry>();
        /// <summary>Tags kept as Raw although they have a decodable header (why, per tag).</summary>
        public readonly List<string> Warnings = new List<string>();
        public int Tags;
        /// <summary>The data is not an ES2 file at all (e.g. README.txt); nothing was parsed.</summary>
        public bool NotEs2;
        /// <summary>The tag structure itself is broken from here on (the rest of the file is one Raw entry).</summary>
        public string Error;
        /// <summary>Type hashes that are not in <see cref="Es2Types"/> (tags kept raw), with the number of tags.</summary>
        public readonly Dictionary<int, int> UnknownHashes = new Dictionary<int, int>();

        internal void NoteUnknownHash(int hash)
        {
            int n;
            UnknownHashes.TryGetValue(hash, out n);
            UnknownHashes[hash] = n + 1;
        }
    }

    public static class Es2Parser
    {
        const byte TagMarker = 126;
        const byte Encrypted = 127;
        const byte Skip = 123;          // ES2Keys.Key._Terminator
        const byte CustomType = 255;    // a type hash follows
        const byte NativeArray = 81;
        const byte Dictionary = 82;
        const byte List = 83;
        const byte HashSet = 84;
        const byte Queue = 85;
        const byte Stack = 86;
        const byte Single = 124;   // ES2Keys.Key._Null: not a collection

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>True when the data starts like an ES2 file (first tag marker) or is empty.</summary>
        public static bool LooksLikeEs2(byte[] data) => data != null && (data.Length == 0 || data[0] == TagMarker);

        public static Es2ParseResult Parse(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            Es2ParseResult result = new Es2ParseResult();
            if (!LooksLikeEs2(data))
            {
                result.NotEs2 = true;
                return result;
            }

            using (MemoryStream ms = new MemoryStream(data, false))
            using (BinaryReader r = new BinaryReader(ms, new UTF8Encoding(false, false)))
            {
                long next = 0;
                while (next < data.Length)
                {
                    ms.Position = next;
                    byte marker = r.ReadByte();
                    if (marker == 0) break;   // terminator (ES2Reader.Next: 0 = end)
                    if (marker != TagMarker)
                    {
                        Broken(result, data, next, "byte " + marker.ToString(Inv) + " where a tag starts");
                        break;
                    }

                    string name;
                    long end;
                    try
                    {
                        name = r.ReadString();
                        int length = r.ReadInt32();
                        end = ms.Position + length;
                    }
                    catch (EndOfStreamException) // catch-ok: a truncated file is a parse result (Error + raw entry), not a fault
                    {
                        Broken(result, data, next, "file ends inside a tag name");
                        break;
                    }
                    if (end < ms.Position || end > data.Length)
                    {
                        Broken(result, data, next, "tag '" + name + "' claims to end outside the file");
                        break;
                    }

                    result.Tags++;
                    long start = ms.Position;
                    ParseTag(r, ms, name, start, end, data, result);
                    next = end;
                }
            }
            return result;
        }

        static void Broken(Es2ParseResult result, byte[] data, long from, string why)
        {
            result.Error = why + " at offset " + from.ToString(Inv);
            result.Entries.Add(RawEntry("<rest@" + from.ToString(Inv) + ">", data, from, data.Length));
        }

        // ------------------------------------------------------------------------------------------------
        // One tag
        // ------------------------------------------------------------------------------------------------

        struct Header
        {
            public byte Collection;
            public Es2Type Value, Key;
            public int ValueHash, KeyHash;
            public bool Encrypted;
        }

        static void ParseTag(BinaryReader r, MemoryStream ms, string name, long start, long end, byte[] data, Es2ParseResult result)
        {
            List<Es2Entry> entries = new List<Es2Entry>();
            string why;
            try
            {
                why = TryParseTag(r, name, end, entries, result);
                // ES2Writer ends every tag with a terminator (123) after the payload.
                while (why == null && ms.Position < end && data[ms.Position] == Skip) ms.Position++;
                if (why == null && ms.Position != end)
                    why = "payload ends at " + ms.Position.ToString(Inv) + ", the tag at " + end.ToString(Inv);
            }
            catch (EndOfStreamException) // catch-ok: undecodable tag -> kept raw with a warning in the result
            {
                why = "payload runs past the end of the file";
            }
            catch (ArgumentException ex) // catch-ok: undecodable tag -> kept raw with a warning in the result
            {
                // BinaryReader.ReadChar / ReadString on bytes that are not valid UTF-8.
                why = "invalid text: " + ex.Message;
            }
            catch (FormatException ex) // catch-ok: undecodable tag -> kept raw with a warning in the result
            {
                why = "invalid data: " + ex.Message;
            }

            if (why == null)
            {
                result.Entries.AddRange(entries);
                return;
            }
            if (why.Length > 0) result.Warnings.Add(name + ": " + why);
            result.Entries.Add(RawEntry(name, data, start, end));
        }

        /// <summary>Null = parsed into <paramref name="entries"/>; "" = kept raw on purpose (no warning); else why.</summary>
        static string TryParseTag(BinaryReader r, string name, long end, List<Es2Entry> entries, Es2ParseResult result)
        {
            Header h;
            string why = ReadHeader(r, out h);
            if (why != null) return why;
            if (h.Encrypted) return "encrypted";
            if (!Es2Types.IsDecodable(h.Value) || (h.Collection == Dictionary && !Es2Types.IsDecodable(h.Key)))
            {
                // Kept raw on purpose; the unknown hashes are listed so a new type in a save is noticed.
                if (h.Value == Es2Type.Unknown) result.NoteUnknownHash(h.ValueHash);
                if (h.Collection == Dictionary && h.Key == Es2Type.Unknown) result.NoteUnknownHash(h.KeyHash);
                return "";
            }

            switch (h.Collection)
            {
                case Single:
                    return ReadValue(r, h.Value, name, entries);

                case NativeArray:
                    return ReadSequence(r, h.Value, name, r.ReadInt32(), end, entries);

                case List:
                case HashSet:
                case Queue:
                case Stack:
                    r.ReadByte();
                    return ReadSequence(r, h.Value, name, r.ReadInt32(), end, entries);

                case Dictionary:
                    {
                        r.ReadByte();
                        r.ReadByte();
                        int n = r.ReadInt32();
                        if (n < 0 || n > end) return "dictionary count " + n.ToString(Inv);
                        entries.Add(Integer(name + ".count", n));
                        List<Es2Entry> key = new List<Es2Entry>(4);
                        for (int i = 0; i < n; i++)
                        {
                            key.Clear();
                            why = ReadValue(r, h.Key, "k", key);
                            if (why != null) return why;
                            string keyText = key.Count == 1 ? key[0].Text : Join(key);
                            why = ReadValue(r, h.Value, name + "{" + keyText + "}", entries);
                            if (why != null) return why;
                        }
                        return null;
                    }

                default:
                    return "collection " + h.Collection.ToString(Inv);
            }
        }

        static string ReadSequence(BinaryReader r, Es2Type type, string name, int n, long end, List<Es2Entry> entries)
        {
            if (n < 0 || n > end) return "count " + n.ToString(Inv);
            entries.Add(Integer(name + ".count", n));
            for (int i = 0; i < n; i++)
            {
                string why = ReadValue(r, type, name + "[" + i.ToString(Inv) + "]", entries);
                if (why != null) return why;
            }
            return null;
        }

        // ES2Reader.ReadHeader, byte for byte.
        static string ReadHeader(BinaryReader r, out Header h)
        {
            h = new Header { Collection = Single, Value = Es2Type.Unknown, Key = Es2Type.Unknown };
            while (true)
            {
                byte b = r.ReadByte();
                if (b == Encrypted) { h.Encrypted = true; continue; }
                if (b == Skip) continue;
                if (b == CustomType)
                {
                    // Dictionaries: in this branch the reader takes the value hash first, then the key hash.
                    h.ValueHash = r.ReadInt32();
                    h.Value = Es2Types.FromHash(h.ValueHash);
                    if (h.Collection == Dictionary)
                    {
                        h.KeyHash = r.ReadInt32();
                        h.Key = Es2Types.FromHash(h.KeyHash);
                    }
                    return null;
                }
                if (b < NativeArray)
                {
                    if (h.Collection == Dictionary) h.Key = Es2Types.FromKey(b);
                    else h.Value = Es2Types.FromKey(b);
                    return null;
                }
                if (b >= 101) return "header byte " + b.ToString(Inv);
                h.Collection = b;
                if (b == Dictionary)
                {
                    byte b2 = r.ReadByte();
                    if (b2 == CustomType)
                    {
                        h.ValueHash = r.ReadInt32();
                        h.KeyHash = r.ReadInt32();
                        h.Value = Es2Types.FromHash(h.ValueHash);
                        h.Key = Es2Types.FromHash(h.KeyHash);
                        return null;
                    }
                    h.Value = Es2Types.FromKey(b2);
                }
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Values (the Read_* methods of the game's ES2Reader)
        // ------------------------------------------------------------------------------------------------

        /// <summary>Null = read; "" = a type that is kept raw (meshes, textures...); else why.</summary>
        static string ReadValue(BinaryReader r, Es2Type type, string name, List<Es2Entry> entries)
        {
            switch (type)
            {
                case Es2Type.Byte: entries.Add(Integer(name, r.ReadByte())); return null;
                case Es2Type.SByte: entries.Add(Integer(name, r.ReadSByte())); return null;
                case Es2Type.String: entries.Add(Text(name, r.ReadString())); return null;
                case Es2Type.Int:
                case Es2Type.Enum: entries.Add(Integer(name, r.ReadInt32())); return null;
                case Es2Type.UInt: entries.Add(Integer(name, r.ReadUInt32())); return null;
                case Es2Type.Short: entries.Add(Integer(name, r.ReadInt16())); return null;
                case Es2Type.UShort: entries.Add(Integer(name, r.ReadUInt16())); return null;
                case Es2Type.Float: entries.Add(Float(name, r.ReadSingle())); return null;
                case Es2Type.Long: entries.Add(Integer(name, r.ReadInt64())); return null;
                case Es2Type.ULong: entries.Add(Integer(name, unchecked((long)r.ReadUInt64()))); return null;
                case Es2Type.Double: entries.Add(Double(name, r.ReadDouble())); return null;
                case Es2Type.Decimal: entries.Add(Decimal(name, r.ReadDecimal())); return null;
                case Es2Type.Bool: entries.Add(Bool(name, r.ReadBoolean())); return null;
                case Es2Type.Char: entries.Add(Integer(name, r.ReadChar())); return null;
                case Es2Type.Vector2: entries.Add(Floats(name, Es2ValueKind.Vector, r, 2)); return null;
                case Es2Type.Vector3: entries.Add(Floats(name, Es2ValueKind.Vector, r, 3)); return null;
                case Es2Type.Vector4: entries.Add(Floats(name, Es2ValueKind.Vector, r, 4)); return null;
                case Es2Type.Rect: entries.Add(Floats(name, Es2ValueKind.Vector, r, 4)); return null;
                case Es2Type.Bounds: entries.Add(Floats(name, Es2ValueKind.Vector, r, 6)); return null;
                case Es2Type.Matrix4x4: entries.Add(Floats(name, Es2ValueKind.Vector, r, 16)); return null;
                case Es2Type.Color: entries.Add(Floats(name, Es2ValueKind.Color, r, 4)); return null;
                case Es2Type.Quaternion: entries.Add(Floats(name, Es2ValueKind.Quaternion, r, 4)); return null;
                case Es2Type.Color32:
                    {
                        double[] c = { r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte() };
                        entries.Add(Numbers(name, Es2ValueKind.Color, c, false));
                        return null;
                    }
                case Es2Type.Transform: return ReadTransform(r, name, entries);
                default:
                    // Meshes, textures, colliders, materials, user types: no fixed layout we follow - kept raw.
                    return "";
            }
        }

        // ES2Reader.Read_Transform: a field count, then position, rotation, localScale, tag in this order.
        static string ReadTransform(BinaryReader r, string name, List<Es2Entry> entries)
        {
            int n = r.ReadByte();
            if (n > 4) return "transform with " + n.ToString(Inv) + " fields";
            if (n > 0) entries.Add(Floats(name + ".pos", Es2ValueKind.Vector, r, 3));
            if (n > 1) entries.Add(Floats(name + ".rot", Es2ValueKind.Quaternion, r, 4));
            if (n > 2) entries.Add(Floats(name + ".scale", Es2ValueKind.Vector, r, 3));
            if (n > 3) entries.Add(Text(name + ".tag", r.ReadString()));
            return null;
        }

        // ------------------------------------------------------------------------------------------------
        // Entry builders
        // ------------------------------------------------------------------------------------------------

        static Es2Entry Bool(string key, bool v) =>
            new Es2Entry { Key = key, Kind = Es2ValueKind.Bool, Text = v ? "true" : "false", Numbers = new double[] { v ? 1 : 0 } };

        static Es2Entry Integer(string key, long v) =>
            new Es2Entry { Key = key, Kind = Es2ValueKind.Integer, Text = v.ToString(Inv), Numbers = new double[] { v } };

        static Es2Entry Float(string key, float v) =>
            new Es2Entry { Key = key, Kind = Es2ValueKind.Float, Text = v.ToString("R", Inv), Numbers = new double[] { v } };

        static Es2Entry Double(string key, double v) =>
            new Es2Entry { Key = key, Kind = Es2ValueKind.Float, Text = v.ToString("R", Inv), Numbers = new double[] { v } };

        static Es2Entry Decimal(string key, decimal v) =>
            new Es2Entry { Key = key, Kind = Es2ValueKind.Float, Text = v.ToString(Inv), Numbers = new double[] { (double)v } };

        static Es2Entry Text(string key, string v) => new Es2Entry { Key = key, Kind = Es2ValueKind.String, Text = v ?? "" };

        static Es2Entry Floats(string key, Es2ValueKind kind, BinaryReader r, int count)
        {
            double[] v = new double[count];
            for (int i = 0; i < count; i++) v[i] = r.ReadSingle();
            return Numbers(key, kind, v, true);
        }

        static Es2Entry Numbers(string key, Es2ValueKind kind, double[] v, bool asFloat)
        {
            StringBuilder sb = new StringBuilder(v.Length * 10);
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(asFloat ? ((float)v[i]).ToString("R", Inv) : v[i].ToString(Inv));
            }
            return new Es2Entry { Key = key, Kind = kind, Text = sb.ToString(), Numbers = v };
        }

        static Es2Entry RawEntry(string key, byte[] data, long from, long to)
        {
            int len = (int)(to - from);
            string md5;
            using (MD5 h = MD5.Create())
            {
                byte[] hash = h.ComputeHash(data, (int)from, len);
                StringBuilder sb = new StringBuilder(32);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", Inv));
                md5 = sb.ToString();
            }
            return new Es2Entry { Key = key, Kind = Es2ValueKind.Raw, Text = "len:" + len.ToString(Inv) + " md5:" + md5 };
        }

        static string Join(List<Es2Entry> parts)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(parts[i].Text);
            }
            return sb.ToString();
        }
    }
}
