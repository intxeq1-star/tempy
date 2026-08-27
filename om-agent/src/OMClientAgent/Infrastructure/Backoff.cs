namespace OMClientAgent.Infrastructure;

public static class Backoff
{
    private static readonly int[] BaseSecondsList = { 1, 2, 4, 8, 15, 30, 60, 60, 60, 60 };

    public static TimeSpan DelaySeconds(int attempt, int maxSeconds)
    {
        if (attempt < 0) attempt = 0;
        var index = Math.Min(attempt, BaseSecondsList.Length - 1);
        var seconds = BaseSecondsList[index];
        seconds = Math.Min(seconds, Math.Max(1, maxSeconds));
        return AddJitter(seconds);
    }

    private static TimeSpan AddJitter(int seconds)
    {
        var random = Random.Shared;
        var fraction = 0.8 + (random.NextDouble() * 0.4);
        var ms = (int)(seconds * 1000 * fraction);
        return TimeSpan.FromMilliseconds(Math.Max(250, ms));
    }
}
