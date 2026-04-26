using AwesomeAssertions;
using Humans.Application.DTOs.Finance;
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
/// Unit tests for <see cref="HoldedSyncService.ResolveMatchAsync"/> covering
/// the four match-resolution rules and their eight observable outcomes.
/// </summary>
public class HoldedSyncServiceMatchTests
{
    private readonly IHoldedClient _client = Substitute.For<IHoldedClient>();
    private readonly IHoldedRepository _repository = Substitute.For<IHoldedRepository>();
    private readonly IBudgetService _budget = Substitute.For<IBudgetService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 26, 12, 0));

    private HoldedSyncService CreateSut() =>
        new(_client, _repository, _budget, _audit, _clock, NullLogger<HoldedSyncService>.Instance);

    private static HoldedDocDto MakeDoc(
        string currency = "eur",
        long? date = null,
        long? accountingDate = null,
        params string[] tags)
    {
        return new HoldedDocDto
        {
            Id = "doc-1",
            DocNumber = "F-1",
            Currency = currency,
            Date = date ?? Instant.FromUtc(2026, 6, 1, 0, 0).ToUnixTimeSeconds(),
            AccountingDate = accountingDate,
            Tags = tags.ToList(),
        };
    }

    private static BudgetYear MakeYear() => new()
    {
        Id = Guid.NewGuid(),
        Year = "2026",
        Name = "FY 2026",
        Status = BudgetYearStatus.Active,
    };

    private static BudgetCategory MakeCategory(Guid id) => new()
    {
        Id = id,
        Name = "Sound",
        Slug = "sound",
    };

    [HumansFact]
    public async Task ResolveMatch_NonEurCurrency_ReturnsUnsupportedCurrency()
    {
        var doc = MakeDoc(currency: "usd");
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.UnsupportedCurrency);
        result.BudgetCategoryId.Should().BeNull();
        await _budget.DidNotReceive().GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResolveMatch_NoYearForDate_ReturnsNoBudgetYearForDate()
    {
        var doc = MakeDoc();
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns((BudgetYear?)null);
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.NoBudgetYearForDate);
        result.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveMatch_EmptyTags_ReturnsNoTags()
    {
        var doc = MakeDoc(); // no tags
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(MakeYear());
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.NoTags);
        result.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveMatch_TagWithoutDash_ReturnsUnknownTag()
    {
        var doc = MakeDoc(tags: new[] { "nodashtag" });
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(MakeYear());
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.UnknownTag);
        result.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveMatch_UnknownGroupOrCategory_ReturnsUnknownTag()
    {
        var doc = MakeDoc(tags: new[] { "departments-unknown" });
        var year = MakeYear();
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(year);
        _budget.GetCategoryBySlugAsync(year.Id, "departments", "unknown", Arg.Any<CancellationToken>())
            .Returns((BudgetCategory?)null);
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.UnknownTag);
        result.BudgetCategoryId.Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveMatch_OneTagResolves_ReturnsMatched()
    {
        var doc = MakeDoc(tags: new[] { "departments-sound" });
        var year = MakeYear();
        var categoryId = Guid.NewGuid();
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(year);
        _budget.GetCategoryBySlugAsync(year.Id, "departments", "sound", Arg.Any<CancellationToken>())
            .Returns(MakeCategory(categoryId));
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.Matched);
        result.BudgetCategoryId.Should().Be(categoryId);
    }

    [HumansFact]
    public async Task ResolveMatch_MultipleTagsSameCategory_ReturnsMatched()
    {
        var doc = MakeDoc(tags: new[] { "departments-sound", "departments-sound" });
        var year = MakeYear();
        var categoryId = Guid.NewGuid();
        var category = MakeCategory(categoryId);
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(year);
        _budget.GetCategoryBySlugAsync(year.Id, "departments", "sound", Arg.Any<CancellationToken>())
            .Returns(category);
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.Matched);
        result.BudgetCategoryId.Should().Be(categoryId);
    }

    [HumansFact]
    public async Task ResolveMatch_MultipleTagsDifferentCategories_ReturnsConflict()
    {
        var doc = MakeDoc(tags: new[] { "departments-sound", "departments-lighting" });
        var year = MakeYear();
        var soundId = Guid.NewGuid();
        var lightingId = Guid.NewGuid();
        _budget.GetYearForDateAsync(Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(year);
        _budget.GetCategoryBySlugAsync(year.Id, "departments", "sound", Arg.Any<CancellationToken>())
            .Returns(MakeCategory(soundId));
        _budget.GetCategoryBySlugAsync(year.Id, "departments", "lighting", Arg.Any<CancellationToken>())
            .Returns(new BudgetCategory { Id = lightingId, Name = "Lighting", Slug = "lighting" });
        var sut = CreateSut();

        var result = await sut.ResolveMatchAsync(doc);

        result.Status.Should().Be(HoldedMatchStatus.MultiMatchConflict);
        result.BudgetCategoryId.Should().BeNull();
    }
}
