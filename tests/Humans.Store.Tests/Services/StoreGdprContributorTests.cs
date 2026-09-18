using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Store.Services;
using Xunit;

namespace Humans.Store.Tests.Services;

/// <summary>
/// Store's Article 15/17 account (nobodies-collective/Humans#1116). The flagged columns
/// (<c>Invoice.IssuedByUserId</c>, <c>OrderLine.AddedByUserId</c>,
/// <c>Payment.RecordedByUserId</c>) are operator attribution, not the buyer, so the
/// declaration is a fiscal retention rather than an erasure — see
/// <see cref="StoreGdprContributor"/> for the reasoning.
/// </summary>
public class StoreGdprContributorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [HumansFact]
    public void ErasureDeclaration_DeclaresStoreRecordsAsRetained()
    {
        var declaration = new StoreGdprContributor().ErasureDeclaration;

        declaration.Should().ContainKey(GdprExportSections.StoreRecords);
        declaration[GdprExportSections.StoreRecords].Should().NotBeNull();
        declaration[GdprExportSections.StoreRecords]!.Length.Should().BeGreaterThanOrEqualTo(20);
    }

    [HumansFact]
    public void ErasureDeclaration_IsReadableOffAnUninitializedInstance()
    {
        // The coverage test (GdprErasureCoverageTests) reads this property off an instance
        // built with RuntimeHelpers.GetUninitializedObject — no constructor runs. If this
        // ever became an instance property instead of backed by a static field, it would
        // throw or return garbage here first.
        var uninitialized = (StoreGdprContributor)RuntimeHelpers.GetUninitializedObject(
            typeof(StoreGdprContributor));

        var declaration = uninitialized.ErasureDeclaration;

        declaration.Should().ContainKey(GdprExportSections.StoreRecords);
    }

    [HumansFact]
    public async Task EraseForUserAsync_IsANoOp_IdempotentAcrossRepeatedCalls()
    {
        var sut = new StoreGdprContributor();

        // No collaborators are injected, so any interaction beyond returning a completed
        // task would fail to compile/construct here — the assertion is that calling it
        // twice completes both times without throwing, and does nothing either time
        // (financial records are retained in full — see ErasureDeclaration).
        await sut.EraseForUserAsync(Guid.NewGuid(), Ct);
        await sut.EraseForUserAsync(Guid.NewGuid(), Ct);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ReturnsNoRows()
    {
        // No read path exists for "invoices issued / lines added / payments recorded by
        // this user" today, and one is not added solely to populate an export slice (see
        // the class doc comment).
        var sut = new StoreGdprContributor();

        var slices = await sut.ContributeForUserAsync(Guid.NewGuid(), Ct);

        slices.Should().BeEmpty();
    }
}
