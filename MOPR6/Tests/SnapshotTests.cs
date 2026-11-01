using System.Collections.Generic;
using System.Linq;
using MOPR6.Core.Snapshot;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class GlobTests
    {
        [Theory]
        [InlineData("YARD/Building/door.go", "YARD/Building/door.go", true)]
        [InlineData("YARD/Building/door.go", "YARD/Building/door.gox", false)]
        [InlineData("*.pose", "SATSUMA(557kg, 248)/body.pose", true)]
        [InlineData("*.pose", "SATSUMA.vel", false)]
        [InlineData("Traffic/*", "Traffic/Cars/car~3.pose", true)]
        [InlineData("Traffic/*", "TrafficX/car.pose", false)]
        [InlineData("*#Screw.f.*", "CARPARTS/bolt~4#Screw.f.Rotation", true)]
        [InlineData("a?c", "abc", true)]
        [InlineData("a?c", "ac", false)]
        [InlineData("*a*b*", "xxaYYbZZ", true)]
        [InlineData("*a*b*", "xxbYYa", false)]
        [InlineData("*", "", true)]
        public void Matches(string pattern, string text, bool expected)
        {
            Assert.Equal(expected, new Glob(pattern).IsMatch(text));
        }
    }

    public class ToleranceTests
    {
        static Tolerances T(params ToleranceRule[] rules) => new Tolerances(rules);

        [Fact]
        public void Without_rules_numbers_must_be_equal_but_formatting_does_not_matter()
        {
            Tolerances t = T();
            Assert.True(t.Same("k", "1", "1.0"));
            Assert.True(t.Same("k", "1,2;3", "1.0,2;3.00"));
            Assert.False(t.Same("k", "1", "1.0001"));
            Assert.False(t.Same("k", "abc", "abd"));
            Assert.False(t.Same("k", "1,2", "1,2,3"));
            Assert.False(t.Same("k", "1;2", "1"));
        }

        [Fact]
        public void Absolute_and_relative_tolerance()
        {
            Tolerances t = T(new ToleranceRule { Pattern = "*.f.*", Abs = 1e-4, Rel = 1e-3 });
            Assert.True(t.Same("x#Fsm.f.Speed", "0.00005", "0.00012"));
            Assert.False(t.Same("x#Fsm.f.Speed", "0", "0.0002"));
            Assert.True(t.Same("x#Fsm.f.Odo", "10000", "10009"));      // 0.1% of 10000 = 10
            Assert.False(t.Same("x#Fsm.f.Odo", "10000", "10011"));
            Assert.False(t.Same("x#Fsm.i.Count", "1", "2"));           // other key: no rule
        }

        [Fact]
        public void Quaternions_compare_by_angle_and_sign()
        {
            Tolerances t = T(new ToleranceRule
            {
                Pattern = "*.pose",
                Groups = new List<ToleranceGroup> { new ToleranceGroup { Abs = 0.05 }, new ToleranceGroup { QuatDeg = 0.5 } }
            });
            // Same rotation, opposite sign; position within 5 cm.
            Assert.True(t.Same("car.pose", "1,2,3;0,0.7071068,0,0.7071068", "1.04,2,3;0,-0.7071068,0,-0.7071068"));
            Assert.False(t.Same("car.pose", "1,2,3;0,0,0,1", "1.06,2,3;0,0,0,1"));
            // 1 degree about Y: q = (0, sin(0.5°), 0, cos(0.5°)).
            Assert.False(t.Same("car.pose", "1,2,3;0,0,0,1", "1,2,3;0,0.0087265,0,0.9999619"));
            Assert.True(t.Same("car.pose", "1,2,3;0,0,0,1", "1,2,3;0,0.0034907,0,0.9999939"));   // 0.4°
        }

        [Fact]
        public void Ignore_and_rule_order()
        {
            Tolerances t = T(new ToleranceRule { Pattern = "clock*", Ignore = true }, new ToleranceRule { Pattern = "*", Abs = 1 });
            Assert.True(t.Same("clock.f.Time", "1", "999"));
            Assert.True(t.Same("other", "1", "1.5"));
            Assert.False(t.Same("other", "1", "2.5"));
            Assert.False(t.Same("other", "a", "b"));
        }

        [Fact]
        public void Volatile_rules_do_not_act_as_tolerances()
        {
            Tolerances t = T(new ToleranceRule { Pattern = "MAP/CloudSystem/*", Volatile = true, Reason = "weather" },
                             new ToleranceRule { Pattern = "*.f.*", Abs = 0.5 });
            Assert.Null(t.RuleFor("MAP/CloudSystem/x.go"));
            Assert.NotNull(t.VolatileRuleFor("MAP/CloudSystem/x.go"));
            Assert.True(t.Same("MAP/CloudSystem/x#Logic.f.Pitch", "1", "1.4"));   // the tolerance rule still applies
            Assert.False(t.Same("MAP/CloudSystem/x.go", "1,0,Untagged", "0,0,Untagged"));
        }

        [Fact]
        public void Nan_only_equals_nan()
        {
            Tolerances t = T(new ToleranceRule { Pattern = "*", Abs = 1 });
            Assert.True(t.Same("k", "NaN", "NaN"));
            Assert.False(t.Same("k", "NaN", "0"));
        }
    }

    public class NoiseModelTests
    {
        static SnapshotData S(params string[] kv)
        {
            SnapshotData d = new SnapshotData();
            for (int i = 0; i < kv.Length; i += 2) d.Add(kv[i], kv[i + 1]);
            return d;
        }

        [Theory]
        [InlineData("r20 battery box(Clone)~1.pose", "r20 battery box(Clone)~*.pose")]
        [InlineData("a~12/b~3#Use~2.f.X", "a~*/b~*#Use~*.f.X")]
        [InlineData("plain/key.go", "plain/key.go")]
        [InlineData("tilde~x", "tilde~x")]
        public void Normalizes_sibling_indices(string key, string expected) => Assert.Equal(expected, NoiseModel.Normalize(key));

        [Fact]
        public void Learns_what_varied_and_turns_it_into_noise()
        {
            NoiseModel model = new NoiseModel();
            int n = model.Learn(new[] { S("box~1.pose", "1", "stable", "1", "gone", "1"), S("box~1.pose", "2", "stable", "1") }, null, "eq-a/fullsnap:x");
            Assert.Equal(2, n);                                   // box (value) and gone (presence)
            Assert.NotNull(model.Find("box~4.pose"));            // another sibling of the same name
            Assert.Null(model.Find("stable"));

            // Three vanilla runs agree, the mod differs on a learned key: noise with the reason, not a failure.
            SnapshotData v = S("box~2.pose", "1", "stable", "1");
            CompareResult r = SnapshotComparer.Compare(new[] { v, v, v }, S("box~2.pose", "5", "stable", "2"), new Tolerances(), null, "eq-b", model);
            Assert.Equal(1, r.Count(DiffClass.Noise));
            Assert.StartsWith("learned: varied in vanilla (eq-a/fullsnap:x)", r.Records.Single(x => x.Key == "box~2.pose").Reason);
            Assert.Equal(1, r.Count(DiffClass.Unexplained));      // "stable" never varied
        }
    }

    public class SnapshotComparerTests
    {
        static SnapshotData S(params string[] kv)
        {
            SnapshotData d = new SnapshotData();
            for (int i = 0; i < kv.Length; i += 2) d.Add(kv[i], kv[i + 1]);
            return d;
        }

        [Fact]
        public void Classifies_every_key()
        {
            SnapshotData a = S("same", "1", "noisy", "1", "explained", "1", "bug", "1", "gone", "1", "vanillaOnlyA", "1");
            SnapshotData b = S("same", "1", "noisy", "2", "explained", "1", "bug", "1", "gone", "1");
            SnapshotData m = S("same", "1", "noisy", "3", "explained", "9", "bug", "2", "new", "1");
            ExpectedDiffs exp = new ExpectedDiffs(new[] { new ExpectedDiff { Pattern = "expl*", Reason = "documented" } });

            CompareResult r = SnapshotComparer.Compare(a, b, m, new Tolerances(), exp, "eq-test");

            Assert.Equal(1, r.Count(DiffClass.Match));
            Assert.Equal(2, r.Count(DiffClass.Noise));          // noisy, vanillaOnlyA (only in one vanilla run)
            Assert.Equal(1, r.Count(DiffClass.Explained));
            Assert.Equal(1, r.Count(DiffClass.Unexplained));
            Assert.Equal(1, r.Count(DiffClass.OnlyVanilla));    // gone
            Assert.Equal(1, r.Count(DiffClass.OnlyMod));        // new
            Assert.Equal(3, r.Failures);
            Assert.Equal("documented", r.Of(DiffClass.Explained).Single().Reason);
            Assert.Equal("bug", r.Of(DiffClass.Unexplained).Single().Key);
            Assert.Equal(new[] { "bug", "explained", "gone", "new", "noisy", "vanillaOnlyA" }, r.Records.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void Several_vanilla_runs()
        {
            // "k": the mod equals the third vanilla run -> match. "n": runs 1 and 2 agree, run 3 differs -> noise.
            // "u": all vanilla runs agree, the mod differs -> unexplained.
            SnapshotData v1 = S("k", "1", "n", "1", "u", "1");
            SnapshotData v2 = S("k", "1", "n", "1", "u", "1");
            SnapshotData v3 = S("k", "2", "n", "5", "u", "1");
            SnapshotData m = S("k", "2", "n", "9", "u", "3");

            CompareResult r = SnapshotComparer.Compare(new[] { v1, v2, v3 }, m, null, null, "x");

            Assert.Equal(1, r.Count(DiffClass.Match));
            DiffRecord noise = r.Of(DiffClass.Noise).Single();
            Assert.Equal("n", noise.Key);
            Assert.Equal("5", noise.B);   // the vanilla run that differs from A
            Assert.Equal("u", r.Of(DiffClass.Unexplained).Single().Key);
            Assert.Throws<System.ArgumentException>(() => SnapshotComparer.Compare(new SnapshotData[0], m, null, null, "x"));
        }

        [Fact]
        public void Volatile_keys_are_noise_even_when_vanilla_agrees()
        {
            Tolerances t = new Tolerances(new[] { new ToleranceRule { Pattern = "MAP/CloudSystem/*", Volatile = true, Reason = "weather" } });
            CompareResult r = SnapshotComparer.Compare(new[] { S("MAP/CloudSystem/a.go", "1", "b", "1"), S("MAP/CloudSystem/a.go", "1", "b", "1") },
                                                       S("MAP/CloudSystem/a.go", "0", "b", "2"), t, null, "x");
            DiffRecord n = r.Of(DiffClass.Noise).Single();
            Assert.Equal("MAP/CloudSystem/a.go", n.Key);
            Assert.Equal("volatile: weather", n.Reason);
            Assert.Equal("b", r.Of(DiffClass.Unexplained).Single().Key);
        }

        [Fact]
        public void Without_second_vanilla_run_there_is_no_noise()
        {
            CompareResult r = SnapshotComparer.Compare(S("k", "1"), null, S("k", "2"), null, null, "x");
            Assert.Equal(1, r.Count(DiffClass.Unexplained));
            Assert.Equal(0, r.Count(DiffClass.Noise));
        }

        [Fact]
        public void Expected_differences_are_scoped_and_counted()
        {
            ExpectedDiffs exp = new ExpectedDiffs(new[]
            {
                new ExpectedDiff { Pattern = "k", Scenario = "eq-home*", Reason = "only at home" },
                new ExpectedDiff { Pattern = "never", Reason = "stale entry" }
            });
            CompareResult home = SnapshotComparer.Compare(S("k", "1"), null, S("k", "2"), null, exp, "eq-home-yard");
            CompareResult trip = SnapshotComparer.Compare(S("k", "1"), null, S("k", "2"), null, exp, "eq-perajarvi-trip");
            Assert.Equal(1, home.Count(DiffClass.Explained));
            Assert.Equal(1, trip.Count(DiffClass.Unexplained));
            Assert.Equal(new[] { "never" }, exp.Stale().Select(e => e.Pattern).ToArray());
        }

        [Fact]
        public void Expected_entries_need_a_reason()
        {
            Assert.Throws<System.ArgumentException>(() => new ExpectedDiffs(new[] { new ExpectedDiff { Pattern = "k" } }));
            Assert.Throws<System.ArgumentException>(() => new Tolerances(new[] { new ToleranceRule { Pattern = "" } }));
            Assert.Throws<System.ArgumentException>(() => new Tolerances(new[] { new ToleranceRule { Pattern = "k", Abs = -1 } }));
        }

        [Fact]
        public void Parses_tsv_with_metadata()
        {
            SnapshotData d = SnapshotData.Parse("#frame\t600\na.go\t1,0,Untagged\nb\t\nb\tx\n");
            Assert.Equal("600", d.Meta["frame"]);
            Assert.Equal("1,0,Untagged", d.Values["a.go"]);
            Assert.Equal("x", d.Values["b"]);
            Assert.Equal(1, d.DuplicateKeys);
            Assert.Equal(2, d.Count);
        }

        [Fact]
        public void Save_files_become_typed_keys()
        {
            byte[] es2;
            using (var ms = new System.IO.MemoryStream())
            using (var w = new System.IO.BinaryWriter(ms))
            {
                var inner = new System.IO.MemoryStream();
                using (var iw = new System.IO.BinaryWriter(inner, System.Text.Encoding.UTF8, true))
                {
                    iw.Write((byte)255); iw.Write(MOPR6.Core.Es2.Es2Types.Hash("UnityEngine.Quaternion"));
                    iw.Write(0f); iw.Write(0f); iw.Write(0f); iw.Write(1f); iw.Write((byte)123);
                }
                w.Write((byte)126); w.Write("Rot"); w.Write((int)inner.Length); w.Write(inner.ToArray());
                w.Flush();
                es2 = ms.ToArray();
            }
            SnapshotData d = new SnapshotData();
            Assert.Empty(SaveSnapshot.AddFile(d, "defaultES2File.txt", es2));
            SaveSnapshot.AddFile(d, "notes.txt", System.Text.Encoding.UTF8.GetBytes("Win+R\r\nline2"));
            Assert.Equal("0,0,0,1", d.Values["save:defaultES2File.txt|Rot!q"]);
            Assert.Equal("line2", d.Values["save:notes.txt:2"]);
            Assert.True(SaveSnapshot.IsIgnoredFile("README.txt"));
        }
    }
}
