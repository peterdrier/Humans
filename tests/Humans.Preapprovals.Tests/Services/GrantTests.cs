using System.Net;
using AwesomeAssertions.Execution;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using AwesomeAssertions;
using Xunit;
using Humans.AuditLog.Contracts;
using Humans.Notifications.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Preapprovals.Domain;
using Humans.Preapprovals.Services;
using Humans.Preapprovals.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Services;

/// <summary>Issue, issue-many, apply, approve, decline, revoke, change amount: the rules and their audit.</summary>
public sealed class GrantTests : PreapprovalsTestHarness
{
    [HumansTheory]
    [InlineData("issue")]
    [InlineData("issue-many")]
    [InlineData("apply")]
    [InlineData("edit-application")]
    [InlineData("approve")]
    [InlineData("decline")]
    [InlineData("revoke")]
    [InlineData("change-amount")]
    public async Task GrantMutation_CompletesNoticesAfterRequestCancellationFollowingSave(string operation)
    {
        var actor = SeedUser("Manager");
        var beneficiary = SeedUser("Beneficiary");
        var program = await SeedProgramAsync(managers: actor);
        var grant = operation is "edit-application" or "approve" or "decline"
            ? await SeedGrantAsync(program, beneficiary, status: GrantStatus.Applied)
            : operation is "revoke" or "change-amount"
                ? await SeedGrantAsync(program, beneficiary)
                : null;
        using var request = new CancellationTokenSource();
        AuditLog.LogAsync(Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string?>())
            .Returns(async _ => await request.CancelAsync());
        Notifications.SendAsync(Arg.Any<NotificationSourceDefinition>(), Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return Task.CompletedTask; });
        Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(call => { call.Arg<CancellationToken>().ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var service = NewService();

        switch (operation)
        {
            case "issue": await service.IssueAsync(program.Id, beneficiary, null, null, actor, request.Token); break;
            case "issue-many": await service.IssueManyAsync(program.Id, [beneficiary, SeedUser()], actor, request.Token); break;
            case "apply":
            case "edit-application": await service.ApplyAsync(program.Id, beneficiary, "Saved note", null, request.Token); break;
            case "approve": await service.ApproveAsync(grant!.Id, null, actor, request.Token); break;
            case "decline": await service.DeclineAsync(grant!.Id, "Declined", actor, request.Token); break;
            case "revoke": await service.RevokeAsync(grant!.Id, "Revoked", actor, request.Token); break;
            case "change-amount": await service.ChangeAmountAsync(grant!.Id, 400m, actor, request.Token); break;
        }

        request.IsCancellationRequested.Should().BeTrue();
        Notifications.ReceivedCalls().Should().HaveCount(operation is "issue-many" ? 2 : 1);
        Email.ReceivedCalls().Should().HaveCount(operation is "issue-many" ? 2 : 1);
        foreach (var call in Notifications.ReceivedCalls().Concat(Email.ReceivedCalls()).Concat(NotificationResolve.ReceivedCalls()))
            ((CancellationToken)call.GetArguments()[^1]!).CanBeCanceled.Should().BeFalse();
        await using var stored = OpenContext();
        (await stored.Grants.CountAsync(Ct)).Should().Be(operation is "issue-many" ? 2 : 1);
    }

    [HumansFact]
    public async Task Apply_CancelledBeforeMutation_DoesNotSaveOrDispatch()
    {
        var program = await SeedProgramAsync();
        var applicant = SeedUser();
        using var request = new CancellationTokenSource();
        await request.CancelAsync();

        var act = () => NewService().ApplyAsync(program.Id, applicant, null, null, request.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var stored = OpenContext();
        (await stored.Grants.CountAsync(Ct)).Should().Be(0);
        Email.ReceivedCalls().Should().BeEmpty();
        Notifications.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task Issue_Fixed_CopiesPerPersonAmount_NotifiesAndEmails_Audits()
    {
        var program = await SeedProgramAsync(perPerson: 300m);
        var person = SeedUser("Person", language: "de");
        var actor = SeedUser("Manager");

        var result = await NewService().IssueAsync(program.Id, person, null, "Fire Lotus", actor, Ct);

        result.Refusal.Should().BeNull();
        var grant = await ReadGrantAsync(result.Value);
        grant.MaxAmount.Should().Be(300m);
        grant.Status.Should().Be(GrantStatus.Open);
        grant.DecidedByUserId.Should().Be(actor);
        grant.Label.Should().Be("Fire Lotus");
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantIssued, AuditEntityTypes.Grant, grant.Id, Arg.Any<string>(), actor, program.Id, AuditEntityTypes.Program);
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Any<string?>(), $"/Preapprovals/{program.Id}",
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "preapproval_issued" && m.Culture == "de"), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Issue_Fixed_RefusesAnAmount()
    {
        var program = await SeedProgramAsync();
        var result = await NewService().IssueAsync(program.Id, SeedUser(), 250m, null, SeedUser(), Ct);
        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountNotAllowed);
    }

    [HumansTheory]
    [InlineData(null, PreapprovalErrorKeys.AmountRequired)]
    [InlineData(0, PreapprovalErrorKeys.AmountPositive)]
    [InlineData(-1, PreapprovalErrorKeys.AmountPositive)]
    public async Task Issue_PerGrant_RequiresPositiveAmount(int? amount, string key)
    {
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null);
        var result = await NewService().IssueAsync(program.Id, SeedUser(), amount, null, SeedUser(), Ct);
        result.Refusal!.Key.Should().Be(key);
    }

