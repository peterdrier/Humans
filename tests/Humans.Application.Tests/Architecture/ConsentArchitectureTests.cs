using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Legal;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Consent;
using Humans.Infrastructure.Services.Consent;
using Humans.Infrastructure.Services.Legal;
using ConsentService = Humans.Application.Services.Consent.ConsentService;
using LegalDocumentSyncService = Humans.Application.Services.Legal.LegalDocumentSyncService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Legal &amp;
/// Consent section's <see cref="ConsentService"/> and the T-04 two-layer
/// cache (<see cref="CachingConsentService"/>, <see cref="CachingLegalDocumentSyncService"/>).
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
/// T-04 introduces a per-user <see cref="UserConsentInfo"/> cache and a
/// global <see cref="LegalDocumentInfo"/> cache. The decorators
/// (<see cref="CachingConsentService"/>, <see cref="CachingLegalDocumentSyncService"/>)
/// own the <c>IMemoryCache</c>-style state; the inner Application-layer
/// services (<see cref="ConsentService"/>,
/// <see cref="LegalDocumentSyncService"/>) remain free of
/// caching dependencies — <c>ConsentService_HasNoIMemoryCacheConstructorParameter</c>
/// and <c>LegalDocumentSyncService_HasNoIMemoryCacheConstructorParameter</c>
/// pin that boundary.
/// </para>
/// </summary>
public class ConsentArchitectureTests
{
    // ── ConsentService ───────────────────────────────────────────────────────

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

    // ── T-04 cache decorators ────────────────────────────────────────────────

    /// <summary>
    /// T-04: <see cref="LegalDocumentSyncService"/> remains the Application-layer
    /// inner service. Its caching cousin lives in Infrastructure as a
    /// decorator (<see cref="CachingLegalDocumentSyncService"/>); the inner
    /// impl stays free of <c>IMemoryCache</c> so the Application-layer
    /// boundary doesn't acquire a Microsoft.Extensions.Caching dep.
    /// </summary>
    [HumansFact]
    public void LegalDocumentSyncService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(LegalDocumentSyncService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "T-04 caching lives in the CachingLegalDocumentSyncService decorator (Infrastructure); the inner Application service must not depend on IMemoryCache");
    }

    /// <summary>
    /// T-04: the decorator implements both <see cref="IConsentService"/>
    /// and <see cref="IConsentCacheInvalidator"/> on the same Singleton so
    /// cross-section signallers (e.g. <c>AccountMergeService</c>) and read
    /// callers hit the same instance.
    /// </summary>
    [HumansFact]
    public void CachingConsentService_ImplementsBothServiceAndInvalidator()
    {
        typeof(IConsentService).IsAssignableFrom(typeof(CachingConsentService))
            .Should().BeTrue();
        typeof(IConsentCacheInvalidator).IsAssignableFrom(typeof(CachingConsentService))
            .Should().BeTrue(
                because: "merge accept fan-out resolves IConsentCacheInvalidator and must hit the same singleton that backs IConsentService");
    }

    /// <summary>
    /// T-04: the global-document decorator implements both
    /// <see cref="ILegalDocumentSyncService"/> and
    /// <see cref="ILegalDocumentCacheInvalidator"/> so the SaveChanges
    /// interceptor and read callers share one instance.
    /// </summary>
    [HumansFact]
    public void CachingLegalDocumentSyncService_ImplementsBothServiceAndInvalidator()
    {
        typeof(ILegalDocumentSyncService).IsAssignableFrom(typeof(CachingLegalDocumentSyncService))
            .Should().BeTrue();
        typeof(ILegalDocumentCacheInvalidator).IsAssignableFrom(typeof(CachingLegalDocumentSyncService))
            .Should().BeTrue(
                because: "LegalDocumentSaveChangesInterceptor resolves ILegalDocumentCacheInvalidator and must hit the same singleton that backs ILegalDocumentSyncService");
    }

    /// <summary>
    /// T-04 load-bearing invariant: the decorator's
    /// <see cref="IConsentService.SubmitConsentAsync"/> override is
    /// declared on the decorator type itself — it cannot be a default
    /// interface implementation or a base-class inheritance, because
    /// synchronous cache invalidation must happen <em>before</em> the
    /// method returns. If a future refactor moves <c>SubmitConsentAsync</c>
    /// off <see cref="CachingConsentService"/>, this test fires.
    /// </summary>
    [HumansFact]
    public void CachingConsentService_DeclaresSubmitConsentAsync()
    {
        var method = typeof(CachingConsentService).GetMethod(
            "SubmitConsentAsync",
            BindingFlags.Public | BindingFlags.Instance);

        method.Should().NotBeNull(
            because: "the decorator must own SubmitConsentAsync so it can synchronously invalidate the user cache before returning to the controller");
        method.DeclaringType.Should().Be(
            typeof(CachingConsentService),
            because: "synchronous invalidation belongs on the decorator, not inherited or delegated");
    }
}
