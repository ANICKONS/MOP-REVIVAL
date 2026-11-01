using System.Collections.Generic;
using System.Linq;
using MOPR6.Core.Catalog;
using Newtonsoft.Json;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class FingerprintTests
    {
        static FsmShape Shape(params string[] actions)
        {
            FsmShape f = new FsmShape { Name = "Use", StartState = "Idle" };
            f.States.Add(new StateShape { Name = "Idle", Actions = actions.ToList(), Transitions = { "FINISHED>Wait" } });
            return f;
        }

        [Fact]
        public void Stable_and_sensitive_to_structure()
        {
            Assert.Equal("cbf29ce484222325", Fingerprint.Of(""));          // FNV-1a 64 offset basis
            Assert.Equal(Fingerprint.Of(Shape("SetBoolValue", "Wait")), Fingerprint.Of(Shape("SetBoolValue", "Wait")));
            Assert.NotEqual(Fingerprint.Of(Shape("SetBoolValue", "Wait")), Fingerprint.Of(Shape("SetBoolValue", "Wait", "ModHookAction")));
            Assert.NotEqual(Fingerprint.Of(Shape("SetBoolValue", "Wait")), Fingerprint.Of(Shape("Wait", "SetBoolValue")));
            FsmShape moved = Shape("SetBoolValue");
            moved.GlobalTransitions.Add("RESET>Idle");
            Assert.NotEqual(Fingerprint.Of(Shape("SetBoolValue")), Fingerprint.Of(moved));
        }
    }

    public class InventoryIOTests
    {
        const string Dump =
            "{\"t\":\"game\",\"asm\":\"ABC\",\"frame\":5}\n" +
            "{\"t\":\"o\",\"p\":\"YARD/Building/WOODSHED/Door\",\"c\":[\"BoxCollider\",\"MeshRenderer\",\"PlayMakerFSM\",\"Transform\"],\"n\":0}\n" +
            "{\"t\":\"o\",\"p\":\"2026-10-28\",\"c\":[\"Transform\"],\"n\":2}\n" +
            "{\"t\":\"f\",\"p\":\"YARD/Building/WOODSHED/Door\",\"name\":\"Use\",\"i\":0,\"start\":\"Idle\"," +
            "\"s\":[{\"n\":\"Idle\",\"a\":[\"SetBoolValue\",\"Wait\"],\"tr\":[\"FINISHED>Wait\"]}],\"g\":[]}\n";

        [Fact]
        public void Dump_builds_the_same_hash_as_the_shape_and_round_trips()
        {
            List<string> problems = new List<string>();
            Inventory inv = InventoryIO.FromDump(new System.IO.StringReader(Dump), problems);
            Assert.Empty(problems);
            Assert.Equal("ABC", inv.GameAssemblyHash);
            Assert.Equal(2, inv.Objects.Count);
            Assert.NotNull(inv.Find("2026-10-28"));    // a date-like name stays a string
            InventoryObject door = inv.Find("YARD/Building/WOODSHED/Door");
            FsmShape shape = new FsmShape { Name = "Use", StartState = "Idle" };
            shape.States.Add(new StateShape { Name = "Idle", Actions = { "SetBoolValue", "Wait" }, Transitions = { "FINISHED>Wait" } });
            Assert.Equal(Fingerprint.Of(shape), Assert.Single(door.Fsms).Hash);

            inv.Created = "2026-10-28";
            System.IO.StringWriter w = new System.IO.StringWriter();
            InventoryIO.Write(inv, w);
            Inventory back = InventoryIO.Read(new System.IO.StringReader(w.ToString()));
            Assert.Equal("2026-10-28", back.Created);
            Assert.Equal(door.ObjectHash(), back.Find(door.Path).ObjectHash());
            Assert.Equal(door.Fsms[0].Hash, back.Find(door.Path).Fsms[0].Hash);

            // A truncated file is an error, not a smaller inventory.
            string cut = w.ToString().Substring(0, w.ToString().LastIndexOf("{\"p\"", System.StringComparison.Ordinal));
            Assert.Throws<System.FormatException>(() => InventoryIO.Read(new System.IO.StringReader(cut)));

            // The keys the mod embeds: distinct "name|hash", with the game hash.
            System.IO.StringWriter kw = new System.IO.StringWriter();
            InventoryIO.WriteFsmKeys(inv, kw);
            HashSet<string> keys = InventoryIO.ReadFsmKeys(new System.IO.StringReader(kw.ToString()), out string game);
            Assert.Equal("ABC", game);
            Assert.Equal(new[] { InventoryIO.FsmKey("Use", door.Fsms[0].Hash) }, keys.ToArray());
            Assert.Throws<System.FormatException>(() => InventoryIO.ReadFsmKeys(new System.IO.StringReader("#game X fsms 2 keys 2\nA|1\n"), out _));
        }

        [Fact]
        public void Problems_are_listed()
        {
            List<string> problems = new List<string>();
            InventoryIO.FromDump(new System.IO.StringReader("{bad\n{\"t\":\"f\",\"p\":\"nowhere\",\"name\":\"X\",\"s\":[]}\n{\"t\":\"zzz\"}\n"), problems);
            Assert.Equal(3, problems.Count);
        }
    }

    public class CatalogValidatorTests
    {
        static Inventory Inv()
        {
            Inventory inv = new Inventory();
            inv.Objects.Add(new InventoryObject { Path = "YARD/Building/WOODSHED/Door", Components = { "BoxCollider", "MeshRenderer", "PlayMakerFSM" }, Fsms = { new InventoryFsm { Name = "Use" } } });
            inv.Objects.Add(new InventoryObject { Path = "SATSUMA(557kg, 248)/Body/BoltPM", Fsms = { new InventoryFsm { Name = "Screw" } } });
            inv.Objects.Add(new InventoryObject { Path = "SATSUMA(557kg, 248)/Body/BoltPM~1", Fsms = { new InventoryFsm { Name = "Screw" } } });
            return inv;
        }

        static CatalogValidator V() => new CatalogValidator
        {
            KnownConditions = { "player.notInside" },
            KnownSpecials = { "fsmStateWipe" },
            TestExists = p => p == "MOPR6/Scenarios/eq-home-yard.json"
        };

        const string Good = @"{
          ""Id"": ""yard.woodshed.door"",
          ""Match"": { ""Path"": ""YARD/Building/WOODSHED/Door"", ""Expect"": { ""Count"": 1, ""Components"": [""BoxCollider""], ""Fsms"": [""Use""] } },
          ""Allow"": { ""Render"": true, ""Sleep"": true },
          ""When"": [""player.notInside""],
          ""NoRestart"": [""Use""],
          ""Special"": [ { ""Kind"": ""fsmStateWipe"", ""Fsm"": ""Use"", ""State"": ""State 1"" } ],
          ""Source"": ""MOP 3.10 GameFixes"",
          ""Test"": ""MOPR6/Scenarios/eq-home-yard.json""
        }";

        [Fact]
        public void A_good_rule_passes()
        {
            CatalogRule r = JsonConvert.DeserializeObject<CatalogRule>(Good);
            Assert.Empty(V().Validate(new[] { r }, Inv()));
            Assert.Equal(2, CatalogValidator.Matches(new RuleMatch { Class = "SATSUMA*/BoltPM*" }, Inv()).Count);
        }

        [Fact]
        public void Every_problem_is_named()
        {
            CatalogRule bad = JsonConvert.DeserializeObject<CatalogRule>(Good);
            bad.When.Add("player.dancing");
            bad.Special.Add(new RuleSpecial { Kind = "magic" });
            bad.Test = "missing.json";
            bad.Source = null;
            bad.Allow.Deactivate = true;
            bad.NoRestart.Add("Nope");
            CatalogRule dup = JsonConvert.DeserializeObject<CatalogRule>(Good);
            CatalogRule cls = new CatalogRule
            {
                Id = "bolts", Source = "x", Test = "MOPR6/Scenarios/eq-home-yard.json", Allow = new RuleAllow(),
                Match = new RuleMatch { Class = "SATSUMA*/BoltPM*", Expect = new RuleExpect { Count = 3 } }
            };
            CatalogRule none = new CatalogRule { Id = "ghost", Source = "x", Test = "MOPR6/Scenarios/eq-home-yard.json", Allow = new RuleAllow(), Match = new RuleMatch { Path = "NOPE" } };
            CatalogRule both = new CatalogRule { Id = "both", Source = "x", Test = "MOPR6/Scenarios/eq-home-yard.json", Allow = new RuleAllow(), Match = new RuleMatch { Path = "a", Class = "b" } };

            List<string> p = V().Validate(new[] { bad, dup, cls, none, both }, Inv());
            Assert.Contains(p, x => x.Contains("unknown condition 'player.dancing'"));
            Assert.Contains(p, x => x.Contains("unknown special case 'magic'"));
            Assert.Contains(p, x => x.Contains("test scenario not found"));
            Assert.Contains(p, x => x.Contains("no source"));
            Assert.Contains(p, x => x.Contains("deactivation without evidence"));
            Assert.Contains(p, x => x.Contains("noRestart FSM Nope"));
            Assert.Contains(p, x => x.Contains("duplicate id"));
            Assert.Contains(p, x => x.Contains("bolts: matches 2 objects, expected 3"));
            Assert.Contains(p, x => x.Contains("ghost: matches nothing"));
            Assert.Contains(p, x => x.Contains("both: exactly one of"));
        }
    }
}
