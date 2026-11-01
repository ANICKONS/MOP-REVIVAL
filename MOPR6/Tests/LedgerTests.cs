using System.Collections.Generic;
using System.Linq;
using MOPR6.Core.Ledger;
using Xunit;

namespace MOPR6.Core.Tests
{
    /// <summary>A scene of fake objects: id -> property values, depth for the hierarchy.</summary>
    sealed class FakeScene : IPropertyAccess
    {
        public readonly Dictionary<long, PropValue> Values = new Dictionary<long, PropValue>();
        public readonly Dictionary<int, int> Depths = new Dictionary<int, int>();
        public readonly HashSet<int> Gone = new HashSet<int>();
        public readonly List<string> Writes = new List<string>();
        /// <summary>Writes to these keys do not stick (a component that resets itself).</summary>
        public readonly HashSet<long> Stubborn = new HashSet<long>();

        public void Put(int id, PropKind kind, PropValue v, int depth = 0)
        {
            Values[new PropKey(id, kind).Packed] = v;
            Depths[id] = depth;
        }

        public PropValue this[int id, PropKind kind]
        {
            get => Values[new PropKey(id, kind).Packed];
            set => Values[new PropKey(id, kind).Packed] = value;
        }

        public bool TryGet(PropKey key, out PropValue value)
        {
            value = default(PropValue);
            if (Gone.Contains(key.Target)) return false;
            return Values.TryGetValue(key.Packed, out value);
        }

        public bool TrySet(PropKey key, PropValue value)
        {
            if (Gone.Contains(key.Target) || !Values.ContainsKey(key.Packed)) return false;
            Writes.Add(key + "=" + value);
            if (!Stubborn.Contains(key.Packed)) Values[key.Packed] = value;
            return true;
        }

        public int Depth(int target) => Depths.TryGetValue(target, out int d) ? d : 0;
        public string Describe(int target) => "obj" + target;
    }

    public class LedgerTests
    {
        static readonly PropValue T = PropValue.Bool(true), F = PropValue.Bool(false);
        static PropKey K(int id, PropKind kind) => new PropKey(id, kind);

        [Fact]
        public void Keeps_the_original_and_restores_it()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RendererEnabled, T);
            s.Put(2, PropKind.Layer, PropValue.Int(5));
            ChangeLedger l = new ChangeLedger(s, null);

            Assert.Equal(SetResult.Ok, l.Set("render", K(1, PropKind.RendererEnabled), F));
            Assert.Equal(SetResult.Ok, l.Set("render", K(1, PropKind.RendererEnabled), F));   // again: same owner
            Assert.Equal(SetResult.Ok, l.Set("world", K(2, PropKind.Layer), PropValue.Int(9)));
            Assert.Equal(F, s[1, PropKind.RendererEnabled]);
            Assert.Equal(2, l.Count);
            Assert.Equal(T, l.Find(K(1, PropKind.RendererEnabled)).Original);

