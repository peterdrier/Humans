using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Consent;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ConsentService = Humans.Application.Services.Consent.ConsentService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Legal &amp;
/// Consent section's <see cref="ConsentService"/> — migrated per issue #547.
///
/// <para>
/// <c>consent_records</c> is append-only per design-rules §12 — the
/// repository exposes only <c>AddAsync</c> for mutations; no
/// <c>UpdateAsync</c>, <c>DeleteAsync</c>, or <c>RemoveAsync</c> surface is
/// allowed. Database triggers additionally reject UPDATE/DELETE at the
/// storage layer. The architecture test
/// <see cref="IConsentRepository_HasNoUpdateOrDeleteOrRemoveMethods"/> pins
/// the interface-level constraint.
/// </para>
///
/// <para>
/// This section's migration is <b>partial</b> as of #547b: <c>ConsentService</c>
/// migrated to the Application layer; <c>LegalDocumentService</c>,
/// <c>AdminLegalDocumentService</c>, and <c>LegalDocumentSyncService</c>
/// remain in <c>Humans.Infrastructure/Services/</c> and are tracked as
/// sub-task #547a.
/// </para>
/// </summary>
public class ConsentArchitectureTests
{
    // ── ConsentService ───────────────────────────────────────────────────────

    [HumansFact]
    public void ConsentService_LivesInHumansApplicationServicesConsentNamespace()
    {
        typeof(ConsentService).Namespace
            .Should().Be("Humans.Application.Services.Consent",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void ConsentService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ConsentService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IConsentRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void ConsentService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(ConsentService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "canonical Consent data is not IMemoryCache-backed; append-only semantics + per-user reads do not justify a decorator");
    }

    [HumansFact]
    public void ConsentService_TakesRepository()
    {
        var ctor = typeof(ConsentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IConsentRepository));
    }

    [HumansFact]
    public void ConsentService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(ConsentService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); ConsentService has no cache layer at all");
    }

    // ── IConsentRepository ───────────────────────────────────────────────────

    [HumansFact]
    public void IConsentRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IConsentRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void ConsentRepository_IsSealed()
    {
        // Mirrors ProfileRepository / UserRepository / AuditLogRepository — repository implementations are terminal;
        // no subclass should extend or override the EF-backed data access.
        var repoType = typeof(ConsentRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    /// <summary>
    /// <c>consent_records</c> is append-only per design-rules §12 — database
    /// triggers block UPDATE and DELETE, and the repository interface must
    /// not expose any mutation surface beyond appending new records. This
    /// test fails if a future refactor adds a method whose name implies
    /// mutation of existing rows.
    /// </summary>
    [HumansFact]
    public void IConsentRepository_HasNoUpdateOrDeleteOrRemoveMethods()
    {
        var methods = typeof(IConsentRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var mutationMethods = methods
            .Where(m =>
                m.Name.StartsWith("Update", StringComparison.Ordinal) ||
                m.Name.StartsWith("Delete", StringComparison.Ordinal) ||
                m.Name.StartsWith("Remove", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

        mutationMethods.Should().BeEmpty(
            because: "consent_records is append-only per design-rules §12 — DB triggers reject UPDATE and DELETE; only AddAsync should exist. New state = new row.");
    }

    /// <summary>
    /// Positive assertion: the repository must expose an <c>AddAsync</c>
    /// method. Pins the append-only surface so removing the only write
    /// primitive fails the suite immediately.
    /// </summary>
    [HumansFact]
    public void IConsentRepository_HasAddAsyncMethod()
    {
        var addAsync = typeof(IConsentRepository)
            .GetMethod("AddAsync", BindingFlags.Public | BindingFlags.Instance);

        addAsync.Should().NotBeNull(
            because: "append-only repositories must expose AddAsync as the sole mutation primitive");
    }
}
