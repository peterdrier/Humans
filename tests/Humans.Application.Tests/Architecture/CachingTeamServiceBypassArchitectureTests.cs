using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Services.Teams;
using Mono.Cecil;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Structural (IL-level) companion to the behavior tests in
/// <c>CachingTeamServiceGetTeamDetailTests</c>. The behavior tests pin that
/// the runtime read path of <see cref="CachingTeamService.GetTeamDetailAsync"/>
/// does not call the bypassed repository methods. This test pins the same
/// invariant symbolically: the compiled IL must contain zero call sites
/// targeting <see cref="ITeamRepository.GetBySlugWithRelationsAsync"/> or
/// <see cref="ITeamRepository.GetRoleDefinitionsAsync"/> from
/// <c>GetTeamDetailAsync</c> (including its async state machine
/// <c>MoveNext</c>). Catches a future regression at compile/test time without
/// needing a mocked decorator setup.
///
/// Source rule: docs/sections/Teams.md — "TeamInfo is the canonical read
/// shape; the inner repository methods are retained only for inner write-path
/// code." PR #581 / T-01 of the cache migration plan.
/// </summary>
public class CachingTeamServiceBypassArchitectureTests
{
    private const string TargetMethodName = nameof(CachingTeamService.GetTeamDetailAsync);

    private static readonly string[] BypassedRepoMethodNames =
    [
        nameof(ITeamRepository.GetBySlugWithRelationsAsync),
        nameof(ITeamRepository.GetRoleDefinitionsAsync),
    ];

    [HumansFact]
    public void GetTeamDetailAsync_does_not_reference_bypassed_repository_methods()
    {
        var assemblyPath = typeof(CachingTeamService).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        var serviceType = module.GetType(typeof(CachingTeamService).FullName)
            ?? throw new InvalidOperationException(
                $"Could not locate {typeof(CachingTeamService).FullName} in {assemblyPath}");

        var violations = new List<string>();
        foreach (var method in EnumerateRelatedMethods(serviceType, TargetMethodName))
        {
            if (method.Body is null) continue;
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is not MethodReference callee) continue;
                if (!string.Equals(callee.DeclaringType.FullName, typeof(ITeamRepository).FullName, StringComparison.Ordinal)) continue;
                if (Array.IndexOf(BypassedRepoMethodNames, callee.Name) < 0) continue;

                violations.Add($"{method.FullName} -> {callee.DeclaringType.Name}.{callee.Name}");
            }
        }

        violations.Should().BeEmpty(
            because: "CachingTeamService.GetTeamDetailAsync must serve reads entirely from the TeamInfo cache "
                   + "(see Teams.md Architecture status, PR #581). Any IL call to ITeamRepository.GetBySlugWithRelationsAsync "
                   + "or ITeamRepository.GetRoleDefinitionsAsync from this method (or its async state machine) "
                   + "would reintroduce the per-render bypass that the cache migration eliminated.");
    }

    /// <summary>
    /// Enumerates the named method plus any compiler-generated async state
    /// machine <c>MoveNext</c> bodies hanging off it. Async methods compile
    /// to a small stub that constructs a state machine struct/class; the
    /// actual work — including any repo calls — lives in
    /// <c>MoveNext</c> on that nested type. We must inspect both to pin the
    /// invariant against the real IL.
    /// </summary>
    private static IEnumerable<MethodDefinition> EnumerateRelatedMethods(
        TypeDefinition declaringType,
        string methodName)
    {
        foreach (var method in declaringType.Methods)
        {
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal)) continue;
            yield return method;
        }

        // Compiler-emits <MethodName>d__N nested types holding MoveNext.
        var prefix = $"<{methodName}>";
        foreach (var nested in declaringType.NestedTypes)
        {
            if (!nested.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            foreach (var method in nested.Methods)
            {
                if (string.Equals(method.Name, "MoveNext", StringComparison.Ordinal))
                    yield return method;
            }
        }
    }
}
