using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public sealed class EventDbSetWriteAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Infrastructure.Data
        {
            public sealed class HumansDbContext
            {
                public FakeDbSet<Event> Events { get; } = new();
                public FakeDbSet<EventCategory> EventCategories { get; } = new();
                public FakeDbSet<EventVenue> EventVenues { get; } = new();
                public FakeDbSet<EventFavourite> EventFavourites { get; } = new();
                public FakeDbSet<EventPreference> EventPreferences { get; } = new();
                public FakeDbSet<EventGuideSetting> EventGuideSettings { get; } = new();
                public FakeDbSet<EventModerationAction> EventModerationActions { get; } = new();
                public FakeDbSet<OtherRow> OtherRows { get; } = new();
            }

            public sealed class Event { }
            public sealed class EventCategory { }
            public sealed class EventVenue { }
            public sealed class EventFavourite { }
            public sealed class EventPreference { }
            public sealed class EventGuideSetting { }
            public sealed class EventModerationAction { }
            public sealed class OtherRow { }

            public sealed class FakeDbSet<T>
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

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0023(Diagnostic d) =>
        string.Equals(d.Id, EventDbSetWriteAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_event_dbset_write_outside_event_repository()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class EventImportJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Infrastructure.Data.Event());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0023).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Fires_on_each_owned_event_dbset_name()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class EventImportJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx)
                    {
                        ctx.EventCategories.Add(new Humans.Infrastructure.Data.EventCategory());
                        ctx.EventVenues.Add(new Humans.Infrastructure.Data.EventVenue());
                        ctx.EventFavourites.Add(new Humans.Infrastructure.Data.EventFavourite());
                        ctx.EventPreferences.Add(new Humans.Infrastructure.Data.EventPreference());
                        ctx.EventGuideSettings.Add(new Humans.Infrastructure.Data.EventGuideSetting());
                        ctx.EventModerationActions.Add(new Humans.Infrastructure.Data.EventModerationAction());
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0023).Should().HaveCount(6);
    }

    [HumansFact]
    public async Task Does_not_fire_inside_event_repository()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Infrastructure.Data.Event());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_event_dbset_reads()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class EventImportJob
                {
                    public int Count(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Count();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
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
                public sealed class EventImportJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.OtherRows.Add(new Humans.Infrastructure.Data.OtherRow());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Downgrades_to_warning_when_class_has_Grandfathered_for_HUM0023()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0023",
                    justification: "Pending migration to EventRepository.",
                    since: "2026-05-25",
                    issueRef: "nobodies-collective/Humans#0")]
                public sealed class EventImportJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Infrastructure.Data.Event());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0023).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Grandfathered_for_a_different_rule_still_fires_error()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-25",
                    issueRef: "nobodies-collective/Humans#0")]
                public sealed class EventImportJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Infrastructure.Data.Event());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0023).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Does_not_fire_outside_infrastructure_assembly()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class EventImportJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Infrastructure.Data.Event());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EventDbSetWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }
}
