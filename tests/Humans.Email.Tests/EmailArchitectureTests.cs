using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.Email.Data;
using Humans.Email.Services;

namespace Humans.Email.Tests;

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
/// Since the section's G5 move the SMTP transport, the renderer and the outbox drain are
/// all section-internal. <c>ProcessEmailOutboxJob</c> / <c>CleanupEmailOutboxJob</c> — the
/// scheduler shims over <c>IEmailOutboxProcessor</c> / <c>IEmailOutboxRetention</c> — and
/// <c>HangfireImmediateOutboxProcessor</c> joined them at G5 lane 5b-1, under
/// <c>Contracts/</c> because Shell names each concrete type at registration.
/// </para>
/// </summary>
public class EmailArchitectureTests
{
    // ── EmailOutboxService ───────────────────────────────────────────────────

    // IMemoryCache check covered by ApplicationServicesTakeNoMemoryCacheRule.
    // TakesRepository check covered by pattern G (positive wiring noise).
    // Sealed-repository check covered by HUM0034 (section types are internal) plus
    // MA0053 (an unsealed internal class is a build error) — not by
    // IRepositoryImplementationsAreSealedRule, which sweeps Humans.Infrastructure only.

    // ── OutboxEmailService ───────────────────────────────────────────────────

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

    // ── Connector abstractions ──────────────────────────────────────────────

    /// <summary>
    /// The section carries Hangfire.Core (HangfireImmediateOutboxProcessor and the two jobs
    /// name it), so a Hangfire parameter on the service compiles. Scheduling stays behind
    /// IImmediateOutboxProcessor — containment of a dependency the section really has.
    /// </summary>
    [HumansFact]
    public void OutboxEmailService_HasNoHangfireDependency()
    {
        var ctor = typeof(OutboxEmailService).GetConstructors().Single();
        var hangfireParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Hangfire", StringComparison.Ordinal));

        hangfireParam.Should().BeNull(
            because: "IImmediateOutboxProcessor abstracts the dispatch — a direct Hangfire "
                   + "parameter bypasses the connector boundary");
    }

    [HumansFact]
    public void ConnectorAbstractions_SitOnTheSideTheirImplementationLivesOn()
    {
        // IImmediateOutboxProcessor is on the contracts leaf because Base used to implement
        // it; HangfireImmediateOutboxProcessor moved into Humans.Email/Contracts/ at G5 lane
        // 5b-1, so both sides are now section-side and the interface could go internal in a
        // later pass. IEmailBodyComposer is section-internal because both sides of it are.
        typeof(IImmediateOutboxProcessor).Namespace
            .Should().Be("Humans.Email.Contracts");
        typeof(IImmediateOutboxProcessor).IsPublic.Should().BeTrue();

        typeof(IEmailBodyComposer).Namespace
            .Should().Be("Humans.Email.Services");
        typeof(IEmailBodyComposer).IsPublic.Should().BeFalse();
    }
}
