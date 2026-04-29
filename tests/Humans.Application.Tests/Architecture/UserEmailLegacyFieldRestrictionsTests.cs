using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Testing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture test enforcing the email-identity-decoupling spec PR 3
/// (<c>docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md</c>).
///
/// <para>
/// PR 3 deletes the public C# properties <c>UserEmail.IsOAuth</c>,
/// <c>UserEmail.DisplayOrder</c>, <c>User.GoogleEmail</c>, and the helper
/// <c>User.GetGoogleServiceEmail()</c>. The underlying DB columns are kept on
/// disk via EF shadow-property declarations (column drops are aggregated into
/// a deferred PR 7 per <c>architecture_no_drops_until_prod_verified</c>). The
/// only legitimate reader of the legacy columns is the one-shot
/// <c>UserEmailProviderBackfillService</c> via <c>EF.Property&lt;T&gt;(...)</c>,
/// which never lowers to a property setter/getter call and therefore sits
/// naturally outside this scan. Any remaining IL reference to the deleted
/// setters/getters or helper inside <c>Humans.Application</c> or
/// <c>Humans.Web</c> is a build that should not have shipped.
/// </para>
///
/// <para>
/// Replacements:
/// <list type="bullet">
/// <item>Auth-side reads ("is this row OAuth-bound?") → <c>UserEmail.Provider != null</c>.</item>
/// <item>Google-Workspace-side reads ("what is this user's canonical Workspace identity?") → the <c>IsGoogle = true</c> <c>UserEmail</c> row.</item>
/// </list>
/// </para>
///
/// <para>
/// Implementation reads IL via Mono.Cecil rather than reflection because
/// reflection cannot inspect method bodies for property-setter / property-getter
/// call instructions. The check fires on object initializers
/// (<c>new UserEmail { IsOAuth = ... }</c>), direct assignments
/// (<c>row.DisplayOrder = ...</c>), reads (<c>if (row.IsOAuth)</c>), and method
/// invocations (<c>user.GetGoogleServiceEmail()</c>) equally — all lower to
/// <c>callvirt</c> on the corresponding member.
/// </para>
///
/// <para>
/// Exemption strategy: zero type-level exemptions in scanned assemblies. The
/// backfill service uses <c>EF.Property&lt;T&gt;</c>, which does not appear as
/// a setter/getter call in IL and is not in the forbidden-member list.
/// </para>
/// </summary>
public class UserEmailLegacyFieldRestrictionsTests
{
    private static readonly string[] ForbiddenMembers =
    {
        // UserEmail
        "set_IsOAuth", "get_IsOAuth",
        "set_DisplayOrder", "get_DisplayOrder",
        // User
        "set_GoogleEmail", "get_GoogleEmail",
        "GetGoogleServiceEmail",
    };

    private static readonly string[] ScannedAssemblies =
    {
        "Humans.Application",
        "Humans.Web",
    };

    [HumansFact]
    public void NoApplicationOrWebCode_ReferencesDeletedUserEmailOrUserLegacyMembers()
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

                        if (!ForbiddenMembers.Contains(mref.Name, StringComparer.Ordinal))
                            continue;

                        if (!IsUserOrUserEmail(mref.DeclaringType))
                            continue;

                        offenders.Add($"{type.FullName}.{method.Name} -> {mref.DeclaringType.Name}.{mref.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "PR 3 of the email-identity-decoupling spec deletes the public C# surface for " +
                     "UserEmail.IsOAuth, UserEmail.DisplayOrder, User.GoogleEmail, and " +
                     "User.GetGoogleServiceEmail(). Auth-side reads must use Provider != null; " +
                     "Google-Workspace-side reads must use the IsGoogle-flagged UserEmail row. " +
                     "The DB columns survive as EF shadow properties for the backfill admin button " +
                     "only (read via EF.Property<>). Offenders found: {0}", string.Join("; ", offenders));
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static bool IsUserOrUserEmail(TypeReference t)
    {
        var current = t;
        var guard = 0;
        while (current is not null && guard++ < 16)
        {
            if (string.Equals(current.FullName, typeof(User).FullName, StringComparison.Ordinal))
                return true;
            if (string.Equals(current.FullName, typeof(UserEmail).FullName, StringComparison.Ordinal))
                return true;
            if (current.Name.StartsWith("IdentityUser", StringComparison.Ordinal))
                return true;

            try
            {
                current = current.Resolve()?.BaseType;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"IsUserOrUserEmail: Cecil resolution failed for {current?.FullName ?? "<null>"}: {ex.GetType().Name}");
                return false;
            }
        }
        return false;
    }

    private static string ResolveAssemblyPath(string assemblyName)
    {
        var hostDir = Path.GetDirectoryName(typeof(UserEmailLegacyFieldRestrictionsTests).Assembly.Location)!;
        var path = Path.Combine(hostDir, $"{assemblyName}.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not locate {assemblyName}.dll at {path}");
        return path;
    }
}