    [HumansFact]
    public async Task Issue_CeilingExceeded_RefusedWithHeadroom_RevokedDoesNotCount()
    {
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null, ceiling: 1000m);
        await SeedGrantAsync(program, amount: 600m);
        await SeedGrantAsync(program, amount: 900m, status: GrantStatus.Revoked);

        var result = await NewService().IssueAsync(program.Id, SeedUser(), 500m, null, SeedUser(), Ct);

        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.CeilingExceeded);
        result.Refusal.Args[0].Should().Be("400.00 €");
    }

    [HumansFact]
    public async Task Issue_ClosedOrPastDeadline_Refused()
    {
        var closed = await SeedProgramAsync(status: ProgramStatus.Closed);
        var past = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var service = NewService();
        (await service.IssueAsync(closed.Id, SeedUser(), null, null, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.ProgramClosed);
        (await service.IssueAsync(past.Id, SeedUser(), null, null, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.DeadlinePassed);
    }

    [HumansFact]
    public async Task IssueMany_AllOrNothing_AgainstCeiling()
    {
        var program = await SeedProgramAsync(perPerson: 300m, ceiling: 1000m);
        var people = new[] { SeedUser("A"), SeedUser("B"), SeedUser("C"), SeedUser("D") };

        var result = await NewService().IssueManyAsync(program.Id, people, SeedUser(), Ct);

        result.Refusal!.Key.Should().Be(PreapprovalErrorKeys.CeilingExceeded);
        (await OpenContext().Grants.CountAsync(Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task IssueMany_IssuesOnePerDistinctPerson_PerGrantRefused()
    {
        var program = await SeedProgramAsync(perPerson: 300m, ceiling: 1000m);
        var a = SeedUser("A"); var b = SeedUser("B");
        var service = NewService();

        var result = await service.IssueManyAsync(program.Id, [a, b, a], SeedUser(), Ct);

        result.Refusal.Should().BeNull();
        result.Value!.Count.Should().Be(2);
        (await OpenContext().Grants.ToListAsync(Ct)).Should().OnlyContain(g => g.MaxAmount == 300m && g.Status == GrantStatus.Open);

        var perGrant = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null);
        (await service.IssueManyAsync(perGrant.Id, [a], SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.IssueManyFixedOnly);
        (await service.IssueManyAsync(program.Id, [], SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.NoBeneficiaries);
    }

    [HumansFact]
    public async Task Apply_CreatesAppliedRow_NotifiesManagers_AndSecondSubmitEdits()
    {
        var manager = SeedUser("Manager");
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null, managers: manager);
        var applicant = SeedUser("Applicant");
        var service = NewService();

        var first = await service.ApplyAsync(program.Id, applicant, "Flame effects", 400m, Ct);
        var second = await service.ApplyAsync(program.Id, applicant, "Bigger flame effects", 450m, Ct);

        first.Refusal.Should().BeNull();
        second.Value.Should().Be(first.Value);
        var grants = await OpenContext().Grants.ToListAsync(Ct);
        var grant = grants.Should().ContainSingle().Subject;
        grant.Status.Should().Be(GrantStatus.Applied);
        grant.RequestedAmount.Should().Be(450m);
        grant.MaxAmount.Should().Be(450m);
        grant.ApplicationNote.Should().Be("Bigger flame effects");
        await Notifications.Received(2).SendAsync(PreapprovalsNotificationSources.PreapprovalApplicationReceived, NotificationClass.Actionable, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == manager), Arg.Is<string?>(b => b!.Contains("Applicant")),
            $"/Preapprovals/Admin/{program.Id}", Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.RequestSourceKey(grant.Id), Arg.Any<CancellationToken>());
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalApplicationReceived, PreapprovalService.RequestSourceKey(grant.Id), applicant, CancellationToken.None);
        await AuditLog.Received(2).LogAsync(AuditAction.PreapprovalGrantApplied, AuditEntityTypes.Grant, grant.Id, Arg.Any<string>(), applicant, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Apply_ApplicantNameLookupFailure_DoesNotFailTheSavedApplication(
        bool isUpdate, bool failEmailLookup)
    {
        var manager = SeedUser("Manager");
        var applicant = SeedUser("Applicant");
        var program = await SeedProgramAsync(managers: manager);
        var service = NewService();
        Guid? originalId = null;
        if (isUpdate)
            originalId = (await service.ApplyAsync(program.Id, applicant, "Original note", null, Ct)).Value;
        var person = await Users.GetUserInfoAsync(applicant, Ct);
        Notifications.ClearReceivedCalls();
        Email.ClearReceivedCalls();
        AuditLog.ClearReceivedCalls();
        var lookups = 0;
        Users.GetUserInfoAsync(applicant, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            // Membership lookup succeeds; one later, post-save channel fails during enrichment.
            if (++lookups == (failEmailLookup ? 3 : 2))
                throw new IOException("Applicant name lookup failed");
            return new ValueTask<Humans.Users.Contracts.UserInfo?>(person);
        });

        var result = await service.ApplyAsync(program.Id, applicant, "Saved note", null, Ct);

        result.Refusal.Should().BeNull();
        var grant = (await OpenContext().Grants.ToListAsync(Ct)).Should().ContainSingle().Subject;
        grant.Id.Should().Be(result.Value);
        if (isUpdate) grant.Id.Should().Be(originalId!.Value);
        grant.ApplicationNote.Should().Be("Saved note");
        grant.Status.Should().Be(GrantStatus.Applied);
        Notifications.ReceivedCalls().Should().HaveCount(failEmailLookup ? 1 : 0);
        Email.ReceivedCalls().Should().HaveCount(failEmailLookup ? 0 : 1);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantApplied, AuditEntityTypes.Grant,
            grant.Id, Arg.Any<string>(), applicant, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task Apply_UnderMergedId_EditsThePendingRow()
    {
        var program = await SeedProgramAsync();
        var oldId = Guid.NewGuid();
        var applicant = SeedUser("Applicant", mergedIds: [oldId]);
        await SeedGrantAsync(program, beneficiary: oldId, status: GrantStatus.Applied);

        await NewService().ApplyAsync(program.Id, applicant, "again", null, Ct);

        (await OpenContext().Grants.CountAsync(Ct)).Should().Be(1);
    }

    [HumansFact]
    public async Task Apply_Fixed_IgnoresRequestedAmount_PerGrantRequiresPositive()
    {
        var fixedProgram = await SeedProgramAsync(perPerson: 300m);
        var perGrant = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null);
        var service = NewService();

        var result = await service.ApplyAsync(fixedProgram.Id, SeedUser(), null, 999m, Ct);
        (await ReadGrantAsync(result.Value)).Should().Match<PreapprovalGrant>(g => g.MaxAmount == 300m && g.RequestedAmount == null);
        (await service.ApplyAsync(perGrant.Id, SeedUser(), null, null, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountRequired);
        (await service.ApplyAsync(perGrant.Id, SeedUser(), null, 0m, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountPositive);
    }

    [HumansFact]
    public async Task Apply_ClosedNotAcceptingOrPastDeadline_Refused()
    {
        var closed = await SeedProgramAsync(status: ProgramStatus.Closed);
        var notAccepting = await SeedProgramAsync(acceptsApplications: false);
        var past = await SeedProgramAsync(claimBy: Today.PlusDays(-1));
        var service = NewService();
        (await service.ApplyAsync(closed.Id, SeedUser(), null, null, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.ProgramClosed);
        (await service.ApplyAsync(notAccepting.Id, SeedUser(), null, null, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.NotAcceptingApplications);
        (await service.ApplyAsync(past.Id, SeedUser(), null, null, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.DeadlinePassed);
        (await OpenContext().Grants.CountAsync(Ct)).Should().Be(0);
    }

    [HumansFact]
    public async Task Approve_PerGrant_LowersToAmount_ChecksCeiling_ResolvesRequest_NotifiesBeneficiary()
    {
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null, ceiling: 1000m);
        await SeedGrantAsync(program, amount: 700m);
        var person = SeedUser("Person");
        var application = await SeedGrantAsync(program, beneficiary: person, amount: 500m, status: GrantStatus.Applied, requestedAmount: 500m);
        var actor = SeedUser("Manager");
        var service = NewService();

        (await service.ApproveAsync(application.Id, null, actor, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.CeilingExceeded);
        var result = await service.ApproveAsync(application.Id, 300m, actor, Ct);

        result.Refusal.Should().BeNull();
        var grant = await ReadGrantAsync(application.Id);
        grant.Status.Should().Be(GrantStatus.Open);
        grant.MaxAmount.Should().Be(300m);
        grant.RequestedAmount.Should().Be(500m);
        grant.DecidedByUserId.Should().Be(actor);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalApplicationReceived, PreapprovalService.RequestSourceKey(grant.Id), actor, CancellationToken.None);
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalFileBy, NotificationClass.Actionable, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), PreapprovalService.FileBySourceKey(grant.Id), Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantApproved, AuditEntityTypes.Grant, grant.Id, Arg.Is<string>(s => s.Contains("requested 500.00 €")), actor, program.Id, AuditEntityTypes.Program);
    }

    [HumansFact]
    public async Task Approve_Fixed_RefusesAmount_UsesPerPerson()
    {
        var program = await SeedProgramAsync(perPerson: 300m);
        var application = await SeedGrantAsync(program, amount: 300m, status: GrantStatus.Applied);
        var service = NewService();

        (await service.ApproveAsync(application.Id, 200m, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountNotAllowed);
        (await service.ApproveAsync(application.Id, null, SeedUser(), Ct)).Refusal.Should().BeNull();
        (await ReadGrantAsync(application.Id)).MaxAmount.Should().Be(300m);
    }

    [HumansFact]
    public async Task Approve_NotApplied_OrClosedProgram_Refused()
    {
        var program = await SeedProgramAsync();
        var open = await SeedGrantAsync(program);
        var closed = await SeedProgramAsync(status: ProgramStatus.Closed);
        var application = await SeedGrantAsync(closed, status: GrantStatus.Applied);
        var service = NewService();
        (await service.ApproveAsync(open.Id, null, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.WrongStatus);
        (await service.ApproveAsync(application.Id, null, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.ProgramClosed);
    }

    [HumansFact]
    public async Task Decline_SetsDeclined_ResolvesRequest_NotifiesWithNote()
    {
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var application = await SeedGrantAsync(program, beneficiary: person, status: GrantStatus.Applied);
        var actor = SeedUser("Manager");

        var result = await NewService().DeclineAsync(application.Id, "Not this year", actor, Ct);

        result.Refusal.Should().BeNull();
        var grant = await ReadGrantAsync(application.Id);
        grant.Status.Should().Be(GrantStatus.Declined);
        grant.DecidedByUserId.Should().Be(actor);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalApplicationReceived, PreapprovalService.RequestSourceKey(grant.Id), actor, CancellationToken.None);
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalGrantDecided, NotificationClass.Informational, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Is<string?>(b => b!.Contains("Not this year")),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), null, Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "preapproval_declined"), Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantDeclined, AuditEntityTypes.Grant, grant.Id, Arg.Is<string>(s => s.Contains("Not this year")), actor, program.Id, AuditEntityTypes.Program);
    }

    [HumansTheory]
    [InlineData((int)GrantStatus.Open)]
    [InlineData((int)GrantStatus.Claimed)]
    public async Task Revoke_KeepsAmountAndReport_ResolvesFileBy_NotifiesHolder(int statusValue)
    {
        var status = (GrantStatus)statusValue;
        var program = await SeedProgramAsync();
        var person = SeedUser("Person");
        var reportId = status == GrantStatus.Claimed ? Guid.NewGuid() : (Guid?)null;
        var grant = await SeedGrantAsync(program, beneficiary: person, amount: 300m, status: status, expenseReportId: reportId);
        var actor = SeedUser("Manager");

        var result = await NewService().RevokeAsync(grant.Id, "Budget cut", actor, Ct);

        result.Refusal.Should().BeNull();
        var after = await ReadGrantAsync(grant.Id);
        after.Status.Should().Be(GrantStatus.Revoked);
        after.MaxAmount.Should().Be(300m);
        after.ExpenseReportId.Should().Be(reportId);
        await NotificationResolve.Received(1).ResolveBySourceKeyAsync(PreapprovalsNotificationSources.PreapprovalFileBy, PreapprovalService.FileBySourceKey(grant.Id), actor, CancellationToken.None);
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalGrantDecided,
            status == GrantStatus.Claimed ? NotificationClass.Actionable : NotificationClass.Informational, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Is<string?>(b => b!.Contains("Budget cut")),
            status == GrantStatus.Claimed ? $"/Expenses/{reportId}" : $"/Preapprovals/{program.Id}",
            Arg.Any<string?>(), Arg.Any<string?>(), null, Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "preapproval_revoked"), Arg.Any<CancellationToken>());
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantRevoked, AuditEntityTypes.Grant, grant.Id, Arg.Is<string>(s => s.Contains("Budget cut")), actor, program.Id, AuditEntityTypes.Program);
    }

    [HumansTheory]
    [InlineData((int)GrantStatus.Applied)]
    [InlineData((int)GrantStatus.Consumed)]
    [InlineData((int)GrantStatus.Declined)]
    [InlineData((int)GrantStatus.Revoked)]
    public async Task Revoke_AndChangeAmount_RefusedOutsideOpenOrClaimed(int statusValue)
    {
        var status = (GrantStatus)statusValue;
        var program = await SeedProgramAsync();
        var grant = await SeedGrantAsync(program, status: status);
        var service = NewService();
        (await service.RevokeAsync(grant.Id, null, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.WrongStatus);
        (await service.ChangeAmountAsync(grant.Id, 50m, SeedUser(), Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.WrongStatus);
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task IssueAndChangeAmount_FormatMemberMessagesInRecipientCulture(string language)
    {
        using var callerCulture = new CultureScope("en");
        const decimal oldAmount = 1234.56m;
        const decimal newAmount = 2345.67m;
        var person = SeedUser("Person", language: language);
        var actor = SeedUser("Manager");
        var program = await SeedProgramAsync(kind: ProgramKind.PerGrantAmount, perPerson: null, ceiling: 10000m);
        var service = NewService(TestPreapprovalsEmails.CreateReal());

        var issued = await service.IssueAsync(program.Id, person, oldAmount, "Art", actor, Ct);
        issued.Refusal.Should().BeNull();
        (await service.ChangeAmountAsync(issued.Value, newAmount, actor, Ct)).Refusal.Should().BeNull();

        string oldText;
        string newText;
        string date;
        using (new CultureScope(language))
        {
            oldText = oldAmount.ToEuro();
            newText = newAmount.ToEuro();
            date = program.ClaimBy.ToDate();
        }
        var notices = Notifications.ReceivedCalls().Where(c =>
            string.Equals(c.GetMethodInfo().Name, "SendAsync", StringComparison.Ordinal)).ToArray();
        var issuedNotice = (string)notices.Single(c =>
            (NotificationSourceDefinition)c.GetArguments()[0]! == PreapprovalsNotificationSources.PreapprovalFileBy).GetArguments()[5]!;
        var changedNotice = (string)notices.Single(c =>
            (NotificationSourceDefinition)c.GetArguments()[0]! == PreapprovalsNotificationSources.PreapprovalGrantDecided).GetArguments()[5]!;
        var emails = Email.ReceivedCalls().Select(c => (EmailMessage)c.GetArguments()[0]!).ToArray();
        var issuedEmail = WebUtility.HtmlDecode(emails.Single(m =>
            string.Equals(m.TemplateName, "preapproval_issued", StringComparison.Ordinal)).HtmlBody);
        var changedEmail = WebUtility.HtmlDecode(emails.Single(m =>
            string.Equals(m.TemplateName, "preapproval_amount_changed", StringComparison.Ordinal)).HtmlBody);

        using var assertions = new AssertionScope();
        issuedNotice.Should().Contain(oldText).And.Contain(date);
        issuedEmail.Should().Contain(oldText).And.Contain(date);
        changedNotice.Should().Contain(oldText).And.Contain(newText);
        changedEmail.Should().Contain(oldText).And.Contain(newText);
    }

    [HumansFact]
    public async Task ChangeAmount_CeilingChecked_AuditedWithOldAndNew_Notifies()
    {
        var program = await SeedProgramAsync(ceiling: 1000m);
        await SeedGrantAsync(program, amount: 600m);
        var person = SeedUser("Person");
        var grant = await SeedGrantAsync(program, beneficiary: person, amount: 300m, status: GrantStatus.Claimed, expenseReportId: Guid.NewGuid());
        var actor = SeedUser("Manager");
        var service = NewService();

        (await service.ChangeAmountAsync(grant.Id, 500m, actor, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.CeilingExceeded);
        (await service.ChangeAmountAsync(grant.Id, 0m, actor, Ct)).Refusal!.Key.Should().Be(PreapprovalErrorKeys.AmountPositive);
        var result = await service.ChangeAmountAsync(grant.Id, 400m, actor, Ct);

        result.Refusal.Should().BeNull();
        (await ReadGrantAsync(grant.Id)).MaxAmount.Should().Be(400m);
        await AuditLog.Received(1).LogAsync(AuditAction.PreapprovalGrantAmountChanged, AuditEntityTypes.Grant, grant.Id,
            Arg.Is<string>(s => s.Contains("300.00 € → 400.00 €")), actor, program.Id, AuditEntityTypes.Program);
        await Notifications.Received(1).SendAsync(PreapprovalsNotificationSources.PreapprovalGrantDecided, NotificationClass.Informational, NotificationPriority.Normal,
            Arg.Any<string>(), Arg.Is<IReadOnlyList<Guid>>(r => r.Single() == person), Arg.Is<string?>(b => b!.Contains("300.00 €")),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), null, Arg.Any<CancellationToken>());
        await Email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.TemplateName == "preapproval_amount_changed"), Arg.Any<CancellationToken>());
    }
}
