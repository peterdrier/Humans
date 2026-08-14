using AwesomeAssertions;
using Humans.Auth.Data;
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
        // stayed in Shell, and stayed there when MagicLinkService came into the section at
        // G5 lane 4b-2i, so those keys stayed in SharedResource (template step 3b's first
        // question, answered "no keys"). MagicLinkService renders no copy: the two emails it
        // sends are built by Email's IEmailMessageFactory against EmailResource.
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
        // Kept as an exact list, not as "no vertical leaves". peters-hard-rules.md says a
        // horizontal is "strictly forbidden from referencing vertical sections ... as that
        // will cause loops in the call graph", and until 2026-08-14 this test read that as
        // "the list contains only horizontal leaves". Peter's Base-floor decision of that
        // date supersedes the reading: a .Contracts leaf is referenceable from anywhere, so
        // a leaf reference is not the loop the rule is about. That is what let
        // MagicLinkService — Auth's own sign-in path, parked in Humans.Application purely
        // because it injects Humans.Email.Contracts — come home at G5 lane 4b-2i, and
        // Humans.Email.Contracts is the row that arrived with it.
        //
        // Enumerating rather than filtering is the whole point: a *section* reference
        // (Humans.Email, not Humans.Email.Contracts) or a second vertical still fails here,
        // and adding a name is a deliberate edit with a reason attached. The one absence
        // still worth naming is Humans.Onboarding.Contracts, which IRoleAssignmentService's
        // two write members returned (OnboardingResult) until Auth's G5 replaced it with the
        // section's own RoleAssignmentResult.
        var sectionRefs = SectionAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Humans.", StringComparison.Ordinal))
            .Where(n => n is not ("Humans.Interfaces" or "Humans.Domain" or "Humans.Application"
                                 or "Humans.Infrastructure" or "Humans.UI" or "Humans.Analyzers"
                                 or "Humans.Auth.Contracts"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Humans.Users.Contracts is the exception RoleAssignmentService already carries in source:
        // "Auth (crosscut) references vertical sections — IUserServiceRead for assignee/creator
        // display stitching". MagicLinkService adds UserManager<User> and IUserEmailService on the
        // same leaf, so the row's weight grew but its justification did not change. #866 names
        // User/UserInfo as sanctioned shared contracts; lane 4 decides whether they end up on the
        // Base floor instead, which would drop this row.
        sectionRefs.Should().BeEquivalentTo(
            [
                "Humans.AuditLog.Contracts",
                "Humans.Email.Contracts",
                "Humans.Gdpr.Contracts",
                "Humans.Notifications.Contracts",
                "Humans.Users.Contracts"
            ],
            because: "Auth references Base, three horizontal leaves, and exactly two vertical leaves — Users (display stitching) and Email (the magic-link send)");
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
