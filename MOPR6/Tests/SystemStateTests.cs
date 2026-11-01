using System;
using MOPR6.Core.Host;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class SystemStateTests
    {
        [Fact]
        public void Install_suspend_resume_remove()
        {
            SystemState s = new SystemState("engine.fsmFastPath");
            Assert.Equal(SystemStatus.NotInstalled, s.Status);
            Assert.False(s.Live);

            s.Install(SystemMode.Verify);
            Assert.True(s.Live);
            Assert.Equal(SystemMode.Verify, s.Mode);
            Assert.Throws<InvalidOperationException>(() => s.Install(SystemMode.On));

            s.Suspend("save");
            Assert.False(s.Live);
            Assert.Equal("save", s.SuspendReason);
            s.Resume();
            Assert.True(s.Live);
            Assert.Null(s.SuspendReason);

            s.SetMode(SystemMode.On);
            Assert.Equal(SystemMode.On, s.Mode);
            s.Remove();
            Assert.Equal(SystemStatus.NotInstalled, s.Status);
            Assert.Throws<InvalidOperationException>(() => s.SetMode(SystemMode.Verify));
            s.Install(SystemMode.On);
            Assert.Equal(2, s.Installs);
        }

        [Fact]
        public void Trip_stays_until_installed_again()
        {
            SystemState s = new SystemState("dormancy");
            s.Install(SystemMode.On);
            s.Suspend("save");
            s.Trip("4 failures in 60 s");
            Assert.Equal(SystemStatus.Tripped, s.Status);
            Assert.False(s.Live);
            s.Resume();                      // a save ends: a tripped system does not come back
            Assert.Equal(SystemStatus.Tripped, s.Status);
            s.Remove();
            Assert.Equal(SystemStatus.Tripped, s.Status);
            Assert.Contains("tripped: 4 failures", s.ToString());
            s.Install(SystemMode.On);
            Assert.True(s.Live);
            Assert.Null(s.TripReason);
        }

        [Fact]
        public void Harmless_calls_in_the_wrong_state()
        {
            SystemState s = new SystemState("render.layers");
            s.Suspend("x");
            s.Resume();
            s.Trip("y");
            s.Remove();
            Assert.Equal(SystemStatus.NotInstalled, s.Status);
            Assert.Throws<ArgumentException>(() => new SystemState(""));
        }
    }
}