            List<RestoreRecord> r = l.RestoreAll();
            Assert.All(r, x => Assert.Equal(RestoreOutcome.Restored, x.Outcome));
            Assert.Equal(T, s[1, PropKind.RendererEnabled]);
            Assert.Equal(PropValue.Int(5), s[2, PropKind.Layer]);
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void Refuses_a_second_owner()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.FsmEnabled, T);
            List<string> log = new List<string>();
            ChangeLedger l = new ChangeLedger(s, log.Add);
            l.Set("dormancy", K(1, PropKind.FsmEnabled), F);
            Assert.Equal(SetResult.DeniedOwner, l.Set("world", K(1, PropKind.FsmEnabled), F));
            Assert.Equal(1, l.Conflicts);
            Assert.Contains(log, m => m.Contains("owned by dormancy"));
            Assert.Equal(RestoreOutcome.Failed, l.Release("world", K(1, PropKind.FsmEnabled)));
            Assert.Equal(F, s[1, PropKind.FsmEnabled]);

            l.ThrowOnConflict = true;
            Assert.Throws<ConflictException>(() => l.Set("world", K(1, PropKind.FsmEnabled), F));
        }

        [Fact]
        public void Leaves_a_value_the_game_changed()
        {
            // Requirement 5.4: the player picks up an item the mod froze - the game's value stays.
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RbKinematic, F);
            List<LedgerEntry> external = new List<LedgerEntry>();
            ChangeLedger l = new ChangeLedger(s, null);
            l.External += external.Add;
            l.Set("world", K(1, PropKind.RbKinematic), T);
            s[1, PropKind.RbKinematic] = F;   // the game unfreezes it (the pick-up action)

            List<RestoreRecord> r = l.RestoreAll();
            Assert.Equal(RestoreOutcome.External, r.Single().Outcome);
            Assert.Equal(F, s[1, PropKind.RbKinematic]);
            Assert.Single(external);
            Assert.Equal(1, l.ExternalChanges);
        }

        [Fact]
        public void A_known_writer_with_the_same_value_is_external_too()
        {
            // The game hides a renderer the mod hid already: without the writer hook a restore would show it again.
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RendererEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            l.Set("render", K(1, PropKind.RendererEnabled), F);
            l.NoteExternalWrite(K(1, PropKind.RendererEnabled), F, "ActivateGameObject");
            Assert.Equal(RestoreOutcome.External, l.Release("render", K(1, PropKind.RendererEnabled)));
            Assert.Equal(F, s[1, PropKind.RendererEnabled]);
        }

        [Fact]
        public void Restore_order_parents_first_created_last()
        {
            FakeScene s = new FakeScene();
            s.Put(10, PropKind.GoActive, T, depth: 2);   // child
            s.Put(11, PropKind.GoActive, T, depth: 0);   // root
            s.Put(12, PropKind.GoActive, T, depth: 1);
            s.Put(20, PropKind.CreatedObject, T);
            s.Put(30, PropKind.RendererEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            l.Set("world", K(11, PropKind.GoActive), F);
            l.Set("world", K(10, PropKind.GoActive), F);
            l.Set("merge", K(20, PropKind.CreatedObject), T);
            l.Set("world", K(12, PropKind.GoActive), F);
            l.Set("render", K(30, PropKind.RendererEnabled), F);
            s.Writes.Clear();

            l.RestoreAll();
            Assert.Equal(new[] { "#30.RendererEnabled=true", "#11.GoActive=true", "#12.GoActive=true", "#10.GoActive=true", "#20.CreatedObject=true" }, s.Writes.ToArray());
        }

        [Fact]
        public void Restart_flags_come_back_after_the_objects_are_on()
        {
            // A part switched off by the world (design §9.2): its FSMs paused first, then the object off. The restore
            // must switch the object on while the FSMs are still paused, else they start over in OnEnable.
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.FsmRestartOnEnable, T, depth: 3);
            s.Put(2, PropKind.FsmEnabled, T, depth: 3);
            s.Put(5, PropKind.GoActive, T, depth: 2);
            ChangeLedger l = new ChangeLedger(s, null);
            l.Set("FsmControl.pause", K(1, PropKind.FsmRestartOnEnable), F);
            l.Set("FsmControl.sleep", K(2, PropKind.FsmEnabled), F);
            l.Set("ActivationArbiter", K(5, PropKind.GoActive), F);
            s.Writes.Clear();

            l.RestoreAll();
            Assert.Equal(new[] { "#2.FsmEnabled=true", "#5.GoActive=true", "#1.FsmRestartOnEnable=true" }, s.Writes.ToArray());
        }

        [Fact]
        public void Frozen_during_the_save_barrier()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RendererEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            l.Freeze();
            Assert.Equal(SetResult.Frozen, l.Set("render", K(1, PropKind.RendererEnabled), F));
            Assert.Equal(T, s[1, PropKind.RendererEnabled]);
            l.Thaw();
            Assert.Equal(SetResult.Ok, l.Set("render", K(1, PropKind.RendererEnabled), F));
        }

        [Fact]
        public void Gone_objects_and_writes_that_do_not_stick()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RendererEnabled, T);
            s.Put(2, PropKind.BehaviourEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            Assert.Equal(SetResult.TargetGone, l.Set("x", K(9, PropKind.Layer), PropValue.Int(1)));
            l.Set("x", K(1, PropKind.RendererEnabled), F);
            l.Set("x", K(2, PropKind.BehaviourEnabled), F);
            s.Gone.Add(1);
            s.Stubborn.Add(K(2, PropKind.BehaviourEnabled).Packed);

            Dictionary<PropKind, RestoreOutcome> r = l.RestoreAll().ToDictionary(x => x.Key.Kind, x => x.Outcome);
            Assert.Equal(RestoreOutcome.TargetGone, r[PropKind.RendererEnabled]);
            Assert.Equal(RestoreOutcome.Failed, r[PropKind.BehaviourEnabled]);
        }

        [Fact]
        public void Drift_check_and_verify()
        {
            FakeScene s = new FakeScene();
            for (int i = 1; i <= 4; i++) s.Put(i, PropKind.ColliderEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            for (int i = 1; i <= 4; i++) l.Set("world", K(i, PropKind.ColliderEnabled), F);
            s[3, PropKind.ColliderEnabled] = T;
            Assert.Single(l.Verify());
            int cursor = 0;
            Assert.Equal(1, l.CheckDrift(10, ref cursor));
            Assert.Empty(l.Verify());   // external entries are not "wrong" any more
        }
    }

    public class ArbiterTests
    {
        static readonly PropValue T = PropValue.Bool(true), F = PropValue.Bool(false);

        [Fact]
        public void Held_while_any_reason_remains()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RendererEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            ReasonArbiter render = new ReasonArbiter(l, "RenderArbiter", PropKind.RendererEnabled, F);

            Assert.Equal(SetResult.Ok, render.Add(1, "render.layers"));
            Assert.Equal(SetResult.Ok, render.Add(1, "world"));
            Assert.Equal(2, render.ReasonCount(1));
            Assert.Equal(F, s[1, PropKind.RendererEnabled]);

            render.Remove(1, "render.layers");
            Assert.Equal(F, s[1, PropKind.RendererEnabled]);
            render.Remove(1, "world");
            Assert.Equal(T, s[1, PropKind.RendererEnabled]);
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void Removing_a_system_releases_only_its_properties()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.FsmEnabled, T);
            s.Put(2, PropKind.FsmEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            ReasonArbiter fsm = new ReasonArbiter(l, "FsmControl", PropKind.FsmEnabled, F);
            fsm.Add(1, "dormancy");
            fsm.Add(2, "dormancy");
            fsm.Add(2, "world");
            Assert.Equal(1, fsm.RemoveSystem("dormancy"));
            Assert.Equal(T, s[1, PropKind.FsmEnabled]);
            Assert.Equal(F, s[2, PropKind.FsmEnabled]);
            Assert.True(fsm.Has(2, "world"));
        }

        [Fact]
        public void The_game_taking_over_drops_the_reasons()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.RbKinematic, F);
            ChangeLedger l = new ChangeLedger(s, null);
            ReasonArbiter physics = new ReasonArbiter(l, "PhysicsArbiter", PropKind.RbKinematic, T);
            List<string> dropped = new List<string>();
            physics.Dropped += (k, systems) => dropped.AddRange(systems);
            physics.Add(1, "world");

            l.NoteExternalWrite(new PropKey(1, PropKind.RbKinematic), F, "PickUp");
            Assert.Equal(new[] { "world" }, dropped.ToArray());
            Assert.Equal(0, physics.Held);
            Assert.Equal(0, l.Count);
            // The system may ask again later (the item is at rest again): a fresh original is taken.
            s[1, PropKind.RbKinematic] = F;
            Assert.Equal(SetResult.Ok, physics.Add(1, "world"));
            Assert.Equal(F, l.Find(new PropKey(1, PropKind.RbKinematic)).Original);
        }

        [Fact]
        public void Two_arbiters_cannot_own_the_same_kind_of_one_object()
        {
            FakeScene s = new FakeScene();
            s.Put(1, PropKind.BehaviourEnabled, T);
            ChangeLedger l = new ChangeLedger(s, null);
            ReasonArbiter a = new ReasonArbiter(l, "BehaviourArbiter", PropKind.BehaviourEnabled, F);
            ReasonArbiter b = new ReasonArbiter(l, "Other", PropKind.BehaviourEnabled, F);
            a.Add(1, "x");
            Assert.Equal(SetResult.DeniedOwner, b.Add(1, "y"));
            Assert.Equal(0, b.Held);
        }
    }
}
