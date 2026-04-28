using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Testing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture test enforcing PR 1 of the email-identity-decoupling spec
/// (<c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>).
///
/// <para>
/// PR 1 stops writes to the three ASP.NET Identity-derived email columns on
/// <see cref="User"/> — <c>Email</c>, <c>NormalizedEmail</c>,
/// <c>EmailConfirmed</c> — in <c>Humans.Application</c> and <c>Humans.Web</c>.
/// <c>UserName</c> / <c>NormalizedUserName</c> are deliberately NOT in this
/// PR's forbidden set: User creation paths now write
/// <c>UserName = user.Id.ToString()</c> (Identity needs a unique non-empty
/// UserName), and that transitional write is part of the decoupling, not a
/// violation of it. PR 2 forbids <c>set_UserName</c> globally once
/// <c>HumansUserStore</c> + virtual overrides take over.
/// </para>
///
/// <para>
/// Reads are unrestricted in PR 1 — they sweep to
/// <see cref="Domain.Entities.UserEmail"/> queries in PR 2 alongside the
/// column-drop migration.
/// </para>
///
/// <para>Exemptions (each carries a downstream read that depends on
/// <c>User.Email</c>; the write stays through PR 1 and is removed in PR 2
/// once the read is swept to <c>UserEmail</c>):</para>
/// <list type="bullet">
///   <item><description><c>Humans.Infrastructure</c> — <c>UserRepository</c>'s rename / merge / purge / deletion-anonymization writes stay through PR 1; they become no-op assignments in PR 2 when the columns are dropped.</description></item>
///   <item><description><c>Humans.Web.Controllers.DevLoginController</c> — <c>EnsurePersonaAsync</c> + <c>SeedProfilelessUserAsync</c> still write <c>Email</c> / <c>EmailConfirmed</c> / <c>UserName = email</c>. The integration-test fixture <c>HumansWebApplicationFactory.SignInAsFullyOnboardedAsync</c> looks up dev personas via <c>db.Users.FirstOrDefaultAsync(u =&gt; u.Email == email)</c>, and <c>DevLoginController.SignInAsUser</c> uses <c>FindByEmailAsync</c> as a legacy-persona reuse path. Dropping the write before PR 2's read sweep would break dev login and the integration suite.</description></item>
///   <item><description><c>Humans.Web.Infrastructure.DevelopmentDashboardSeeder</c> — dev seed cleanup queries <c>u.Email</c> at <c>ResetAsync</c> (DevelopmentDashboardSeeder.cs:421-424) to find seeded dev users. Same shape as the DevLoginController exemption.</description></item>
///   <item><description><c>Humans.Application.Services.Profile.ContactService</c> — <c>CreateContactAsync</c> still writes <c>Email</c> / <c>UserName</c> / <c>EmailConfirmed</c>. The /Contacts admin UI (<c>ContactsController.Detail</c> + <c>AdminContactRow</c>) renders <c>c.Email</c>, and <c>GetFilteredContactsAsync</c> uses <c>User.Email</c> for search. Stopping the write here without sweeping those reads first produces blank-email contacts.</description></item>
/// </list>
///
/// <para>
/// Implementation reads IL via Mono.Cecil rather than reflection because
/// reflection cannot inspect method bodies for property-setter call
/// instructions. The check fires on object initializers
/// (<c>new User { Email = ... }</c>) and on direct assignments
/// (<c>user.Email = ...</c>) equally — both lower to <c>callvirt</c> on the
/// generated setter.
/// </para>
/// </summary>
public class IdentityColumnWriteRestrictionsTests
{
    private static readonly string[] ForbiddenSetters =
    {
        "set_Email",
        "set_NormalizedEmail",
        "set_EmailConfirmed",
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
            because: "PR 1 of the email-identity-decoupling spec stops writes to the three Identity-derived " +
                     "User EMAIL columns (Email/NormalizedEmail/EmailConfirmed) in Application + Web. " +
                     "On user creation: leave these columns at defaults and set UserName = user.Id.ToString(); " +
                     "the UserEmail row created alongside the User carries the email going forward. " +
                     "Offenders found: {0}", string.Join("; ", offenders));
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static bool IsExemptType(string fullName)
    {
        // Each exemption carries a downstream User.Email READ that depends on
        // the write. PR 2 sweeps both reads and writes; until then the writes
        // stay to keep the read paths working. See class XML doc for the
        // specific read each exemption protects.
        if (fullName.StartsWith("Humans.Web.Controllers.DevLoginController", StringComparison.Ordinal))
            return true;

        if (fullName.StartsWith("Humans.Web.Infrastructure.DevelopmentDashboardSeeder", StringComparison.Ordinal))
            return true;

        if (fullName.StartsWith("Humans.Application.Services.Profile.ContactService", StringComparison.Ordinal))
            return true;

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
