using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public sealed class NotificationDbSetWriteAnalyzerTests
{
    private const string Stubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbSet<T>
            {
                public void Add(T row) { }
                public void AddRange(System.Collections.Generic.IEnumerable<T> rows) { }
                public void Attach(T row) { }
                public void AttachRange(System.Collections.Generic.IEnumerable<T> rows) { }
                public void Remove(T row) { }
                public void RemoveRange(System.Collections.Generic.IEnumerable<T> rows) { }
                public void Update(T row) { }
                public void UpdateRange(System.Collections.Generic.IEnumerable<T> rows) { }
                public int Count() => 0;
            }
        }

        namespace Humans.Infrastructure.Data
        {
            using Microsoft.EntityFrameworkCore;

            public sealed class HumansDbContext
            {
                public DbSet<Notification> Notifications { get; } = new();
                public DbSet<NotificationRecipient> NotificationRecipients { get; } = new();
                public DbSet<OtherRow> OtherRows { get; } = new();
            }

            public sealed class Notification { }
            public sealed class NotificationRecipient { }
            public sealed class OtherRow { }
        }

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0022(Diagnostic d) =>
        string.Equals(d.Id, NotificationDbSetWriteAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_notifications_write_outside_notification_repository()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Notifications.Add(new Humans.Infrastructure.Data.Notification());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0022).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Fires_on_notification_recipients_write_outside_notification_repository()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.NotificationRecipients.Remove(new Humans.Infrastructure.Data.NotificationRecipient());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0022).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Does_not_fire_inside_notification_repository()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Notifications
            {
                public sealed class NotificationRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Notifications.Add(new Humans.Infrastructure.Data.Notification());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_notification_reads()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    public int Count(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Notifications.Count();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_other_dbset_writes()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.OtherRows.Add(new Humans.Infrastructure.Data.OtherRow());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_infrastructure_assembly()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Notifications.Add(new Humans.Infrastructure.Data.Notification());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_dbset_captured_into_local()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx)
                    {
                        var set = ctx.Notifications;
                        set.Add(new Humans.Infrastructure.Data.Notification());
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0022).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Fires_on_dbset_captured_into_field()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class CleanupNotificationsJob
                {
                    private readonly Microsoft.EntityFrameworkCore.DbSet<Humans.Infrastructure.Data.NotificationRecipient> _set;

                    public CleanupNotificationsJob(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        _set = ctx.NotificationRecipients;

                    public void Run() =>
                        _set.RemoveRange(new System.Collections.Generic.List<Humans.Infrastructure.Data.NotificationRecipient>());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0022).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Grandfathered_writer_reports_warning()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0022",
                    justification: "Existing notification write path.",
                    since: "2026-05-25",
                    issueRef: "docs/sections/Notifications.md")]
                public sealed class CleanupNotificationsJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Notifications.Add(new Humans.Infrastructure.Data.Notification());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new NotificationDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0022).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Warning);
    }
}
