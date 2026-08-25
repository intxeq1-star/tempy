using LanAgent.Core.Util;
using Xunit;

namespace LanAgent.Core.Tests;

public class BackoffTests
{
    [Fact]
    public void Grows_exponentially_up_to_cap()
    {
        var backoff = new BackoffCalculator(initialSeconds: 2, maxSeconds: 60, multiplier: 2.0, jitterFraction: 0.0);
        Assert.Equal(2.0, backoff.Next().TotalSeconds, 3);
        Assert.Equal(4.0, backoff.Next().TotalSeconds, 3);
        Assert.Equal(8.0, backoff.Next().TotalSeconds, 3);
        Assert.Equal(16.0, backoff.Next().TotalSeconds, 3);
        Assert.Equal(32.0, backoff.Next().TotalSeconds, 3);
        Assert.Equal(60.0, backoff.Next().TotalSeconds, 3); // capped
        Assert.Equal(60.0, backoff.Next().TotalSeconds, 3); // still capped
        Assert.Equal(6, backoff.AttemptCount);
    }

    [Fact]
    public void Jitter_stays_within_bounds()
    {
        var backoff = new BackoffCalculator(initialSeconds: 10, maxSeconds: 60, multiplier: 2.0, jitterFraction: 0.25);
        for (int i = 0; i < 100; i++)
        {
            double seconds = backoff.ResetAndNext();
            Assert.InRange(seconds, 10 * 0.75 - 0.01, 10 * 1.25 + 0.01);
        }
    }

    [Fact]
    public void Reset_returns_to_initial()
    {
        var backoff = new BackoffCalculator(2, 60, 2.0, 0.0);
        backoff.Next(); backoff.Next(); backoff.Next();
        backoff.Reset();
        Assert.Equal(0, backoff.AttemptCount);
        Assert.Equal(2.0, backoff.Next().TotalSeconds, 3);
    }

    [Fact]
    public void Constructor_validates_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffCalculator(0, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffCalculator(10, 5));
    }
}

internal static class BackoffTestExtensions
{
    public static double ResetAndNext(this BackoffCalculator calculator)
    {
        calculator.Reset();
        return calculator.Next().TotalSeconds;
    }
}
