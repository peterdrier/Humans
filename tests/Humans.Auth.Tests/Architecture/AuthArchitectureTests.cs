using AwesomeAssertions;
using Humans.Auth.Data;

namespace Humans.Auth.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing section-specific invariants for the Auth section.
/// </summary>
/// <remarks>
/// <para>
/// The <c>MagicLinkService</c> half of the old <c>Humans.Application.Tests</c> file stayed
/// there: that service is a cross-section orchestrator and did not move
/// (see <c>Humans.Auth.Section</c>).
/// </para>
/// <para>
/// Generic cross-section invariants (sealed repos, no <c>IMemoryCache</c> unless
/// allowlisted, namespace placement) are covered by the generic rules in
/// <c>Humans.Application.Tests/Architecture/Rules/</c> and are not repeated here.
/// </para>
/// </remarks>
public class AuthArchitectureTests
{
    private static System.Reflection.Assembly SectionAssembly => typeof(IRoleAssignmentRepository).Assembly;

    [HumansFact]
    public void SectionServicesTakeNoDbContextOrStore()
    {
        // Restates two older assertions at once: the moved file's "constructor takes no
        // Humans.Application.Interfaces.Stores type", and the generic
        // "GetReferencedAssemblies() does not contain EntityFrameworkCore" shape, which
        // stops meaning anything once the repository ships in the same assembly as the
        // service (G5-SECTION-TEMPLATE.md step 11). The real invariant is that only the
        // repository touches a context.
        var offenders = SectionAssembly
            .GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("Humans.Auth.Services", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Select(param => (Type: t, param.ParameterType)))
            .Where(x => typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(x.ParameterType)
                        || (x.ParameterType.IsGenericType
                            && x.ParameterType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<>))
                        || (x.ParameterType.Namespace ?? string.Empty)
                            .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal))
            .Select(x => $"{x.Type.FullName} takes {x.ParameterType.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "only RoleAssignmentRepository may touch AuthDbContext, and the section has no store abstraction (peters-hard-rules.md)");
    }

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // The section deliberately ships no Resources/ folder and no AuthResource: it has
        // no controller and no view. AccountController and its Views/Account/* — and with
        // them every Login_*/MagicLink*/GateLogin_*/CompleteSignup_*/AccessDenied_* key —
        // stayed in Shell with the magic-link orchestrator, so those keys stayed in
        // SharedResource (template step 3b's first question, answered "no keys").
        var offenders = SectionAssembly
            .GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                .Select(param => (Type: t, param.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && string.Equals(
                            x.ParameterType.GetGenericTypeDefinition().FullName,
                            "Microsoft.Extensions.Localization.IStringLocalizer`1",
                            StringComparison.Ordinal))
            .Select(x => $"{x.Type.FullName} takes {x.ParameterType.Name}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "Auth has no resource set; a localizer here means copy was added without carving one");
    }

    [HumansFact]
    public void SectionReferencesNoVerticalSection()
    {
        // Auth is a *horizontal* section. peters-hard-rules.md: horizontals "are strictly
        // forbidden from referencing vertical sections ... as that will cause loops in the
        // call graph". The referenced-assembly list is where that stops being a convention.
        //
        // The three names below are all horizontal leaves. Two absences are the load-bearing
        // part: Humans.Email.Contracts, which MagicLinkService injects and which is why that
        // orchestrator stayed in Humans.Application; and Humans.Onboarding.Contracts, which
        // IRoleAssignmentService's two write members returned (OnboardingResult) until this
        // move replaced it with the section's own RoleAssignmentResult.
        var sectionRefs = SectionAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Humans.", StringComparison.Ordinal))
            .Where(n => n is not ("Humans.Interfaces" or "Humans.Domain" or "Humans.Application"
                                 or "Humans.Infrastructure" or "Humans.UI" or "Humans.Analyzers"
                                 or "Humans.Auth.Contracts"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        sectionRefs.Should().BeEquivalentTo(
            ["Humans.AuditLog.Contracts", "Humans.Gdpr.Contracts", "Humans.Notifications.Contracts"],
            because: "a horizontal section may reference only Base and other horizontals");
    }

    [HumansFact]
    public void ContractsLeafNamesNoAspNetType()
    {
        // The leaf is framework-free by construction so Base consumers can name it without
        // dragging ASP.NET in; the one piece of Auth's public surface that needs
        // Microsoft.AspNetCore.Authorization (RoleAssignmentOperationRequirement) lives in
        // Humans.Auth's own Contracts/ *folder* instead — Tickets' both-halves split.
        var leafRefs = typeof(Contracts.IRoleAssignmentService).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        leafRefs.Should().BeEmpty(
            because: "Humans.Auth.Contracts is a framework-free leaf (Microsoft.NET.Sdk)");
    }
}
