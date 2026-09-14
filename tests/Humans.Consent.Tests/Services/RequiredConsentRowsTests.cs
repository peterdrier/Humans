using AwesomeAssertions;
using Humans.Consent.Services;
using NodaTime;

namespace Humans.Consent.Tests.Services;

/// <summary>
/// Pins <see cref="RequiredConsentRows.BuildOrdered"/> — the single row-shaping
/// path shared by <c>ConsentService</c> and <c>CachingConsentService</c>:
/// current version = latest <c>EffectiveFrom &lt;= now</c>, future-only documents
/// skipped, unsigned rows first, then ordinal title order.
/// </summary>
public sealed class RequiredConsentRowsTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 3, 1, 12, 0);

    [HumansFact]
    public void BuildOrdered_PicksLatestEffectiveVersion_AndSkipsFutureOnlyDocuments()
    {
        var oldVersion = Version(Now.Minus(Duration.FromDays(60)));
        var currentVersion = Version(Now.Minus(Duration.FromDays(1)));
        var pendingVersion = Version(Now.Plus(Duration.FromDays(1)));
        var futureOnly = Version(Now.Plus(Duration.FromDays(7)));

        var rows = RequiredConsentRows.BuildOrdered(
            [
                Doc("Privacy", oldVersion, currentVersion, pendingVersion),
                Doc("Statutes", futureOnly)
            ],
            new HashSet<Guid>(),
            Now);

        rows.Should().ContainSingle(because: "a document with no effective version yet yields no row");
        rows[0].DocumentVersionId.Should().Be(currentVersion.Id,
            because: "the row binds to the latest version already in effect, not a future one");
    }

    [HumansFact]
    public void BuildOrdered_OrdersUnsignedFirst_ThenByTitleOrdinal()
    {
        var signedVersion = Version(Now.Minus(Duration.FromDays(1)));
        var unsignedB = Version(Now.Minus(Duration.FromDays(1)));
        var unsignedA = Version(Now.Minus(Duration.FromDays(1)));

        var rows = RequiredConsentRows.BuildOrdered(
            [
                Doc("Alpha Signed", signedVersion),
                Doc("Beta Unsigned", unsignedB),
                Doc("Aardvark Unsigned", unsignedA)
            ],
            new HashSet<Guid> { signedVersion.Id },
            Now);

        rows.Select(r => r.Title).Should().Equal(
            "Aardvark Unsigned", "Beta Unsigned", "Alpha Signed");
        rows.Select(r => r.Signed).Should().Equal(false, false, true);
    }

    private static ActiveRequiredLegalDocumentSnapshot Doc(
        string name, params LegalDocumentVersionSnapshot[] versions) =>
        new(Guid.NewGuid(), name, Guid.NewGuid(), "Volunteers", Now, versions);

    private static LegalDocumentVersionSnapshot Version(Instant effectiveFrom) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Doc", 7, "v1.0",
            new Dictionary<string, string>(StringComparer.Ordinal), effectiveFrom, false, effectiveFrom, null);
}
