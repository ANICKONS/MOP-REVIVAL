using MOPR6.Core.World;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class DormancyPolicyTests
    {
        static DormancyPolicy New() => new DormancyPolicy { FarSeconds = 10, CooldownSeconds = 30, MaxEventWakes = 3, WakeWindow = 300, BanSeconds = 300, MaxBanSeconds = 1200 };

        [Fact]
        public void Only_after_the_player_stayed_far()
        {
            DormancyPolicy p = New();
            Assert.Equal("near", p.MayDeactivate("STORE", 150, 200, 0));
            Assert.NotNull(p.MayDeactivate("STORE", 250, 200, 1));      // far from now on
            Assert.NotNull(p.MayDeactivate("STORE", 250, 200, 10.5));   // 9.5 s
            Assert.Null(p.MayDeactivate("STORE", 250, 200, 11));
            // Coming near starts the far stretch again.
            Assert.Equal("near", p.MayDeactivate("STORE", 190, 200, 12));
            Assert.NotNull(p.MayDeactivate("STORE", 250, 200, 13));
            Assert.Null(p.MayDeactivate("STORE", 250, 200, 23));
        }

        [Fact]
        public void Hysteresis()
        {
            DormancyPolicy p = New();
            Assert.False(p.MustWake(185, 200));
            Assert.True(p.MustWake(179, 200));
        }

        [Fact]
        public void Cooldown_after_a_wake()
        {
            DormancyPolicy p = New();
            p.MayDeactivate("CABIN", 300, 200, 0);
            Assert.Null(p.MayDeactivate("CABIN", 300, 200, 10));
            Assert.False(p.Woken("CABIN", 10, true));
            Assert.NotNull(p.MayDeactivate("CABIN", 300, 200, 30));     // far again only since 30, woken at 10
            Assert.NotNull(p.MayDeactivate("CABIN", 300, 200, 39.9));   // cooldown until 40
            Assert.Null(p.MayDeactivate("CABIN", 300, 200, 40));
        }

        [Fact]
        public void Restless_parts_are_banned_with_doubling()
        {
            DormancyPolicy p = New();
            Assert.False(p.Woken("DANCEHALL", 0, true));
            Assert.False(p.Woken("DANCEHALL", 10, true));
            Assert.False(p.Woken("DANCEHALL", 20, false));               // the player came near: not counted
            Assert.False(p.Woken("DANCEHALL", 30, true));
            Assert.True(p.Woken("DANCEHALL", 40, true, "event X"));      // the 4th by events within 300 s
            Assert.True(p.IsBanned("DANCEHALL", 339));
            Assert.False(p.IsBanned("DANCEHALL", 341));
            Assert.Contains("restless", p.MayDeactivate("DANCEHALL", 900, 500, 100));
            for (int i = 0; i < 4; i++) p.Woken("DANCEHALL", 400 + i, true);
            Assert.Equal(600, p.BanLength("DANCEHALL"));
            p.Ban("DANCEHALL", 1000, "the game");
            p.Ban("DANCEHALL", 1000, "the game");
            Assert.Equal(1200, p.BanLength("DANCEHALL"));               // capped
            Assert.Equal(4, p.Bans);
        }

        [Fact]
        public void Old_wakes_do_not_count()
        {
            DormancyPolicy p = New();
            for (int i = 0; i < 10; i++) Assert.False(p.Woken("SOCCER", i * 200, true));
        }
    }
}
