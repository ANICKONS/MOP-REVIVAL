using System;
using MOPR6.Core.Bench;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class BenchGateTests
    {
        static Sample S(params double[] v) => new Sample(v);

        [Fact]
        public void Sample_statistics()
        {
            Sample s = S(2, 4, 4, 4, 5, 5, 7, 9);
            Assert.Equal(5, s.Mean, 10);
            Assert.Equal(Math.Sqrt(32.0 / 7), s.StdDev, 10);
            Assert.Equal(0, S(3).StdDev);
            Assert.True(double.IsNaN(S().Mean));
        }

        [Fact]
        public void Fps_within_one_percent_passes()
        {
            // Single runs: only the 1 % margin.
            Assert.True(BenchGate.Check("home", GateMetric.AvgFps, S(198.1), S(200)).Pass);
            Assert.False(BenchGate.Check("home", GateMetric.AvgFps, S(197.9), S(200)).Pass);
            Assert.True(BenchGate.Check("home", GateMetric.AvgFps, S(250), S(200)).Pass);
        }

        [Fact]
        public void Noise_widens_the_margin()
        {
            // sigma = sqrt(4/3 + 4/3) = 1.633; 2 sigma = 3.27 > 1 % of 200.
            GateCheck g = BenchGate.Check("yard", GateMetric.AvgFps, S(194, 196, 198), S(198, 200, 202));
            Assert.Equal(Math.Sqrt(8.0 / 3), g.Sigma, 10);
            Assert.Equal(2 * Math.Sqrt(8.0 / 3), g.Margin, 10);
            Assert.False(g.Pass);   // 196 < 200 - 3.27
            Assert.True(BenchGate.Check("yard", GateMetric.AvgFps, S(195, 197, 199), S(198, 200, 202)).Pass);
            Assert.Equal(-1.5, BenchGate.Check("yard", GateMetric.AvgFps, S(195, 197, 199), S(198, 200, 202)).DeltaPercent, 10);
        }

        [Fact]
        public void P99_lower_is_better()
        {
            Assert.True(BenchGate.Check("road", GateMetric.P99Ms, S(10.2), S(10)).Pass);    // +2 % allowed
            Assert.False(BenchGate.Check("road", GateMetric.P99Ms, S(10.3), S(10)).Pass);
            Assert.True(BenchGate.Check("road", GateMetric.P99Ms, S(5), S(10)).Pass);
        }

        [Fact]
        public void Missing_points_fail_the_gate()
        {
            GateCheck missing = BenchGate.Check("shop", GateMetric.AvgFps, S(), S(100));
            Assert.True(missing.Missing);
            Assert.False(BenchGate.AllPass(new[] { missing }));
            Assert.False(BenchGate.AllPass(new GateCheck[0]));
            Assert.True(BenchGate.AllPass(new[] { BenchGate.Check("a", GateMetric.AvgFps, S(100), S(100)) }));
        }
    }
}
