using AwesomeAssertions;
using Serilog.Events;
using Xunit;

namespace Humans.Integration.Tests.Infrastructure;

/// <summary>
/// Coverage for nobodies-collective/Humans#1060: a preview env whose <c>preview-db.yml</c>
/// clone hasn't run yet must fail with a clear, database-naming FATAL log line — not just the
/// incidental Hangfire warning from <see cref="Humans.Web.Extensions.RecurringJobExtensions"/>
/// — and it must still refuse to start (no graceful degradation).
/// </summary>
public sealed class MissingDatabaseStartupTests(HumansTestDatabase database)
{
    [HumansFact]
    public async Task MissingDatabase_StillRefusesToStart_AndLogsOneClearFatalLineNamingIt()
    {
        // Reachable Postgres server, but this catalog was never created — exactly what a
        // preview host looks like before the preview-db workflow clones humans_pr_{N}.
        var missingDatabaseName = $"humans_missing_{Guid.NewGuid():N}";
        var connectionString = database.ConnectionStringForMissingDatabase(missingDatabaseName);

        await using var factory = new HumansWebApplicationFactory(connectionString);

        // Criterion 5: the app must still refuse to start — no quiet tolerance of a
        // missing database, in preview or anywhere else.
        await Assert.ThrowsAnyAsync<Exception>(async () => await factory.InitializeAsync());

        // Criterion 3: the real cause must be legible as one clear FATAL line naming the
        // database. Before Program.cs's LogStartupFailure, this line read the generic
        // "Application terminated unexpectedly" with the database name buried only in the
        // attached exception, not the rendered message — this assertion fails against that
        // text.
        var logWaitDeadline = DateTimeOffset.UtcNow.AddSeconds(3);
        IReadOnlyList<Serilog.Events.LogEvent> fatalEvents = [];
        while (DateTimeOffset.UtcNow < logWaitDeadline)
        {
            fatalEvents = Humans.Base.Logging.InMemoryLogSink.Instance.GetEvents(minLevel: LogEventLevel.Fatal);
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
