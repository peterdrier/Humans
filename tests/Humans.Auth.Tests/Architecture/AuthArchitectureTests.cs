using AwesomeAssertions;
using Humans.Auth.Services;

namespace Humans.Auth.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing section-specific invariants for the Auth section.
/// </summary>
/// <remarks>
/// <para>
/// The two <c>MagicLinkService</c> rules that used to live in
/// <c>Humans.Application.Tests/Architecture/AuthArchitectureTests.cs</c> are at the bottom of
/// this file: the service moved here at nobodies-collective/Humans#866 G5 lane 4b-2i, so they
/// followed their subject. That file is gone; its third job — asserting the service was in
/// Base — was the premise this lane reverses and is not restated anywhere.
/// </para>
/// <para>
/// Generic cross-section invariants (sealed repos, no <c>IMemoryCache</c> unless
/// allowlisted, namespace placement) are covered by the generic rules in
/// <c>Humans.Application.Tests/Architecture/Rules/</c> and are not repeated here.
/// </para>
/// </remarks>
public class AuthArchitectureTests
{
    // SectionReferencesNoVerticalSection was retired here at nobodies-collective/Humans#866 G5
    // lane 4b-2i, the same call and for the same reason lane 4b-2h made in
    // AuditLogArchitectureTests. It pinned Humans.Auth.GetReferencedAssemblies() to exactly
    // ["Humans.AuditLog.Contracts", "Humans.Gdpr.Contracts", "Humans.Notifications.Contracts",
    // "Humans.Users.Contracts"] — three horizontal leaves plus one documented exception —
    // because peters-hard-rules.md forbade a horizontal from referencing a vertical.
    //
    // Peter's Base-floor decision of 2026-08-14 deleted that premise: a .Contracts leaf is
    // referenceable from anywhere, which is what let MagicLinkService come home from
    // Humans.Application along with the Humans.Email.Contracts reference it carries. Adding
    // that fifth string would have kept the test green while asserting nothing beyond the
    // contents of Humans.Auth.csproj two directories away — a list, not an invariant.
    //
    // If "a horizontal may name leaves but never another section's *project*" is wanted as a
    // rule, it belongs once as a generic rule over every horizontal in
    // tests/Humans.Application.Tests/Architecture/Rules/, not restated per section. The
    // reference set itself is documented, with a reason per name, in Humans.Auth.csproj.

    [HumansFact]
    public void ContractsLeafNamesNoAspNetType()
    {
        // Nothing in the project setup keeps ASP.NET out of this contracts project — the
        // framework reference reaches it anyway through Humans.Interfaces, so it would
        // compile against ASP.NET types happily. This test is the only thing stopping it,
        // which is what lets anything in Base name the leaf without dragging ASP.NET in.
        var leafRefs = typeof(Contracts.IRoleAssignmentService).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        leafRefs.Should().BeEmpty(
            because: "Humans.Auth.Contracts must stay free of ASP.NET types");
    }

    // --- The two rules that followed MagicLinkService in from Humans.Application.Tests. ---

    [HumansFact]
    public void MagicLinkService_has_no_email_settings_or_data_protection_constructor_parameter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var settingsParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("EmailSettings", StringComparison.Ordinal) ||
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("IDataProtectionProvider", StringComparison.Ordinal));

        settingsParam.Should().BeNull(
            because: "Data-protection and URL construction live behind IMagicLinkUrlBuilder");
    }

    [HumansFact]
    public void MagicLinkService_calls_no_repository()
    {
        // This used to be the reason it stayed in Base. It is not that any more — an
        // orchestrator may live in the section it orchestrates for — but the shape is still
        // worth pinning: if the sign-in path grows a repository it has grown tables, and the
        // hard rules' orchestrator/service split has to be re-decided rather than drifted into.
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => p.ParameterType.Name.EndsWith("Repository", StringComparison.Ordinal));

        repositoryParam.Should().BeNull(
            because: "MagicLinkService is an orchestrator; orchestrators do not call repositories (peters-hard-rules.md)");
    }
}
