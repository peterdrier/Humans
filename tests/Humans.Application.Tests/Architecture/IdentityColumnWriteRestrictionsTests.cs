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
/// PR 1 stops writes to the four ASP.NET Identity-derived <see cref="User"/>
/// columns (<c>Email</c>, <c>NormalizedEmail</c>, <c>EmailConfirmed</c>,
/// <c>UserName</c>; <c>NormalizedUserName</c> is included for completeness even
/// though Identity auto-derives it) in <c>Humans.Application</c> and
/// <c>Humans.Web</c> assemblies. Reads are unrestricted in PR 1 — they sweep
/// to <see cref="Domain.Entities.UserEmail"/> queries in PR 2 alongside the
/// column-drop migration.
/// </para>
///
/// <para>Exemptions:</para>
/// <list type="bullet">
///   <item><description><c>Humans.Infrastructure</c> — <c>UserRepository</c>'s rename / merge / purge / deletion-anonymization writes stay through PR 1; they become no-op assignments in PR 2 when the columns are dropped.</description></item>
///   <item><description><c>Humans.Web.Controllers.DevLoginController</c> — the transitional <c>UserName = id.ToString()</c> write during persona seeding is allowed; this exemption deletes in PR 2 once <c>HumansUserStore</c> + virtual property overrides land.</description></item>
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
            because: "PR 1 of the email-identity-decoupling spec stops writes to the four Identity-derived " +
                     "User columns (Email/NormalizedEmail/EmailConfirmed/UserName, plus NormalizedUserName) " +
                     "in Application + Web. Set UserName = user.Id.ToString() on creation and leave the email " +
                     "columns at defaults; the UserEmail row carries the email going forward. " +
                     "DevLoginController is exempt for transitional UserName seeding (cleared in PR 2). " +
                     "Offenders found: {0}", string.Join("; ", offenders));
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static bool IsExemptType(string fullName)
    {
        // DevLoginController's UserName = id.ToString() during persona seed is transitional.
        // Removed in PR 2 once HumansUserStore + virtual property overrides land.
        if (fullName.StartsWith("Humans.Web.Controllers.DevLoginController", StringComparison.Ordinal))
            return true;

        // DevelopmentDashboardSeeder seeds 120 dev humans for dashboard demo data.
        // Cleanup queries u.Email to find them (DevelopmentDashboardSeeder.cs:421-424),
        // so stopping the write here would orphan the cleanup path. PR 2 sweeps reads
        // and updates the cleanup to query via UserEmails, removing this exemption.
        if (fullName.StartsWith("Humans.Web.Infrastructure.DevelopmentDashboardSeeder", StringComparison.Ordinal))
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
            catch
            {
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
