using System.Collections.Generic;
using MOPR6.Core.Ledger;
using Xunit;

namespace MOPR6.Core.Tests
{
    /// <summary>The arbiter of computed values (camera layer cull distances, shadow distance) and array values.</summary>
    public class ValueArbiterTests
    {
        static PropValue D(params float[] v) => PropValue.Floats(v);

        static (FakeScene, ChangeLedger, ValueArbiter) Setup()
        {
            FakeScene s = new FakeScene();
            s.Put(7, PropKind.CameraLayerCull, D(0, 0, 0, 500));
            ChangeLedger l = new ChangeLedger(s, null);
            return (s, l, new ValueArbiter(l, "RenderArbiter", PropKind.CameraLayerCull));
        }

        [Fact]
        public void Arrays_compare_by_value_and_are_copied()
        {
            float[] a = { 1, 2, 3 };
            PropValue v = PropValue.Floats(a);
            a[0] = 9;
            Assert.Equal(D(1, 2, 3), v);
            Assert.NotEqual(D(1, 2, 3.0001f), v);
            Assert.NotEqual(D(1, 2), v);
            Assert.Equal("[1 2 3]", v.ToString());
        }

        [Fact]
        public void Holds_a_computed_value_and_restores_the_original()
        {
            var (s, l, a) = Setup();
            Assert.Equal(SetResult.Ok, a.Set(7, "render.layers", D(60, 60, 0, 300)));
            Assert.Equal(SetResult.Ok, a.Set(7, "render.layers", D(80, 80, 0, 300)));   // recomputed: same holder
            Assert.Equal(D(80, 80, 0, 300), s[7, PropKind.CameraLayerCull]);
            Assert.Equal(D(0, 0, 0, 500), a.Original(7).Value);
            Assert.Equal("render.layers", a.Holder(7));

            a.Remove(7, "render.layers");
            Assert.Equal(D(0, 0, 0, 500), s[7, PropKind.CameraLayerCull]);
            Assert.Null(a.Holder(7));
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void A_second_system_is_refused()
        {
            var (s, _, a) = Setup();
            a.Set(7, "render.layers", D(60, 60, 0, 300));
            Assert.Equal(SetResult.DeniedOwner, a.Set(7, "experimental.x", D(1, 1, 1, 1)));
            Assert.Equal(D(60, 60, 0, 300), s[7, PropKind.CameraLayerCull]);
            a.Remove(7, "experimental.x");                       // not the holder: nothing happens
            Assert.Equal(D(60, 60, 0, 300), s[7, PropKind.CameraLayerCull]);
            Assert.Equal(1, a.RemoveSystem("render.layers"));
            Assert.Equal(D(0, 0, 0, 500), s[7, PropKind.CameraLayerCull]);
        }

        [Fact]
        public void The_game_takes_over_and_the_holder_is_told()
        {
            var (s, l, a) = Setup();
            List<string> dropped = new List<string>();
            a.Dropped += (key, system) => dropped.Add(system);
            a.Set(7, "render.layers", D(60, 60, 0, 300));
            s[7, PropKind.CameraLayerCull] = D(10, 10, 10, 10);   // the game writes its own distances

            int cursor = 0;
            Assert.Equal(1, l.CheckDrift(10, ref cursor));
            Assert.Equal(new[] { "render.layers" }, dropped.ToArray());
            Assert.Null(a.Holder(7));
            Assert.Equal(D(10, 10, 10, 10), s[7, PropKind.CameraLayerCull]);   // their value stays
            Assert.Equal(0, l.Count);

            // Computed again from the game's value: the new original is theirs.
            Assert.Equal(SetResult.Ok, a.Set(7, "render.layers", D(5, 5, 5, 5)));
            Assert.Equal(D(10, 10, 10, 10), a.Original(7).Value);
        }

        [Fact]
        public void Restore_all_brings_the_original_back()
        {
            var (s, l, a) = Setup();
            a.Set(7, "render.layers", D(60, 60, 0, 300));
            List<RestoreRecord> r = l.RestoreAll();
            a.Forget();
            Assert.Equal(RestoreOutcome.Restored, r[0].Outcome);
            Assert.Equal(D(0, 0, 0, 500), s[7, PropKind.CameraLayerCull]);
            Assert.Null(a.Holder(7));
        }
    }
}
