// RulesCheck: offline checks of the MOPR rule readers. See RulesCheck.csproj.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MOP.Rules;

namespace RulesCheck
{
    static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0)
            {
                Console.WriteLine("usage: convert <legacyDir> <outDir> | verify <legacyDir> <jsonDir> | selftest");
                return 2;
            }
            switch (args[0])
            {
                case "convert": return Convert(args[1], args[2]);
                case "verify": return Verify(args[1], args[2]);
                case "selftest": return SelfTest();
                default: Console.WriteLine("unknown command " + args[0]); return 2;
            }
        }

        // ----- convert --------------------------------------------------------------------------------

        static int Convert(string legacyDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            int files = 0, issues = 0;
            foreach (string path in Directory.GetFiles(legacyDir, "*" + LegacyRules.Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string[] lines = File.ReadAllLines(path);
                RuleSet set = LegacyRules.Read(lines, Path.GetFileName(path));
                foreach (RuleIssue i in set.Issues) { Console.WriteLine("  " + i); issues++; }

                // The file's own comments become // comments; the "# <name>.mopconfig" title line is replaced.
                List<string> header = new List<string> { $"MOPR rules for the \"{set.Mod}\" mod." };
                foreach (string raw in lines)
                {
                    string l = raw.Trim().Trim('\uFEFF').Trim();
                    if (!l.StartsWith("#")) continue;
                    string text = l.TrimStart('#').Trim();
                    if (text.Length == 0 || text.EndsWith(LegacyRules.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                    header.Add(text);
                }
                foreach (RuleIssue i in set.Issues.Where(i => !i.IsError))
                    header.Add("Note (from the old file): " + i.Message);

                File.WriteAllText(Path.Combine(outDir, set.Mod + RuleJson.Extension), RuleJson.Write(set, header), new UTF8Encoding(false));
                files++;
            }
            Console.WriteLine($"converted {files} files, {issues} issues");
            return 0;
        }

        // ----- verify ---------------------------------------------------------------------------------

        static int Verify(string legacyDir, string jsonDir)
        {
            int ok = 0, bad = 0;
            List<RuleSet> all = new List<RuleSet>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(legacyDir, "*" + LegacyRules.Extension).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                RuleSet legacy = LegacyRules.Read(File.ReadAllLines(path), Path.GetFileName(path));
                string jsonPath = Path.Combine(jsonDir, legacy.Mod + RuleJson.Extension);
                seen.Add(legacy.Mod);
                if (!File.Exists(jsonPath)) { Console.WriteLine("MISSING " + jsonPath); bad++; continue; }

                RuleSet json = RuleJson.Read(File.ReadAllText(jsonPath), Path.GetFileName(jsonPath), RuleSource.Bundled);
                string a = RuleJson.Write(legacy), b = RuleJson.Write(json);
                List<RuleIssue> errors = json.Issues.Where(i => i.IsError).ToList();
                if (a != b || errors.Count > 0 || !string.Equals(json.Mod, legacy.Mod, StringComparison.Ordinal))
                {
                    Console.WriteLine("DIFFERENT " + legacy.Mod);
                    foreach (RuleIssue i in errors) Console.WriteLine("  " + i);
                    if (a != b) { Console.WriteLine("--- legacy\n" + a + "--- json\n" + b); }
                    bad++;
                }
                else ok++;
                all.Add(json);
            }
            foreach (string path in Directory.GetFiles(jsonDir, "*" + RuleJson.Extension))
                if (!seen.Contains(Path.GetFileNameWithoutExtension(path))) { Console.WriteLine("NO LEGACY SOURCE " + path); bad++; }

            RuleBook book = RuleBook.Compile(all, null);
            Console.WriteLine($"verify: {ok} identical, {bad} different; compiled {book.Active.Count} sets, {book.RuleCount} rules, " +
                              $"{book.Manage.Count} managed, {book.Reparent.Count} reparent, {book.Sectors.Count} sectors, " +
                              $"satsuma exclude={book.SatsumaExclude} keepRenderers={book.SatsumaKeepRenderers}");
            return bad == 0 ? 0 : 1;
        }

        // ----- selftest -------------------------------------------------------------------------------

        static int passed, failed;

        static void Check(string name, bool condition, string detail = null)
        {
            if (condition) passed++;
            else { failed++; Console.WriteLine("FAIL " + name + (detail != null ? ": " + detail : "")); }
        }

        static RuleSet Legacy(string name, params string[] lines) => LegacyRules.Read(lines, name);
        static RuleSet Json(string text, RuleSource source = RuleSource.User) => RuleJson.Read(text, "test.json", source);
        static string Issues(RuleSet s) => string.Join(" | ", s.Issues.Select(i => i.ToString()));

        static int SelfTest()
        {
            // Russian Windows: decimal comma. Sector numbers must still read with a dot.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");

            // --- legacy format ---
            RuleSet s = Legacy("Roofless.mopconfig", "ignore: \"SATSUMA(557kg, 248)\" InteriorLight");
            Check("quoted place + child", s.KeepActiveIn.Count == 1 && s.KeepActiveIn[0].Parent == "SATSUMA(557kg, 248)" && s.KeepActiveIn[0].Name == "InteriorLight", Issues(s));
            Check("mod from file name", s.Mod == "Roofless");

            s = Legacy("A.mopconfig", "ignore:SATSUMA(557kg,%20248)");
            Check("%20 is a space", s.KeepActive.Count == 1 && s.KeepActive[0] == "SATSUMA(557kg, 248)");

            s = Legacy("A.mopconfig", "ignore: \"Body Pillow(Clone)\" fullIgnore", "ignore:TRAFFIC fullignore");
            Check("fullignore -> exclude", s.Exclude.SequenceEqual(new[] { "Body Pillow(Clone)", "TRAFFIC" }) && s.KeepActive.Count == 0, Issues(s));

            s = Legacy("GifuPump.mopconfig", "ignore:GIFU(750/450psi) MESH fullignore");
            Check("old quirk: first object excluded", s.Exclude.SequenceEqual(new[] { "GIFU(750/450psi)" }) && s.KeepActiveIn.Count == 0);
            Check("old quirk: note, not error", s.Issues.Count == 1 && !s.Issues[0].IsError, Issues(s));

            s = Legacy("A.mopconfig", "# comment", "\uFEFFignore: HUMANS", "  min_ver: 4.0.0 ");
            Check("BOM in the middle, min_ver ignored", s.KeepActive.SequenceEqual(new[] { "HUMANS" }) && s.Issues.Count == 0, Issues(s));

            s = Legacy("A.mopconfig", "toggle: X item", "toggle: Y bogus", "toggle: Z vehicle_physics", "  IGNORE : W ");
            Check("bad line skipped, rest kept", s.Manage.Count == 2 && s.Manage[0].Kind == ManageKind.Items && s.Manage[1].Kind == ManageKind.VehiclePhysics
                  && s.KeepActive.SequenceEqual(new[] { "W" }), Issues(s));
            Check("error has the real line", s.Issues.Count == 1 && s.Issues[0].IsError && s.Issues[0].Line == 2, Issues(s));

            s = Legacy("A.mopconfig", "change_parent: BUS null", "change_parent: ONLYONE", "change_parent: A B");
            Check("change_parent", s.Reparent.Count == 2 && s.Reparent[0].Parent == null && s.Reparent[1].Parent == "B" && s.Issues.Count == 1 && s.Issues[0].Line == 2, Issues(s));

            s = Legacy("A.mopconfig", "sector: -1285.5,0.43,1078.3 7,1,8 0,32,0 PERAJARVI LakeNice");
            Check("sector with a dot on ru-RU", s.Sectors.Count == 1 && Math.Abs(s.Sectors[0].Position[0] + 1285.5f) < 0.001f && Math.Abs(s.Sectors[0].Position[2] - 1078.3f) < 0.001f
                  && s.Sectors[0].Rotation[1] == 32 && s.Sectors[0].KeepVisible.SequenceEqual(new[] { "PERAJARVI", "LakeNice" }), Issues(s));
            s = Legacy("A.mopconfig", "sector: 1,2 3,4,5 0,0,0");
            Check("bad sector is an error", s.Sectors.Count == 0 && s.ErrorCount == 1);

            s = Legacy("A.mopconfig", "ignore: PLAYER", "ignore: OK");
            Check("protected name", s.KeepActive.SequenceEqual(new[] { "OK" }) && s.ErrorCount == 1);

            s = Legacy("A.mopconfig", "no_lods", "toggle_all_vehicles_physics_only");
            Check("custom-only flags rejected in a mod file", !s.NoLods && !s.AllVehiclesPhysicsOnly && s.ErrorCount == 2);
            s = Legacy("Custom.txt", "no_lods", "toggle_all_vehicles_physics_only", "ignore_mod_vehicles");
            Check("custom-only flags in Custom.txt", s.NoLods && s.AllVehiclesPhysicsOnly && s.Mod == null && s.ErrorCount == 0 && s.Issues.Count == 1, Issues(s));

            s = Legacy("A.mopconfig", "bogus: X", "ignore: \"unclosed", "ignore:", "satsuma_ignore_renderer", "satsuma_ignore", "satsuma_ignore_renderer");
            Check("unknown flag, unclosed quote, missing name", s.ErrorCount == 3, Issues(s));
            Check("satsuma exclude wins", s.Satsuma == SatsumaRule.Exclude);

            s = Legacy("A.mopconfig", "ignore: A  B", "dont_destroy_empty_bottles", "skip_fury_collider_fix", "no_lod: GAZ");
            Check("double space, bottles, fury, no_lod", s.KeepActiveIn.Count == 1 && s.KeepActiveIn[0].Name == "B" && s.KeepEmptyBottles && s.SkipFuryColliderFix
                  && s.NoLod.SequenceEqual(new[] { "GAZ" }), Issues(s));

            // --- JSON format ---
            s = Json("// comment\n{\n  \"Mod\": \"BetterMSC\", /* block */\n  \"KEEPACTIVE\": [\"COMPUTER\", \"YARD TV\", \"COMPUTER\"],\n" +
                     "  \"keepActiveIn\": { \"YARD\": [\"Handle\", \"Hatch\"] },\n  \"manage\": { \"items\": [\"Scale(itemx)\"], \"vehiclePhysics\": \"SVOBODA(855kg)\" },\n" +
                     "  \"reparent\": { \"BUS\": null, \"X\": \"Y\" },\n  \"satsuma\": \"keepRenderers\",\n  \"skipFixes\": [\"furyCollider\"],\n}\n");
            Check("json: comments, case-insensitive keys, trailing comma", s.Mod == "BetterMSC" && s.KeepActive.SequenceEqual(new[] { "COMPUTER", "YARD TV" })
                  && s.KeepActiveIn.Count == 2 && s.Manage.Count == 2 && s.Reparent.Count == 2 && s.Reparent[0].Parent == null
                  && s.Satsuma == SatsumaRule.KeepRenderers && s.SkipFuryColliderFix && s.Issues.Count == 0, Issues(s));

            s = Json("{\n  \"keepActiv\": [\"A\"],\n  \"exclude\": 5,\n  \"keepActive\": [\"B\", 7, \"\", \"PLAYER\"]\n}");
            Check("json: unknown key with suggestion", s.Issues.Any(i => i.Line == 2 && i.Message.Contains("did you mean \"keepActive\"")), Issues(s));
            Check("json: wrong types reported, good entries kept", s.KeepActive.SequenceEqual(new[] { "B" }) && s.ErrorCount == 5 && !s.Unreadable, Issues(s));

            s = Json("{\n  \"exclude\": [\"A\"]\n  \"keepActive\": [\"B\"]\n}");
            Check("json: syntax error is unreadable with its line", s.Unreadable && s.Issues.Count == 1 && s.Issues[0].Line >= 2 && s.Issues[0].Line <= 3, Issues(s));
            Check("json: missing comma gets a hint", s.Issues.Count == 1 && s.Issues[0].Message.Contains("missing comma"), Issues(s));

            s = Json("{ \"exclude\": [\"A\"],\n  \"Exclude\": [\"B\"] }");
            Check("json: duplicate key reported, the first one used", !s.Unreadable && s.ErrorCount == 1 && s.Issues[0].Line == 2
                  && s.Exclude.SequenceEqual(new[] { "A" }), Issues(s));
            s = Json("{ \"mod\": \"2026-10-19\", \"exclude\": [\"A\", ] }");
            Check("json: dates stay text, trailing comma in a list", s.Mod == "2026-10-19" && s.Exclude.Count == 1 && s.Issues.Count == 0, Issues(s));
            s = Json("");
            Check("json: empty file", s.Unreadable && s.ErrorCount == 1, Issues(s));
            s = Json("{ \"exclude\": [\"A\"");
            Check("json: cut off file", s.Unreadable && s.ErrorCount == 1, Issues(s));

            s = Json("{ \"mod\": \"X\", \"noLods\": true }");
            Check("json: whole-game switch rejected with mod", !s.NoLods && s.ErrorCount == 1, Issues(s));
            s = Json("{ \"noLods\": true, \"allVehiclesPhysicsOnly\": true }");
            Check("json: whole-game switches in a global user file", s.NoLods && s.AllVehiclesPhysicsOnly && s.Issues.Count == 0, Issues(s));
            s = Json("{ \"mod\": \"X\", \"replaceBundled\": true }", RuleSource.Bundled);
            Check("json: replaceBundled rejected in a bundled file", !s.ReplaceBundled && s.ErrorCount == 1, Issues(s));
            s = Json("{ \"exclude\": [\"A\"] }", RuleSource.Bundled);
            Check("json: bundled file needs mod", s.ErrorCount == 1, Issues(s));

            s = Json("{ \"sectors\": [ { \"position\": [-1285.5, 0.43, 1078.3], \"size\": [7, 1, 8], \"keepVisible\": [\"A\"], \"drawDistance\": 500 }, { \"size\": [1,1,1] }, { \"position\": [1,2], \"size\": [1,1,1] } ] }");
            Check("json: sectors", s.Sectors.Count == 1 && s.Sectors[0].DrawDistance == 500 && s.Sectors[0].Rotation[1] == 0 && s.ErrorCount == 2, Issues(s));

            s = Json("[ 1, 2 ]");
            Check("json: not an object", s.Unreadable && s.ErrorCount == 1);
            s = Json("{ \"exclude\": [\"A\"] } trailing");
            Check("json: text after the object", s.Unreadable && s.ErrorCount == 1, Issues(s));

            // --- writer round trip ---
            RuleSet full = Legacy("Full.mopconfig", "ignore: A", "ignore: B fullignore", "ignore: P C", "toggle: \"I 1\" item", "toggle: V vehicle",
                                  "change_parent: BUS null", "no_lod: L", "sector: 1.5,2,3 4,5,6 0,90,0 K", "satsuma_ignore_renderer", "dont_destroy_empty_bottles",
                                  "skip_fury_collider_fix");
            string w1 = RuleJson.Write(full);
            RuleSet back = RuleJson.Read(w1, "Full.json", RuleSource.Bundled);
            Check("writer round trip", RuleJson.Write(back) == w1 && back.Issues.Count == 0, Issues(back) + "\n" + w1);

            // --- compiled book ---
            RuleSet a = Json("{ \"keepActive\": [\"X\", \"Y\"], \"keepActiveIn\": { \"YARD\": [\"H\"] }, \"noLod\": [\"L\"] }");
            a.Name = "a.json";
            RuleSet b = Json("{ \"exclude\": [\"X\"], \"keepActiveIn\": { \"YARD\": [\"H\", \"J\"] }, \"manage\": { \"items\": [\"I\"] }, \"satsuma\": \"exclude\" }");
            b.Name = "b.json";
            RuleSet c = Json("{ \"manage\": { \"items\": [\"I\"] } }");
            c.Name = "c.json";
            RuleBook book = RuleBook.Compile(new[] { a, b, c }, null);
            Check("book: exclude wins over keepActive", book.Object("X") == ObjectRule.Exclude && book.Object("Y") == ObjectRule.KeepActive && book.Object("Z") == ObjectRule.None);
            Check("book: children merged", book.KeepActiveChildren("YARD").SequenceEqual(new[] { "H", "J" }) && book.KeepActiveChildren("NOPE").Length == 0);
            Check("book: manage deduplicated", book.Manage.Count == 1);
            Check("book: satsuma exclude implies keep renderers", book.SatsumaExclude && book.SatsumaKeepRenderers);
            Check("book: noLod", book.IsNoLod("L") && !book.IsNoLod("X"));
            Check("book: unmatched", book.Unmatched().Count == 0);
            RuleBook fresh = RuleBook.Compile(new[] { a }, null);
            fresh.Object("X");
            Check("book: unmatched lists names never asked for", fresh.Unmatched().Count == 1 && fresh.Unmatched()[0].StartsWith("Y"));
            Check("book: why", book.Why("X").StartsWith("exclude (a.json, b.json)") && book.Why("H").Contains("stays on inside YARD"), book.Why("X"));
            Check("empty book", RuleBook.Empty.Object("X") == ObjectRule.None && !RuleBook.Empty.HasObjectRules);

            Console.WriteLine($"selftest: {passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
