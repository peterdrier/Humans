using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Backdoor.Data;
using Humans.Backdoor.Domain;
using Humans.Backdoor.Services;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Backdoor.Tests.Services;

/// <summary>
/// The key lifecycle: who may hold one, what the database keeps, and what the caller gets
/// back (nobodies-collective/Humans#1128).
/// </summary>
public class BackdoorApiKeyServiceTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 8, 24, 10, 0);

    private readonly IBackdoorApiKeyRepository _repository = Substitute.For<IBackdoorApiKeyRepository>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly BackdoorApiKeyService _sut;

    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();

    public BackdoorApiKeyServiceTests()
    {
        _sut = new BackdoorApiKeyService(
            _repository, _roles, _users, _audit, new FakeClock(Now),
            NullLogger<BackdoorApiKeyService>.Instance);
    }

    private void MakeEligible(
        Guid userId, bool admin = true, bool board = false, UserState state = UserState.Active)
    {
        _roles.IsUserAdminAsync(userId, Arg.Any<CancellationToken>()).Returns(admin);
        _roles.IsUserBoardMemberAsync(userId, Arg.Any<CancellationToken>()).Returns(board);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                new User { Id = userId, State = state, PreferredLanguage = "en" },
                [], [], [], profile: null, [])));
    }

    // ==========================================================================
    // Issue
    // ==========================================================================

    [HumansFact]
    public async Task Issue_stores_only_a_hash_and_a_prefix()
    {
        MakeEligible(_owner);
        BackdoorApiKey? stored = null;
        await _repository.AddAsync(Arg.Do<BackdoorApiKey>(k => stored = k), Arg.Any<CancellationToken>());

        var result = await _sut.IssueAsync(_owner, "triage agent", _actor);

        result.Succeeded.Should().BeTrue();
        result.PlaintextKey.Should().StartWith("hmn_");

        stored.Should().NotBeNull();
        stored!.KeyHash.Should().HaveLength(64).And.NotContain(result.PlaintextKey!);
        stored.DisplayPrefix.Should().Be(result.PlaintextKey![..12]);
        stored.UserId.Should().Be(_owner);
        stored.CreatedByUserId.Should().Be(_actor);
        stored.CreatedAt.Should().Be(Now);
        stored.IsActive.Should().BeTrue();
    }

    [HumansFact]
    public async Task Issue_to_a_Board_member_is_allowed()
    {
        MakeEligible(_owner, admin: false, board: true);

        var result = await _sut.IssueAsync(_owner, "board laptop", _actor);

        result.Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Issue_to_a_plain_member_is_refused()
    {
        MakeEligible(_owner, admin: false, board: false);

        var result = await _sut.IssueAsync(_owner, "triage agent", _actor);

        result.Succeeded.Should().BeFalse();
        result.PlaintextKey.Should().BeNull();
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [HumansFact]
    public async Task Issue_requires_a_label()
    {
        MakeEligible(_owner);

        var result = await _sut.IssueAsync(_owner, "  ", _actor);

        result.Succeeded.Should().BeFalse();
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [HumansFact]
    public async Task Issue_refuses_a_label_longer_than_the_column()
    {
        MakeEligible(_owner);

        // The form caps this at 100, but the endpoint is reachable without the form and the
        // column is varchar(100) — an unvalidated label would be a 500 from the insert.
        var result = await _sut.IssueAsync(_owner, new string('x', 101), _actor);

        result.Succeeded.Should().BeFalse();
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [HumansFact]
    public async Task Issue_records_an_audit_entry_naming_the_owner()
    {
        MakeEligible(_owner);

        await _sut.IssueAsync(_owner, "triage agent", _actor);

        await _audit.Received(1).LogAsync(
            AuditAction.BackdoorApiKeyIssued,
            AuditEntityTypes.BackdoorApiKey,
            Arg.Any<Guid>(),
            Arg.Is<string>(d => d.Contains("triage agent")),
            _actor,
            relatedEntityId: _owner,
            relatedEntityType: AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task Two_issues_never_produce_the_same_key()
    {
        MakeEligible(_owner);

        var first = await _sut.IssueAsync(_owner, "one", _actor);
        var second = await _sut.IssueAsync(_owner, "two", _actor);

        first.PlaintextKey.Should().NotBe(second.PlaintextKey);
    }

    // ==========================================================================
    // Revoke
    // ==========================================================================

    [HumansFact]
    public async Task Revoke_stamps_the_row_and_audits()
    {
        var key = ExistingKey();
        _repository.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _repository.RevokeAsync(key.Id, _actor, Now, Arg.Any<CancellationToken>()).Returns(true);

        (await _sut.RevokeAsync(key.Id, _actor)).Should().BeTrue();

        await _audit.Received(1).LogAsync(
            AuditAction.BackdoorApiKeyRevoked, AuditEntityTypes.BackdoorApiKey, key.Id,
            Arg.Any<string>(), _actor, key.UserId, AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task Revoke_of_an_already_revoked_key_is_a_no_op()
    {
        var key = ExistingKey();
        key.RevokedAt = Now;
        _repository.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);

        (await _sut.RevokeAsync(key.Id, _actor)).Should().BeFalse();

        await _repository.DidNotReceiveWithAnyArgs().RevokeAsync(default, default, default);
    }

    // ==========================================================================
    // Rotate
    // ==========================================================================

    [HumansFact]
    public async Task Rotate_revokes_the_old_key_and_issues_a_new_one_with_the_same_label()
    {
        var key = ExistingKey();
        MakeEligible(key.UserId);
        _repository.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _repository.RevokeAsync(key.Id, _actor, Now, Arg.Any<CancellationToken>()).Returns(true);
        BackdoorApiKey? replacement = null;
        await _repository.AddAsync(Arg.Do<BackdoorApiKey>(k => replacement = k), Arg.Any<CancellationToken>());

        var result = await _sut.RotateAsync(key.Id, _actor);

        result.Succeeded.Should().BeTrue();
        replacement.Should().NotBeNull();
        replacement!.Label.Should().Be(key.Label);
        replacement.UserId.Should().Be(key.UserId);
        replacement.KeyHash.Should().NotBe(key.KeyHash);
        await _repository.Received(1).RevokeAsync(key.Id, _actor, Now, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The audit half of a rotate: two entries, revoke then issue, both naming the actor and
    /// the owner. Without this, a rotate that silently skipped one of them would still pass.
    /// </summary>
    [HumansFact]
    public async Task Rotate_writes_a_revoke_entry_then_an_issue_entry()
    {
        var key = ExistingKey();
        MakeEligible(key.UserId);
        _repository.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);
        _repository.RevokeAsync(key.Id, _actor, Now, Arg.Any<CancellationToken>()).Returns(true);

        (await _sut.RotateAsync(key.Id, _actor)).Succeeded.Should().BeTrue();

        Received.InOrder(() =>
        {
            _ = _audit.LogAsync(
                AuditAction.BackdoorApiKeyRevoked, AuditEntityTypes.BackdoorApiKey, key.Id,
                Arg.Any<string>(), _actor, key.UserId, AuditEntityTypes.User);
            _ = _audit.LogAsync(
                AuditAction.BackdoorApiKeyIssued, AuditEntityTypes.BackdoorApiKey, Arg.Any<Guid>(),
                Arg.Any<string>(), _actor, key.UserId, AuditEntityTypes.User);
        });
    }

    [HumansFact]
    public async Task Rotate_refuses_when_the_owner_has_lost_Admin_and_Board()
    {
        var key = ExistingKey();
        MakeEligible(key.UserId, admin: false, board: false);
        _repository.GetByIdAsync(key.Id, Arg.Any<CancellationToken>()).Returns(key);

        var result = await _sut.RotateAsync(key.Id, _actor);

        result.Succeeded.Should().BeFalse();
        await _repository.DidNotReceiveWithAnyArgs().RevokeAsync(default, default, default);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    // ==========================================================================
    // Resolve
    // ==========================================================================

    [HumansFact]
    public async Task ResolveOwner_returns_the_owner_and_stamps_last_used()
    {
        MakeEligible(_owner);
        BackdoorApiKey? stored = null;
        await _repository.AddAsync(Arg.Do<BackdoorApiKey>(k => stored = k), Arg.Any<CancellationToken>());
        var plaintext = (await _sut.IssueAsync(_owner, "triage agent", _actor)).PlaintextKey!;

        _repository.FindActiveByHashAsync(stored!.KeyHash, Arg.Any<CancellationToken>()).Returns(stored);

        (await _sut.ResolveOwnerAsync(plaintext)).Should().Be(_owner);
        await _repository.Received(1).TouchAsync(stored.Id, Now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResolveOwner_returns_null_for_an_unknown_key()
    {
        _repository.FindActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<BackdoorApiKey?>(null));

        (await _sut.ResolveOwnerAsync("hmn_nope")).Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveOwner_returns_null_for_an_empty_key()
    {
        (await _sut.ResolveOwnerAsync(string.Empty)).Should().BeNull();

        await _repository.DidNotReceiveWithAnyArgs().FindActiveByHashAsync(default!, default);
    }

    [HumansFact]
    public async Task ResolveOwner_refuses_a_key_whose_owner_is_suspended()
    {
        var key = ExistingKey();
        _repository.FindActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(key);
        // Suspension moves users.State and leaves the Admin assignment standing, so the role
        // half of the predicate still says yes.
        MakeEligible(_owner, admin: true, state: UserState.AdminSuspended);

        (await _sut.ResolveOwnerAsync("hmn_whatever")).Should().BeNull();

        await _repository.DidNotReceiveWithAnyArgs().TouchAsync(default, default, default);
    }

    [HumansFact]
    public async Task Issue_to_a_suspended_admin_is_refused()
    {
        MakeEligible(_owner, admin: true, state: UserState.Suspended);

        var result = await _sut.IssueAsync(_owner, "triage agent", _actor);

        result.Succeeded.Should().BeFalse();
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [HumansFact]
    public async Task ResolveOwner_refuses_a_key_whose_owner_lost_both_roles()
    {
        var key = ExistingKey();
        _repository.FindActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(key);
        MakeEligible(_owner, admin: false, board: false);

        (await _sut.ResolveOwnerAsync("hmn_whatever")).Should().BeNull();

        // Refused, not revoked — restoring the role restores the key.
        await _repository.DidNotReceiveWithAnyArgs().TouchAsync(default, default, default);
        await _repository.DidNotReceiveWithAnyArgs().RevokeAsync(default, default, default, default);
    }

    // ==========================================================================
    // User-data fan-outs
    // ==========================================================================

    [HumansFact]
    public async Task Export_lists_the_owner_keys_without_the_hash()
    {
        var key = ExistingKey();
        _repository.GetForUserAsync(_owner, Arg.Any<CancellationToken>()).Returns([key]);

        var slices = await _sut.ContributeForUserAsync(_owner, Xunit.TestContext.Current.CancellationToken);

        var slice = slices.Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(GdprExportSections.BackdoorApiKeys);
        System.Text.Json.JsonSerializer.Serialize(slice.Data)
            .Should().Contain("triage agent").And.NotContain(key.KeyHash);
    }

    [HumansFact]
    public async Task Export_slice_is_null_when_the_human_holds_no_key()
    {
        _repository.GetForUserAsync(_owner, Arg.Any<CancellationToken>()).Returns([]);

        var slices = await _sut.ContributeForUserAsync(_owner, Xunit.TestContext.Current.CancellationToken);

        slices.Should().ContainSingle().Which.Data.Should().BeNull();
    }

    [HumansFact]
    public void Erasure_declares_backdoor_keys_fully_erased()
    {
        _sut.ErasureDeclaration.Should().ContainKey(GdprExportSections.BackdoorApiKeys)
            .WhoseValue.Should().BeNull("a credential has no basis to outlive its owner");
    }

    [HumansFact]
    public async Task Erase_hard_deletes_through_the_repository()
    {
        await _sut.EraseForUserAsync(_owner, Xunit.TestContext.Current.CancellationToken);

        await _repository.Received(1).EraseForUserAsync(_owner, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Merge_folds_the_eliminated_account_keys_onto_the_survivor()
    {
        var survivor = Guid.NewGuid();

        await _sut.ReassignAsync(_owner, survivor, _actor, Now, Xunit.TestContext.Current.CancellationToken);

        await _repository.Received(1).ReassignToUserAsync(_owner, survivor, Arg.Any<CancellationToken>());
    }

    private BackdoorApiKey ExistingKey() => new()
    {
        Id = Guid.NewGuid(),
        UserId = _owner,
        KeyHash = new string('a', 64),
        DisplayPrefix = "hmn_abcdefgh",
        Label = "triage agent",
        CreatedAt = Now - Duration.FromDays(30),
        CreatedByUserId = _actor,
    };
}
