// MOP - REVIVAL
// Copyright(C) 2026 ANICKON
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Reads the old rule format (MOP 3.10 .mopconfig files and Custom.txt) into a RuleSet, with the meaning MOP 3.10
// gave each line. Differences on purpose:
//   * a bad line is reported with its real line number and skipped; MOP 3.10 skipped the rest of the file;
//   * flags are case-insensitive and may have spaces around them; only the first ':' splits the line;
//   * any number of quoted names per line ("a b" or a%20b), MOP 3.10 understood one;
//   * sector numbers are read with '.' as the decimal point on every system language (MOP 3.10 used the system
//     language, so a Russian Windows read "1078.3" as an error and made the sector at 0,0,0);
//   * min_ver is ignored (every bundled value is older than 5.0).
//
// Pure C#: no Unity, no MSCLoader.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MOP.Rules
{
    static class LegacyRules
    {
        public const string Extension = ".mopconfig";
        public const string CustomFile = "Custom.txt";

        /// <param name="name">File name: "&lt;ModID&gt;.mopconfig" or "Custom.txt".</param>
        public static RuleSet Read(string[] lines, string name)
        {
            bool custom = string.Equals(name, CustomFile, StringComparison.OrdinalIgnoreCase);
            RuleSet set = new RuleSet
            {
                Name = name,
                Source = RuleSource.Legacy,
                Mod = custom ? null : StripExtension(name),
            };

            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = i + 1;
                string line = TrimLine(lines[i]);
                if (line.Length == 0 || line[0] == '#')
                    continue;

                try
                {
                    ReadLine(set, line, lineNo, custom);
                }
                catch (Exception ex)
                {
                    set.Error(lineNo, "could not read this line (" + ex.Message + ")");
                }
            }

            return set;
        }

        static void ReadLine(RuleSet set, string line, int lineNo, bool custom)
        {
            int colon = line.IndexOf(':');
            string flag = (colon < 0 ? line : line.Substring(0, colon)).Trim().ToLowerInvariant();
            string rest = colon < 0 ? "" : line.Substring(colon + 1);

            List<string> args = Tokenize(rest, out string tokenError);
            if (tokenError != null)
            {
                set.Error(lineNo, tokenError);
                return;
            }

            for (int i = 0; i < args.Count; i++)
            {
                if (RuleSet.IsProtected(args[i]))
                {
                    set.Error(lineNo, $"\"{args[i]}\" is used by MOP itself and cannot have a rule");
                    return;
                }
            }

            switch (flag)
            {
                case "ignore":
                    if (!Need(set, lineNo, flag, args, 1)) return;
                    if (args.Count == 1)
                    {
                        RuleSet.AddUnique(set.KeepActive, args[0]);
                    }
                    else if (args[args.Count - 1].Equals("fullignore", StringComparison.OrdinalIgnoreCase))
                    {
                        // MOP 3.10 excluded the first object only, whatever stood between it and "fullignore".
                        RuleSet.AddUnique(set.Exclude, args[0]);
                        if (args.Count > 2)
                            set.Note(lineNo, $"only \"{args[0]}\" is excluded; the words between it and fullignore have no effect");
                    }
                    else
                    {
                        // "ignore: <place or vehicle> <child>": the child stays on while its parent is switched off.
                        AddChild(set, args[0], args[1]);
                        if (args.Count > 2)
                            set.Note(lineNo, "only the first two names are used (place, then object)");
                    }
                    break;

                case "ignore_full":
                    if (!Need(set, lineNo, flag, args, 1)) return;
                    RuleSet.AddUnique(set.Exclude, args[0]);
                    break;

                case "toggle":
                    if (!Need(set, lineNo, flag, args, 1)) return;
                    ManageKind kind = ManageKind.Objects;
                    if (args.Count > 1)
                    {
                        switch (args[1].ToLowerInvariant())
                        {
                            case "renderer": kind = ManageKind.Renderers; break;
                            case "item": kind = ManageKind.Items; break;
                            case "vehicle": kind = ManageKind.Vehicles; break;
                            case "vehicle_physics": kind = ManageKind.VehiclePhysics; break;
                            default:
                                set.Error(lineNo, $"unknown toggle mode \"{args[1]}\" (renderer, item, vehicle or vehicle_physics)");
                                return;
                        }
                    }
                    AddManage(set, args[0], kind);
                    break;

                case "change_parent":
                    if (args.Count != 2)
                    {
                        set.Error(lineNo, "change_parent needs two names: the object and its new parent (or null)");
                        return;
                    }
                    set.Reparent.Add(new ReparentRule { Name = args[0], Parent = args[1].Equals("null", StringComparison.OrdinalIgnoreCase) ? null : args[1] });
                    break;

                case "satsuma_ignore":
                    set.Satsuma = SatsumaRule.Exclude;
                    break;

                case "satsuma_ignore_renderer":
                    if (set.Satsuma != SatsumaRule.Exclude) set.Satsuma = SatsumaRule.KeepRenderers;
                    break;

                case "dont_destroy_empty_bottles":
                case "dont_destroy_empty_beer_bottles":
                    set.KeepEmptyBottles = true;
                    break;

                case "sector":
                    if (!Need(set, lineNo, flag, args, 3)) return;
                    SectorRule sector = new SectorRule();
                    if (!ParseVector(args[0], sector.Position) || !ParseVector(args[1], sector.Size) || !ParseVector(args[2], sector.Rotation))
                    {
                        set.Error(lineNo, "a sector needs three vectors: position, size and rotation, like 1.5,0,-2");
                        return;
                    }
                    for (int i = 3; i < args.Count; i++)
                        RuleSet.AddUnique(sector.KeepVisible, args[i]);
                    set.Sectors.Add(sector);
                    break;

                case "min_ver":
                    break;

                case "skip_fury_collider_fix":
                    set.SkipFuryColliderFix = true;
                    break;

                case "no_lod":
                    if (!Need(set, lineNo, flag, args, 1)) return;
                    RuleSet.AddUnique(set.NoLod, args[0]);
                    break;

                case "toggle_all_vehicles_physics_only":
                case "no_lods":
                case "ignore_mod_vehicles":
                    if (!custom)
                    {
                        set.Error(lineNo, $"{flag} only works in Custom.txt");
                        return;
                    }
                    if (flag == "toggle_all_vehicles_physics_only") set.AllVehiclesPhysicsOnly = true;
                    else if (flag == "no_lods") set.NoLods = true;
                    else set.Note(lineNo, "ignore_mod_vehicles never had an effect and is not supported");
                    break;

                default:
                    set.Error(lineNo, $"unknown flag \"{flag}\"");
                    break;
            }
        }

        static bool Need(RuleSet set, int lineNo, string flag, List<string> args, int count)
        {
            if (args.Count >= count) return true;
            set.Error(lineNo, count == 1 ? $"{flag} needs an object name" : $"{flag} needs {count} values");
            return false;
        }

        static void AddChild(RuleSet set, string parent, string child)
        {
            for (int i = 0; i < set.KeepActiveIn.Count; i++)
                if (set.KeepActiveIn[i].Parent == parent && set.KeepActiveIn[i].Name == child) return;
            set.KeepActiveIn.Add(new ChildRule { Parent = parent, Name = child });
        }

        static void AddManage(RuleSet set, string name, ManageKind kind)
        {
            for (int i = 0; i < set.Manage.Count; i++)
                if (set.Manage[i].Name == name && set.Manage[i].Kind == kind) return;
            set.Manage.Add(new ManageRule { Name = name, Kind = kind });
        }

        /// <summary>Whitespace and byte order marks (some files have one in the middle) around the line.</summary>
        static string TrimLine(string line)
        {
            if (line == null) return "";
            return line.Trim().Trim('\uFEFF').Trim();
        }

        /// <summary>Space-separated words; "quoted words" and %20 keep spaces inside a name.</summary>
        internal static List<string> Tokenize(string s, out string error)
        {
            error = null;
            List<string> tokens = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false, any = false;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    quoted = !quoted;
                    any = true;
                    continue;
                }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (any) tokens.Add(current.ToString().Replace("%20", " "));
                    current.Length = 0;
                    any = false;
                    continue;
                }
                current.Append(c);
                any = true;
            }

            if (quoted)
            {
                error = "a quote is not closed";
                return tokens;
            }
            if (any) tokens.Add(current.ToString().Replace("%20", " "));

            // A pair of quotes with nothing inside is not a name.
            tokens.RemoveAll(t => t.Trim().Length == 0);
            return tokens;
        }

        internal static bool ParseVector(string s, float[] into)
        {
            string[] parts = s.Split(',');
            if (parts.Length != 3) return false;
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out into[i]))
                    return false;
            }
            return true;
        }

        static string StripExtension(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : name;
        }
    }
}
