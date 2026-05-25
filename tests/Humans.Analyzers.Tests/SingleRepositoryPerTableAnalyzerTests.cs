using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public sealed class SingleRepositoryPerTableAnalyzerTests
{
    // A minimal HumansDbContext with three DbSets — Events and AuditLogEntries
    // declared directly, Users inherited from a base context (mirrors the real
    // IdentityDbContext-derived Users DbSet). Plus the IRepository marker and a
    // GrandfatheredAttribute whose 5th ctor arg is the optional scope.
    private const string Stubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbSet<T>
            {
                public void Add(T row) { }
                public int Count() => 0;
            }

            public class DbContext
            {
                public DbSet<T> Set<T>() => new DbSet<T>();
            }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
        }

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef, string? scope = null) { }
            }
        }

        namespace Humans.Domain.Entities
        {
            public sealed class Event { }
            public sealed class AuditLogEntry { }
            public sealed class User { }
        }

        namespace Humans.Infrastructure.Data
        {
            using Microsoft.EntityFrameworkCore;
            using Humans.Domain.Entities;

            public class IdentityishContext : DbContext
            {
                public DbSet<User> Users => Set<User>();
            }

            public sealed class HumansDbContext : IdentityishContext
            {
                public DbSet<Event> Events => Set<Event>();
                public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();
            }
        }
        """;

    private static bool IsHum0025(Diagnostic d) =>
        string.Equals(d.Id, SingleRepositoryPerTableAnalyzer.DiagnosticId, StringComparison.Ordinal);

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> RunAsync(string source) =>
        AnalyzerTestHarness.RunAsync(new SingleRepositoryPerTableAnalyzer(), "Humans.Infrastructure", source);

    [HumansFact]
    public async Task Fires_at_each_site_when_two_repositories_reference_the_same_dbset()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Repositories.AuditLog
            {
                public sealed class AuditLogRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Touch(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0025).ToList();

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
        diagnostics.Should().OnlyContain(d => d.GetMessage().Contains("Events") && d.GetMessage().Contains("2 repositories"));
    }

    [HumansFact]
    public async Task Does_not_fire_when_only_one_repository_references_a_dbset()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                    public int Count(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Count();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Where(IsHum0025).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Counts_reads_not_just_writes()
    {
        // EventRepository writes Events; AuditLogRepository only reads it.
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Repositories.AuditLog
            {
                public sealed class AuditLogRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public int Count(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Count();
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Where(IsHum0025).Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Grandfathered_scope_downgrades_both_participants_to_warning()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                [Humans.Application.Architecture.Grandfathered("HUM0025", "j", "2026-05-25", "i", scope: "Events")]
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Repositories.AuditLog
            {
                [Humans.Application.Architecture.Grandfathered("HUM0025", "j", "2026-05-25", "i", scope: "Events")]
                public sealed class AuditLogRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Touch(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0025).ToList();

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task New_sharer_of_a_grandfathered_table_still_errors()
    {
        // EventRepository is grandfathered for Events; AuditLogRepository is the
        // new sharer and is NOT — its site must still be an Error.
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                [Humans.Application.Architecture.Grandfathered("HUM0025", "j", "2026-05-25", "i", scope: "Events")]
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Repositories.AuditLog
            {
                public sealed class AuditLogRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Touch(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0025).ToList();

        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Warning);
        diagnostics.Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Grandfather_for_a_different_scope_does_not_downgrade()
    {
        // The grandfather names AuditLogEntries, but the violation is on Events —
        // scope mismatch, so the diagnostic stays an Error.
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Repositories.AuditLog
            {
                [Humans.Application.Architecture.Grandfathered("HUM0025", "j", "2026-05-25", "i", scope: "AuditLogEntries")]
                public sealed class AuditLogRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Touch(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0025).ToList();

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Non_repository_dbcontext_users_do_not_count_toward_the_total()
    {
        // A non-IRepository class touches Events, but only one *repository* does,
        // so HUM0025 does not fire (that class is HUM0009's concern, not this rule's).
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Services
            {
                public sealed class RogueService
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }
            """;

        var diagnostics = await RunAsync(source);

        diagnostics.Where(IsHum0025).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Detects_ctx_Set_generic_and_inherited_identity_dbset()
    {
        // RepoA reaches Users via the inherited property; RepoB via ctx.Set<User>().
        // Both must resolve to the same "Users" table so N=2 fires.
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Users
            {
                public sealed class UserRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public int Count(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Users.Count();
                }
            }

            namespace Humans.Infrastructure.Repositories.GoogleIntegration
            {
                public sealed class DriveActivityMonitorRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Touch(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Set<Humans.Domain.Entities.User>().Add(new Humans.Domain.Entities.User());
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0025).ToList();

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.GetMessage().Contains("Users"));
    }

    [HumansFact]
    public async Task Does_not_fire_outside_infrastructure_assembly()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Events
            {
                public sealed class EventRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Save(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }

            namespace Humans.Infrastructure.Repositories.AuditLog
            {
                public sealed class AuditLogRepository : Humans.Application.Interfaces.Repositories.IRepository
                {
                    public void Touch(Humans.Infrastructure.Data.HumansDbContext ctx) =>
                        ctx.Events.Add(new Humans.Domain.Entities.Event());
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SingleRepositoryPerTableAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0025).Should().BeEmpty();
    }
}
