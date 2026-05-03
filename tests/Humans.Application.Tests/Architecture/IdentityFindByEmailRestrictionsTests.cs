using AwesomeAssertions;
using Humans.Testing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture test forbidding <c>UserManager.FindByEmailAsync</c> /
/// <c>UserManager.FindByNameAsync</c> calls in Application + Web after PR 2
/// of the email-identity-decoupling spec.
///
/// <para>
/// PR 2 routes <c>User.Email</c> / <c>NormalizedEmail</c> / <c>UserName</c> /
/// <c>NormalizedUserName</c> through virtual overrides that compute from
/// <c>UserEmails</c> / <c>Id</c>. The DB columns those values used to write
/// to are no longer populated for users created post-PR 1 — Identity's
/// <c>FindByEmailAsync</c> and <c>FindByNameAsync</c> still query those
/// columns directly, so they silently return <c>null</c> for any user
/// provisioned by the new flows.
/// </para>
///
/// <para>
/// Replacements: email lookups go through
/// <c>IUserEmailService.FindVerifiedEmailWithUserAsync</c> (or the wrapping
/// <c>IMagicLinkService.FindUserByVerifiedEmailAsync</c>); user-by-id lookups
/// continue to use <c>FindByIdAsync</c> which is unaffected.
/// </para>
///
/// <para>
/// The PR 2 plan listed a <c>HumansUserStore</c> custom store that would
/// route Identity's email-lookup contract through <c>IUserEmailService</c>.
/// Shipping a custom store with no current callers is dead weight; this
/// architecture test is the chosen guard against future regression
/// instead. If a real need to call Identity's email-lookup APIs from
/// production code re-emerges, ship the store at that point.
/// </para>
/// </summary>
public class IdentityFindByEmailRestrictionsTests
{
    private static readonly string[] ForbiddenMethods =
    {
        "FindByEmailAsync",
        "FindByNameAsync",
    };

    private static readonly string[] ScannedAssemblies =
    {
        "Humans.Application",
        "Humans.Web",
    };

    [HumansFact]
    public void NoApplicationOrWebCode_CallsUserManagerFindByEmailOrName()
    {
        var offenders = new List<string>();

        foreach (var assemblyName in ScannedAssemblies)
        {
            var assemblyPath = ResolveAssemblyPath(assemblyName);
            using var module = ModuleDefinition.ReadModule(assemblyPath);

            foreach (var type in module.Types.SelectMany(Flatten))
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode != OpCodes.Callvirt && instr.OpCode != OpCodes.Call)
                            continue;

                        if (instr.Operand is not MethodReference mref)
                            continue;

                        if (!ForbiddenMethods.Contains(mref.Name, StringComparer.Ordinal))
                            continue;

                        if (!IsUserManagerOfUser(mref.DeclaringType))
                            continue;

                        offenders.Add($"{type.FullName}.{method.Name} -> {mref.DeclaringType.Name}.{mref.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "PR 2 of the email-identity-decoupling spec stops populating the " +
                     "AspNetUsers.Email / NormalizedEmail / NormalizedUserName columns. " +
                     "UserManager.FindByEmailAsync / FindByNameAsync query those columns " +
                     "directly and silently return null for users created post-PR 1. Use " +
                     "IUserEmailService.FindVerifiedEmailWithUserAsync (or the wrapping " +
                     "IMagicLinkService.FindUserByVerifiedEmailAsync) for email lookups, " +
                     "and FindByIdAsync for id lookups. Offenders found: {0}",
            string.Join("; ", offenders));
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static bool IsUserManagerOfUser(TypeReference t)
    {
        // UserManager<User>, possibly through SignInManager generic helpers.
        // Match by simple name to avoid pulling in Microsoft.AspNetCore.Identity
        // resolution; the method names FindByEmailAsync / FindByNameAsync are
        // distinctive enough to scope to Identity.
        return t.Name.StartsWith("UserManager", StringComparison.Ordinal);
    }

    private static string ResolveAssemblyPath(string assemblyName)
    {
        var hostDir = Path.GetDirectoryName(typeof(IdentityFindByEmailRestrictionsTests).Assembly.Location)!;
        var path = Path.Combine(hostDir, $"{assemblyName}.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not locate {assemblyName}.dll at {path}");
        return path;
    }
}
