using AwesomeAssertions;
using Humans.Gate.Contracts;
using Humans.Gate.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Gate.Tests;

/// <summary>
/// Pins the retention job's configuration contract: <c>Gate:RetentionDays</c>
/// defaults to 365, and a non-positive value disables the purge entirely.
/// </summary>
public class GateRetentionJobTests
{
    private readonly IGateScanRetention _retention = Substitute.For<IGateScanRetention>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 3, 1, 12, 0));

    private GateRetentionJob Job(params (string Key, string? Value)[] config) =>
        new(_retention,
            new ConfigurationBuilder()
                .AddInMemoryCollection(config.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal))
                .Build(),
            _clock,
            NullLogger<GateRetentionJob>.Instance);

    [HumansFact]
    public async Task DefaultWindow_PurgesRowsOlderThan365Days()
    {
        await Job().ExecuteAsync();

        await _retention.Received(1).PurgeScansBeforeAsync(
            _clock.GetCurrentInstant().Minus(Duration.FromDays(365)), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NonPositiveWindow_DisablesThePurge()
    {
        await Job(("Gate:RetentionDays", "0")).ExecuteAsync();
        await Job(("Gate:RetentionDays", "-1")).ExecuteAsync();

        await _retention.DidNotReceiveWithAnyArgs().PurgeScansBeforeAsync(default, default);
    }
}
