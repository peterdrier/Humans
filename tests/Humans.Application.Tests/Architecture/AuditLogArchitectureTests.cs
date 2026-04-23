using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.AuditLog;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AuditLogService = Humans.Application.Services.AuditLog.AuditLogService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Audit
/// Log section — migrated per issue #552.
///
/// <para>
/// Audit Log chose <b>Option A</b> (no caching decorator, no dict cache).
/// Writes are scattered across every section (~96 call sites) and reads are
/// admin-only, so a cache is not warranted — same rationale used by Users
/// (#243), Governance (#242), Budget (#544), and City Planning (#543) when
/// they skipped the decorator.
/// </para>
///
/// <para>
/// <c>audit_log</c> is append-only per design-rules §12 — the repository
/// exposes only <c>AddAsync</c> for mutations; no <c>UpdateAsync</c> or
/// <c>DeleteAsync</c> surface is allowed. The architecture test
/// <see cref="IAuditLogRepository_HasNoUpdateOrDeleteMethods"/> pins that
/// constraint.
/// </para>
/// </summary>
public class AuditLogArchitectureTests
{
    // ── AuditLogService ──────────────────────────────────────────────────────

    [Fact]
    public void AuditLogService_LivesInHumansApplicationServicesAuditLogNamespace()
    {
        typeof(AuditLogService).Namespace
            .Should().Be("Humans.Application.Services.AuditLog",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void AuditLogService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(AuditLogService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IAuditLogRepository instead (design-rules §3)");
    }

    [Fact]
    public void AuditLogService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(AuditLogService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "canonical Audit Log data is not IMemoryCache-backed; §15 Option A applies (no caching decorator warranted)");
    }

    [Fact]
    public void AuditLogService_TakesRepository()
    {
        var ctor = typeof(AuditLogService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IAuditLogRepository));
    }

    [Fact]
    public void AuditLogService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(AuditLogService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); Audit Log Option A does not use a store at all");
    }

    // ── IAuditLogRepository ──────────────────────────────────────────────────

    [Fact]
    public void IAuditLogRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IAuditLogRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void AuditLogRepository_IsSealed()
    {
        var repoType = typeof(AuditLogRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [Fact]
    public void IAuditLogRepository_HasNoUpdateOrDeleteMethods()
    {
        // audit_log is append-only per design-rules §12.
        // The repository must not expose any UpdateAsync/DeleteAsync/RemoveAsync surface.
        var methods = typeof(IAuditLogRepository).GetMethods().Select(m => m.Name).ToList();

        methods.Should().NotContain(
            m => m.StartsWith("Update", StringComparison.Ordinal)
                 || m.StartsWith("Delete", StringComparison.Ordinal)
                 || m.StartsWith("Remove", StringComparison.Ordinal),
            because: "audit_log is append-only (§12); repositories for append-only tables expose only Add/Get methods");
    }
}
