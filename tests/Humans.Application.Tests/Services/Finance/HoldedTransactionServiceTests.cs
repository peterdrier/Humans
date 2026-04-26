using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Finance;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Finance;

/// <summary>
/// Smoke tests for <see cref="HoldedTransactionService"/>: confirms the persisted
/// <see cref="HoldedMatchStatus"/> is rendered as a plain-language reason and that
/// the Holded deep-link URL is synthesised from <c>HoldedDocId</c>.
/// </summary>
public class HoldedTransactionServiceTests
{
    private readonly IHoldedRepository _repository = Substitute.For<IHoldedRepository>();

    private HoldedTransactionService CreateSut() => new(_repository);

    [HumansFact]
    public async Task GetUnmatched_MapsMatchStatusToPlainLanguage()
    {
        _repository.GetUnmatchedAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new HoldedTransaction
            {
                HoldedDocId = "abc123",
                HoldedDocNumber = "F1",
                ContactName = "Vendor",
                Date = new LocalDate(2026, 4, 1),
                Total = 100m,
                Currency = "eur",
                Tags = new[] { "sound" },
                MatchStatus = HoldedMatchStatus.UnknownTag,
            },
        });

        var sut = CreateSut();

        var result = await sut.GetUnmatchedAsync();

        result.Should().HaveCount(1);
        result[0].MatchStatusReason.Should().Contain("not found");
        result[0].HoldedDeepLinkUrl.Should().StartWith("https://app.holded.com/");
    }
}
