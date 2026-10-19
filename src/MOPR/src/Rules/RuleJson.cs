// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// The JSON rule format (5.1). Every key is optional; keys are case-insensitive; // and /* */ comments are allowed.
//
// {
//   "mod": "BetterMSC",                       MSCLoader ID of the mod (without it, a user file always applies)
//   "replaceBundled": true,                   user files: don't use MOP's built-in rules for that mod
//   "exclude":      [ "TRAFFIC" ],            MOP never touches these objects
//   "keepActive":   [ "COMPUTER" ],           MOP never switches these off (may still hide or freeze them)
//   "keepActiveIn": { "YARD": [ "Handle" ] }, children that stay on while their place / vehicle is off
//   "manage": { "objects": [], "renderers": [], "items": [], "vehicles": [], "vehiclePhysics": [] },
//   "reparent":     { "BUS": null },          new parent at load (null = scene root)
//   "noLod":        [ "GAZ24(1420kg)" ],      no simplified far-away copy
//   "sectors": [ { "position": [x,y,z], "size": [x,y,z], "rotation": [x,y,z], "keepVisible": [], "drawDistance": 300 } ],
//   "satsuma": "keepRenderers" | "exclude",
//   "keepEmptyBottles": true,                 MOP keeps empty bottles even if "Remove empty bottles" is on
//   "skipFixes": [ "furyCollider" ],
//   "allVehiclesPhysicsOnly": true,           user files without "mod" only
//   "noLods": true                            user files without "mod" only
// }
//
// A wrong key or value is reported with its line and skipped; the rest of the file still applies. Only a JSON
// syntax error (a missing comma or quote) makes the whole file unreadable - the message says where it is.
//
// Read with Newtonsoft's streaming JsonTextReader into a small tree of our own. Not LINQ to JSON (JObject):
// the game's Mono runtime has no System.ComponentModel.INotifyPropertyChanging, so JObject cannot even load there.
//
// Pure C#: no Unity, no MSCLoader.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace MOP.Rules
{
    static class RuleJson
    {
        public const string Extension = ".json";

        static readonly string[] KnownKeys =
        {
            "mod", "replaceBundled", "exclude", "keepActive", "keepActiveIn", "manage", "reparent", "noLod", "sectors",
            "satsuma", "keepEmptyBottles", "skipFixes", "allVehiclesPhysicsOnly", "noLods",
        };

        static readonly string[] ManageKeys = { "objects", "renderers", "items", "vehicles", "vehiclePhysics" };

        // ===== A minimal JSON tree with line numbers =====================================================

        enum Kind { Object, Array, String, Number, Bool, Null }

        sealed class Node
        {
            public Kind Kind;
            public int Line;
            public object Value;                                // String, Number (double), Bool
            public List<Prop> Props;                            // Object, in file order
            public List<Node> Items;                            // Array

            public string Text => Value as string;
            public bool IsNumber => Kind == Kind.Number;
            public float Float => System.Convert.ToSingle(Value, CultureInfo.InvariantCulture);
        }

        sealed class Prop
        {
            public string Name;
            public int Line;
            public Node Value;
        }

        sealed class JsonSyntaxError : Exception
        {
            public readonly int Line;
            public JsonSyntaxError(int line, string message) : base(message) { Line = line; }
        }

        static bool Next(JsonTextReader r)
        {
            while (r.Read())
                if (r.TokenType != JsonToken.Comment) return true;
            return false;
        }

        /// <summary>The value at the reader's current token. Duplicate keys are reported (the first one counts).</summary>
        static Node ReadValue(JsonTextReader r, RuleSet set)
        {
            Node n = new Node { Line = r.LineNumber };
            switch (r.TokenType)
            {
                case JsonToken.StartObject:
                    n.Kind = Kind.Object;
                    n.Props = new List<Prop>();
                    while (true)
                    {
                        if (!Next(r)) throw new JsonSyntaxError(r.LineNumber, "the file ends before the closing }");
                        if (r.TokenType == JsonToken.EndObject) break;
                        if (r.TokenType != JsonToken.PropertyName) throw new JsonSyntaxError(r.LineNumber, "expected a \"key\"");
                        Prop p = new Prop { Name = (string)r.Value, Line = r.LineNumber };
                        if (!Next(r)) throw new JsonSyntaxError(r.LineNumber, "the file ends after \"" + p.Name + "\":");
                        p.Value = ReadValue(r, set);
                        Prop earlier = n.Props.Find(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                        if (earlier != null)
                            set.Error(p.Line, $"\"{p.Name}\" appears twice (first on line {earlier.Line}); only the first one is used - merge them");
                        else
                            n.Props.Add(p);
                    }
                    break;
                case JsonToken.StartArray:
                    n.Kind = Kind.Array;
                    n.Items = new List<Node>();
                    while (true)
                    {
                        if (!Next(r)) throw new JsonSyntaxError(r.LineNumber, "the file ends before the closing ]");
                        if (r.TokenType == JsonToken.EndArray) break;
                        n.Items.Add(ReadValue(r, set));
                    }
                    break;
                case JsonToken.String:
                    n.Kind = Kind.String;
                    n.Value = (string)r.Value;
                    break;
                case JsonToken.Integer:
                case JsonToken.Float:
                    n.Kind = Kind.Number;
                    n.Value = System.Convert.ToDouble(r.Value, CultureInfo.InvariantCulture);
                    break;
                case JsonToken.Boolean:
                    n.Kind = Kind.Bool;
                    n.Value = (bool)r.Value;
                    break;
                case JsonToken.Null:
                case JsonToken.Undefined:
                    n.Kind = Kind.Null;
                    break;
                default:
                    throw new JsonSyntaxError(r.LineNumber, "unexpected " + r.TokenType);
            }
            return n;
        }

        // ===== Reading ===================================================================================

        public static RuleSet Read(string text, string name, RuleSource source)
        {
            RuleSet set = new RuleSet { Name = name, Source = source };

            Node root;
            try
            {
                using (JsonTextReader reader = new JsonTextReader(new StringReader(text ?? "")))
                {
                    // Names are names: never turn "2026-10-19" into a date.
                    reader.DateParseHandling = DateParseHandling.None;
                    if (!Next(reader))
                    {
                        set.Unreadable = true;
                        set.Error(0, "the file is empty; it needs one { ... } object");
                        return set;
                    }
                    root = ReadValue(reader, set);
                    if (root.Kind != Kind.Object)
                    {
                        set.Unreadable = true;
                        set.Error(root.Line, "the file must be one { ... } object");
                        return set;
                    }
                    if (Next(reader))
                    {
                        set.Unreadable = true;
                        set.Error(reader.LineNumber, "unexpected text after the closing }");
                        return set;
                    }
                }
            }
            catch (JsonSyntaxError ex)
            {
                set.Unreadable = true;
                set.Error(ex.Line, "JSON syntax error: " + ex.Message);
                return set;
            }
            catch (JsonReaderException ex)
            {
                set.Unreadable = true;
                string hint = ex.Message.StartsWith("After parsing a value an unexpected character", StringComparison.Ordinal)
                    ? " (probably a missing comma at the end of the line before)" : "";
                set.Error(ex.LineNumber, "JSON syntax error at column " + ex.LinePosition + ": " + FirstSentence(ex.Message) + hint);
                return set;
            }
            catch (Exception ex)
            {
                set.Unreadable = true;
                set.Error(0, "could not read the file: " + ex.Message);
                return set;
            }

            foreach (Prop p in root.Props)
            {
                string key = Canonical(p.Name, KnownKeys);
                int line = p.Line;
                try
                {
                    switch (key)
                    {
                        case "mod":
                            set.Mod = String(set, p.Value, "mod");
                            break;
                        case "replaceBundled":
                            if (Bool(set, p.Value, key))
                            {
                                if (source == RuleSource.Bundled) set.Error(line, "replaceBundled only works in your own files");
                                else set.ReplaceBundled = true;
                            }
                            break;
                        case "exclude":
                            Names(set, p.Value, key, set.Exclude);
                            break;
                        case "keepActive":
                            Names(set, p.Value, key, set.KeepActive);
                            break;
                        case "noLod":
                            Names(set, p.Value, key, set.NoLod);
                            break;
                        case "keepActiveIn":
                            KeepActiveIn(set, p.Value);
                            break;
                        case "manage":
                            Manage(set, p.Value);
                            break;
                        case "reparent":
                            Reparent(set, p.Value);
                            break;
                        case "sectors":
                            Sectors(set, p.Value);
                            break;
                        case "satsuma":
                            string mode = String(set, p.Value, key);
                            if (mode == null) break;
                            if (mode.Equals("keepRenderers", StringComparison.OrdinalIgnoreCase)) set.Satsuma = SatsumaRule.KeepRenderers;
                            else if (mode.Equals("exclude", StringComparison.OrdinalIgnoreCase)) set.Satsuma = SatsumaRule.Exclude;
                            else set.Error(p.Value.Line, "satsuma must be \"keepRenderers\" or \"exclude\"");
                            break;
                        case "keepEmptyBottles":
                            set.KeepEmptyBottles = Bool(set, p.Value, key);
                            break;
                        case "skipFixes":
                            List<string> fixes = new List<string>();
                            Names(set, p.Value, key, fixes);
                            foreach (string fix in fixes)
                            {
                                if (fix.Equals("furyCollider", StringComparison.OrdinalIgnoreCase)) set.SkipFuryColliderFix = true;
                                else set.Error(p.Value.Line, $"unknown fix \"{fix}\" (known: furyCollider)");
                            }
                            break;
                        case "allVehiclesPhysicsOnly":
                        case "noLods":
                            bool on = Bool(set, p.Value, key);
                            if (!on) break;
                            if (source == RuleSource.Bundled || HasKey(root, "mod"))
                            {
                                set.Error(line, key + " changes the whole game and only works in your own files without \"mod\"");
                                break;
                            }
                            if (key == "noLods") set.NoLods = true; else set.AllVehiclesPhysicsOnly = true;
                            break;
                        default:
                            set.Error(line, $"unknown key \"{p.Name}\"" + Suggest(p.Name, KnownKeys));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    set.Error(line, $"\"{p.Name}\": {ex.Message}");
                }
            }

            if (source == RuleSource.Bundled && string.IsNullOrEmpty(set.Mod))
                set.Error(0, "a built-in rule file needs \"mod\"");

            return set;
        }

        static void KeepActiveIn(RuleSet set, Node value)
        {
            if (value.Kind != Kind.Object)
            {
                set.Error(value.Line, "keepActiveIn must be { \"parent\": [ \"child\", ... ] }");
                return;
            }
            foreach (Prop p in value.Props)
            {
                string parent = CleanName(set, p.Name, p.Line);
                if (parent == null) continue;
                List<string> children = new List<string>();
                Names(set, p.Value, "keepActiveIn." + p.Name, children);
                foreach (string child in children)
                {
                    bool known = false;
                    for (int i = 0; i < set.KeepActiveIn.Count && !known; i++)
                        known = set.KeepActiveIn[i].Parent == parent && set.KeepActiveIn[i].Name == child;
                    if (!known) set.KeepActiveIn.Add(new ChildRule { Parent = parent, Name = child });
                }
            }
        }

        static void Manage(RuleSet set, Node value)
        {
            if (value.Kind != Kind.Object)
            {
                set.Error(value.Line, "manage must be { \"items\": [ ... ], \"vehicles\": [ ... ], ... }");
                return;
            }
            foreach (Prop p in value.Props)
            {
                ManageKind kind;
                switch (Canonical(p.Name, ManageKeys))
                {
                    case "objects": kind = ManageKind.Objects; break;
                    case "renderers": kind = ManageKind.Renderers; break;
                    case "items": kind = ManageKind.Items; break;
                    case "vehicles": kind = ManageKind.Vehicles; break;
                    case "vehiclePhysics": kind = ManageKind.VehiclePhysics; break;
                    default:
                        set.Error(p.Line, $"unknown manage group \"{p.Name}\"" + Suggest(p.Name, ManageKeys));
                        continue;
                }
                List<string> names = new List<string>();
                Names(set, p.Value, "manage." + p.Name, names);
                foreach (string n in names)
                {
                    bool known = false;
                    for (int i = 0; i < set.Manage.Count && !known; i++)
                        known = set.Manage[i].Name == n && set.Manage[i].Kind == kind;
                    if (!known) set.Manage.Add(new ManageRule { Name = n, Kind = kind });
                }
            }
        }

        static void Reparent(RuleSet set, Node value)
        {
            if (value.Kind != Kind.Object)
            {
                set.Error(value.Line, "reparent must be { \"object\": \"new parent\" or null }");
                return;
            }
            foreach (Prop p in value.Props)
            {
                string name = CleanName(set, p.Name, p.Line);
                if (name == null) continue;
                string parent = null;
                if (p.Value.Kind == Kind.String)
                {
                    string s = p.Value.Text.Trim();
                    if (s.Length > 0 && !s.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        parent = CleanName(set, s, p.Value.Line);
                        if (parent == null) continue;
                    }
                }
                else if (p.Value.Kind != Kind.Null)
                {
                    set.Error(p.Value.Line, $"the new parent of \"{name}\" must be a name or null");
                    continue;
                }
                set.Reparent.Add(new ReparentRule { Name = name, Parent = parent });
            }
        }

        static void Sectors(RuleSet set, Node value)
        {
            if (value.Kind != Kind.Array)
            {
                set.Error(value.Line, "sectors must be a list: [ { \"position\": [x, y, z], \"size\": [x, y, z] }, ... ]");
                return;
            }
            foreach (Node item in value.Items)
            {
                if (item.Kind != Kind.Object)
                {
                    set.Error(item.Line, "every sector must be { \"position\": [x, y, z], \"size\": [x, y, z], ... }");
                    continue;
                }
                SectorRule sector = new SectorRule();
                bool ok = true, hasPosition = false, hasSize = false;
                foreach (Prop p in item.Props)
                {
                    switch (Canonical(p.Name, "position", "size", "rotation", "keepVisible", "drawDistance"))
                    {
                        case "position": ok &= Vector(set, p.Value, "position", sector.Position); hasPosition = true; break;
                        case "size": ok &= Vector(set, p.Value, "size", sector.Size); hasSize = true; break;
                        case "rotation": ok &= Vector(set, p.Value, "rotation", sector.Rotation); break;
                        case "keepVisible": Names(set, p.Value, "keepVisible", sector.KeepVisible); break;
                        case "drawDistance":
                            if (p.Value.IsNumber)
                            {
                                float d = p.Value.Float;
                                if (d >= 1 && d <= 100000) sector.DrawDistance = (int)Math.Round(d);
                                else { set.Error(p.Value.Line, "drawDistance must be between 1 and 100000"); ok = false; }
                            }
                            else { set.Error(p.Value.Line, "drawDistance must be a number"); ok = false; }
                            break;
                        default:
                            set.Error(p.Line, $"unknown sector key \"{p.Name}\"");
                            break;
                    }
                }
                if (!hasPosition || !hasSize)
                {
                    set.Error(item.Line, "a sector needs \"position\" and \"size\"");
                    ok = false;
                }
                if (ok) set.Sectors.Add(sector);
            }
        }

        static bool Vector(RuleSet set, Node value, string key, float[] into)
        {
            if (value.Kind == Kind.Array && value.Items.Count == 3)
            {
                bool numbers = true;
                for (int i = 0; i < 3; i++)
                {
                    if (value.Items[i].IsNumber) into[i] = value.Items[i].Float;
                    else numbers = false;
                }
                if (numbers) return true;
            }
            set.Error(value.Line, key + " must be three numbers: [x, y, z]");
            return false;
        }

        /// <summary>A list of names (a single "name" is accepted too). Bad entries are reported and skipped.</summary>
        static void Names(RuleSet set, Node value, string key, List<string> into)
        {
            if (value.Kind == Kind.String)
            {
                string one = CleanName(set, value.Text, value.Line);
                if (one != null) RuleSet.AddUnique(into, one);
                return;
            }
            if (value.Kind != Kind.Array)
            {
                set.Error(value.Line, key + " must be a list of names: [ \"name\", ... ]");
                return;
            }
            foreach (Node item in value.Items)
            {
                if (item.Kind != Kind.String)
                {
                    set.Error(item.Line, key + ": every entry must be a name in quotes");
                    continue;
                }
                string name = CleanName(set, item.Text, item.Line);
                if (name != null) RuleSet.AddUnique(into, name);
            }
        }

        static string CleanName(RuleSet set, string name, int line)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
            {
                set.Error(line, "an empty name");
                return null;
            }
            if (RuleSet.IsProtected(name))
            {
                set.Error(line, $"\"{name}\" is used by MOP itself and cannot have a rule");
                return null;
            }
            return name;
        }

        static string String(RuleSet set, Node value, string key)
        {
            if (value.Kind == Kind.String)
            {
                string s = value.Text.Trim();
                if (s.Length > 0) return s;
            }
            if (value.Kind == Kind.Null) return null;
            set.Error(value.Line, key + " must be a text in quotes");
            return null;
        }

        static bool Bool(RuleSet set, Node value, string key)
        {
            if (value.Kind == Kind.Bool) return (bool)value.Value;
            set.Error(value.Line, key + " must be true or false");
            return false;
        }

        static bool HasKey(Node root, string key)
        {
            foreach (Prop p in root.Props)
                if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The known spelling of a key, matched case-insensitively; null if unknown.</summary>
        static string Canonical(string key, params string[] known)
        {
            for (int i = 0; i < known.Length; i++)
                if (known[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return known[i];
            return null;
        }

        /// <summary>" - did you mean "keepActive"?" for a key one or two letters off.</summary>
        static string Suggest(string key, string[] known)
        {
            string best = null;
            int bestDistance = 3;
            foreach (string k in known)
            {
                int d = Distance(key.ToLowerInvariant(), k.ToLowerInvariant());
                if (d < bestDistance) { bestDistance = d; best = k; }
            }
            return best != null ? $" - did you mean \"{best}\"?" : "";
        }

        static int Distance(string a, string b)
        {
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }

        static string FirstSentence(string message)
        {
            int path = message.IndexOf(" Path '", StringComparison.Ordinal);
            if (path > 0) message = message.Substring(0, path);
            return message.Trim();
        }

        // ===== Writing ===================================================================================

        /// <summary>The set as a rule file. Only keys with something in them are written, always in the same order.</summary>
        public static string Write(RuleSet set, IList<string> headerComments = null)
        {
            StringBuilder sb = new StringBuilder();
            if (headerComments != null)
                foreach (string c in headerComments)
                    sb.Append("// ").Append(c).Append('\n');

            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(set.Mod)) parts.Add(Key("mod") + Quote(set.Mod));
            if (set.ReplaceBundled) parts.Add(Key("replaceBundled") + "true");
            if (set.Exclude.Count > 0) parts.Add(Key("exclude") + List(set.Exclude, 1));
            if (set.KeepActive.Count > 0) parts.Add(Key("keepActive") + List(set.KeepActive, 1));

            if (set.KeepActiveIn.Count > 0)
            {
                List<string> parents = new List<string>();
                foreach (ChildRule c in set.KeepActiveIn) if (!parents.Contains(c.Parent)) parents.Add(c.Parent);
                List<string> entries = new List<string>();
                foreach (string parent in parents)
                {
                    List<string> children = new List<string>();
                    foreach (ChildRule c in set.KeepActiveIn) if (c.Parent == parent) children.Add(c.Name);
                    entries.Add(Indent(2) + Quote(parent) + ": " + List(children, 2));
                }
                parts.Add(Key("keepActiveIn") + "{\n" + string.Join(",\n", entries.ToArray()) + "\n" + Indent(1) + "}");
            }

            if (set.Manage.Count > 0)
            {
                List<string> groups = new List<string>();
                foreach (ManageKind kind in new[] { ManageKind.Objects, ManageKind.Renderers, ManageKind.Items, ManageKind.Vehicles, ManageKind.VehiclePhysics })
                {
                    List<string> names = new List<string>();
                    foreach (ManageRule m in set.Manage) if (m.Kind == kind) names.Add(m.Name);
                    if (names.Count > 0) groups.Add(Indent(2) + Quote(ManageKeys[(int)kind]) + ": " + List(names, 2));
                }
                parts.Add(Key("manage") + "{\n" + string.Join(",\n", groups.ToArray()) + "\n" + Indent(1) + "}");
            }

            if (set.Reparent.Count > 0)
            {
                List<string> entries = new List<string>();
                foreach (ReparentRule r in set.Reparent)
                    entries.Add(Indent(2) + Quote(r.Name) + ": " + (r.Parent == null ? "null" : Quote(r.Parent)));
                parts.Add(Key("reparent") + "{\n" + string.Join(",\n", entries.ToArray()) + "\n" + Indent(1) + "}");
            }

            if (set.NoLod.Count > 0) parts.Add(Key("noLod") + List(set.NoLod, 1));

            if (set.Sectors.Count > 0)
            {
                List<string> sectors = new List<string>();
                foreach (SectorRule s in set.Sectors)
                {
                    List<string> fields = new List<string>
                    {
                        Indent(3) + "\"position\": " + Vec(s.Position),
                        Indent(3) + "\"size\": " + Vec(s.Size),
                        Indent(3) + "\"rotation\": " + Vec(s.Rotation),
                    };
                    if (s.KeepVisible.Count > 0) fields.Add(Indent(3) + "\"keepVisible\": " + List(s.KeepVisible, 3));
                    if (s.DrawDistance != SectorRule.DefaultDrawDistance) fields.Add(Indent(3) + "\"drawDistance\": " + s.DrawDistance.ToString(CultureInfo.InvariantCulture));
                    sectors.Add(Indent(2) + "{\n" + string.Join(",\n", fields.ToArray()) + "\n" + Indent(2) + "}");
                }
                parts.Add(Key("sectors") + "[\n" + string.Join(",\n", sectors.ToArray()) + "\n" + Indent(1) + "]");
            }

            if (set.Satsuma == SatsumaRule.KeepRenderers) parts.Add(Key("satsuma") + "\"keepRenderers\"");
            else if (set.Satsuma == SatsumaRule.Exclude) parts.Add(Key("satsuma") + "\"exclude\"");
            if (set.KeepEmptyBottles) parts.Add(Key("keepEmptyBottles") + "true");
            if (set.SkipFuryColliderFix) parts.Add(Key("skipFixes") + "[ \"furyCollider\" ]");
            if (set.AllVehiclesPhysicsOnly) parts.Add(Key("allVehiclesPhysicsOnly") + "true");
            if (set.NoLods) parts.Add(Key("noLods") + "true");

            sb.Append("{\n").Append(string.Join(",\n", parts.ToArray())).Append(parts.Count > 0 ? "\n" : "").Append("}\n");
            return sb.ToString();
        }

        static string Indent(int level) => new string(' ', level * 2);
        static string Key(string key) => Indent(1) + Quote(key) + ": ";
        static string Quote(string s) => JsonConvert.ToString(s);

        static string List(List<string> names, int level)
        {
            if (names.Count == 0) return "[]";
            StringBuilder sb = new StringBuilder("[\n");
            for (int i = 0; i < names.Count; i++)
            {
                sb.Append(Indent(level + 1)).Append(Quote(names[i]));
                if (i < names.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            return sb.Append(Indent(level)).Append(']').ToString();
        }

        static string Vec(float[] v)
        {
            return "[ " + Num(v[0]) + ", " + Num(v[1]) + ", " + Num(v[2]) + " ]";
        }

        static string Num(float f) => f.ToString("R", CultureInfo.InvariantCulture);
    }
}
