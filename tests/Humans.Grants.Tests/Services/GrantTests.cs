using Humans.Grants.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Grants.Domain;
using Humans.Grants.Services;
using Humans.Grants.Tests.Infrastructure;
using Humans.Notifications.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Humans.Grants.Tests.Services;

/// <summary>Issue, revoke, resend by managers; the source write; expiry; notices and audit.</summary>
public sealed class GrantTests : GrantsTestHarness
{
    private Task ReceivedNoticeAsync(NotificationSourceDefinition source, Guid recipient, int times = 1) =>
        Notifications.Received(times).SendAsync(source, NotificationClass.Informational, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.SequenceEqual(new[] { recipient })),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());

    [HumansFact]
    public async Task IssueTo_SkipsLiveHolders_AuditsAndTellsEachNewHolder()
    {
        var actor = SeedUser("Manager");
        var holder = SeedUser("Holder");
        var fresh = SeedUser("Fresh");
        var program = await SeedProgramAsync(managers: actor);
        await SeedGrantAsync(program, holder);

        var result = await NewService().IssueToAsync(program.Id, [holder, fresh, fresh], new LocalDate(2027, 9, 1), actor, Ct);

        result.Refusal.Should().BeNull();
        var created = result.Value.Should().ContainSingle().Subject;
        var grant = await ReadGrantAsync(created);
        grant.UserId.Should().Be(fresh);
        grant.ExpiresOn.Should().Be(new LocalDate(2027, 9, 1));
        grant.NotifiedAt.Should().Be(Clock.GetCurrentInstant());
        await AuditLog.Received(1).LogAsync(AuditAction.GrantIssued, AuditEntityTypes.Grant, created,
            Arg.Any<string>(), actor, fresh, AuditEntityTypes.User);
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantReceived, fresh);
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == $"{fresh}@example.org"), Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [Xunit.InlineData(false, true, 2028)]
    [Xunit.InlineData(true, true, 2028)]
    [Xunit.InlineData(false, false, 2027)]
    [Xunit.InlineData(true, false, 2027)]
    public async Task GrantAudits_FollowTheProgramsEventYear_WithStoredYearFallback(
        bool source, bool eventAvailable, int expectedYear)
    {
        var actor = SeedUser("Manager");
        var person = SeedUser("Holder");
        var program = await SeedProgramAsync(source ? GrantIssuer.Source : GrantIssuer.Managers, "low-income");
        await using (var db = OpenContext())
        {
            (await db.Programs.SingleAsync(Ct)).EventSettingsId = Event2027.Id;
            await db.SaveChangesAsync(Ct);
        }
        var movedEvent = BurnFixtures.Burn(year: 2028) with { Id = Event2027.Id };
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(eventAvailable ? [movedEvent] : []);
        var service = NewService();

        var grantId = source
            ? (await service.IssueAsync("low-income", person, "app-42", actor, Ct)).GrantId
            : (await service.IssueToAsync(program.Id, [person], null, actor, Ct)).Value!.Single();
        (await service.ResendNoticeAsync(grantId, actor, Ct)).Refusal.Should().BeNull();
        if (source)
            (await service.RevokeAsync("low-income", person, "Withdrew", actor, Ct)).Succeeded.Should().BeTrue();
        else
            (await service.RevokeGrantAsync(grantId, "Withdrew", actor, Ct)).Refusal.Should().BeNull();

        foreach (var action in new[] { AuditAction.GrantIssued, AuditAction.GrantNoticeResent, AuditAction.GrantRevoked })
            await AuditLog.Received(1).LogAsync(action, AuditEntityTypes.Grant, grantId,
                Arg.Is<string>(d => d.Contains($"({expectedYear})", StringComparison.Ordinal)),
                actor, person, AuditEntityTypes.User);
    }

    [HumansFact]
    public async Task IssueTo_RefusesClosedAndSourcePrograms()
    {
        var closed = await SeedProgramAsync(status: GrantProgramStatus.Closed);
        var source = await SeedProgramAsync(GrantIssuer.Source);
        var service = NewService();

        (await service.IssueToAsync(closed.Id, [SeedUser()], null, SeedUser(), Ct)).Refusal.Should().NotBeNull();
        (await service.IssueToAsync(source.Id, [SeedUser()], null, SeedUser(), Ct)).Refusal.Should().NotBeNull();
        (await ReadGrantsAsync()).Should().BeEmpty();
    }

    [HumansFact]
    public async Task IssueTo_MergedAliasesIssueOneGrantToTheSurvivor_AndRetriesSkipIt()
    {
        var archived = SeedUser("Archived");
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var actor = SeedUser("Manager");
        var program = await SeedProgramAsync();
        var service = NewService();

        var first = await service.IssueToAsync(program.Id, [archived, survivor], null, actor, Ct);
        var retry = await service.IssueToAsync(program.Id, [archived], null, actor, Ct);

        first.Value.Should().ContainSingle();
        retry.Value.Should().BeEmpty();
        (await ReadGrantsAsync()).Should().ContainSingle().Which.UserId.Should().Be(survivor);
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantReceived, survivor);
        await Email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task IssueTo_AnExistingGrantUnderAMergedIdCountsAsHeld()
    {
        var archived = SeedUser("Archived");
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var program = await SeedProgramAsync();
        var held = await SeedGrantAsync(program, archived);

        var result = await NewService().IssueToAsync(program.Id, [survivor, archived], null, SeedUser(), Ct);

        result.Value.Should().BeEmpty();
        (await ReadGrantsAsync()).Should().ContainSingle().Which.Id.Should().Be(held.Id);
        AuditLog.ReceivedCalls().Should().BeEmpty();
        Email.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SourceWrite_MergedAliasIssuesToTheSurvivor_RetriesReturnIt_AndAliasRevocationFindsIt()
    {
        var archived = SeedUser("Archived");
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var actor = SeedUser("Admin");
        await SeedProgramAsync(GrantIssuer.Source, "low-income");
        var service = NewService();

        var first = await service.IssueAsync("low-income", archived, "app-42", actor, Ct);
        var retry = await service.IssueAsync("low-income", survivor, "app-43", actor, Ct);

        first.Succeeded.Should().BeTrue();
        retry.Should().Be(first);
        (await ReadGrantsAsync()).Should().ContainSingle().Which.UserId.Should().Be(survivor);
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantReceived, survivor);
        (await service.RevokeAsync("low-income", archived, "Withdrew", actor, Ct)).Revoked.Should().BeTrue();
        (await ReadGrantAsync(first.GrantId)).RevokeReason.Should().Be("Withdrew");
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantRevoked, survivor);
    }

    [HumansFact]
    public async Task SourceWrite_FindsAnExistingGrantUnderAMergedId_ForIssueAndRevoke()
    {
        var archived = SeedUser("Archived");
        var survivor = SeedUser("Survivor", mergedIds: [archived]);
        AliasMergedId(archived, survivor);
        var actor = SeedUser("Admin");
        var program = await SeedProgramAsync(GrantIssuer.Source, "low-income");
        var held = await SeedGrantAsync(program, archived);
        var service = NewService();

        var issue = await service.IssueAsync("low-income", survivor, null, actor, Ct);

        issue.GrantId.Should().Be(held.Id);
        Email.ReceivedCalls().Should().BeEmpty();
        (await service.RevokeAsync("low-income", survivor, "Withdrew", actor, Ct)).Revoked.Should().BeTrue();
        (await ReadGrantAsync(held.Id)).RevokeReason.Should().Be("Withdrew");
        (await ReadGrantsAsync()).Should().ContainSingle();
    }

    [HumansFact]
    public async Task IssueTo_AfterRevoke_IssuesAgain()
    {
        var person = SeedUser();
        var program = await SeedProgramAsync();
        await SeedGrantAsync(program, person, revoked: true);

        var result = await NewService().IssueToAsync(program.Id, [person], null, SeedUser(), Ct);

        result.Value.Should().ContainSingle();
        (await ReadGrantsAsync()).Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Revoke_RecordsWhoWhenWhy_TellsThePerson_OnlyOnce()
    {
        var actor = SeedUser("Manager");
        var person = SeedUser("Holder");
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, person);
        var service = NewService();

        (await service.RevokeGrantAsync(grant.Id, " Left the crew ", actor, Ct)).Refusal.Should().BeNull();
        (await service.RevokeGrantAsync(grant.Id, null, actor, Ct)).Refusal.Should().NotBeNull();

        var saved = await ReadGrantAsync(grant.Id);
        saved.RevokedAt.Should().Be(Clock.GetCurrentInstant());
        saved.RevokedByUserId.Should().Be(actor);
        saved.RevokeReason.Should().Be("Left the crew");
        await AuditLog.Received(1).LogAsync(AuditAction.GrantRevoked, AuditEntityTypes.Grant, grant.Id,
            Arg.Any<string>(), actor, person, AuditEntityTypes.User);
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantRevoked, person);
        await Email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Resend_StampsNotifiedAt_AuditsAndSendsAgain_RefusedWhenRevoked()
    {
        var actor = SeedUser("Manager");
        var person = SeedUser();
        var program = await SeedProgramAsync();
        var live = await SeedGrantAsync(program, person);
        var revoked = await SeedGrantAsync(program, SeedUser(), revoked: true);
        Clock.Advance(Duration.FromDays(2));
        var service = NewService();

        (await service.ResendNoticeAsync(live.Id, actor, Ct)).Refusal.Should().BeNull();
        (await service.ResendNoticeAsync(revoked.Id, actor, Ct)).Refusal.Should().NotBeNull();

        (await ReadGrantAsync(live.Id)).NotifiedAt.Should().Be(Clock.GetCurrentInstant());
        await AuditLog.Received(1).LogAsync(AuditAction.GrantNoticeResent, AuditEntityTypes.Grant, live.Id,
            Arg.Any<string>(), actor, person, AuditEntityTypes.User);
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantReceived, person);
    }

    [HumansFact]
    public async Task NoticeFailure_IsLogged_NotThrown_GrantStands()
    {
        var person = SeedUser();
        var program = await SeedProgramAsync();
        Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("smtp down"));

        var result = await NewService().IssueToAsync(program.Id, [person], null, SeedUser(), Ct);

        result.Refusal.Should().BeNull();
        (await ReadGrantsAsync()).Should().ContainSingle();
        Logger.Entries.Should().Contain(e => e.Exception is InvalidOperationException);
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantReceived, person);
    }

    [HumansFact]
    public async Task InAppNoticeFailure_StillSendsTheEmail()
    {
        var person = SeedUser();
        var program = await SeedProgramAsync();
        Notifications.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("inbox down"));

        var result = await NewService().IssueToAsync(program.Id, [person], null, SeedUser(), Ct);

        result.Refusal.Should().BeNull();
        Logger.Entries.Should().Contain(e => e.Exception is InvalidOperationException);
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.RecipientEmail == $"{person}@example.org"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SourceIssue_FindsTheOpenProgramByKey_IsIdempotentPerPerson()
    {
        var person = SeedUser();
        var actor = SeedUser("Admin");
        await SeedProgramAsync(GrantIssuer.Source, "low-income", status: GrantProgramStatus.Closed, name: "Last year");
        var program = await SeedProgramAsync(GrantIssuer.Source, "low-income");
        var service = NewService();

        var first = await service.IssueAsync("low-income", person, "app-42", actor, Ct);
        var second = await service.IssueAsync("low-income", person, "app-43", actor, Ct);

        first.Succeeded.Should().BeTrue();
        second.Should().Be(first);
        var grant = (await ReadGrantsAsync()).Should().ContainSingle().Subject;
        first.GrantId.Should().Be(grant.Id);
        grant.ProgramId.Should().Be(program.Id);
        grant.SourceRef.Should().Be("app-42");
        grant.IssuedByUserId.Should().Be(actor);
        await Email.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SourceIssue_WithoutSourceRef_StoresNone()
    {
        await SeedProgramAsync(GrantIssuer.Source, "low-income");

        await NewService().IssueAsync("low-income", SeedUser(), null, SeedUser(), Ct);

        (await ReadGrantsAsync()).Should().ContainSingle().Which.SourceRef.Should().BeNull();
    }

    [HumansFact]
    public async Task SourceWrite_RefusesAKeyWithNoOpenProgram()
    {
        await SeedProgramAsync(GrantIssuer.Source, "low-income");
        await SeedProgramAsync(GrantIssuer.Source, "workers", status: GrantProgramStatus.Closed);
        await SeedProgramAsync();
        var service = NewService();

        foreach (var key in new[] { "workers", "nobody" })
        {
            var issue = await service.IssueAsync(key, SeedUser(), null, SeedUser(), Ct);
            issue.Succeeded.Should().BeFalse();
            issue.Refusal.Should().Be(GrantWriteRefusal.NoOpenProgram);
            var revoke = await service.RevokeAsync(key, SeedUser(), null, SeedUser(), Ct);
            revoke.Succeeded.Should().BeFalse();
            revoke.Refusal.Should().Be(GrantWriteRefusal.NoOpenProgram);
        }
        (await ReadGrantsAsync()).Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task SourceWrite_OverlongInputReturnsRefusalBeforeMutation(bool revoke)
    {
        var person = SeedUser();
        var actor = SeedUser();
        var program = await SeedProgramAsync(GrantIssuer.Source, "low-income");
        var grant = await SeedGrantAsync(program, person);
        var service = NewService();

        if (revoke)
        {
            var result = await service.RevokeAsync("low-income", person, new string('x', 501), actor, Ct);
            result.Succeeded.Should().BeFalse();
            result.Refusal.Should().Be(GrantWriteRefusal.ReasonTooLong);
        }
        else
        {
            var result = await service.IssueAsync("low-income", SeedUser(), new string('x', 129), actor, Ct);
            result.Succeeded.Should().BeFalse();
            result.Refusal.Should().Be(GrantWriteRefusal.SourceReferenceTooLong);
        }

        (await ReadGrantsAsync()).Should().ContainSingle();
        (await ReadGrantAsync(grant.Id)).RevokedAt.Should().BeNull();
        Email.ReceivedCalls().Should().BeEmpty();
        Notifications.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task SourceRevoke_RevokesTheLiveGrant_FalseWithout()
    {
        var person = SeedUser();
        var program = await SeedProgramAsync(GrantIssuer.Source, "low-income");
        var grant = await SeedGrantAsync(program, person);
        var service = NewService();

        (await service.RevokeAsync("low-income", SeedUser(), null, SeedUser(), Ct)).Should().Be(new GrantRevokeResult(false));
        (await service.RevokeAsync("low-income", person, "Withdrew", SeedUser(), Ct)).Should().Be(new GrantRevokeResult(true));

        (await ReadGrantAsync(grant.Id)).RevokeReason.Should().Be("Withdrew");
        await ReceivedNoticeAsync(GrantsNotificationSources.GrantRevoked, person);
    }

    [HumansFact]
    public async Task State_GrantDateOverridesProgramDate_ExpiresTheDayAfter()
    {
        var person = SeedUser();
        var program = await SeedProgramAsync(expiresOn: Today);
        await SeedGrantAsync(program, person);
        await SeedGrantAsync(await SeedProgramAsync(name: "Other", expiresOn: Today), person, expiresOn: Today.PlusDays(10));
        var service = NewService();

        var grants = await service.GetGrantsForUserAsync(person, Ct);
        grants.Select(g => g.State(Today)).Should().AllBeEquivalentTo(GrantState.Active);
        grants.Single(g => string.Equals(g.ProgramName, "Other", StringComparison.Ordinal)).ExpiresOn.Should().Be(Today.PlusDays(10));

        SetToday(Today.PlusDays(1));
        service.Today.Should().Be(Today.PlusDays(1));
        grants.Single(g => string.Equals(g.ProgramName, "Build crew 2027", StringComparison.Ordinal)).State(service.Today).Should().Be(GrantState.Expired);
        grants.Single(g => string.Equals(g.ProgramName, "Other", StringComparison.Ordinal)).State(service.Today).Should().Be(GrantState.Active);
    }
}
