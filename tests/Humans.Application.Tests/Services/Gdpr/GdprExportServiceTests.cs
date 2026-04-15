using AwesomeAssertions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Services.Gdpr;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services.Gdpr;

public class GdprExportServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 4, 15, 10, 30);

    private static GdprExportService CreateService(params IUserDataContributor[] contributors) =>
        new(
            contributors,
            new FakeClock(FixedNow),
            NullLogger<GdprExportService>.Instance);

    [Fact]
    public async Task ExportForUserAsync_StampsExportedAtFromClock()
    {
        var service = CreateService(new FakeContributor("Profile", new { Name = "Jane" }));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        export.ExportedAt.Should().Be("2026-04-15T10:30:00Z");
    }

    [Fact]
    public async Task ExportForUserAsync_MergesSlicesKeyedBySectionName()
    {
        var profile = new { Name = "Jane", City = "Barcelona" };
        var consents = new[] { new { Document = "Code of Conduct" } };
        var service = CreateService(
            new FakeContributor("Profile", profile),
            new FakeContributor("Consents", consents));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        export.Sections.Should().HaveCount(2);
        export.Sections["Profile"].Should().BeSameAs(profile);
        export.Sections["Consents"].Should().BeSameAs(consents);
    }

    [Fact]
    public async Task ExportForUserAsync_DropsNullSlices()
    {
        var service = CreateService(
            new FakeContributor("Profile", new { Name = "Jane" }),
            new FakeContributor("Applications", (object?)null));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        export.Sections.Should().ContainKey("Profile");
        export.Sections.Should().NotContainKey("Applications");
    }

    [Fact]
    public async Task ExportForUserAsync_PassesUserIdAndCancellationTokenToEveryContributor()
    {
        var userId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var first = new FakeContributor("A", new object());
        var second = new FakeContributor("B", new object());
        var service = CreateService(first, second);

        await service.ExportForUserAsync(userId, cts.Token);

        first.CalledWithUserId.Should().Be(userId);
        first.CalledWithToken.Should().Be(cts.Token);
        second.CalledWithUserId.Should().Be(userId);
        second.CalledWithToken.Should().Be(cts.Token);
    }

    [Fact]
    public async Task ExportForUserAsync_FailsLoudlyOnDuplicateSectionName()
    {
        var service = CreateService(
            new FakeContributor("Profile", new { A = 1 }),
            new FakeContributor("Profile", new { B = 2 }));

        var act = async () => await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Profile*");
    }

    [Fact]
    public async Task ExportForUserAsync_PropagatesContributorFailure()
    {
        var boom = new InvalidOperationException("boom");
        var service = CreateService(
            new FakeContributor("Profile", new { A = 1 }),
            new FakeContributor("Applications", boom));

        var act = async () => await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task ExportForUserAsync_WithNoContributors_ReturnsEmptySectionBag()
    {
        var service = CreateService();

        var export = await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        export.Sections.Should().BeEmpty();
        export.ExportedAt.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExportForUserAsync_FlattensMultipleSlicesFromOneContributor()
    {
        var service = CreateService(new FakeContributor(
            new UserDataSlice("Profile", new { Name = "Jane" }),
            new UserDataSlice("ContactFields", new[] { new { Field = "email" } }),
            new UserDataSlice("Languages", new[] { new { Code = "es" } })));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), CancellationToken.None);

        export.Sections.Should().HaveCount(3);
        export.Sections.Should().ContainKey("Profile");
        export.Sections.Should().ContainKey("ContactFields");
        export.Sections.Should().ContainKey("Languages");
    }

    private sealed class FakeContributor : IUserDataContributor
    {
        private readonly UserDataSlice[] _slices;
        private readonly Exception? _throw;

        public FakeContributor(string sectionName, object? data)
        {
            _slices = [new UserDataSlice(sectionName, data)];
        }

        public FakeContributor(string sectionName, Exception throwOnCall)
        {
            _slices = [new UserDataSlice(sectionName, null)];
            _throw = throwOnCall;
        }

        public FakeContributor(params UserDataSlice[] slices)
        {
            _slices = slices;
        }

        public Guid? CalledWithUserId { get; private set; }
        public CancellationToken? CalledWithToken { get; private set; }

        public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
        {
            CalledWithUserId = userId;
            CalledWithToken = ct;
            if (_throw is not null) throw _throw;
            return Task.FromResult<IReadOnlyList<UserDataSlice>>(_slices);
        }
    }
}
