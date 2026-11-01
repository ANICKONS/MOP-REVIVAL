using System;
using System.Collections.Generic;
using MOPR6.Core.Errors;
using Xunit;

namespace MOPR6.Core.Tests
{
    public class ErrorSinkTests
    {
        sealed class ListOutput : IErrorOutput
        {
            public readonly List<ErrorRecord> Written = new List<ErrorRecord>();
            public void Write(ErrorRecord first) => Written.Add(first);
        }

        static Exception Thrown()
        {
            try { throw new InvalidOperationException("boom"); }
            catch (InvalidOperationException e) { return e; }
        }

        [Fact]
        public void Writes_once_per_place_and_type_and_counts_the_rest()
        {
            ListOutput o = new ListOutput();
            ErrorSink s = new ErrorSink(o);
            Exception e = Thrown();
            Assert.True(s.Report("engine/Update", e, "PLAYER#Look", 1));
            Assert.False(s.Report("engine/Update", e, "PLAYER#Look", 2));
            Assert.False(s.Report("engine/Update", e, null, 3));
            Assert.True(s.Report("engine/LateUpdate", e, null, 4));
            Assert.True(s.Report("engine/Update", new ArgumentException("x"), null, 5));

            Assert.Equal(3, o.Written.Count);
            Assert.Equal(5, s.Total);
            ErrorRecord first = s.Records[0];
            Assert.Equal(3, first.Count);
            Assert.Equal(1, first.FirstTime);
            Assert.Equal(3, first.LastTime);
            Assert.Equal("PLAYER#Look", first.Context);
            Assert.Contains("Thrown", first.Stack);
            Assert.Equal("engine/Update: InvalidOperationException: boom [PLAYER#Look] (x3)", first.ToString());
        }

        [Fact]
        public void Bounded_number_of_keys()
        {
            ErrorSink s = new ErrorSink(null) { MaxKeys = 2 };
            s.Report("a", "T", "m", null, null, 0);
            s.Report("b", "T", "m", null, null, 0);
            Assert.False(s.Report("c", "T", "m", null, null, 0));
            Assert.Equal(2, s.Records.Count);
            Assert.Equal(1, s.Dropped);
            s.Clear();
            Assert.Equal(0, s.Total);
            Assert.Empty(s.Records);
        }
    }

    public class FailurePolicyTests
    {
        [Fact]
        public void Trips_on_the_fourth_failure_within_a_minute()
        {
            FailurePolicy p = new FailurePolicy();
            Assert.False(p.Fail(0));
            Assert.False(p.Fail(10));
            Assert.False(p.Fail(20));
            Assert.True(p.Fail(30));
            Assert.True(p.Tripped);
            Assert.False(p.Fail(31));   // trips once
            Assert.Equal(5, p.TotalFailures);
        }

        [Fact]
        public void Old_failures_leave_the_window()
        {
            FailurePolicy p = new FailurePolicy(3, 60);
            p.Fail(0);
            p.Fail(1);
            p.Fail(2);
            Assert.False(p.Fail(61.5));     // 0 and 1 are older than 60 s
            Assert.Equal(2, p.RecentFailures(61.5));
            Assert.False(p.Tripped);
        }

        [Fact]
        public void Reset_after_reinstall()
        {
            FailurePolicy p = new FailurePolicy(0, 60);
            Assert.True(p.Fail(0));
            p.Reset();
            Assert.False(p.Tripped);
            Assert.Throws<ArgumentOutOfRangeException>(() => new FailurePolicy(-1, 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FailurePolicy(3, 0));
        }
    }
}
