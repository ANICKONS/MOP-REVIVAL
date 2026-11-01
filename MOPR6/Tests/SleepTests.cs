using System.Collections.Generic;
using MOPR6.Core.Sleep;
using Xunit;

namespace MOPR6.Core.Tests
{
    /// <summary>Script sleep logic (requirement 8.1, 8.4): hysteresis, restless FSMs, the place in PlayMaker's FSM list.</summary>
    public class SleepTests
    {
        sealed class Fsm { public readonly string Name; public Fsm(string n) { Name = n; } public override string ToString() => Name; }

        [Fact]
        public void Sleeps_only_after_30_frames_and_half_a_second_in_the_same_state()
        {
            SleepPolicy p = new SleepPolicy();
            Fsm f = new Fsm("a");
            object idle = new object(), other = new object();
            Assert.False(p.Idle(f, idle, 100, 10.0));      // the stretch starts
            Assert.False(p.Idle(f, idle, 129, 11.0));      // 29 frames
            Assert.False(p.Idle(f, idle, 200, 10.4));      // 100 frames but 0.4 s
            Assert.True(p.Idle(f, idle, 130, 10.5));       // 30 frames and 0.5 s
            Assert.False(p.Idle(f, other, 131, 10.6));     // another state: again from the start
            Assert.True(p.Idle(f, other, 161, 11.1));
            p.Busy(f);                                     // a state was entered
            Assert.False(p.Idle(f, other, 300, 20.0));
        }

        [Fact]
        public void A_restless_fsm_is_banned_and_the_ban_grows()
        {
            SleepPolicy p = new SleepPolicy();
            Fsm f = new Fsm("a");
            object s = new object();
            for (int i = 0; i < 5; i++) Assert.False(p.Woken(f, 1.0 + i));   // 5 wakes in 10 s: fine
            Assert.True(p.Woken(f, 6.0));                                    // the 6th: banned for 30 s
            Assert.True(p.IsBanned(f, 35.9));
            Assert.False(p.Idle(f, s, 1000, 20.0));
            Assert.False(p.IsBanned(f, 36.1));
            for (int i = 0; i < 5; i++) p.Woken(f, 40.0 + i);
            Assert.True(p.Woken(f, 45.0));                                   // again: 60 s
            Assert.True(p.IsBanned(f, 104.9));
            Assert.False(p.IsBanned(f, 105.1));
            Assert.Equal(2, p.Bans);
        }

        [Fact]
        public void Old_wakes_leave_the_window()
        {
            SleepPolicy p = new SleepPolicy();
            Fsm f = new Fsm("a");
            for (int i = 0; i < 20; i++) Assert.False(p.Woken(f, i * 3.0));  // one every 3 s: never more than 4 in 10 s
        }

        static List<Fsm> List(params string[] names)
        {
            List<Fsm> l = new List<Fsm>();
            foreach (string n in names) l.Add(new Fsm(n));
            return l;
        }

        static string Names(List<Fsm> l) => string.Join(",", l.ConvertAll(x => x.Name).ToArray());

        [Fact]
        public void A_woken_fsm_goes_back_to_its_place()
        {
            List<Fsm> list = List("a", "b", "c", "d");
            OrderKeeper<Fsm> k = new OrderKeeper<Fsm>();
            k.Reset(list);
            Fsm b = list[1];
            list.Remove(b);                     // b sleeps (OnDisable)
            Fsm e = new Fsm("e");
            list.Add(e);                        // e is enabled by the game: end of the list
            k.Enabled(e);
            list.Add(b);                        // b wakes: OnEnable appends it
            Assert.Equal(1, k.Restore(list, b));
            Assert.Equal("a,b,c,d,e", Names(list));
            Assert.Equal(0, k.Violations(list));
        }

        [Fact]
        public void An_fsm_enabled_again_by_the_game_gets_a_new_place()
        {
            List<Fsm> list = List("a", "b", "c");
            OrderKeeper<Fsm> k = new OrderKeeper<Fsm>();
            k.Reset(list);
            Fsm a = list[0];
            list.Remove(a);                     // the game disables a and enables it again: the end, as in the game
            list.Add(a);
            k.Enabled(a);
            Assert.Equal("b,c,a", Names(list));
            Assert.Equal(0, k.Violations(list));
            Fsm c = list[1];
            list.Remove(c);                     // c sleeps and wakes
            list.Add(c);
            Assert.Equal(1, k.Restore(list, c));
            Assert.Equal("b,c,a", Names(list));
        }

        [Fact]
        public void Restore_moves_nothing_it_does_not_know()
        {
            List<Fsm> list = List("a", "b");
            OrderKeeper<Fsm> k = new OrderKeeper<Fsm>();
            k.Reset(list);
            Fsm x = new Fsm("x");
            list.Add(x);
            Assert.Equal(-1, k.Restore(list, x));            // no stamp
            Assert.Equal(-1, k.Restore(list, list[0]));      // not the last element
            Assert.Equal("a,b,x", Names(list));
            Assert.Equal(0, k.Violations(list));             // items without a stamp are not counted
        }

        [Fact]
        public void Violations_count_out_of_order_items()
        {
            List<Fsm> list = List("a", "b", "c");
            OrderKeeper<Fsm> k = new OrderKeeper<Fsm>();
            k.Reset(list);
            list.Reverse();
            Assert.Equal(2, k.Violations(list));
            Assert.Equal(1, k.Purge(f => f.Name == "b"));
            Assert.Equal(1, k.Violations(list));
        }

        [Fact]
        public void One_item_in_a_wrong_place_is_one_violation()
        {
            List<Fsm> list = List("a", "b", "c", "d", "e");
            OrderKeeper<Fsm> k = new OrderKeeper<Fsm>();
            k.Reset(list);
            Fsm e = list[4];
            list.RemoveAt(4);
            list.Insert(1, e);                  // a,e,b,c,d: e jumped over three
            Assert.Equal(1, k.Violations(list));
        }

        [Fact]
        public void A_stamp_taken_before_nested_enables_keeps_the_list_order()
        {
            // The game enables a (prefix: stamped), a's OnEnable restarts it and its actions enable b (nested): the
            // list is a,b and so are the stamps. A stamp taken after the OnEnable (postfix) would put a after b.
            List<Fsm> list = new List<Fsm>();
            OrderKeeper<Fsm> k = new OrderKeeper<Fsm>();
            k.Reset(list);
            Fsm a = new Fsm("a"), b = new Fsm("b");
            k.Enabled(a);
            list.Add(a);
            k.Enabled(b);
            list.Add(b);
            Assert.Equal(0, k.Violations(list));
            list.Remove(a);                     // a sleeps and wakes: back before b
            list.Add(a);
            Assert.Equal(0, k.Restore(list, a));
            Assert.Equal("a,b", Names(list));
        }
    }
}
