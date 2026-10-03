using AwesomeAssertions;
using AwesomeAssertions.Execution;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Domain;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// The GDPR Article 17 side of <see cref="GoogleSyncLogService"/>, around merge chains (#1704).
/// Erasure is driven per id by Users' AccountDeletionService, which walks the chain raw, so a
/// contributor that fans out again reaches rows that are not the subject's.
/// </summary>
public sealed class GoogleSyncLogServiceGdprTests
{
    private readonly IGoogleSyncLogRepository _repo = Substitute.For<IGoogleSyncLogRepository>();
    private readonly ITeamResourceService _teamResources = Substitute.For<ITeamResourceService>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly GoogleSyncLogService _service;

    public GoogleSyncLogServiceGdprTests()
    {
        _service = new GoogleSyncLogService(
            _repo,
            _teamResources,
            _userService,
            _userEmails,
            _serviceProvider,
            new FakeClock(Instant.FromUtc(2026, 4, 21, 12, 0)),
            NullLogger<GoogleSyncLogService>.Instance);
    }

    /// <summary>Answers the resolving read the way Users does: the tombstone id reads back as the
    /// survivor's record, carrying every id folded into it.</summary>
    private void StubResolvesTo(Guid requestedId, Guid survivorId, params Guid[] mergedUserIds)
    {
        var survivor = new User { Id = survivorId, PreferredLanguage = "en" };
        var info = survivor.ToUserInfo() with { MergedUserIds = mergedUserIds };
        _userService.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));
    }

    [HumansFact]
    public async Task EraseForUserAsync_MergeTombstone_DropsOnlyThatIdsRows()
    {
        // A direct admin purge of a tombstone must erase that archived row and nothing else.
        // The resolving read answers with the survivor, so a fan-out here would delete the
        // living survivor's sync-log trail and that of every sibling merged into them.
        var archived = Guid.NewGuid();
        var sibling = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        StubResolvesTo(archived, survivor, archived, sibling);
        _userEmails.GetNobodiesTeamEmailAsync(archived, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        await _service.EraseForUserAsync(archived, Xunit.TestContext.Current.CancellationToken);

        await _repo.Received(1).DeleteByUserIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(archived)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EraseForUserAsync_AsksForTheWorkspaceAddressOfTheIdBeingErased()
    {
        // The suspend is the irreversible half: the address is looked up by the subject id, and
        // Users answers that raw, so erasing a tombstone cannot reach a living human's mailbox.
        var archived = Guid.NewGuid();
        var survivor = Guid.NewGuid();
        StubResolvesTo(archived, survivor, archived);
        _userEmails.GetNobodiesTeamEmailAsync(archived, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        await _service.EraseForUserAsync(archived, Xunit.TestContext.Current.CancellationToken);

        await _userEmails.Received(1).GetNobodiesTeamEmailAsync(archived, Arg.Any<CancellationToken>());
        _serviceProvider.DidNotReceive().GetService(typeof(IGoogleAdminService));
    }

    [HumansFact]
    public async Task ContributeForUserAsync_StillFansOutAcrossTheMergeChain()
    {
        // The export side keeps the fan-out: it is asked once, about a living human, and their
        // trail includes everything the accounts folded into them did.
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        StubResolvesTo(survivor, survivor, archived);
        var resourceId = Guid.NewGuid();
        _repo.GetAllByUserIdsContributorAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new GoogleSyncLogEntry
            {
                Id = Guid.NewGuid(), UserId = archived, ResourceId = resourceId,
                Action = GoogleSyncLogAction.AccessRevoked,
                OccurredAt = Instant.FromUtc(2026, 4, 21, 12, 0),
                Description = "Sync: revoked access", UserEmail = "alice@example.com",
                Role = "reader", Source = GoogleSyncSource.ScheduledSync,
                Success = false, ErrorMessage = "Permission denied"
            }]);
        _teamResources.GetResourceNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [resourceId] = "Shared folder" });

        var slices = await _service.ContributeForUserAsync(survivor, Xunit.TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(slices.Single().Data,
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }));
        json.RootElement.GetArrayLength().Should().Be(1);
        var row = json.RootElement[0];
        using var scope = new AssertionScope();
        row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "Action", "OccurredAt", "Description", "ResourceName", "UserEmail", "Role", "Source", "Success", "ErrorMessage");
        row.GetProperty("OccurredAt").GetRawText().Should().Be("\"2026-04-21T12:00:00Z\"");
        row.GetProperty("Action").GetString().Should().Be("AccessRevoked");
        row.GetProperty("Description").GetString().Should().Be("Sync: revoked access");
        row.GetProperty("ResourceName").GetString().Should().Be("Shared folder");
        row.GetProperty("UserEmail").GetString().Should().Be("alice@example.com");
        row.GetProperty("Role").GetString().Should().Be("reader");
        row.GetProperty("Source").GetString().Should().Be("ScheduledSync");
        row.GetProperty("Success").GetBoolean().Should().BeFalse();
        row.GetProperty("ErrorMessage").GetString().Should().Be("Permission denied");

        await _repo.Received(1).GetAllByUserIdsContributorAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids =>
                ids.Count == 2 && ids.Contains(survivor) && ids.Contains(archived)),
            Arg.Any<CancellationToken>());
    }
}
