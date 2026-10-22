using System.Reflection;
using Humans.Consent.Data;
using AwesomeAssertions;
using Humans.Consent.Contracts;
using Humans.Consent.Services;
using Humans.Base;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;
using ConsentService = Humans.Consent.Services.ConsentService;

namespace Humans.Consent.Tests.Architecture;

/// <summary>
/// <c>consent_records</c> is append-only (database triggers reject UPDATE/DELETE). The caching
/// decorators own all cache state; <see cref="LegalDocumentSyncService"/> is the sole writer for
/// <c>legal_documents</c>/<c>document_versions</c> and evicts its decorator directly after each write.
/// </summary>
public sealed class ConsentArchitectureTests
{
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

    /// <summary>
    /// Load-bearing invariant: the decorator's
    /// <see cref="Humans.Consent.Contracts.IConsentSubmission.SubmitConsentAsync"/> override is
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

    /// <summary>
    /// <see cref="IConsentManagement"/> and <see cref="IConsentService"/> resolve to one singleton from
    /// the production <c>Section.Register</c>, not a test-built mirror of it.
    /// </summary>
    [HumansFact]
    public void IConsentManagement_And_IConsentService_ResolveToSameSingleton()
    {
        var services = new ServiceCollection();
        // The section registers against the host's baseline; the caching
        // singletons only need logging, a clock and Users' singleton IUserService to construct.
        services.AddLogging();
        services.AddSingleton(Substitute.For<IClock>());
        services.AddSingleton(Substitute.For<IUserService>());

        new Section().Register(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var fromManagement = provider.GetRequiredService<IConsentManagement>();
        var fromPublic = provider.GetRequiredService<IConsentService>();

        fromManagement.Should().BeOfType<CachingConsentService>(
            because: "the cache decorator is the section's public face");
        ReferenceEquals(fromManagement, fromPublic).Should().BeTrue(
            because: "both interfaces must expose the same singleton so the cache is shared");
    }

    [HumansFact]
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // This section keeps its own translation file; a type asking for a third gets the key
        // name back instead of the text. SharedResource stays allowed for the Nav_Consents link.
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
