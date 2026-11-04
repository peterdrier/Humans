using System.Text.Json;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Controllers;
using Humans.Web.Services;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class FileStatsApiControllerTests
{
    private static readonly CancellationToken Ct = Xunit.TestContext.Current.CancellationToken;
    private readonly FileStatsApiController _sut;

    public FileStatsApiControllerTests()
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 11, 4, 16, 0));
        var storage = Substitute.For<IFileStorage>();
        storage.ListAsync("uploads", Arg.Any<CancellationToken>()).Returns([
            new StoredFileInfo("uploads/camps/c1/g1.webp", 10, Instant.FromUtc(2026, 11, 2, 0, 0)),
        ]);
        _sut = new FileStatsApiController(new FileStatsService(storage), clock);
    }

    [HumansFact]
    public async Task Without_asOfDate_returns_yesterday_only()
    {
        var days = await DaysAsync(null);

        days.Should().ContainSingle();
        days[0].GetProperty("date").GetString().Should().Be("2026-11-03");
        days[0].GetProperty("count").GetInt32().Should().Be(1);
        days[0].GetProperty("categories").GetProperty("camps").GetProperty("bytes").GetInt64().Should().Be(10);
    }

    [HumansFact]
    public async Task With_asOfDate_returns_each_day_through_yesterday()
    {
        var days = await DaysAsync("2026-11-01");

        days.Select(d => d.GetProperty("date").GetString()).Should().Equal("2026-11-01", "2026-11-02", "2026-11-03");
    }

    [HumansTheory]
    [Xunit.InlineData("2026-11-04")]
    [Xunit.InlineData("not-a-date")]
    [Xunit.InlineData("2025-11-02")]
    public async Task Rejects_today_malformed_and_over_a_year(string asOfDate)
    {
        (await _sut.Get(asOfDate, Ct)).Should().BeOfType<BadRequestObjectResult>();
    }

    private async Task<List<JsonElement>> DaysAsync(string? asOfDate)
    {
        var result = await _sut.Get(asOfDate, Ct);
        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        return JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
    }
}
