using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

/// <summary>
/// Tests for manual reassignment: local match is saved first (source of truth),
/// audit log is always written, and the Holded tag write-back is best-effort —
/// failure surfaces as a warning, not an exception.
/// </summary>
public class HoldedSyncServiceReassignTests
{
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedRepository _repository = Substitute.For<IHoldedRepository>();
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 26, 12, 0));

    private HoldedSyncService CreateSut() =>
        new(_client, _repository, _budget, _audit, _clock, NullLogger<HoldedSyncService>.Instance);

    private static BudgetCategory MakeCategoryWithGroup(Guid id, string groupSlug, string categorySlug)
    {
        var group = new BudgetGroup
        {
            Id = Guid.NewGuid(),
            Name = "Departments",
            Slug = groupSlug,
        };
        return new BudgetCategory
        {
            Id = id,
            BudgetGroupId = group.Id,
            BudgetGroup = group,
            Name = "Sound",
            Slug = categorySlug,
        };
    }

    [HumansFact]
    public async Task ReassignAsync_OnPutSuccess_SavesLocalAndReturnsTagPushedTrue()
    {
        var docId = "h-99";
        var categoryId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _budget.GetCategoryByIdAsync(categoryId)
            .Returns(MakeCategoryWithGroup(categoryId, "departments", "sound"));
        _client.TryAddTagAsync(docId, "departments-sound", Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = CreateSut();
        var result = await sut.ReassignAsync(docId, categoryId, actorId);

        result.LocalMatchSaved.Should().BeTrue();
        result.TagPushedToHolded.Should().BeTrue();
        result.Warning.Should().BeNull();

        await _repository.Received(1).AssignCategoryAsync(
            docId, categoryId, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.HoldedReassign,
            nameof(HoldedTransaction),
            categoryId,
            Arg.Is<string>(s => s.Contains(docId, StringComparison.Ordinal)
                                && s.Contains("departments-sound", StringComparison.Ordinal)),
            actorId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
        await _client.Received(1).TryAddTagAsync(docId, "departments-sound", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReassignAsync_OnPutFailure_StillSavesLocalAndReturnsWarning()
    {
        var docId = "h-100";
        var categoryId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        _budget.GetCategoryByIdAsync(categoryId)
            .Returns(MakeCategoryWithGroup(categoryId, "departments", "lighting"));
        _client.TryAddTagAsync(docId, "departments-lighting", Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = CreateSut();
        var result = await sut.ReassignAsync(docId, categoryId, actorId);

        result.LocalMatchSaved.Should().BeTrue();
        result.TagPushedToHolded.Should().BeFalse();
        result.Warning.Should().NotBeNullOrEmpty();
        result.Warning.Should().Contain("manually", because: "the warning should tell the user to add the tag manually");

        await _repository.Received(1).AssignCategoryAsync(
            docId, categoryId, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.HoldedReassign,
            nameof(HoldedTransaction),
            categoryId,
            Arg.Any<string>(),
            actorId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }
}
