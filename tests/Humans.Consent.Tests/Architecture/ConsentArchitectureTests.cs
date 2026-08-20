using System.Reflection;
using Humans.Consent.Data;
using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Consent.Services;
using Humans.Base;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using ConsentService = Humans.Consent.Services.ConsentService;

namespace Humans.Consent.Tests.Architecture;

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
/// services (<see cref="ConsentService"/>, <see cref="LegalDocumentSyncService"/>)
/// hold no cache state themselves. <see cref="LegalDocumentSyncService"/> is the
/// sole writer for <c>legal_documents</c>/<c>document_versions</c>
/// (nobodies-collective/Humans#751) and depends on
/// <see cref="ILegalDocumentCacheInvalidator"/> to signal the decorator
/// directly after each successful write, rather than via a SaveChanges
/// interceptor.
/// </para>
/// </summary>
public sealed class ConsentArchitectureTests
{
    // ── IConsentRepository ───────────────────────────────────────────────────

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
                because: "LegalDocumentSyncService resolves ILegalDocumentCacheInvalidator directly after each write and must hit the same singleton that backs ILegalDocumentSyncService (nobodies-collective/Humans#751)");
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

    // ── IConsentServiceRead split (memory/architecture/section-read-write-split.md) ──

    /// <summary>
    /// Asserts that <see cref="IConsentService"/> inherits from
    /// <see cref="IConsentServiceRead"/> so external sections can inject the
    /// narrow read surface while the full service still satisfies the full interface.
    /// </summary>
    [HumansFact]
    public void IConsentService_InheritsIConsentServiceRead()
    {
        typeof(IConsentServiceRead).IsAssignableFrom(typeof(IConsentService))
            .Should().BeTrue(
                because: "IConsentService is the full Consent surface; external sections inject the narrow IConsentServiceRead. " +
                         "See memory/architecture/section-read-write-split.md.");
    }

    /// <summary>
    /// Asserts that <see cref="IConsentService"/> and <see cref="IConsentServiceRead"/>
    /// resolve to the same singleton instance from the production DI registration.
    /// </summary>
    [HumansFact]
    public void IConsentService_And_IConsentServiceRead_ResolveToSameSingleton()
    {
        // Mirrors the Teams-section DI shape: the same CachingConsentService
        // singleton is exposed under both interface keys.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IConsentRepository>());
        services.AddSingleton(Substitute.For<ILegalDocumentSyncService>());
        services.AddSingleton(Substitute.For<IClock>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<ILogger<CachingConsentService>>());

        services.AddSingleton<CachingConsentService>();
        services.AddSingleton<IConsentService>(sp => sp.GetRequiredService<CachingConsentService>());
        services.AddSingleton<IConsentServiceRead>(sp => sp.GetRequiredService<CachingConsentService>());

        using var provider = services.BuildServiceProvider();

        var fromFull = provider.GetRequiredService<IConsentService>();
        var fromRead = provider.GetRequiredService<IConsentServiceRead>();
        var concrete = provider.GetRequiredService<CachingConsentService>();

        ReferenceEquals(fromFull, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // This section keeps its own translation file. A type that asks for a third
        // one gets nothing back and shows people the key name instead of the text.
        // ConsentController shipped exactly that on five paths, all of them behind a
        // form submission no page test goes through. SharedResource is fine — the
        // Nav_Consents link stayed there and the login partial renders it.
        var allowed = new[] { typeof(ConsentResource), typeof(SharedResource) };
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && !allowed.Contains(p.ParameterType.GetGenericArguments()[0]))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "the section's copy lives in ConsentResource and Nav_Consents in "
                   + "SharedResource; resolving a key through any third set renders the key "
                   + "itself and no error");
    }
}
