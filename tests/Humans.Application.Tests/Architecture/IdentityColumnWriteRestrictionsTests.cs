using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Testing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture test enforcing the email-identity-decoupling spec
/// (<c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>).
///
/// <para>
/// PR 2 forbids writes to the five ASP.NET Identity-derived columns on
/// <see cref="User"/> — <c>Email</c>, <c>NormalizedEmail</c>,
/// <c>EmailConfirmed</c>, <c>UserName</c>, <c>NormalizedUserName</c> — in
/// <c>Humans.Application</c> and <c>Humans.Web</c>. By the end of PR 2
/// the columns themselves are dropped from <c>AspNetUsers</c> and the
/// in-memory properties are routed through virtual overrides on
/// <see cref="User"/> (<c>Email</c> / <c>NormalizedEmail</c> /
/// <c>EmailConfirmed</c> compute from <see cref="Domain.Entities.UserEmail"/>;
/// <c>UserName</c> / <c>NormalizedUserName</c> compute from <c>User.Id</c>).
/// All five setters either throw <see cref="NotSupportedException"/> or
/// silently ignore — so any direct write from Application/Web is at best
/// a runtime exception and at worst a stale in-memory value.
/// </para>
///
/// <para>
/// PR 1 (predecessor) was write-only with three type exemptions
/// (<c>DevLoginController</c>, <c>DevelopmentDashboardSeeder</c>,
/// <c>ContactService</c>) that protected downstream reads. PR 2 sweeps
/// those reads to <see cref="Domain.Entities.UserEmail"/> queries,
/// removes the entire <c>/Contacts</c> admin surface, and clears all type
/// exemptions.
/// </para>
///
/// <para>Exemption strategy in PR 2: ZERO type-level exemptions for
/// scanned assemblies. The override implementations themselves live in
/// <c>Humans.Domain.Entities.User</c> (Domain layer — not scanned) and
/// <c>Humans.Infrastructure.Identity.HumansUserStore</c> (Infrastructure
/// layer — not scanned). Identity's internal calls to
/// <c>SetNormalizedUserNameAsync</c> originate inside
/// <c>Microsoft.AspNetCore.Identity</c> (not scanned). Therefore no
/// in-scope code path can legitimately write any of the five forbidden
/// setters.</para>
///
/// <para>
/// Implementation reads IL via Mono.Cecil rather than reflection because
/// reflection cannot inspect method bodies for property-setter call
/// instructions. The check fires on object initializers
/// (<c>new User { Email = ... }</c>) and on direct assignments
/// (<c>user.Email = ...</c>) equally — both lower to <c>callvirt</c> on the
/// generated setter.
/// </para>
///
/// <para>
/// PR 2 deliberately does NOT forbid property GETs. Reads are routed
/// correctly via the virtual overrides on <see cref="User"/> — accessing
/// <c>user.Email</c> after PR 2 returns the first verified
/// <see cref="Domain.Entities.UserEmail"/> row, which is the correct
/// semantic. The DB-translated reads that ARE broken by the column drop
/// (LINQ filters in repos / dev seeders / test fixtures) are swept site-by-site
/// in this PR's other tasks; the failing-build feedback from removed
/// methods (<c>GetByEmailOrAlternateAsync</c>, <c>GetByNormalizedEmailAsync</c>,
/// <c>GetContactUsersAsync</c>) is the safety net for those.
/// </para>
/// </summary>
public class IdentityColumnWriteRestrictionsTests
{
    private static readonly string[] ForbiddenSetters =
    {
        "set_Email",
        "set_NormalizedEmail",
        "set_EmailConfirmed",
        "set_UserName",
        "set_NormalizedUserName",
    };

    private static readonly string[] ScannedAssemblies =
    {
        "Humans.Application",
        "Humans.Web",
    };

    [HumansFact]
    public void NoApplicationOrWebCode_WritesIdentityEmailColumnsOnUser()
    {
        var offenders = new List<string>();

        foreach (var assemblyName in ScannedAssemblies)
        {
            var assemblyPath = ResolveAssemblyPath(assemblyName);
            using var module = ModuleDefinition.ReadModule(assemblyPath);

            foreach (var type in module.Types.SelectMany(Flatten))
            {
                if (IsExemptType(type.FullName))
                    continue;

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode != OpCodes.Callvirt && instr.OpCode != OpCodes.Call)
                            continue;

                        if (instr.Operand is not MethodReference mref)
                            continue;

                        if (!ForbiddenSetters.Contains(mref.Name, StringComparer.Ordinal))
                            continue;

                        if (!IsUserOrIdentityUser(mref.DeclaringType))
                            continue;

                        offenders.Add($"{type.FullName}.{method.Name} -> {mref.DeclaringType.Name}.{mref.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "PR 2 of the email-identity-decoupling spec forbids writes to all five " +
                     "Identity-derived User columns (Email/NormalizedEmail/EmailConfirmed/UserName/" +
                     "NormalizedUserName) in Application + Web. The columns are dropped from the DB " +
                     "and the in-memory properties are routed through virtual overrides on User " +
                     "(Email/NormalizedEmail/EmailConfirmed compute from UserEmails; UserName/" +
                     "NormalizedUserName compute from User.Id). Caller code should never write any " +
                     "of these — create UserEmail rows via IUserEmailService for emails, and let " +
                     "the User overrides supply UserName/NormalizedUserName from Id. " +
                     "Offenders found: {0}", string.Join("; ", offenders));
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static bool IsExemptType(string fullName)
    {
        // PR 2: zero type-level exemptions in scanned assemblies. The override
        // implementations live in Humans.Domain (User.cs) and
        // Humans.Infrastructure (HumansUserStore) — neither is scanned, so the
        // legitimate "writers" naturally fall outside this test's scope. See
        // class XML doc for the full strategy.
        return false;
    }

    private static bool IsUserOrIdentityUser(TypeReference t)
    {
        var current = t;
        var guard = 0;
        while (current is not null && guard++ < 16)
        {
            if (string.Equals(current.FullName, typeof(User).FullName, StringComparison.Ordinal))
                return true;

            // Match IdentityUser / IdentityUser<T> by name to avoid resolving the
            // Microsoft.AspNetCore.Identity.* assembly transitively.
            if (current.Name.StartsWith("IdentityUser", StringComparison.Ordinal))
                return true;

            try
            {
                current = current.Resolve()?.BaseType;
            }
            catch (Exception ex)
            {
                // Cecil cannot resolve external-assembly types (e.g., NuGet
                // dependencies whose .dll isn't in the test output dir). A
                // resolution failure here means the type is not a User
                // subclass — the check returns false, which is the intended
                // behaviour for non-User types regardless of why resolution
                // failed. Diagnostic written so a future debugger can see
                // why a particular type wasn't classified as a User subclass.
                System.Diagnostics.Debug.WriteLine(
                    $"IsUserOrIdentityUser: Cecil resolution failed for {current?.FullName ?? "<null>"}: {ex.GetType().Name}");
                return false;
            }
        }
        return false;
    }

    private static string ResolveAssemblyPath(string assemblyName)
    {
        var hostDir = Path.GetDirectoryName(typeof(IdentityColumnWriteRestrictionsTests).Assembly.Location)!;
        var path = Path.Combine(hostDir, $"{assemblyName}.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not locate {assemblyName}.dll at {path}");
        return path;
    }
}
