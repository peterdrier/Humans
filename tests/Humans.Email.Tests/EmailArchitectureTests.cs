using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Application.Interfaces.Profiles;
using Humans.Email.Data;
using Humans.Email.Services;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

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
/// all section-internal; only <c>ProcessEmailOutboxJob</c> / <c>CleanupEmailOutboxJob</c>
/// stay in <c>Humans.Infrastructure</c>, as scheduler shims over
/// <c>IEmailOutboxProcessor</c> / <c>IEmailOutboxRetention</c>.
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

    // ── Connector abstractions ──────────────────────────────────────────────

    [HumansFact]
    public void ConnectorAbstractions_SitOnTheSideTheirImplementationLivesOn()
    {
        // IImmediateOutboxProcessor is on the contracts leaf because Base implements it —
        // HangfireImmediateOutboxProcessor enqueues the recurring job. IEmailBodyComposer
        // is section-internal because both sides of it are.
        typeof(IImmediateOutboxProcessor).Namespace
            .Should().Be("Humans.Email.Contracts");
        typeof(IImmediateOutboxProcessor).IsPublic.Should().BeTrue();

        typeof(IEmailBodyComposer).Namespace
            .Should().Be("Humans.Email.Services");
        typeof(IEmailBodyComposer).IsPublic.Should().BeFalse();
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The 70 Email_* keys moved into EmailResource with their one renderer; a type
        // left bound to SharedResource would render every transactional email as raw
        // keys, in all six languages, with no test noticing (design §15 step 3b).
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()))
            .Select(p => p.ParameterType)
            .Where(t => t.IsGenericType
                        && t.GetGenericTypeDefinition() == typeof(IStringLocalizer<>))
            .Select(t => t.GetGenericArguments()[0])
            .Where(t => t != typeof(EmailResource))
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            because: "every localized string in this section is an Email_* key in EmailResource");
    }

    // ── IEmailService surface (issue #712) ──────────────────────────────────

    [HumansFact]
    public void IEmailService_HasNoPerContentTypeSendMethods()
    {
        // #712 collapsed the transport interface to one generic
        // SendAsync(EmailMessage); per-message construction moved to
        // IEmailMessageFactory. Guard against the fat per-content-type
        // interface growing back.
        var sendMethods = typeof(IEmailService).GetMethods()
            .Where(m => m.Name.StartsWith("Send", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

        sendMethods.Should().ContainSingle(
                because: "the only transport entry point is SendAsync(EmailMessage); "
                    + "domain verbs live on IEmailMessageFactory, not IEmailService")
            .Which.Should().Be("SendAsync");
    }
}
