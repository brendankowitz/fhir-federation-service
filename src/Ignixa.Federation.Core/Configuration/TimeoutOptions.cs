namespace Ignixa.Federation.Core.Configuration;

/// <summary>The <c>Federation:Timeouts</c> section (design spec §8). Every deadline sits within <see cref="Request"/>.</summary>
public sealed class TimeoutOptions
{
    /// <summary>Gets or sets the deadline for a whole federated request (design spec §8, §7.8). Default 30 seconds.</summary>
    public TimeSpan Request { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the deadline for one backend call (design spec §8). Default 10 seconds.</summary>
    public TimeSpan BackendCall { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the deadline for a chain sub-query (design spec §7.5, §8). Default 15 seconds.</summary>
    public TimeSpan ChainSubquery { get; set; } = TimeSpan.FromSeconds(15);
}
