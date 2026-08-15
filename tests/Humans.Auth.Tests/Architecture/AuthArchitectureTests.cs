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
        // This test is the ONLY thing enforcing the property. The comment here used to say the
        // leaf was "framework-free by construction" — that was measured false in G5 lane 3c
        // (2026-08-15). Humans.Interfaces carries FrameworkReference Microsoft.AspNetCore.App
        // and FrameworkReference flows transitively through ProjectReference, so
        // Humans.Auth.Contracts resolves Microsoft.AspNetCore.App
        // (IsTransitiveFrameworkReference=true) and would compile against ASP.NET types happily.
        // What keeps them out is this assertion, not the SDK. The one piece of Auth's public
        // surface that needs Microsoft.AspNetCore.Authorization
        // (RoleAssignmentOperationRequirement) lives in Humans.Auth's own Contracts/ *folder*
        // instead — Tickets' both-halves split.
        //
        // Note this inspects the EMITTED assembly's referenced-assembly list, i.e. what the leaf
        // actually names, which is why it still passes and still means something.
        var leafRefs = typeof(Contracts.IRoleAssignmentService).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        leafRefs.Should().BeEmpty(
            because: "Humans.Auth.Contracts must name no ASP.NET type — a choice this test " +
                     "enforces, not a property the SDK gives us (see the comment above)");
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
