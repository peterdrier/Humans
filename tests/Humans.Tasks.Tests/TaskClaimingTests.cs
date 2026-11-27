using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Email.Contracts;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static Humans.Tasks.Tests.TaskTestKit;

namespace Humans.Tasks.Tests;

public sealed class TaskClaimingTests
{
    // TaskItemStatus is internal, so theory data uses these public stand-ins.
    public enum Finish
    {
        Done,
        Cancelled,
    }

    public enum Open
    {
        Todo,
        InProgress,
        Blocked,
    }

    public enum Decision
    {
        Approve,
        Decline,
    }

    private readonly TaskTestKit _kit = new();
    private readonly Guid _sam = Guid.NewGuid();
    private readonly Guid _robin = Guid.NewGuid();

    private Task<TaskResult> ClaimAsync(Guid taskId, Guid? who = null) =>
        _kit.Service.ClaimAsync(taskId, who ?? _sam, Ct);

    private Task AuditedAsync(AuditActionDefinition action, Guid taskId, Guid actor, Guid subject, string contains) =>
        _kit.Audit.Received(1).LogAsync(
            action, "TaskItem", taskId,
            Arg.Is<string>(d => d.Contains(contains, StringComparison.Ordinal)),
            actor, subject, AuditSubjectTypes.User);

    [HumansFact]
    public async Task Claim_WithoutApproval_TakesSlotAtOnce()
    {
        var task = await _kit.CreateAsync(Input(slots: 2));

        var result = await ClaimAsync(task.Id);

        var after = result.Task!;
        after.Assignments.Should().ContainSingle().Which.Should().Match<TaskAssignmentInfo>(a => a.UserId == _sam && a.IsConfirmed);
        after.OpenSlots.Should().Be(1);
        await AuditedAsync(TasksAuditActions.TaskClaimed, task.Id, _sam, _sam, "Took a place on task 'Build the bar'");
        _kit.SentEmails.Should().BeEmpty("only an answer to a request sends email");
    }

