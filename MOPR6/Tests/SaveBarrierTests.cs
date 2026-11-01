using MOPR6.Core.Save;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class SaveBarrierTests
    {
        [Fact]
        public void Save_and_quit_holds_until_the_scene_is_gone()
        {
            SaveBarrier b = new SaveBarrier();
            Assert.True(b.Raise("SAVEGAME living room", 10));
            Assert.False(b.Raise("Save* action", 10.1));    // second trigger of the same save
            b.Written();
            for (double t = 10.25; t < 20; t += 0.25) Assert.Equal(BarrierAction.None, b.Tick(t, savePointIdle: false));
            b.Unloaded();
            Assert.Equal(BarrierState.Live, b.State);
            Assert.Equal(0, b.Cancels);
        }

        [Fact]
        public void A_cancelled_save_resumes_within_two_seconds()
        {
            // Requirement 6.4: the player looks away from the save point - the FSM goes back to waiting.
            SaveBarrier b = new SaveBarrier();
            b.Raise("SAVEGAME", 0);
            double resumed = -1;
            for (double t = 0.25; t <= 5; t += 0.25)
                if (b.Tick(t, savePointIdle: t >= 0.5) == BarrierAction.Resume) { resumed = t; break; }
            Assert.Equal(0.5, resumed);
            Assert.Equal(1, b.Cancels);
            Assert.Equal(BarrierState.Live, b.State);
        }

        [Fact]
        public void Nothing_written_counts_as_cancelled()
        {
            SaveBarrier b = new SaveBarrier();
            b.Raise("death", 0);
            double resumed = -1;
            for (double t = 0.25; t <= 5; t += 0.25)
                if (b.Tick(t, savePointIdle: false) == BarrierAction.Resume) { resumed = t; break; }
            Assert.True(resumed > 0 && resumed <= 2.0, "resumed at " + resumed);
        }

        [Fact]
        public void A_save_without_quitting_resumes_when_finished()
        {
            SaveBarrier b = new SaveBarrier();
            b.Raise("phone", 0);
            b.Written();
            Assert.Equal(BarrierAction.None, b.Tick(3, savePointIdle: true));   // idle but written: not a cancel
            b.Finished();
            Assert.Equal(BarrierAction.Resume, b.Tick(3.25, savePointIdle: true));
            Assert.Equal(0, b.Cancels);
            Assert.True(b.Raise("again", 10));
            Assert.Equal(2, b.Raises);
        }
    }
}
