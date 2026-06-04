using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Surveys;

/// <summary>
/// Application-layer <see cref="ISurveyService"/>. Plain Scoped service (no caching decorator, per
/// spec §12). Cross-domain display data is stitched from <c>I…ServiceRead</c> interfaces — the
/// repository never resolves user/team navs. Authoring lives here; send/submit/results/export/GDPR
/// are added in their phases (constructor grows with them).
/// </summary>
public sealed class SurveyService(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<SurveyService> logger,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    ITicketServiceRead ticketService,
    IShiftView shiftView,
    IUserEmailService userEmailService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ISurveyInviteTokenProvider tokenProvider) : ISurveyService
{
    public async Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        var surveys = await repo.GetAllSummariesAsync(ct);
        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);
        var responses = await repo.GetResponseCountsBySurveyAsync(ct);

        return surveys.Select(s => new SurveySummary(
            s.Id,
            s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
            s.Status,
            invited.GetValueOrDefault(s.Id),
            responses.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default)
    {
        var s = await repo.GetByIdAsync(surveyId, ct);
        if (s is null) return null;

        var input = new SurveyEditInput(
            s.Title, s.Intro, s.ThankYou, s.DefaultCulture, s.AllowAnonymous, s.OpensAt, s.ClosesAt,
            s.AudienceType, s.AudienceTeamId, s.PublicSlug,
            s.Questions
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => new QuestionInput(
                    q.Id, q.PageNumber, q.Order, q.Type, q.Prompt, q.HelpText, q.IsRequired,
                    q.RatingMin, q.RatingMax, q.RatingMinLabel, q.RatingMaxLabel, q.ShowIf,
                    q.Options.OrderBy(o => o.Order)
                        .Select(o => new OptionInput(o.Id, o.Order, o.Value, o.Label)).ToList()))
                .ToList());

        return new SurveyDetail(s.Id, s.Status, input);
    }

    public async Task<Guid> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var surveyId = Guid.NewGuid();
        var questions = MapQuestions(surveyId, input);
        ValidateBranching(questions);

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            Status = SurveyStatus.Draft,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
            Questions = questions,
        };

        await repo.AddAsync(survey, ct);
        logger.LogInformation("Survey {SurveyId} created by {UserId}", surveyId, actorUserId);
        await auditLog.LogAsync(AuditAction.SurveyCreated, nameof(Survey), surveyId,
            $"Created survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}'", actorUserId);
        return surveyId;
    }

    public async Task UpdateAsync(Guid surveyId, SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var questions = MapQuestions(surveyId, input);
        ValidateBranching(questions);

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            UpdatedAt = now,
            Questions = questions,
        };

        await repo.UpdateAsync(survey, ct);
        await auditLog.LogAsync(AuditAction.SurveyUpdated, nameof(Survey), surveyId, "Updated survey", actorUserId);
    }

    public async Task OpenAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Open) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Open, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyOpened, nameof(Survey), surveyId, "Opened survey", actorUserId);
    }

    public async Task CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Closed) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Closed, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyClosed, nameof(Survey), surveyId, "Closed survey", actorUserId);
    }

    public async Task<int> PreviewAudienceCountAsync(Guid surveyId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct);
        if (survey?.AudienceType is null) return 0;

        var recipients = await ResolveRecipientIdsAsync(survey.AudienceType.Value, survey.AudienceTeamId, ct);
        return recipients.Count;
    }

    public async Task<SendResult> SendInvitesAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var survey = await repo.GetByIdAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (survey.Status != SurveyStatus.Open)
            throw new InvalidOperationException("Invitations can only be sent for an Open survey.");
        if (survey.AudienceType is null)
            throw new InvalidOperationException("Survey has no audience to invite.");

        var now = clock.GetCurrentInstant();
        var target = await ResolveRecipientIdsAsync(survey.AudienceType.Value, survey.AudienceTeamId, ct);
        var alreadyInvited = await repo.GetInvitedUserIdsAsync(surveyId, ct);
        var netNew = target.Except(alreadyInvited).ToList();

        var emails = await userEmailService.GetNotificationTargetEmailsAsync(netNew, ct);
        var users = await userService.GetUserInfosAsync(netNew, ct);
        var title = survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture);

        var invitationsCreated = 0;
        var emailsQueued = 0;
        var failed = 0;

        foreach (var userId in netNew)
        {
            if (!emails.TryGetValue(userId, out var email))
            {
                logger.LogWarning(
                    "User {UserId} has no notification email for survey {SurveyId}; skipping invitation",
                    userId, surveyId);
                continue;
            }

            var inv = new SurveyInvitation
            {
                Id = Guid.NewGuid(),
                SurveyId = surveyId,
                UserId = userId,
                SentAt = now,
                LatestEmailStatus = EmailOutboxStatus.Queued,
                CreatedAt = now,
            };
            await repo.AddInvitationAndSaveAsync(inv, ct);
            invitationsCreated++;

            var culture = users.TryGetValue(userId, out var user) ? user.PreferredLanguage : survey.DefaultCulture;
            var name = user?.BurnerName ?? string.Empty;
            var token = tokenProvider.Create(inv.Id);
            var msg = emailMessages.SurveyInvitation(email, name, title, token, culture);

            try
            {
                await emailService.SendAsync(msg, ct);
                emailsQueued++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to enqueue survey invitation email for user {UserId} invitation {InvitationId} in survey {SurveyId}",
                    userId, inv.Id, surveyId);
                await repo.UpdateInvitationStatusAsync(inv.Id, EmailOutboxStatus.Failed, now, ct);
                failed++;
            }
        }

        await auditLog.LogAsync(AuditAction.SurveyInvitesSent, nameof(Survey), surveyId,
            $"Sent {invitationsCreated} invitation(s)", actorUserId);

        return new SendResult(invitationsCreated, emailsQueued, failed);
    }

    public async Task<IReadOnlyList<SurveyInviteStatus>> GetInviteStatusesAsync(Guid surveyId, CancellationToken ct = default)
    {
        var invitations = await repo.GetInvitationsAsync(surveyId, ct);
        if (invitations.Count == 0) return [];

        var userIds = invitations.Select(i => i.UserId).Distinct().ToList();
        var users = await userService.GetUserInfosAsync(userIds, ct);

        return invitations.Select(i => new SurveyInviteStatus(
            i.UserId,
            users.TryGetValue(i.UserId, out var user) ? user.BurnerName : i.UserId.ToString(),
            i.LatestEmailStatus,
            i.Completed,
            i.Started,
            i.SentAt,
            i.ReminderSentAt)).ToList();
    }

    /// <summary>
    /// Resolves an audience predicate into the set of recipient user ids via cross-section read
    /// interfaces. No marketing opt-out filter — surveys are System/always-send.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> ResolveRecipientIdsAsync(
        SurveyAudienceType type, Guid? teamId, CancellationToken ct)
    {
        switch (type)
        {
            case SurveyAudienceType.Team:
            {
                if (teamId is null) return new HashSet<Guid>();
                var team = await teamService.GetTeamAsync(teamId.Value, ct);
                return team?.Members.Select(m => m.UserId).ToHashSet() ?? new HashSet<Guid>();
            }

            case SurveyAudienceType.AllActiveMembers:
                return (await ActiveMemberIdsAsync(ct)).ToHashSet();

            case SurveyAudienceType.TicketHolders:
            {
                var orders = await ticketService.GetTicketOrdersAsync(ct);
                return orders
                    .Where(o => o.IsCurrentEvent)
                    .SelectMany(o => o.Attendees)
                    .Where(a => a.MatchedUserId.HasValue
                        && (a.Status == TicketAttendeeStatus.Valid || a.Status == TicketAttendeeStatus.CheckedIn))
                    .Select(a => a.MatchedUserId!.Value)
                    .ToHashSet();
            }

            case SurveyAudienceType.ShiftParticipants:
            {
                var activeIds = await ActiveMemberIdsAsync(ct);
                var views = await shiftView.GetUsersAsync(activeIds, ct);
                return views
                    .Where(kv => kv.Value.HasShift)
                    .Select(kv => kv.Key)
                    .ToHashSet();
            }

            default:
                return new HashSet<Guid>();
        }
    }

    private async Task<List<Guid>> ActiveMemberIdsAsync(CancellationToken ct)
    {
        var users = await userService.GetAllUserInfosAsync(ct);
        return users
            .Where(u => u.IsApproved && !u.IsGdprAnonymized && !u.IsDeletionPending && !u.IsMerged)
            .Select(u => u.Id)
            .ToList();
    }

    /// <summary>Maps builder input to tracked entities, assigning new ids where the input id is null.</summary>
    private static List<SurveyQuestion> MapQuestions(Guid surveyId, SurveyEditInput input)
        => input.Questions.Select(q =>
        {
            var questionId = q.Id ?? Guid.NewGuid();
            return new SurveyQuestion
            {
                Id = questionId,
                SurveyId = surveyId,
                PageNumber = q.PageNumber,
                Order = q.Order,
                Type = q.Type,
                Prompt = q.Prompt,
                HelpText = q.HelpText,
                IsRequired = q.IsRequired,
                RatingMin = q.RatingMin,
                RatingMax = q.RatingMax,
                RatingMinLabel = q.RatingMinLabel,
                RatingMaxLabel = q.RatingMaxLabel,
                ShowIf = q.ShowIf,
                Options = q.Options.Select(o => new SurveyQuestionOption
                {
                    Id = o.Id ?? Guid.NewGuid(),
                    QuestionId = questionId,
                    Order = o.Order,
                    Value = o.Value,
                    Label = o.Label,
                }).ToList<SurveyQuestionOption>(),
            };
        }).ToList();

    private static void ValidateBranching(IReadOnlyList<SurveyQuestion> questions)
    {
        var offenders = SurveyBranchingEvaluator.ValidateNoForwardReferences(questions);
        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                $"A branching condition references a question that is not strictly earlier. Offending question ids: {string.Join(", ", offenders)}.");
        }
    }

    private static string? NormalizeSlug(string? slug)
        => string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();
}
