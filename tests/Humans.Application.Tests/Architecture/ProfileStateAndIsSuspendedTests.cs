using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Testing;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15i decisions on
/// <see cref="Profile.IsSuspended"/> and <see cref="Profile.State"/>:
///
/// <para>
/// 1. <c>Profile_IsSuspended_HasNoNewWriters</c> — pins the small allowlist
/// of writers that still mutate the legacy <c>IsSuspended</c> bool. New
/// writers must add themselves to the allowlist (which signals they should
/// have been routed through <c>ProfileState.Suspended</c> instead).
/// </para>
/// <para>
/// 2. <c>ProfileState_PropertyExists</c> — guards against accidental removal
/// of the new <see cref="Profile.State"/> column property.
/// </para>
/// <para>
/// IL inspection via Mono.Cecil so object-initializer + property-set
/// instructions both surface as <c>set_IsSuspended</c> callvirts. Read-only
/// LINQ filters (<c>Where(p => !p.IsSuspended)</c>) lower to
/// <c>get_IsSuspended</c> and are intentionally NOT pinned — readers stay
/// canonical until the lazy-State-backfill follow-up makes State the
/// canonical read.
/// </para>
/// </summary>
public class ProfileStateAndIsSuspendedTests
{
    /// <summary>
    /// Allowlist of types whose IL is permitted to call
    /// <c>set_IsSuspended</c> on <see cref="Profile"/>. Each entry is
    /// dual-writing State alongside the legacy bool until the follow-up
    /// PR drops the <c>IsSuspended</c> column after prod soak.
    /// </summary>
    private static readonly string[] AllowedWriters =
    {
        // ProfileService.SetSuspendedAsync: dual-writes IsSuspended + State
        "Humans.Application.Services.Profile.ProfileService",
        // ProfileRepository.SuspendManyAsync: dual-writes IsSuspended + State
        "Humans.Infrastructure.Repositories.Profiles.ProfileRepository",
        // ProfileRepository compiler-generated nested types (LINQ closures
        // inside the Suspend-many path) — Cecil reports them with backticks
        // or angle brackets in the FullName.
    };

    private static readonly string[] ScannedAssemblies =
    {
        "Humans.Application",
        "Humans.Web",
        "Humans.Infrastructure",
    };

    [HumansFact]
    public void Profile_IsSuspended_HasNoNewWriters()
    {
        var offenders = new List<string>();

        foreach (var assemblyName in ScannedAssemblies)
        {
            var assemblyPath = ResolveAssemblyPath(assemblyName);
            using var module = ModuleDefinition.ReadModule(assemblyPath);

            foreach (var type in module.Types.SelectMany(Flatten))
            {
                var declaringTopLevel = TopLevelDeclaringTypeName(type);
                if (AllowedWriters.Contains(declaringTopLevel, StringComparer.Ordinal))
                    continue;

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode != OpCodes.Callvirt && instr.OpCode != OpCodes.Call)
                            continue;

                        if (instr.Operand is not MethodReference mref)
                            continue;

                        if (!string.Equals(mref.Name, "set_IsSuspended", StringComparison.Ordinal))
                            continue;

                        if (!string.Equals(
                                mref.DeclaringType.FullName,
                                typeof(Profile).FullName,
                                StringComparison.Ordinal))
                            continue;

                        offenders.Add($"{type.FullName}.{method.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "Issue #635 (§15i): Profile.IsSuspended is [Obsolete]. " +
                     "New writers must mutate Profile.State (= ProfileState.Suspended) " +
                     "instead. Allowlisted writers ({0}) dual-write the legacy bool " +
                     "until the follow-up PR drops the column after prod soak. " +
                     "Offenders found: {1}",
            string.Join(", ", AllowedWriters), string.Join("; ", offenders));
    }

    [HumansFact]
    public void Profile_State_PropertyExists()
    {
        var stateProperty = typeof(Profile).GetProperty(nameof(Profile.State));
        stateProperty.Should().NotBeNull(
            because: "Issue #635 (§15i): Profile.State is the canonical lifecycle marker; " +
                     "removing it would silently break the lazy-backfill path.");
    }

    private static string TopLevelDeclaringTypeName(TypeDefinition t)
    {
        var current = t;
        while (current.DeclaringType is not null)
            current = current.DeclaringType;
        return current.FullName;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static string ResolveAssemblyPath(string assemblyName)
    {
        var hostDir = Path.GetDirectoryName(typeof(ProfileStateAndIsSuspendedTests).Assembly.Location)!;
        var path = Path.Combine(hostDir, $"{assemblyName}.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not locate {assemblyName}.dll at {path}");
        return path;
    }
}
