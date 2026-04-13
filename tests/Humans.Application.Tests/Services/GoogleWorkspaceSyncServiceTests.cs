using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Authorization;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Service-layer authorization enforcement tests for GoogleWorkspaceSyncService.
///
/// These tests verify that unauthorized callers cannot invoke methods with
/// external Google Workspace side effects. The guard clauses throw
/// <see cref="UnauthorizedAccessException"/> BEFORE touching any Google API
/// client, so we do not need real Google SDK setup to exercise them —
/// the authorization check fires first.
/// </summary>
public sealed class GoogleWorkspaceSyncServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly IDbContextFactory<HumansDbContext> _dbContextFactory;
    private readonly IAuthorizationService _authorizationService = Substitute.For<IAuthorizationService>();
    private readonly GoogleWorkspaceSyncService _service;

    public GoogleWorkspaceSyncServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _dbContextFactory = Substitute.For<IDbContextFactory<HumansDbContext>>();

        // Default: authorization denies. Happy-path tests override per test.
        _authorizationService.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Failed());

        var settings = Options.Create(new GoogleWorkspaceSettings
        {
            Domain = "example.test",
            CustomerId = "customerX",
            TeamFoldersParentId = "parent-id"
        });

        _service = new GoogleWorkspaceSyncService(
            _dbContext,
            _dbContextFactory,
            settings,
            new FakeClock(Instant.FromUtc(2026, 4, 13, 12, 0)),
            Substitute.For<IAuditLogService>(),
            Substitute.For<ISyncSettingsService>(),
            _authorizationService,
            NullLogger<GoogleWorkspaceSyncService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ClaimsPrincipal Unprivileged() => new(new ClaimsIdentity());

    // --- Execute-class operations ---

    [Fact]
    public async Task ProvisionTeamFolderAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.ProvisionTeamFolderAsync(
            Guid.NewGuid(), "Folder", Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task ProvisionTeamGroupAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.ProvisionTeamGroupAsync(
            Guid.NewGuid(), "group@example.test", "Group", Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task AddUserToGroupAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.AddUserToGroupAsync(
            Guid.NewGuid(), "user@example.test", Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task RemoveUserFromGroupAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.RemoveUserFromGroupAsync(
            Guid.NewGuid(), "user@example.test", Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task AddUserToTeamResourcesAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.AddUserToTeamResourcesAsync(
            Guid.NewGuid(), Guid.NewGuid(), Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task RemoveUserFromTeamResourcesAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.RemoveUserFromTeamResourcesAsync(
            Guid.NewGuid(), Guid.NewGuid(), Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task SyncTeamGroupMembersAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.SyncTeamGroupMembersAsync(
            Guid.NewGuid(), Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task EnsureTeamGroupAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.EnsureTeamGroupAsync(
            Guid.NewGuid(), Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task RestoreUserToAllTeamsAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.RestoreUserToAllTeamsAsync(
            Guid.NewGuid(), Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task RemediateGroupSettingsAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.RemediateGroupSettingsAsync(
            "group@example.test", Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task UpdateDriveFolderPathsAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.UpdateDriveFolderPathsAsync(Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task SetInheritedPermissionsDisabledAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.SetInheritedPermissionsDisabledAsync(
            "file-id", true, Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task EnforceInheritedAccessRestrictionsAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.EnforceInheritedAccessRestrictionsAsync(Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    // --- Preview-class operations ---

    [Fact]
    public async Task SyncResourcesByTypeAsync_Preview_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.SyncResourcesByTypeAsync(
            GoogleResourceType.Group, SyncAction.Preview, Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task SyncResourcesByTypeAsync_Execute_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.SyncResourcesByTypeAsync(
            GoogleResourceType.Group, SyncAction.Execute, Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task SyncSingleResourceAsync_Execute_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.SyncSingleResourceAsync(
            Guid.NewGuid(), SyncAction.Execute, Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task CheckGroupSettingsAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.CheckGroupSettingsAsync(Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task GetEmailMismatchesAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.GetEmailMismatchesAsync(Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    [Fact]
    public async Task GetAllDomainGroupsAsync_ThrowsUnauthorized_ForUnprivilegedPrincipal()
    {
        var act = async () => await _service.GetAllDomainGroupsAsync(Unprivileged());

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        await AssertAuthorizationServiceCalledOnceAsync();
    }

    // --- Requirement selection: Preview vs Execute ---

    [Fact]
    public async Task SyncResourcesByTypeAsync_UsesPreviewRequirement_ForPreviewAction()
    {
        try
        {
            await _service.SyncResourcesByTypeAsync(
                GoogleResourceType.Group, SyncAction.Preview, Unprivileged());
        }
        catch (UnauthorizedAccessException)
        {
            // Expected — we just want to inspect the call.
        }

        await _authorizationService.Received(1).AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Is<IEnumerable<IAuthorizationRequirement>>(r =>
                r.OfType<GoogleSyncOperationRequirement>().Any(x =>
                    x == GoogleSyncOperationRequirement.Preview)));
    }

    [Fact]
    public async Task SyncResourcesByTypeAsync_UsesExecuteRequirement_ForExecuteAction()
    {
        try
        {
            await _service.SyncResourcesByTypeAsync(
                GoogleResourceType.Group, SyncAction.Execute, Unprivileged());
        }
        catch (UnauthorizedAccessException)
        {
            // Expected.
        }

        await _authorizationService.Received(1).AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Is<IEnumerable<IAuthorizationRequirement>>(r =>
                r.OfType<GoogleSyncOperationRequirement>().Any(x =>
                    x == GoogleSyncOperationRequirement.Execute)));
    }

    // --- Null principal guard ---

    [Fact]
    public async Task AddUserToGroupAsync_ThrowsArgumentNull_ForNullPrincipal()
    {
        var act = async () => await _service.AddUserToGroupAsync(
            Guid.NewGuid(), "user@example.test", principal: null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // --- Helpers ---

    private async Task AssertAuthorizationServiceCalledOnceAsync()
    {
        await _authorizationService.Received(1).AuthorizeAsync(
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<object?>(),
            Arg.Any<IEnumerable<IAuthorizationRequirement>>());
    }
}
