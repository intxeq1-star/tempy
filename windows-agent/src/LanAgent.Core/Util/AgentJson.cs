using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanAgent.Core.Util;

/// <summary>JSON helpers. All protocol serialization goes through here so field naming is exact.</summary>
public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,          // dictionary keys and POCO property names are written verbatim (snake_case by convention)
        DictionaryKeyPolicy = null,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Serialize(object? value)
        => JsonSerializer.Serialize(value, Options);

    public static JsonDocument ParseDocument(string json)
        => JsonDocument.Parse(json);

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException($"Deserialized null for {typeof(T).Name}");
}

/// <summary>Time and identifier helpers. Protocol timestamps are ISO-8601 with local offset, e.g. 2026-08-25T18:30:00+05:30.</summary>
public static class Tx
{
    public static string NowIso()
        => DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    public static string ToIso(DateTimeOffset value)
        => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    public static DateTimeOffset ParseIso(string value)
        => DateTimeOffset.Parse(value);

    public static string NewUuid()
        => Guid.NewGuid().ToString("D");
}

/// <summary>Exponential backoff with jitter (see PROTOCOL_CONTRACT §8.5: 2s initial, x2, cap 60s, +/-25% jitter, reset after stable connection).</summary>
public sealed class BackoffCalculator
{
    private readonly double _initialSeconds;
    private readonly double _maxSeconds;
    private readonly double _multiplier;
    private readonly double _jitterFraction;
    private readonly Random _random = new();
    private double _current;
    private readonly object _lock = new();

    public BackoffCalculator(double initialSeconds = 2.0, double maxSeconds = 60.0, double multiplier = 2.0, double jitterFraction = 0.25)
    {
        if (initialSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(initialSeconds));
        if (maxSeconds < initialSeconds) throw new ArgumentOutOfRangeException(nameof(maxSeconds));
        _initialSeconds = initialSeconds;
        _maxSeconds = maxSeconds;
        _multiplier = multiplier <= 1 ? 2.0 : multiplier;
        _jitterFraction = jitterFraction;
        _current = initialSeconds;
    }

    public int AttemptCount { get; private set; }

    /// <summary>Returns the next delay (with jitter applied) and advances the backoff.</summary>
    public TimeSpan Next()
    {
        lock (_lock)
        {
            AttemptCount++;
            double baseDelay = _current;
            _current = Math.Min(_maxSeconds, _current * _multiplier);
            double jitter = 1.0 + ((_random.NextDouble() * 2.0) - 1.0) * _jitterFraction;
            double seconds = Math.Max(0.1, baseDelay * jitter);
            return TimeSpan.FromSeconds(Math.Min(_maxSeconds, seconds));
        }
    }

    /// <summary>Peek at the current base delay without advancing.</summary>
    public double CurrentBaseSeconds
    {
        get { lock (_lock) { return _current; } }
    }

    /// <summary>Reset to the initial delay (called after a stable connection).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _current = _initialSeconds;
            AttemptCount = 0;
        }
    }
}
