namespace BastionVault.Core;

/// <summary>The production time seam.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>The shared instance; the class is stateless and thread-safe.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>A clock that only moves when a test moves it.</summary>
public sealed class FixedClock : IClock
{
    /// <summary>Creates a clock reading <paramref name="now"/>.</summary>
    /// <param name="now">The time the clock reports until it is changed.</param>
    public FixedClock(DateTimeOffset now) => UtcNow = now;

    /// <summary>The time this clock reports; settable so a test can advance it.</summary>
    public DateTimeOffset UtcNow { get; set; }
}