    [HumansFact]
    public async Task Claim_NeedingApproval_MakesRequestWithoutSlot()
    {
        var task = await _kit.CreateAsync(Input(slots: 1, needsApproval: true, reason: "chainsaw licence"));

        var after = (await ClaimAsync(task.Id)).Task!;

        after.Assignments.Should().ContainSingle().Which.Should().Match<TaskAssignmentInfo>(a => a.UserId == _sam && !a.IsConfirmed);
        after.OpenSlots.Should().Be(1);
        after.ConfirmedCount.Should().Be(0);
        await AuditedAsync(TasksAuditActions.TaskRequested, task.Id, _sam, _sam, "Asked to join task 'Build the bar'");
        _kit.SentEmails.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Claim_ManyPeopleMayRequestTheSameSingleSlot()
    {
        var task = await _kit.CreateAsync(Input(slots: 1, needsApproval: true));

        (await ClaimAsync(task.Id, _sam)).Succeeded.Should().BeTrue();
        (await ClaimAsync(task.Id, _robin)).Succeeded.Should().BeTrue();

        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().HaveCount(2);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_IsRefusedOnAFullTask(bool needsApproval)
    {
        var task = await _kit.CreateAsync(Input(slots: 1, needsApproval: needsApproval));
        await _kit.Service.AssignAsync(task.Id, _robin, _kit.Actor, Ct);

        var result = await ClaimAsync(task.Id);

        result.Refusal.Should().Be("Tasks_Refusal_Full");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle();
    }

    [HumansTheory]
    [InlineData(Finish.Done)]
    [InlineData(Finish.Cancelled)]
    public async Task Claim_IsRefusedOnADoneOrCancelledTask(Finish status)
    {
        var task = await _kit.CreateAsync(Input(slots: 3));
        await _kit.SetStatusAsync(task.Id, Enum.Parse<TaskItemStatus>(status.ToString()));

        (await ClaimAsync(task.Id)).Refusal.Should().Be("Tasks_Refusal_Finished");
    }

    [HumansTheory]
    [InlineData(Open.Todo)]
    [InlineData(Open.InProgress)]
    [InlineData(Open.Blocked)]
    public async Task Claim_IsAllowedOnAnyUnfinishedTask(Open status)
    {
        var task = await _kit.CreateAsync(Input(slots: 3));
        await _kit.SetStatusAsync(task.Id, Enum.Parse<TaskItemStatus>(status.ToString()), status == Open.Blocked ? "permit" : null);

        (await ClaimAsync(task.Id)).Succeeded.Should().BeTrue();
    }

    [HumansFact]
    public async Task Claim_WhenAlreadyOnOrAsked_IsRefused()
    {
        var plain = await _kit.CreateAsync(Input(slots: 3));
        var gated = await _kit.CreateAsync(Input(slots: 3, needsApproval: true));
        await ClaimAsync(plain.Id);
        await ClaimAsync(gated.Id);

        (await ClaimAsync(plain.Id)).Refusal.Should().Be("Tasks_Refusal_YouAreOn");
        (await ClaimAsync(gated.Id)).Refusal.Should().Be("Tasks_Refusal_YouRequested");
        (await _kit.Service.GetTaskAsync(plain.Id, Ct))!.Assignments.Should().ContainSingle();
        (await _kit.Service.GetTaskAsync(gated.Id, Ct))!.Assignments.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Claim_OnATaskThatIsGone_IsRefused() =>
        (await ClaimAsync(Guid.NewGuid())).Refusal.Should().Be("Tasks_Refusal_NoTask");

    [HumansFact]
    public async Task Claim_ConcurrentClaims_BothLand()
    {
        var task = await _kit.CreateAsync(Input(slots: 1));
        // Hold both writes until both have read the task with its one free slot.
        var arrived = 0;
        using var gate = new SemaphoreSlim(0);
        _kit.Repository.BeforeAddAssignment = async () =>
        {
            Interlocked.Increment(ref arrived);
            await gate.WaitAsync(Ct);
        };

        var both = Task.WhenAll(ClaimAsync(task.Id, _sam), ClaimAsync(task.Id, _robin));
        while (Volatile.Read(ref arrived) < 2)
            await Task.Delay(5, Ct);
        gate.Release(2);
        var results = await both;

        results.Should().OnlyContain(r => r.Succeeded);
        var after = (await _kit.Service.GetTaskAsync(task.Id, Ct))!;
        after.ConfirmedCount.Should().Be(2);
        after.OpenSlots.Should().Be(0, "never below zero: it shows as 2 of 1");
    }

    [HumansFact]
    public async Task Claim_NeverMakesAnyoneATeamMember()
    {
        var task = await _kit.CreateAsync(Input(slots: 2, needsApproval: true));
        await ClaimAsync(task.Id, _sam);
        await _kit.Service.ApproveRequestAsync(task.Id, _sam, _kit.Actor, Ct);
        var plain = await _kit.CreateAsync(Input(slots: 2));
        await ClaimAsync(plain.Id, _robin);
        await _kit.Service.AssignAsync(plain.Id, Guid.NewGuid(), _kit.Actor, Ct);

        // Tasks reads teams for names and checks, and never calls a write on Teams.
        _kit.Teams.ReceivedCalls().Select(c => c.GetMethodInfo().Name).Should().OnlyContain(n => n.StartsWith("Get", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Release_FreesTheConfirmedSlot()
    {
        var task = await _kit.CreateAsync(Input(slots: 1));
        await ClaimAsync(task.Id);

        var result = await _kit.Service.ReleaseAsync(task.Id, _sam, Ct);

        result.Task!.Assignments.Should().BeEmpty();
        result.Task.OpenSlots.Should().Be(1);
        await AuditedAsync(TasksAuditActions.TaskReleased, task.Id, _sam, _sam, "Gave back their place on task 'Build the bar'");
        _kit.SentEmails.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Release_WithoutConfirmedPlace_LeavesRequestAlone()
    {
        var gated = await _kit.CreateAsync(Input(needsApproval: true));
        var other = await _kit.CreateAsync();
        await ClaimAsync(gated.Id);

        (await _kit.Service.ReleaseAsync(gated.Id, _sam, Ct)).Refusal.Should().Be("Tasks_Refusal_YouAreNotOn");
        (await _kit.Service.ReleaseAsync(other.Id, _sam, Ct)).Refusal.Should().Be("Tasks_Refusal_YouAreNotOn");
        (await _kit.Service.GetTaskAsync(gated.Id, Ct))!.Assignments.Should().ContainSingle(a => !a.IsConfirmed);
    }

    [HumansFact]
    public async Task Release_OnlyGivesBackTheActorsOwnPlace()
    {
        var task = await _kit.CreateAsync(Input(slots: 2));
        await ClaimAsync(task.Id, _sam);
        await ClaimAsync(task.Id, _robin);

        await _kit.Service.ReleaseAsync(task.Id, _sam, Ct);

        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle().Which.UserId.Should().Be(_robin);
    }

    [HumansFact]
    public async Task Withdraw_TakesBackRequestSilently()
    {
        var task = await _kit.CreateAsync(Input(needsApproval: true));
        await ClaimAsync(task.Id);

        var result = await _kit.Service.WithdrawRequestAsync(task.Id, _sam, Ct);

        result.Task!.Assignments.Should().BeEmpty();
        await AuditedAsync(TasksAuditActions.TaskRequestWithdrawn, task.Id, _sam, _sam, "Took back their request");
        _kit.SentEmails.Should().BeEmpty();
        (await ClaimAsync(task.Id)).Succeeded.Should().BeTrue("they may ask again");
    }

    [HumansFact]
    public async Task Withdraw_WithoutRequest_KeepsConfirmedPlace()
    {
        var task = await _kit.CreateAsync(Input(slots: 2));
        await ClaimAsync(task.Id);

        (await _kit.Service.WithdrawRequestAsync(task.Id, _sam, Ct)).Refusal.Should().Be("Tasks_Refusal_NoYourRequest");
        (await _kit.Service.WithdrawRequestAsync(task.Id, _robin, Ct)).Refusal.Should().Be("Tasks_Refusal_NoYourRequest");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle(a => a.IsConfirmed);
    }

    [HumansTheory]
    [InlineData(Decision.Approve)]
    [InlineData(Decision.Decline)]
    public async Task AnswerRequest_CancelledAfterAudit_CompletesEmail(Decision decision)
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        var task = await _kit.CreateAsync(Input(needsApproval: true));
        await ClaimAsync(task.Id);
        var person = await _kit.Users.GetUserInfoAsync(_sam, Ct);
        using var request = new CancellationTokenSource();
        var action = decision == Decision.Approve ? TasksAuditActions.TaskRequestApproved : TasksAuditActions.TaskRequestDeclined;
        _kit.Audit.LogAsync(action, "TaskItem", task.Id, Arg.Any<string>(), _kit.Actor, _sam, AuditSubjectTypes.User)
            .Returns(_ => request.CancelAsync());
        _kit.Users.GetUserInfoAsync(_sam, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return person;
        });
        _kit.Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        var result = decision == Decision.Approve
            ? await _kit.Service.ApproveRequestAsync(task.Id, _sam, _kit.Actor, request.Token)
            : await _kit.Service.DeclineRequestAsync(task.Id, _sam, "Not this time", _kit.Actor, request.Token);

        request.IsCancellationRequested.Should().BeTrue();
        result.Refusal.Should().BeNull();
        var current = (await _kit.Service.GetTaskAsync(task.Id, Ct))!;
        current.Assignments.Count.Should().Be(decision == Decision.Approve ? 1 : 0);
        var mail = _kit.SentEmails.Should().ContainSingle().Subject;
        mail.TemplateName.Should().Be(decision == Decision.Approve ? "task_request_approved" : "task_request_declined");
        await _kit.Email.Received(1).SendAsync(Arg.Any<EmailMessage>(), CancellationToken.None);
    }

    [HumansFact]
    public async Task Approve_ConfirmsRequest_EmailsInTheirLanguage()
    {
        _kit.GiveEmail(_sam, "sam@example.com", language: "es");
        var task = await _kit.CreateAsync(Input(title: "Welfare tent", slots: 2, needsApproval: true));
        await ClaimAsync(task.Id);

        var result = await _kit.Service.ApproveRequestAsync(task.Id, _sam, _kit.Actor, Ct);

        result.Task!.Assignments.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();
        result.Task.OpenSlots.Should().Be(1);
        await AuditedAsync(TasksAuditActions.TaskRequestApproved, task.Id, _kit.Actor, _sam, "(1 of 2)");
        var mail = _kit.SentEmails.Should().ContainSingle().Subject;
        mail.RecipientEmail.Should().Be("sam@example.com");
        mail.UserId.Should().Be(_sam);
        mail.Category.Should().Be(MessageCategory.VolunteerUpdates);
        mail.TemplateName.Should().Be("task_request_approved");
        mail.Culture.Should().Be("es");
        mail.Subject.Should().Contain("Welfare tent").And.Contain("Estás");
        mail.HtmlBody.Should().Contain($"https://humans.test/Tasks/{task.Id}");
    }

    [HumansFact]
    public async Task Approve_OverfillsATaskThatFilledUpMeanwhile()
    {
        var task = await _kit.CreateAsync(Input(slots: 1, needsApproval: true));
        await ClaimAsync(task.Id, _sam);
        await _kit.Service.AssignAsync(task.Id, _robin, _kit.Actor, Ct);

        var result = await _kit.Service.ApproveRequestAsync(task.Id, _sam, _kit.Actor, Ct);

        result.Succeeded.Should().BeTrue();
        result.Task!.ConfirmedCount.Should().Be(2);
        result.Task.OpenSlots.Should().Be(0);
    }

    [HumansFact]
    public async Task Approve_WithoutRequest_SendsNothing()
    {
        var task = await _kit.CreateAsync(Input(slots: 2));
        await _kit.Service.AssignAsync(task.Id, _sam, _kit.Actor, Ct);

        (await _kit.Service.ApproveRequestAsync(task.Id, _sam, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoRequest");
        (await _kit.Service.ApproveRequestAsync(task.Id, _robin, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoRequest");
        (await _kit.Service.ApproveRequestAsync(Guid.NewGuid(), _sam, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoTask");
        _kit.SentEmails.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Approve_StillSucceeds_WhenTheEmailCannotBeQueued()
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        _kit.Email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("outbox down"));
        var task = await _kit.CreateAsync(Input(needsApproval: true));
        await ClaimAsync(task.Id);

        var result = await _kit.Service.ApproveRequestAsync(task.Id, _sam, _kit.Actor, Ct);

        result.Succeeded.Should().BeTrue();
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle(a => a.IsConfirmed);
    }

    [HumansFact]
    public async Task Decline_RemovesRequest_PersonMayAskAgain()
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        var task = await _kit.CreateAsync(Input(title: "Welfare tent", slots: 1, needsApproval: true, reason: "first aid"));
        await ClaimAsync(task.Id);

        var result = await _kit.Service.DeclineRequestAsync(task.Id, _sam, "  Needs a <b>first-aid</b> certificate  ", _kit.Actor, Ct);

        result.Task!.Assignments.Should().BeEmpty();
        result.Task.ApprovalReason.Should().Be("first aid", "the decline reason is not stored on the task");
        await AuditedAsync(TasksAuditActions.TaskRequestDeclined, task.Id, _kit.Actor, _sam, ": Needs a <b>first-aid</b> certificate");
        var mail = _kit.SentEmails.Should().ContainSingle().Subject;
        mail.TemplateName.Should().Be("task_request_declined");
        mail.Category.Should().Be(MessageCategory.VolunteerUpdates);
        mail.HtmlBody.Should().Contain("Needs a &lt;b&gt;first-aid&lt;/b&gt; certificate").And.NotContain("<b>first-aid</b>");
        (await ClaimAsync(task.Id)).Task!.Assignments.Should().ContainSingle(a => !a.IsConfirmed, "a declined person may ask again");
    }

    [HumansFact]
    public async Task Decline_WithoutReason_EmailHasNoReasonLine()
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        var task = await _kit.CreateAsync(Input(needsApproval: true));
        await ClaimAsync(task.Id);

        await _kit.Service.DeclineRequestAsync(task.Id, _sam, "   ", _kit.Actor, Ct);

        _kit.SentEmails.Should().ContainSingle().Which.HtmlBody.Should().NotContain("Reason:");
        await _kit.Audit.Received(1).LogAsync(
            TasksAuditActions.TaskRequestDeclined, "TaskItem", task.Id,
            Arg.Is<string>(d => d.EndsWith("on Production", StringComparison.Ordinal)),
            _kit.Actor, _sam, AuditSubjectTypes.User);
    }

    [HumansFact]
    public async Task Decline_TooLongReason_ChangesNothing()
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        var task = await _kit.CreateAsync(Input(needsApproval: true));
        await ClaimAsync(task.Id);

        var result = await _kit.Service.DeclineRequestAsync(task.Id, _sam, new string('x', 201), _kit.Actor, Ct);

        result.Refusal.Should().Be("Tasks_Refusal_ReasonTooLong");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle(a => !a.IsConfirmed);
        _kit.SentEmails.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Decline_WithoutRequest_KeepsConfirmedPlace()
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        var task = await _kit.CreateAsync(Input(slots: 2));
        await _kit.Service.AssignAsync(task.Id, _sam, _kit.Actor, Ct);

        (await _kit.Service.DeclineRequestAsync(task.Id, _sam, "no", _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoRequest");
        (await _kit.Service.DeclineRequestAsync(task.Id, _robin, null, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NoRequest");

        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle(a => a.IsConfirmed);
        _kit.SentEmails.Should().BeEmpty();
    }

    [HumansFact]
    public async Task OnlyApproveAndDecline_SendEmail()
    {
        _kit.GiveEmail(_sam, "sam@example.com");
        _kit.GiveEmail(_robin, "robin@example.com");
        var gated = await _kit.CreateAsync(Input(slots: 3, needsApproval: true));
        var plain = await _kit.CreateAsync(Input(slots: 3));

        await ClaimAsync(gated.Id, _sam);
        await ClaimAsync(plain.Id, _robin);
        await _kit.Service.WithdrawRequestAsync(gated.Id, _sam, Ct);
        await _kit.Service.ReleaseAsync(plain.Id, _robin, Ct);
        await _kit.Service.AssignAsync(plain.Id, _robin, _kit.Actor, Ct);
        await _kit.Service.UnassignAsync(plain.Id, _robin, _kit.Actor, Ct);
        _kit.SentEmails.Should().BeEmpty();

        await ClaimAsync(gated.Id, _sam);
        await ClaimAsync(gated.Id, _robin);
        await _kit.Service.ApproveRequestAsync(gated.Id, _sam, _kit.Actor, Ct);
        await _kit.Service.DeclineRequestAsync(gated.Id, _robin, null, _kit.Actor, Ct);

        _kit.SentEmails.Select(m => m.TemplateName).Should().Equal("task_request_approved", "task_request_declined");
    }

    [HumansFact]
    public async Task Assign_WhenPersonAlreadyAsked_IsRefused()
    {
        var task = await _kit.CreateAsync(Input(slots: 3, needsApproval: true));
        await ClaimAsync(task.Id);

        (await _kit.Service.AssignAsync(task.Id, _sam, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_AlreadyOn");
    }

    [HumansFact]
    public async Task Unassign_OnARequest_IsRefused()
    {
        var task = await _kit.CreateAsync(Input(slots: 3, needsApproval: true));
        await ClaimAsync(task.Id);

        (await _kit.Service.UnassignAsync(task.Id, _sam, _kit.Actor, Ct)).Refusal.Should().Be("Tasks_Refusal_NotOn");
        (await _kit.Service.GetTaskAsync(task.Id, Ct))!.Assignments.Should().ContainSingle(a => a.UserId == _sam && !a.IsConfirmed);
    }
}
