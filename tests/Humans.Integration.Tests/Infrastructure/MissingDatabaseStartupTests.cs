using AwesomeAssertions;
using Serilog.Events;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// A preview whose database clone hasn't run must log one clear FATAL line naming the database, and still refuse to
/// start.
/// </summary>
public sealed class MissingDatabaseStartupTests(HumansTestDatabase database)
{
    [HumansFact]
    public async Task MissingDatabase_StillRefusesToStart_AndLogsOneClearFatalLineNamingIt()
    {
        var missingDatabaseName = $"humans_missing_{Guid.NewGuid():N}";
        var connectionString = database.ConnectionStringForMissingDatabase(missingDatabaseName);

        await using var factory = new HumansWebApplicationFactory(connectionString);

        await Assert.ThrowsAnyAsync<Exception>(async () => await factory.InitializeAsync());

        // The database name must be in the rendered message, not only the attached exception.
        var logWaitDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        IReadOnlyList<LogEvent> fatalEvents = [];
        while (DateTimeOffset.UtcNow < logWaitDeadline)
        {
            fatalEvents = factory.LogSink.GetEvents(minLevel: LogEventLevel.Fatal);
            if (fatalEvents.Any(e => e.RenderMessage().Contains(missingDatabaseName, StringComparison.Ordinal)))
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        fatalEvents.Should().Contain(
            e => e.RenderMessage().Contains(missingDatabaseName, StringComparison.Ordinal),
            "a startup failure caused by a missing database must name it in a FATAL log line, " +
            "not just in an attached exception a dashboard may collapse");
    }
}
