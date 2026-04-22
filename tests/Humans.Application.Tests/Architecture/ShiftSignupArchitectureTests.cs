using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ShiftSignupService = Humans.Application.Services.Shifts.ShiftSignupService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <c>ShiftSignupService</c> (issue #541, sub-task b).
///
/// <para>
/// ShiftSignupService chose <b>Option A</b> (no caching decorator). Shift
/// signup reads are request-scoped — a user's own signups, a shift's pending
/// approvals — and don't benefit from a dict cache. Same rationale used by
/// Users (#243), Governance (#242), Feedback (#549), and Auth (#551) when
/// they skipped the decorator.
/// </para>
/// </summary>
public class ShiftSignupArchitectureTests
{
    // ── ShiftSignupService ──────────────────────────────────────────────────

    [Fact]
    public void ShiftSignupService_LivesInHumansApplicationServicesShiftsNamespace()
    {
        typeof(ShiftSignupService).Namespace
            .Should().Be("Humans.Application.Services.Shifts",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void ShiftSignupService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ShiftSignupService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IShiftSignupRepository instead (design-rules §3)");
    }

    [Fact]
    public void ShiftSignupService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(ShiftSignupService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "shift-signup reads are request-scoped; §15 Option A applies (no caching decorator warranted)");
    }

    [Fact]
    public void ShiftSignupService_TakesRepository()
    {
        var ctor = typeof(ShiftSignupService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftSignupRepository));
    }

    [Fact]
    public void ShiftSignupService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(ShiftSignupService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); ShiftSignup Option A does not use a store at all");
    }

    [Fact]
    public void ShiftSignupService_IsSealed()
    {
        typeof(ShiftSignupService).IsSealed.Should().BeTrue(
            because: "Application-layer services are sealed to prevent ad-hoc extension; migrate behavior changes to the repository or a new service");
    }

    // ── IShiftSignupRepository ──────────────────────────────────────────────

    [Fact]
    public void IShiftSignupRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IShiftSignupRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void ShiftSignupRepository_IsSealed()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.ShiftSignupRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [Fact]
    public void ShiftSignupRepository_NamespaceIsHumansInfrastructureRepositories()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.ShiftSignupRepository);

        repoType.Namespace
            .Should().Be("Humans.Infrastructure.Repositories",
                because: "repository implementations live in Humans.Infrastructure.Repositories per design-rules §3 — the only layer that may reference Microsoft.EntityFrameworkCore");
    }
}
