using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.Services;
using NodaTime;
using NSubstitute;

namespace Humans.Web.Tests.Services;

public class FileStatsServiceTests
{
    private static readonly CancellationToken Ct = Xunit.TestContext.Current.CancellationToken;
    private readonly FileStatsService _sut;

    public FileStatsServiceTests()
    {
        var storage = Substitute.For<IFileStorage>();
        storage.ListAsync("uploads", Arg.Any<CancellationToken>()).Returns([
            new StoredFileInfo($"uploads/profile-pictures/{Guid.NewGuid()}.jpg", 100, Instant.FromUtc(2026, 11, 1, 23, 59)),
            new StoredFileInfo("uploads/camps/c1/g1.webp", 10, Instant.FromUtc(2026, 11, 2, 0, 0)),
            new StoredFileInfo("uploads/camps/c1/g2.webp", 20, Instant.FromUtc(2026, 11, 3, 12, 0)),
            new StoredFileInfo("uploads/stray.txt", 1, Instant.FromUtc(2026, 11, 3, 13, 0)),
        ]);
        _sut = new FileStatsService(storage);
    }

    [HumansFact]
    public async Task Each_day_counts_files_written_before_its_end_by_category_folder()
    {
        var days = await _sut.GetDailyAsync(new LocalDate(2026, 11, 1), new LocalDate(2026, 11, 3), Ct);

        days.Select(d => d.Date).Should().Equal(new LocalDate(2026, 11, 1), new LocalDate(2026, 11, 2), new LocalDate(2026, 11, 3));
        days.Select(d => d.Count).Should().Equal(1, 2, 4);
        days[0].Categories.Keys.Should().Equal("profile-pictures");
        days[2].Bytes.Should().Be(131);
        days[2].Categories["camps"].Should().Be(new FileStatsTotal(2, 30));
        days[2].Categories["(root)"].Should().Be(new FileStatsTotal(1, 1));
    }

    [HumansFact]
    public async Task Categories_never_carry_the_key_remainder()
    {
        var days = await _sut.GetDailyAsync(new LocalDate(2026, 11, 3), new LocalDate(2026, 11, 3), Ct);

        days[0].Categories.Keys.Should().BeEquivalentTo(["camps", "profile-pictures", "(root)"]);
    }
}
