using AwesomeAssertions;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;
using EmailOutboxService = Humans.Application.Services.Email.EmailOutboxService;
using OutboxEmailService = Humans.Application.Services.Email.OutboxEmailService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Email
/// section — migrated per issue #548.
///
/// <para>
/// The Email section's §15 migration chose the <b>no-decorator</b> variant
/// (same rationale as Governance and User): outbox reads are sequential
/// queue drains, not a hot-path request pattern that would benefit from an
/// in-memory entity dict. Admin dashboard reads are infrequent and small.
/// </para>
/// <para>
/// The SMTP-send path stays in <c>Humans.Infrastructure</c> as a connector
/// (<c>SmtpEmailTransport</c> + <c>ProcessEmailOutboxJob</c>); only the
/// outbox persistence layer moves to Application.
/// </para>
/// </summary>
public class EmailArchitectureTests
{
    // ── EmailOutboxService ───────────────────────────────────────────────────

    [HumansFact]
    public void EmailOutboxService_LivesInHumansApplicationServicesEmailNamespace()
    {
        typeof(EmailOutboxService).Namespace
            .Should().Be("Humans.Application.Services.Email",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void EmailOutboxService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(EmailOutboxService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IEmailOutboxRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void EmailOutboxService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(EmailOutboxService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "Email outbox is not IMemoryCache-backed; reads are infrequent admin dashboard queries that go straight to the repository (no decorator variant per Email §15 choice)");
    }

    [HumansFact]
    public void EmailOutboxService_TakesRepository()
    {
        var ctor = typeof(EmailOutboxService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEmailOutboxRepository));
    }

    // ── OutboxEmailService ───────────────────────────────────────────────────

    [HumansFact]
    public void OutboxEmailService_LivesInHumansApplicationServicesEmailNamespace()
    {
        typeof(OutboxEmailService).Namespace
            .Should().Be("Humans.Application.Services.Email",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void OutboxEmailService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IEmailOutboxRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void OutboxEmailService_TakesOutboxRepositoryAndUserEmailService()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEmailOutboxRepository),
            because: "outbox writes go through the Email section's repository");
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "looking up UserId by email is a Profile-section query — routed through IUserEmailService rather than direct access to user_emails (§2c)");
    }

    [HumansFact]
    public void OutboxEmailService_TakesConnectorAbstractions()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IEmailBodyComposer),
            because: "branded email wrapping lives in Infrastructure (captures IHostEnvironment + EmailSettings); Application-layer service takes the abstraction so it stays config-free");
        paramTypes.Should().Contain(typeof(IImmediateOutboxProcessor),
            because: "triggering an immediate outbox run uses Hangfire's IBackgroundJobClient — Application layer takes the abstraction rather than the Hangfire type");
    }

    [HumansFact]
    public void OutboxEmailService_HasNoHangfireDependency()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var hangfireParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Hangfire", StringComparison.Ordinal));

        hangfireParam.Should().BeNull(
            because: "Application layer must not reference Hangfire types directly — IImmediateOutboxProcessor abstracts the dispatch");
    }

    // ── IEmailOutboxRepository ───────────────────────────────────────────────

    [HumansFact]
    public void IEmailOutboxRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IEmailOutboxRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void EmailOutboxRepository_IsSealed()
    {
        var repoType = typeof(IEmailOutboxRepository).Assembly
            .GetExportedTypes()
            .Concat(typeof(EmailOutboxRepository).Assembly.GetExportedTypes())
            .Single(t => string.Equals(t.Name, "EmailOutboxRepository", StringComparison.Ordinal)
                         && typeof(IEmailOutboxRepository).IsAssignableFrom(t));

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    // ── Connector abstractions ──────────────────────────────────────────────

    [HumansFact]
    public void IEmailBodyComposer_AndIImmediateOutboxProcessor_LiveInApplicationInterfaces()
    {
        typeof(IEmailBodyComposer).Namespace
            .Should().Be("Humans.Application.Interfaces.Email");
        typeof(IImmediateOutboxProcessor).Namespace
            .Should().Be("Humans.Application.Interfaces.Email");
    }
}
