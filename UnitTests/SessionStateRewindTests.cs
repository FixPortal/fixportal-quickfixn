using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuickFix;
using QuickFix.Store;

namespace UnitTests;

[TestFixture]
public class SessionStateRewindTests
{
    private static SessionState NewState(ulong next)
    {
        var s = new SessionState(false, NullLogger.Instance, 30, new MemoryStore());
        s.NextTargetMsgSeqNum = next;
        return s;
    }

    [TestCase(10UL, 3UL, true, 7UL)]
    [TestCase(10UL, 9UL, true, 1UL)]
    [TestCase(10UL, 10UL, false, 10UL)]
    [TestCase(10UL, 11UL, false, 10UL)]
    [TestCase(10UL, 0UL, false, 10UL)]
    public void TryRewind_AppliesOnlyWhenResultStaysAtOrAboveOne(ulong start, ulong by, bool ok, ulong expected)
    {
        var s = NewState(start);

        s.TryRewindNextTargetMsgSeqNum(by, out var current).Should().Be(ok);

        current.Should().Be(expected);
        s.NextTargetMsgSeqNum.Should().Be(expected);
    }

    [Test]
    public void TryRewind_ConcurrentWithIncrements_LosesNoUpdate()
    {
        const int increments = 100_000;
        const int rewinds = 20_000;
        for (var run = 0; run < 5; run++)
        {
            var s = NewState(1_000_000UL);
            using var go = new ManualResetEventSlim();
            var recv = Task.Run(() =>
            {
                go.Wait();
                for (var i = 0; i < increments; i++)
                    s.IncrNextTargetMsgSeqNum();
            });
            var rew = Task.Run(() =>
            {
                go.Wait();
                for (var i = 0; i < rewinds; i++)
                    s.TryRewindNextTargetMsgSeqNum(1UL, out _).Should().BeTrue();
            });
            go.Set();
            Task.WaitAll(recv, rew);

            s.NextTargetMsgSeqNum.Should().Be(1_000_000UL + increments - rewinds);
        }
    }
}
