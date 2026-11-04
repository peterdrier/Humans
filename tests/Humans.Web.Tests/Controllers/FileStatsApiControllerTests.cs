using System.Text.Json;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Controllers;

public class FileStatsApiControllerTests
{
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly FileStatsApiController _sut;
    private static readonly CancellationToken Ct = Xunit.TestContext.Current.CancellationToken;

    public FileStatsApiControllerTests()
    {
        var clock = Substitute.For<IClock>();
        clock.GetCurrentInstant().Returns(Instant.FromUtc(2026, 11, 4, 16, 0));
        var profileId = Guid.NewGuid();
        _storage.ListAsync("uploads", Arg.Any<CancellationToken>()).Returns([
            new StoredFileInfo($"uploads/profile-pictures/{profileId}.jpg", 100, Instant.FromUtc(2026, 11, 1, 23, 59)),
            new StoredFileInfo("uploads/camps/c1/g1.webp", 10, Instant.FromUtc(2026, 11, 2, 0, 0)),
            new StoredFileInfo("uploads/camps/c1/g2.webp", 20, Instant.FromUtc(2026, 11, 3, 12, 0)),
            new StoredFileInfo("uploads/camps/c1/today.webp", 5, Instant.FromUtc(2026, 11, 4, 9, 0)),
        ]);
        _sut = new FileStatsApiController(_storage, clock);
    }

    [HumansFact]
    public async Task Without_asOfDate_returns_yesterday_only()
    {
        var days = await DaysAsync(null);

        days.Should().ContainSingle();
        days[0].GetProperty("date").GetString().Should().Be("2026-11-03");
        days[0].GetProperty("count").GetInt32().Should().Be(3);
        days[0].GetProperty("bytes").GetInt64().Should().Be(130);
        days[0].GetProperty("categories").GetProperty("camps").GetProperty("count").GetInt32().Should().Be(2);
    }

    [HumansFact]
    public async Task With_asOfDate_returns_each_day_through_yesterday_counting_files_written_by_its_end()
    {
        var days = await DaysAsync("2026-11-01");

        days.Select(d => d.GetProperty("date").GetString()).Should().Equal("2026-11-01", "2026-11-02", "2026-11-03");
        days.Select(d => d.GetProperty("count").GetInt32()).Should().Equal(1, 2, 3);
        days[0].GetProperty("categories").TryGetProperty("camps", out _).Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData("2026-11-04")]
    [Xunit.InlineData("not-a-date")]
    [Xunit.InlineData("2025-11-02")]
    public async Task Rejects_today_malformed_and_over_a_year(string asOfDate)
    {
        (await _sut.Get(asOfDate, Ct)).Should().BeOfType<BadRequestObjectResult>();
    }

    [HumansFact]
    public async Task Exposes_no_key_remainder()
    {
        var result = await _sut.Get("2026-11-01", Ct);

        JsonSerializer.Serialize(((OkObjectResult)result).Value).Should().NotContain("c1").And.NotContain(".jpg");
    }

    private async Task<List<JsonElement>> DaysAsync(string? asOfDate)
    {
        var result = await _sut.Get(asOfDate, Ct);
        var json = JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value);
        return JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
    }
}
